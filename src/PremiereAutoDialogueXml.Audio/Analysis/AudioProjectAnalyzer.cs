using PremiereAutoDialogueXml.Audio.Pcm;
using PremiereAutoDialogueXml.Audio.Vad;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.ProjectModel;

namespace PremiereAutoDialogueXml.Audio.Analysis;

public sealed class AudioProjectAnalyzer
{
    private readonly Func<IVoiceActivityDetector> _detectorFactory;
    private readonly PcmWaveSampleReader _sampleReader;

    public AudioProjectAnalyzer()
        : this(() => new SileroVoiceActivityDetector(), new PcmWaveSampleReader())
    {
    }

    public AudioProjectAnalyzer(
        Func<IVoiceActivityDetector> detectorFactory,
        PcmWaveSampleReader sampleReader)
    {
        ArgumentNullException.ThrowIfNull(detectorFactory);
        ArgumentNullException.ThrowIfNull(sampleReader);
        _detectorFactory = detectorFactory;
        _sampleReader = sampleReader;
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
        var results = new TrackAudioAnalysis[tracks.Count];
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

                using var detector = _detectorFactory();
                var scanner = new TrackAudioScanner(_sampleReader);
                var observations = scanner.Scan(track, detector, preset, token);
                var analyzer = new TrackDialogueAnalyzer(new TimelinePcmAccessor(_sampleReader));
                results[position] = analyzer.Analyze(track, observations, preset, token);

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
        var resolved = bleedResolver.Resolve(project.Sequence, results, preset, cancellationToken);

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
            ShadowEvidence = shadowEvidence
        };
    }
}
