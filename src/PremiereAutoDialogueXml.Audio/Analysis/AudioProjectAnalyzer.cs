using PremiereAutoDialogueXml.Audio.Pcm;
using PremiereAutoDialogueXml.Audio.Vad;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.ProjectModel;

namespace PremiereAutoDialogueXml.Audio.Analysis;

public sealed class AudioProjectAnalyzer
{
    private readonly Func<IVoiceActivityDetector> _detectorFactory;
    private readonly PcmWaveSampleReader _sampleReader;
    private readonly bool _enableVadFrontEndShadow;

    public AudioProjectAnalyzer()
        : this(
            () => new SileroVoiceActivityDetector(),
            new PcmWaveSampleReader(),
            enableVadFrontEndShadow: true)
    {
    }

    public AudioProjectAnalyzer(
        Func<IVoiceActivityDetector> detectorFactory,
        PcmWaveSampleReader sampleReader,
        bool enableVadFrontEndShadow = false)
    {
        ArgumentNullException.ThrowIfNull(detectorFactory);
        ArgumentNullException.ThrowIfNull(sampleReader);
        _detectorFactory = detectorFactory;
        _sampleReader = sampleReader;
        _enableVadFrontEndShadow = enableVadFrontEndShadow;
    }

    public async Task<ProjectAudioAnalysis> AnalyzeAsync(
        PremiereProject project,
        DialogueProcessingPreset preset,
        IProgress<AudioAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(preset);
        var presetIssues = preset.Validate();
        if (presetIssues.Count > 0)
        {
            throw new ArgumentException(
                $"Preset không hợp lệ: {string.Join(", ", presetIssues.Select(issue => issue.Code))}",
                nameof(preset));
        }

        var tracks = project.Sequence.AudioTracks;
        var legacyResults = new TrackAudioAnalysis[tracks.Count];
        var candidateResults = _enableVadFrontEndShadow
            ? new TrackAudioAnalysis[tracks.Count]
            : legacyResults;
        var observationComparisons = _enableVadFrontEndShadow
            ? new ObservationComparison[tracks.Count]
            : [];
        var completed = 0;
        progress?.Report(new(0, tracks.Count, null, "Bắt đầu phân tích âm thanh."));

        await Parallel.ForEachAsync(
            Enumerable.Range(0, tracks.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = preset.MaximumWorkers,
                CancellationToken = cancellationToken
            },
            (position, token) =>
            {
                var track = tracks[position];
                progress?.Report(new(
                    Volatile.Read(ref completed),
                    tracks.Count,
                    track.Index,
                    $"Đang phân tích track {track.Index}."));

                var scanner = new TrackAudioScanner(_sampleReader);
                var analyzer = new TrackDialogueAnalyzer(new TimelinePcmAccessor(_sampleReader));
                using (var detector = _detectorFactory())
                {
                    var observations = scanner.Scan(
                        track,
                        detector,
                        preset,
                        VadResamplingMode.LegacyStride3,
                        token);
                    legacyResults[position] = analyzer.Analyze(track, observations, preset, token);

                    if (_enableVadFrontEndShadow)
                    {
                        using var candidateDetector = _detectorFactory();
                        var candidateObservations = scanner.Scan(
                            track,
                            candidateDetector,
                            preset,
                            VadResamplingMode.AntiAliasFir,
                            token);
                        candidateResults[position] = analyzer.Analyze(
                            track,
                            candidateObservations,
                            preset,
                            token);
                        observationComparisons[position] = CompareObservations(
                            observations,
                            candidateObservations);
                    }
                }

                var nowCompleted = Interlocked.Increment(ref completed);
                progress?.Report(new(
                    nowCompleted,
                    tracks.Count,
                    track.Index,
                    $"Đã phân tích xong track {track.Index}."));
                return ValueTask.CompletedTask;
            });

        progress?.Report(new(tracks.Count, tracks.Count, null, "Đang đối chiếu bleed giữa các track."));
        var bleedResolver = new BleedResolver(new TimelinePcmAccessor(_sampleReader));
        var resolvedLegacy = bleedResolver.Resolve(project.Sequence, legacyResults, preset, cancellationToken);
        IReadOnlyList<TrackAudioAnalysis> resolved;
        VadFrontEndComparison? frontEndComparison = null;
        if (_enableVadFrontEndShadow)
        {
            var resolvedCandidate = bleedResolver.Resolve(
                project.Sequence,
                candidateResults,
                preset,
                cancellationToken);
            var merged = new TrackAudioAnalysis[tracks.Count];
            var trackComparisons = new VadFrontEndTrackComparison[tracks.Count];
            for (var position = 0; position < tracks.Count; position++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var observationComparison = observationComparisons[position];
                (merged[position], trackComparisons[position]) = ConservativeVadAnalysisMerger.Merge(
                    resolvedLegacy[position],
                    resolvedCandidate[position],
                    observationComparison.ObservationCount,
                    observationComparison.ChangedObservationCount,
                    observationComparison.MaximumProbabilityDelta);
            }

            resolved = merged;
            frontEndComparison = new(
                VadResamplingMode.LegacyStride3.ToString(),
                VadResamplingMode.AntiAliasFir.ToString(),
                trackComparisons);
        }
        else
        {
            resolved = resolvedLegacy;
        }

        progress?.Report(new(tracks.Count, tracks.Count, null, "Đang tạo bằng chứng review đa mic."));
        var shadowEvidenceBuilder = new CrossTrackShadowEvidenceBuilder(
            new TimelinePcmAccessor(_sampleReader));
        var shadowEvidence = shadowEvidenceBuilder.Build(
            project.Sequence,
            resolved,
            preset,
            cancellationToken);

        progress?.Report(new(tracks.Count, tracks.Count, null, "Đã phân tích xong toàn bộ track."));
        return new(resolved, SileroVadModelInfo.Version, SileroVadModelInfo.Sha256)
        {
            ShadowEvidence = shadowEvidence,
            VadFrontEndComparison = frontEndComparison
        };
    }

    private static ObservationComparison CompareObservations(
        IReadOnlyList<AudioFrameObservation> legacy,
        IReadOnlyList<AudioFrameObservation> candidate)
    {
        if (legacy.Count != candidate.Count)
        {
            throw new InvalidDataException("Legacy/candidate VAD không cùng số observation.");
        }

        var changed = 0;
        var maximumDelta = 0f;
        for (var index = 0; index < legacy.Count; index++)
        {
            if (legacy[index].TimelineStartSample != candidate[index].TimelineStartSample ||
                legacy[index].TimelineEndSample != candidate[index].TimelineEndSample ||
                legacy[index].ContainsMedia != candidate[index].ContainsMedia ||
                Math.Abs(legacy[index].RmsDbfs - candidate[index].RmsDbfs) > 0.0001f ||
                Math.Abs(legacy[index].SamplePeakDbfs - candidate[index].SamplePeakDbfs) > 0.0001f)
            {
                throw new InvalidDataException("Legacy/candidate VAD không cùng timeline/PCM observation.");
            }

            var delta = Math.Abs(legacy[index].VadProbability - candidate[index].VadProbability);
            if (delta > 0.000001f)
            {
                changed++;
                maximumDelta = Math.Max(maximumDelta, delta);
            }
        }

        return new(legacy.Count, changed, maximumDelta);
    }

    private readonly record struct ObservationComparison(
        int ObservationCount,
        int ChangedObservationCount,
        float MaximumProbabilityDelta);
}
