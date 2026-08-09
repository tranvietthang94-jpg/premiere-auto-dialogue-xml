namespace PremiereAutoDialogueXml.Audio.Analysis;

internal sealed class StreamingFirDecimator3
{
    internal const int InputSampleRate = 48_000;
    internal const int OutputSampleRate = 16_000;
    internal const int DecimationFactor = InputSampleRate / OutputSampleRate;
    internal const int TapCount = 127;
    internal const double CutoffFrequencyHz = 7_000;

    private static readonly float[] Coefficients = BuildCoefficients();
    private readonly float[] _history = new float[TapCount];
    private int _writeIndex;
    private int _phase;

    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        var outputCount = 0;
        foreach (var sample in input)
        {
            _history[_writeIndex] = sample;
            _writeIndex = (_writeIndex + 1) % TapCount;

            if (_phase == 0)
            {
                if (outputCount >= output.Length)
                {
                    throw new ArgumentException("Buffer output resampler không đủ.", nameof(output));
                }

                output[outputCount++] = FilterCurrentSample();
            }

            _phase++;
            if (_phase == DecimationFactor)
            {
                _phase = 0;
            }
        }

        return outputCount;
    }

    public void Reset()
    {
        Array.Clear(_history);
        _writeIndex = 0;
        _phase = 0;
    }

    private float FilterCurrentSample()
    {
        double value = 0;
        var historyIndex = _writeIndex - 1;
        if (historyIndex < 0)
        {
            historyIndex = TapCount - 1;
        }

        for (var coefficientIndex = 0; coefficientIndex < TapCount; coefficientIndex++)
        {
            value += Coefficients[coefficientIndex] * _history[historyIndex];
            historyIndex--;
            if (historyIndex < 0)
            {
                historyIndex = TapCount - 1;
            }
        }

        return (float)value;
    }

    private static float[] BuildCoefficients()
    {
        var coefficients = new double[TapCount];
        var center = (TapCount - 1) / 2;
        var normalizedCutoff = CutoffFrequencyHz / InputSampleRate;
        double sum = 0;

        for (var index = 0; index < TapCount; index++)
        {
            var offset = index - center;
            var sinc = offset == 0
                ? 2 * normalizedCutoff
                : Math.Sin(2 * Math.PI * normalizedCutoff * offset) / (Math.PI * offset);
            var window = 0.42 -
                         (0.5 * Math.Cos(2 * Math.PI * index / (TapCount - 1))) +
                         (0.08 * Math.Cos(4 * Math.PI * index / (TapCount - 1)));
            coefficients[index] = sinc * window;
            sum += coefficients[index];
        }

        if (!double.IsFinite(sum) || Math.Abs(sum) < 1e-12)
        {
            throw new InvalidOperationException("Không thể chuẩn hóa hệ số resampler.");
        }

        return coefficients.Select(value => (float)(value / sum)).ToArray();
    }
}
