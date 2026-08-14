using PremiereAutoDialogueXml.Audio.Pcm;
using PremiereAutoDialogueXml.Core.ProjectModel;
using PremiereAutoDialogueXml.Core.Timing;

namespace PremiereAutoDialogueXml.Audio.Analysis;

public sealed class TimelinePcmAccessor
{
    private readonly PcmWaveSampleReader _sampleReader;

    public TimelinePcmAccessor(PcmWaveSampleReader sampleReader)
        : this(sampleReader, PremiereNdfFrameGrid.Create(25))
    {
    }

    public TimelinePcmAccessor(
        PcmWaveSampleReader sampleReader,
        PremiereNdfFrameGrid frameGrid)
    {
        _sampleReader = sampleReader ?? throw new ArgumentNullException(nameof(sampleReader));
        if (!frameGrid.IsValid)
        {
            throw new ArgumentException("Frame-grid Premiere không hợp lệ.", nameof(frameGrid));
        }

        FrameGrid = frameGrid;
    }

    public PremiereNdfFrameGrid FrameGrid { get; }

    public float MeasureSamplePeakDbfs(
        PremiereAudioTrack track,
        long timelineStartSample,
        long timelineEndSample,
        CancellationToken cancellationToken = default)
    {
        var peak = 0f;
        VisitMediaRange(
            track,
            timelineStartSample,
            timelineEndSample,
            (block, _) =>
            {
                foreach (var sample in block.Span)
                {
                    peak = MathF.Max(peak, MathF.Abs(sample));
                }
            },
            cancellationToken);

        return AudioMath.LinearToDbfs(peak);
    }

    public int ReadTimelineRangeInto(
        PremiereAudioTrack track,
        long timelineStartSample,
        long timelineEndSample,
        float[] destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ValidateRange(timelineStartSample, timelineEndSample);
        var length = timelineEndSample - timelineStartSample;
        if (length > destination.Length)
        {
            throw new ArgumentException("Buffer waveform không đủ cho timeline range.", nameof(destination));
        }

        var sampleCount = checked((int)length);
        Array.Clear(destination, 0, sampleCount);
        VisitMediaRange(
            track,
            timelineStartSample,
            timelineEndSample,
            (block, timelineOffset) =>
            {
                var destinationOffset = checked((int)(timelineOffset - timelineStartSample));
                block.Span.CopyTo(destination.AsSpan(destinationOffset));
            },
            cancellationToken);
        return sampleCount;
    }

    private void VisitMediaRange(
        PremiereAudioTrack track,
        long timelineStartSample,
        long timelineEndSample,
        Action<ReadOnlyMemory<float>, long> consume,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(consume);
        ValidateRange(timelineStartSample, timelineEndSample);

        foreach (var clip in track.Clips)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clipStart = FrameGrid.FrameToSample(clip.TimelineStartFrame);
            var clipEnd = FrameGrid.FrameToSample(clip.TimelineEndFrame);
            var intersectionStart = Math.Max(timelineStartSample, clipStart);
            var intersectionEnd = Math.Min(timelineEndSample, clipEnd);
            if (intersectionStart >= intersectionEnd)
            {
                continue;
            }

            var sourceOffset = intersectionStart - clipStart;
            var sourceAvailable = (clip.SourceEndSample - clip.SourceStartSample) - sourceOffset;
            var readable = Math.Min(intersectionEnd - intersectionStart, Math.Max(0, sourceAvailable));
            if (readable == 0)
            {
                continue;
            }

            var emitted = 0L;
            _sampleReader.ReadRange(
                clip.SourceMedia.Wave,
                checked(clip.SourceStartSample + sourceOffset),
                readable,
                block =>
                {
                    consume(block, checked(intersectionStart + emitted));
                    emitted += block.Length;
                },
                cancellationToken);
        }
    }

    private static void ValidateRange(long start, long end)
    {
        if (start < 0 || end <= start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), "Timeline range phải có độ dài dương.");
        }
    }
}
