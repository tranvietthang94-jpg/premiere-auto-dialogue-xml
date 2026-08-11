using PremiereAutoDialogueXml.Audio.Pcm;
using PremiereAutoDialogueXml.Audio.Vad;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.ProjectModel;

namespace PremiereAutoDialogueXml.Audio.Analysis;

public sealed class TrackAudioScanner(PcmWaveSampleReader sampleReader)
{
    private const int SourceSamplesPerVadChunk =
        SileroVoiceActivityDetector.SupportedChunkSampleCount *
        (AudioMath.SourceSampleRate / SileroVoiceActivityDetector.SupportedSampleRate);

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

        if (detector.SampleRate != SileroVoiceActivityDetector.SupportedSampleRate ||
            detector.ChunkSampleCount != SileroVoiceActivityDetector.SupportedChunkSampleCount)
        {
            throw new ArgumentException("Detector không khớp hợp đồng Silero 16 kHz/512 mẫu.", nameof(detector));
        }

        var observations = new List<AudioFrameObservation>();
        var sourceChunk = new float[SourceSamplesPerVadChunk];
        var silence = new float[SourceSamplesPerVadChunk];
        var vadChunk = new float[SileroVoiceActivityDetector.SupportedChunkSampleCount];
        var antiAliasResampler = resamplingMode == VadResamplingMode.AntiAliasFir
            ? new StreamingFirDecimator3()
            : null;
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
            if (antiAliasResampler is null)
            {
                for (var index = 0; index < vadChunk.Length; index++)
                {
                    vadChunk[index] = sourceChunk[index * 3];
                }
            }
            else
            {
                var outputCount = antiAliasResampler.Process(sourceChunk, vadChunk);
                if (outputCount != vadChunk.Length)
                {
                    throw new InvalidDataException(
                        $"Resampler tạo {outputCount} mẫu thay vì {vadChunk.Length} mẫu VAD.");
                }
            }

            double squareSum = 0;
            var peak = 0f;
            for (var index = 0; index < filled; index++)
            {
                var absolute = MathF.Abs(sourceChunk[index]);
                peak = MathF.Max(peak, absolute);
                squareSum += sourceChunk[index] * sourceChunk[index];
            }

            var probability = detector.ProcessChunk(vadChunk);
            var rms = filled == 0 ? 0 : Math.Sqrt(squareSum / filled);
            observations.Add(new(
                chunkStart,
                checked(chunkStart + filled),
                probability,
                AudioMath.LinearToDbfs(rms),
                AudioMath.LinearToDbfs(peak),
                containsMedia));

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
            var clipStart = AudioMath.FramesToSamples(clip.TimelineStartFrame);
            var clipEnd = AudioMath.FramesToSamples(clip.TimelineEndFrame);

            if (!hasCursor)
            {
                cursor = clipStart;
                hasCursor = true;
                detector.Reset();
                antiAliasResampler?.Reset();
            }
            else if (clipStart > cursor)
            {
                var gap = clipStart - cursor;
                if (gap >= resetGapSamples)
                {
                    EmitChunk();
                    detector.Reset();
                    antiAliasResampler?.Reset();
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

            sampleReader.ReadRange(
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
        return observations;
    }
}
