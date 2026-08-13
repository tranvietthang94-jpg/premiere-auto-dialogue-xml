namespace PremiereAutoDialogueXml.Audio.Analysis;

internal static class WaveformCorrelation
{
    public static CorrelationResult FindBest(
        ReadOnlySpan<float> target48Khz,
        ReadOnlySpan<float> other48Khz,
        int maximumLagMilliseconds)
    {
        var sampleCount = Math.Min(target48Khz.Length, other48Khz.Length) / 3;
        if (sampleCount < 64)
        {
            return new(0, 0, 0);
        }

        var maximumLag = checked(maximumLagMilliseconds * 16);
        var bestCorrelation = 0f;
        var bestLag = 0;
        for (var lag = -maximumLag; lag <= maximumLag; lag++)
        {
            var targetStart = Math.Max(0, -lag);
            var otherStart = Math.Max(0, lag);
            var count = sampleCount - Math.Abs(lag);
            if (count < 64)
            {
                continue;
            }

            var correlation = NormalizedCorrelation48Khz(
                target48Khz,
                other48Khz,
                targetStart,
                otherStart,
                count);
            if (MathF.Abs(correlation) > MathF.Abs(bestCorrelation))
            {
                bestCorrelation = correlation;
                bestLag = lag;
            }
        }

        return new(MathF.Abs(bestCorrelation), (int)Math.Round(bestLag / 16d), bestLag);
    }

    public static float ResidualToTargetDb(
        ReadOnlySpan<float> target48Khz,
        ReadOnlySpan<float> other48Khz,
        int lagSamples16Khz)
    {
        var sampleCount = Math.Min(target48Khz.Length, other48Khz.Length) / 3;
        var targetStart = Math.Max(0, -lagSamples16Khz);
        var otherStart = Math.Max(0, lagSamples16Khz);
        var count = sampleCount - Math.Abs(lagSamples16Khz);
        if (count < 64)
        {
            return 0;
        }

        double dot = 0;
        double otherEnergy = 0;
        double targetEnergy = 0;
        for (var index = 0; index < count; index++)
        {
            var target = target48Khz[(targetStart + index) * 3];
            var other = other48Khz[(otherStart + index) * 3];
            dot += target * other;
            otherEnergy += other * other;
            targetEnergy += target * target;
        }

        if (otherEnergy <= double.Epsilon || targetEnergy <= double.Epsilon)
        {
            return 0;
        }

        var scale = dot / otherEnergy;
        double residualEnergy = 0;
        for (var index = 0; index < count; index++)
        {
            var target = target48Khz[(targetStart + index) * 3];
            var other = other48Khz[(otherStart + index) * 3];
            var residual = target - (scale * other);
            residualEnergy += residual * residual;
        }

        return residualEnergy <= double.Epsilon
            ? AudioMath.SilenceDbfs
            : (float)(10 * Math.Log10(residualEnergy / targetEnergy));
    }

    public static float TransferScaleToTarget(
        ReadOnlySpan<float> target48Khz,
        ReadOnlySpan<float> other48Khz,
        int lagSamples16Khz)
    {
        var sampleCount = Math.Min(target48Khz.Length, other48Khz.Length) / 3;
        var targetStart = Math.Max(0, -lagSamples16Khz);
        var otherStart = Math.Max(0, lagSamples16Khz);
        var count = sampleCount - Math.Abs(lagSamples16Khz);
        if (count < 64)
        {
            return 0;
        }

        double dot = 0;
        double otherEnergy = 0;
        for (var index = 0; index < count; index++)
        {
            var target = target48Khz[(targetStart + index) * 3];
            var other = other48Khz[(otherStart + index) * 3];
            dot += target * other;
            otherEnergy += other * other;
        }

        return otherEnergy <= double.Epsilon ? 0 : (float)(dot / otherEnergy);
    }

    public static float RmsDbfs(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return AudioMath.SilenceDbfs;
        }

        double squareSum = 0;
        foreach (var sample in samples)
        {
            squareSum += sample * sample;
        }

        return AudioMath.LinearToDbfs(Math.Sqrt(squareSum / samples.Length));
    }

    private static float NormalizedCorrelation48Khz(
        ReadOnlySpan<float> left,
        ReadOnlySpan<float> right,
        int leftStart16Khz,
        int rightStart16Khz,
        int sampleCount16Khz)
    {
        double leftSum = 0;
        double rightSum = 0;
        double dot = 0;
        double leftSquares = 0;
        double rightSquares = 0;
        for (var index = 0; index < sampleCount16Khz; index++)
        {
            var leftSample = left[(leftStart16Khz + index) * 3];
            var rightSample = right[(rightStart16Khz + index) * 3];
            leftSum += leftSample;
            rightSum += rightSample;
            dot += leftSample * rightSample;
            leftSquares += leftSample * leftSample;
            rightSquares += rightSample * rightSample;
        }

        var leftEnergy = leftSquares - ((leftSum * leftSum) / sampleCount16Khz);
        var rightEnergy = rightSquares - ((rightSum * rightSum) / sampleCount16Khz);
        var centeredDot = dot - ((leftSum * rightSum) / sampleCount16Khz);
        var denominator = Math.Sqrt(leftEnergy * rightEnergy);
        return denominator <= double.Epsilon ? 0 : (float)(centeredDot / denominator);
    }

    internal readonly record struct CorrelationResult(
        float Correlation,
        int LagMilliseconds,
        int LagSamples16Khz);
}
