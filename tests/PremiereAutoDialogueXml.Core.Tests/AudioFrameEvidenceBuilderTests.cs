using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Core.Domain;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class AudioFrameEvidenceBuilderTests
{
    [TestMethod]
    public void BuildUsesSameVadAndAdaptiveEnergyRulesAsDialogueAnalysis()
    {
        var observations = new[]
        {
            Observation(0, 0.01f, -60f),
            Observation(1_536, 0.90f, -45f),
            Observation(3_072, 0.40f, -45f)
        };

        var evidence = new AudioFrameEvidenceBuilder().Build(
            observations,
            DialogueProcessingPreset.Balanced);

        Assert.HasCount(3, evidence);
        Assert.AreEqual(-60f, evidence[0].AdaptiveNoiseFloorDbfs, 0.001f);
        Assert.IsFalse(evidence[0].IsVadSpeech);
        Assert.IsFalse(evidence[0].IsAboveDirectEnergyThreshold);
        Assert.IsTrue(evidence[1].IsVadSpeech);
        Assert.IsTrue(evidence[1].IsDirectEvidence);
        Assert.AreEqual(-60f, evidence[1].AdaptiveNoiseFloorDbfs, 0.001f);
        Assert.IsFalse(evidence[2].IsVadSpeech);
        Assert.IsTrue(evidence[2].IsAboveDirectEnergyThreshold);
        Assert.IsFalse(evidence[2].IsDirectEvidence);
    }

    private static AudioFrameObservation Observation(long start, float probability, float rms) =>
        new(start, start + 1_536, probability, rms, rms, ContainsMedia: true);
}
