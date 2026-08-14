namespace PremiereAutoDialogueXml.Audio.Analysis;

internal static class AudioMath
{
    public const int SourceSampleRate = 48_000;
    public const float SilenceDbfs = -144f;

    public static long MillisecondsToSamples(int milliseconds) =>
        checked((long)milliseconds * SourceSampleRate / 1_000);

    public static float LinearToDbfs(double value) =>
        value <= 0 ? SilenceDbfs : (float)(20 * Math.Log10(value));
}
