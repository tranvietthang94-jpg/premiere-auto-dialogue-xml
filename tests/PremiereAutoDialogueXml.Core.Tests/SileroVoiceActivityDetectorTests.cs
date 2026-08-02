using System.Security.Cryptography;
using PremiereAutoDialogueXml.Audio.Vad;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class SileroVoiceActivityDetectorTests
{
    [TestMethod]
    public void EmbeddedModelMatchesPinnedChecksum()
    {
        var model = SileroVadModelInfo.LoadVerifiedModel();

        Assert.HasCount(2_327_524, model);
        Assert.AreEqual(SileroVadModelInfo.Sha256, Convert.ToHexString(SHA256.HashData(model)));
    }

    [TestMethod]
    public void SilenceProducesFiniteLowProbabilityAndResetIsDeterministic()
    {
        using var detector = new SileroVoiceActivityDetector();
        var silence = new float[detector.ChunkSampleCount];

        var firstProbability = detector.ProcessChunk(silence);
        var secondProbability = detector.ProcessChunk(silence);
        detector.Reset();
        var resetProbability = detector.ProcessChunk(silence);

        Assert.IsTrue(float.IsFinite(firstProbability));
        Assert.IsLessThan(0.10f, firstProbability, $"Xác suất silence quá cao: {firstProbability}");
        Assert.IsLessThan(0.10f, secondProbability, $"Xác suất silence thứ hai quá cao: {secondProbability}");
        Assert.AreEqual(firstProbability, resetProbability, 0.000001f);
    }

    [TestMethod]
    public void ProcessChunkRejectsWrongLength()
    {
        using var detector = new SileroVoiceActivityDetector();

        Assert.ThrowsExactly<ArgumentException>(() => detector.ProcessChunk(new float[511]));
    }
}
