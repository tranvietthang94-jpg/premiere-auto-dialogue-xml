using PremiereAutoDialogueXml.Audio.Pcm;
using PremiereAutoDialogueXml.Audio.Vad;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.ProjectModel;
using PremiereAutoDialogueXml.Core.Timing;

namespace PremiereAutoDialogueXml.Audio.Analysis;

public sealed class TrackAudioScanner
{
    private const int SourceSamplesPerVadChunk =
        SileroVoiceActivityDetector.SupportedChunkSampleCount *
        (AudioMath.SourceSampleRate / SileroVoiceActivityDetector.SupportedSampleRate);
    private readonly PremiereNdfFrameGrid _frameGrid;
    private readonly PcmWaveSampleReader _sampleReader;

    public TrackAudioScanner(PcmWaveSampleReader sampleReader)
        : this(sampleReader, PremiereNdfFrameGrid.Create(25))
    {
    }

    public TrackAudioScanner(
        PcmWaveSampleReader sampleReader,
        PremiereNdfFrameGrid frameGrid)
    {
        _sampleReader = sampleReader ?? throw new ArgumentNullException(nameof(sampleReader));
        if (!frameGrid.IsValid)
        {
            throw new ArgumentException("Frame-grid Premiere không hợp lệ.", nameof(frameGrid));
        }

        _frameGrid = frameGrid;
    }

    public IReadOnlyList<AudioFrameObservation> Scan(
        PremiereAudioTrack track,
        IVoiceActivityDetector detector,
        DialogueProcessingPreset preset,
        CancellationToken cancellationToken = default) =>
        Scan(track, detector, preset, VadResamplingMode.LegacyStride3, cancellationToken);

    public IReadOnlyList<AudioFrameObservation> Scan(
        PremiereAudioTrack track,
        IVoiceActivityDetector detector,
        DialogueProcessingPreset preset,
        VadResamplingMode resamplingMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(preset);
        var target = new ScanTarget(detector, resamplingMode, nameof(detector));
        ScanCore(track, preset, [target], cancellationToken);
        return target.Observations;
    }

    public VadObservationPair ScanPair(
        PremiereAudioTrack track,
        IVoiceActivityDetector legacyDetector,
        IVoiceActivityDetector antiAliasDetector,
        DialogueProcessingPreset preset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(legacyDetector);
        ArgumentNullException.ThrowIfNull(antiAliasDetector);
        ArgumentNullException.ThrowIfNull(preset);
        if (ReferenceEquals(legacyDetector, antiAliasDetector))
        {
            throw new ArgumentException("Hai front-end VAD phải dùng detector state độc lập.", nameof(antiAliasDetector));
        }

        var legacy = new ScanTarget(legacyDetector, VadResamplingMode.LegacyStride3, nameof(legacyDetector));
        var antiAlias = new ScanTarget(
            antiAliasDetector,
            VadResamplingMode.AntiAliasFir,
            nameof(antiAliasDetector));
        ScanCore(track, preset, [legacy, antiAlias], cancellationToken);
        return new(legacy.Observations, antiAlias.Observations);
    }

    private void ScanCore(
        PremiereAudioTrack track,
        DialogueProcessingPreset preset,
        IReadOnlyList<ScanTarget> targets,
        CancellationToken cancellationToken)
    {
        var sourceChunk = new float[SourceSamplesPerVadChunk];
        var silence = new float[SourceSamplesPerVadChunk];
        var filled = 0;
        var chunkStart = 0L;
        var cursor = 0L;
        var hasCursor = false;
        var containsMedia = false;
        var resetGapSamples = AudioMath.MillisecondsToSamples(preset.PhraseBreakMilliseconds);

        void EmitChunk()
        {
            if (filled == 0)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            sourceChunk.AsSpan(filled).Clear();
            double squareSum = 0;
            var peak = 0f;
            for (var index = 0; index < filled; index++)
            {
                var absolute = MathF.Abs(sourceChunk[index]);
                peak = MathF.Max(peak, absolute);
                squareSum += sourceChunk[index] * sourceChunk[index];
            }

            var rms = filled == 0 ? 0 : Math.Sqrt(squareSum / filled);
            var rmsDbfs = AudioMath.LinearToDbfs(rms);
            var peakDbfs = AudioMath.LinearToDbfs(peak);
            foreach (var target in targets)
            {
                target.Emit(
                    sourceChunk,
                    chunkStart,
                    checked(chunkStart + filled),
                    rmsDbfs,
                    peakDbfs,
                    containsMedia);
            }

            sourceChunk.AsSpan(0, filled).Clear();
            filled = 0;
            containsMedia = false;
        }

        void Consume(ReadOnlySpan<float> samples, bool isMedia)
        {
            while (!samples.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (filled == 0)
                {
                    chunkStart = cursor;
                }

                var copyCount = Math.Min(sourceChunk.Length - filled, samples.Length);
                samples[..copyCount].CopyTo(sourceChunk.AsSpan(filled));
                filled += copyCount;
                cursor = checked(cursor + copyCount);
                containsMedia |= isMedia;
                samples = samples[copyCount..];

                if (filled == sourceChunk.Length)
                {
                    EmitChunk();
                }
            }
        }

        void ConsumeSilence(long sampleCount, bool isMedia)
        {
            while (sampleCount > 0)
            {
                var count = checked((int)Math.Min(sourceChunk.Length, sampleCount));
                Consume(silence.AsSpan(0, count), isMedia);
                sampleCount -= count;
            }
        }

        foreach (var clip in track.Clips.OrderBy(clip => clip.TimelineStartFrame))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clipStart = _frameGrid.FrameToSample(clip.TimelineStartFrame);
            var clipEnd = _frameGrid.FrameToSample(clip.TimelineEndFrame);

            if (!hasCursor)
            {
                cursor = clipStart;
                hasCursor = true;
                foreach (var target in targets)
                {
                    target.Reset();
                }
            }
            else if (clipStart > cursor)
            {
                var gap = clipStart - cursor;
                if (gap >= resetGapSamples)
                {
                    EmitChunk();
                    foreach (var target in targets)
                    {
                        target.Reset();
                    }
                    cursor = clipStart;
                }
                else
                {
                    ConsumeSilence(gap, isMedia: false);
                }
            }
            else if (clipStart < cursor)
            {
                throw new InvalidDataException($"Track {track.Index} có timeline chồng lấn khi quét audio.");
            }

            var timelineSampleCount = clipEnd - clipStart;
            var sourceSampleCount = clip.SourceEndSample - clip.SourceStartSample;
            var readableSampleCount = Math.Min(timelineSampleCount, sourceSampleCount);

            _sampleReader.ReadRange(
                clip.SourceMedia.Wave,
                clip.SourceStartSample,
                readableSampleCount,
                block => Consume(block.Span, isMedia: true),
                cancellationToken);

            var roundingTail = timelineSampleCount - readableSampleCount;
            if (roundingTail > 0)
            {
                ConsumeSilence(roundingTail, isMedia: false);
            }
        }

        EmitChunk();
    }

    private sealed class ScanTarget
    {
        private readonly IVoiceActivityDetector _detector;
        private readonly float[] _vadChunk = new float[SileroVoiceActivityDetector.SupportedChunkSampleCount];
        private readonly StreamingFirDecimator3? _antiAliasResampler;

        public ScanTarget(
            IVoiceActivityDetector detector,
            VadResamplingMode resamplingMode,
            string parameterName)
        {
            if (detector.SampleRate != SileroVoiceActivityDetector.SupportedSampleRate ||
                detector.ChunkSampleCount != SileroVoiceActivityDetector.SupportedChunkSampleCount)
            {
                throw new ArgumentException(
                    "Detector không khớp hợp đồng Silero 16 kHz/512 mẫu.",
                    parameterName);
            }

            _detector = detector;
            _antiAliasResampler = resamplingMode == VadResamplingMode.AntiAliasFir
                ? new StreamingFirDecimator3()
                : null;
        }

        public List<AudioFrameObservation> Observations { get; } = [];

        public void Emit(
            ReadOnlySpan<float> sourceChunk,
            long timelineStartSample,
            long timelineEndSample,
            float rmsDbfs,
            float peakDbfs,
            bool containsMedia)
        {
            if (_antiAliasResampler is null)
            {
                for (var index = 0; index < _vadChunk.Length; index++)
                {
                    _vadChunk[index] = sourceChunk[index * 3];
                }
            }
            else
            {
                var outputCount = _antiAliasResampler.Process(sourceChunk, _vadChunk);
                if (outputCount != _vadChunk.Length)
                {
                    throw new InvalidDataException(
                        $"Resampler tạo {outputCount} mẫu thay vì {_vadChunk.Length} mẫu VAD.");
                }
            }

            Observations.Add(new(
                timelineStartSample,
                timelineEndSample,
                _detector.ProcessChunk(_vadChunk),
                rmsDbfs,
                peakDbfs,
                containsMedia));
        }

        public void Reset()
        {
            _detector.Reset();
            _antiAliasResampler?.Reset();
        }
    }
}

public sealed record VadObservationPair(
    IReadOnlyList<AudioFrameObservation> Legacy,
    IReadOnlyList<AudioFrameObservation> AntiAlias);
