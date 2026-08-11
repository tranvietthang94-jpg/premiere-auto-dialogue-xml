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
        var legacyBaselineResults = new TrackAudioAnalysis[tracks.Count];
        var legacyNoiseCandidateResults = _enableVadFrontEndShadow
            ? new TrackAudioAnalysis[tracks.Count]
            : legacyBaselineResults;
        var antiAliasBaselineResults = _enableVadFrontEndShadow
            ? new TrackAudioAnalysis[tracks.Count]
            : legacyBaselineResults;
        var antiAliasNoiseCandidateResults = _enableVadFrontEndShadow
            ? new TrackAudioAnalysis[tracks.Count]
            : legacyBaselineResults;
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
                    if (_enableVadFrontEndShadow)
                    {
                        var legacyShadow = analyzer.AnalyzeShadow(track, observations, preset, token);
                        legacyBaselineResults[position] = legacyShadow.Baseline;
                        legacyNoiseCandidateResults[position] = legacyShadow.Candidate;

                        using var candidateDetector = _detectorFactory();
                        var candidateObservations = scanner.Scan(
                            track,
                            candidateDetector,
                            preset,
                            VadResamplingMode.AntiAliasFir,
                            token);
                        var antiAliasShadow = analyzer.AnalyzeShadow(
                            track, candidateObservations, preset, token);
                        antiAliasBaselineResults[position] = antiAliasShadow.Baseline;
                        antiAliasNoiseCandidateResults[position] = antiAliasShadow.Candidate;
                        observationComparisons[position] = CompareObservations(
                            observations,
                            candidateObservations);
                    }
                    else
                    {
                        legacyBaselineResults[position] = analyzer.Analyze(
                            track, observations, preset, token);
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
        var resolvedLegacyBaseline = bleedResolver.Resolve(
            project.Sequence,
            legacyBaselineResults,
            preset,
            cancellationToken);
        IReadOnlyList<TrackAudioAnalysis> resolved;
        VadFrontEndComparison? frontEndComparison = null;
        NoiseBoundaryProjectComparison? noiseBoundaryComparison = null;
        if (_enableVadFrontEndShadow)
        {
            var resolvedLegacyNoiseCandidate = bleedResolver.Resolve(
                project.Sequence,
                legacyNoiseCandidateResults,
                preset,
                cancellationToken);
            var resolvedAntiAliasBaseline = bleedResolver.Resolve(
                project.Sequence,
                antiAliasBaselineResults,
                preset,
                cancellationToken);
            var resolvedAntiAliasNoiseCandidate = bleedResolver.Resolve(
                project.Sequence,
                antiAliasNoiseCandidateResults,
                preset,
                cancellationToken);
            var mergedLegacyNoise = new TrackAudioAnalysis[tracks.Count];
            var mergedAntiAliasNoise = new TrackAudioAnalysis[tracks.Count];
            var legacyNoiseComparisons = new NoiseBoundaryTrackComparison[tracks.Count];
            var antiAliasNoiseComparisons = new NoiseBoundaryTrackComparison[tracks.Count];
            for (var position = 0; position < tracks.Count; position++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var legacyComparison = NoiseBoundaryShadowComparer.Compare(
                    resolvedLegacyBaseline[position],
                    resolvedLegacyNoiseCandidate[position]);
                (mergedLegacyNoise[position], legacyNoiseComparisons[position]) =
                    ConservativeNoiseBoundaryAnalysisMerger.Merge(
                        resolvedLegacyBaseline[position],
                        resolvedLegacyNoiseCandidate[position],
                        legacyComparison);

                var antiAliasComparison = NoiseBoundaryShadowComparer.Compare(
                    resolvedAntiAliasBaseline[position],
                    resolvedAntiAliasNoiseCandidate[position]);
                (mergedAntiAliasNoise[position], antiAliasNoiseComparisons[position]) =
                    ConservativeNoiseBoundaryAnalysisMerger.Merge(
                        resolvedAntiAliasBaseline[position],
                        resolvedAntiAliasNoiseCandidate[position],
                        antiAliasComparison);
            }

            var merged = new TrackAudioAnalysis[tracks.Count];
            var trackComparisons = new VadFrontEndTrackComparison[tracks.Count];
            for (var position = 0; position < tracks.Count; position++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var observationComparison = observationComparisons[position];
                (merged[position], trackComparisons[position]) = ConservativeVadAnalysisMerger.Merge(
                    mergedLegacyNoise[position],
                    mergedAntiAliasNoise[position],
                    observationComparison.ObservationCount,
                    observationComparison.ChangedObservationCount,
                    observationComparison.MaximumProbabilityDelta);
            }

            resolved = merged;
            frontEndComparison = new(
                VadResamplingMode.LegacyStride3.ToString(),
                VadResamplingMode.AntiAliasFir.ToString(),
                trackComparisons);
            noiseBoundaryComparison = new(
            [
                new(VadResamplingMode.LegacyStride3.ToString(), legacyNoiseComparisons),
                new(VadResamplingMode.AntiAliasFir.ToString(), antiAliasNoiseComparisons)
            ]);
        }
        else
        {
            resolved = resolvedLegacyBaseline;
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
            VadFrontEndComparison = frontEndComparison,
            NoiseBoundaryComparison = noiseBoundaryComparison
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
