using PremiereAutoDialogueXml.Core.Domain;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class DialogueProcessingPresetTests
{
    [TestMethod]
    public void BalancedPresetMatchesLockedProductDefaults()
    {
        var preset = DialogueProcessingPreset.Balanced;

        Assert.AreEqual("Cân bằng", preset.Name);
        Assert.AreEqual(0.50, preset.VadThreshold, 0.000001);
        Assert.AreEqual(120, preset.MinimumSpeechMilliseconds);
        Assert.AreEqual(350, preset.PhraseBreakMilliseconds);
        Assert.AreEqual(200, preset.PaddingBeforeMilliseconds);
        Assert.AreEqual(300, preset.PaddingAfterMilliseconds);
        Assert.AreEqual(10.0, preset.DirectVoiceAboveNoiseDb, 0.000001);
        Assert.AreEqual(12.0, preset.BleedOtherMicAdvantageDb, 0.000001);
        Assert.AreEqual(0.80, preset.BleedCorrelationThreshold, 0.000001);
        Assert.AreEqual(12, preset.BleedMaximumLagMilliseconds);
        Assert.AreEqual(-6.0, preset.TargetSamplePeakDbfs, 0.000001);
        Assert.AreEqual("mono-center-equal-power-to-stereo", preset.PremiereRoutingProfile);
        Assert.AreEqual(3.010299956639812, preset.PremiereCenterPanCompensationDb, 0.000001);
        Assert.AreEqual(
            "max-direct-speech-and-frame-aligned-enabled-phrase-peak",
            preset.GainReferencePeakPolicy);
        Assert.AreEqual(18.0, preset.MaximumBoostDb, 0.000001);
        Assert.AreEqual(4, preset.MaximumWorkers);
        Assert.HasCount(0, preset.Validate());
    }

    [TestMethod]
    public void InvalidSafetyLimitsAreReported()
    {
        var preset = DialogueProcessingPreset.Balanced with
        {
            VadThreshold = 1.2,
            PremiereCenterPanCompensationDb = 7,
            MaximumBoostDb = 19,
            MaximumWorkers = 5
        };

        var issues = preset.Validate();

        CollectionAssert.IsSubsetOf(
            new[]
            {
                "vad-threshold-out-of-range",
                "routing-compensation-invalid",
                "boost-out-of-range",
                "worker-count-out-of-range"
            },
            issues.Select(issue => issue.Code).ToArray());
    }
}
