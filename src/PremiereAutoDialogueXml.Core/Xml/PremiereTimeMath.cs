using System.Numerics;

namespace PremiereAutoDialogueXml.Core.Xml;

public static class PremiereTimeMath
{
    public const long TicksPerSecond = 254_016_000_000;

    public static long ScaleFloor(long value, long numerator, long denominator)
    {
        ValidateScale(value, numerator, denominator);
        return checked((long)((BigInteger)value * numerator / denominator));
    }

    public static long ScaleCeiling(long value, long numerator, long denominator)
    {
        ValidateScale(value, numerator, denominator);
        var scaled = (BigInteger)value * numerator;
        return checked((long)((scaled + denominator - 1) / denominator));
    }

    private static void ValidateScale(long value, long numerator, long denominator)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        if (numerator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numerator));
        }

        if (denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(denominator));
        }
    }
}
