using PremiereAutoDialogueXml.Core.Xml;

namespace PremiereAutoDialogueXml.Core.Timing;

public readonly record struct PremiereNdfFrameGrid
{
    public const int RequiredAudioSampleRate = 48_000;

    private PremiereNdfFrameGrid(int framesPerSecond, int audioSampleRate)
    {
        FramesPerSecond = framesPerSecond;
        AudioSampleRate = audioSampleRate;
        SamplesPerFrame = audioSampleRate / framesPerSecond;
    }

    public int FramesPerSecond { get; }

    public int AudioSampleRate { get; }

    public int SamplesPerFrame { get; }

    public static bool IsSupportedFrameRate(int framesPerSecond) =>
        framesPerSecond is 24 or 25 or 30;

    public static PremiereNdfFrameGrid Create(
        int framesPerSecond,
        int audioSampleRate = RequiredAudioSampleRate)
    {
        if (!IsSupportedFrameRate(framesPerSecond))
        {
            throw new ArgumentOutOfRangeException(
                nameof(framesPerSecond),
                framesPerSecond,
                "Phase 12 chỉ định nghĩa lưới NDF nguyên 24, 25 hoặc 30 fps.");
        }

        if (audioSampleRate != RequiredAudioSampleRate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(audioSampleRate),
                audioSampleRate,
                "Cổng frame-rate Phase 12 chỉ dùng audio 48 kHz.");
        }

        if (audioSampleRate % framesPerSecond != 0 ||
            PremiereTimeMath.TicksPerSecond % framesPerSecond != 0)
        {
            throw new ArgumentException(
                "Frame rate phải tạo được số sample và pproTicks nguyên trên mỗi video frame.",
                nameof(framesPerSecond));
        }

        return new(framesPerSecond, audioSampleRate);
    }

    public long FrameToSample(long frame) =>
        PremiereTimeMath.ScaleFloor(frame, AudioSampleRate, FramesPerSecond);

    public long SampleToFrameFloor(long sample) =>
        PremiereTimeMath.ScaleFloor(sample, FramesPerSecond, AudioSampleRate);

    public long SampleToFrameCeiling(long sample) =>
        PremiereTimeMath.ScaleCeiling(sample, FramesPerSecond, AudioSampleRate);

    public long FrameToTicks(long frame) =>
        PremiereTimeMath.ScaleFloor(frame, PremiereTimeMath.TicksPerSecond, FramesPerSecond);

    public long TicksToSampleFloor(long ticks) =>
        PremiereTimeMath.ScaleFloor(ticks, AudioSampleRate, PremiereTimeMath.TicksPerSecond);

    public long TicksToSampleCeiling(long ticks) =>
        PremiereTimeMath.ScaleCeiling(ticks, AudioSampleRate, PremiereTimeMath.TicksPerSecond);
}
