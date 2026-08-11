using PremiereAutoDialogueXml.Audio.Analysis;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class StreamingFirDecimator3Tests
{
    [TestMethod]
    public void ProcessIsIndependentOfInputBlockBoundaries()
    {
        var input = Enumerable.Range(0, 48_000)
            .Select(index =>
                (0.35f * MathF.Sin(2 * MathF.PI * 1_000 * index / 48_000)) +
                (0.08f * MathF.Sin(2 * MathF.PI * 5_500 * index / 48_000)))
            .ToArray();

        var contiguous = Process(input, [input.Length]);
        var fragmented = Process(input, [1, 7, 31, 512, 1_537, 4_096]);

        CollectionAssert.AreEqual(contiguous, fragmented);
        Assert.HasCount(16_000, contiguous);
    }

    [TestMethod]
    public void ProcessPreservesSpeechBandLevelAfterWarmup()
    {
        var input = Tone(1_000, amplitude: 0.5f);
        var output = Process(input, [257, 1_024, 3_333]);

        var inputRms = Rms(input.AsSpan(1_000));
        var outputRms = Rms(output.AsSpan(500));
        var deltaDb = 20 * Math.Log10(outputRms / inputRms);

        Assert.IsLessThan(0.05, Math.Abs(deltaDb), $"Passband lệch {deltaDb:0.000} dB.");
    }

    [TestMethod]
    public void ProcessSuppressesToneAboveOutputNyquistBeforeDecimation()
    {
        var passband = Process(Tone(1_000, amplitude: 0.5f), [4_096]);
        var stopband = Process(Tone(12_000, amplitude: 0.5f), [4_096]);

        var passbandRms = Rms(passband.AsSpan(500));
        var stopbandRms = Rms(stopband.AsSpan(500));
        var rejectionDb = 20 * Math.Log10(stopbandRms / passbandRms);

        Assert.IsLessThan(-60, rejectionDb, $"Stopband chỉ suy giảm {rejectionDb:0.0} dB.");
    }

    [TestMethod]
    public void ResetClearsFilterHistoryAndPhase()
    {
        var resampler = new StreamingFirDecimator3();
        var warmupOutput = new float[1_000];
        _ = resampler.Process(Enumerable.Repeat(0.8f, 3_000).ToArray(), warmupOutput);
        resampler.Reset();

        var silenceOutput = new float[1_000];
        var count = resampler.Process(new float[3_000], silenceOutput);

        Assert.AreEqual(1_000, count);
        Assert.IsTrue(silenceOutput.All(value => value == 0));
    }

    private static float[] Tone(int frequency, float amplitude) =>
        Enumerable.Range(0, 48_000)
            .Select(index => amplitude * MathF.Sin(2 * MathF.PI * frequency * index / 48_000))
            .ToArray();

    private static float[] Process(float[] input, IReadOnlyList<int> blockPattern)
    {
        var resampler = new StreamingFirDecimator3();
        var output = new List<float>(input.Length / 3 + 1);
        var inputOffset = 0;
        var patternIndex = 0;
        while (inputOffset < input.Length)
        {
            var blockLength = Math.Min(blockPattern[patternIndex++ % blockPattern.Count], input.Length - inputOffset);
            var blockOutput = new float[(blockLength + 2) / 3 + 1];
            var count = resampler.Process(
                input.AsSpan(inputOffset, blockLength),
                blockOutput);
            output.AddRange(blockOutput.AsSpan(0, count).ToArray());
            inputOffset += blockLength;
        }

        return output.ToArray();
    }

    private static double Rms(ReadOnlySpan<float> values)
    {
        double sum = 0;
        foreach (var value in values)
        {
            sum += value * value;
        }

        return Math.Sqrt(sum / values.Length);
    }
}
