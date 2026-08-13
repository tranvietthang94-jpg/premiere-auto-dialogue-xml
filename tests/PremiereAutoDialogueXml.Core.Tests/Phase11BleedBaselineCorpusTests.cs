using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Audio.Pcm;
using PremiereAutoDialogueXml.Core.Domain;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class Phase11BleedBaselineCorpusTests
{
    [TestMethod]
    public void Phase10ResolverMatchesLockedSyntheticCorpus()
    {
        var scenarioNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var scenario in Phase11BleedSyntheticCorpus.CreateAll())
        {
            using (scenario)
            {
                Assert.IsTrue(scenarioNames.Add(scenario.Name), $"Duplicate scenario: {scenario.Name}");
                var segment = ResolveTarget(scenario);
                var expected = scenario.Expected;

                Assert.AreEqual(expected.Status, segment.Status, scenario.Name);
                Assert.AreEqual(expected.Reason, segment.Reason, scenario.Name);
                Assert.AreEqual(expected.HasEvidence, segment.BleedEvidence is not null, scenario.Name);

                if (expected.HasEvidence)
                {
                    var evidence = segment.BleedEvidence!;
                    Assert.AreEqual(expected.OtherTrackIndex, evidence.OtherTrackIndex, scenario.Name);
                    Assert.AreEqual(
                        expected.AbsoluteLagMilliseconds,
                        Math.Abs(evidence.LagMilliseconds),
                        scenario.Name);
                    Assert.IsGreaterThanOrEqualTo(0.80f, evidence.WaveformCorrelation, scenario.Name);
                    Assert.IsGreaterThanOrEqualTo(12f, evidence.OtherMicAdvantageDb, scenario.Name);

                    if (expected.Status == AudioSegmentStatus.Bleed)
                    {
                        Assert.IsLessThanOrEqualTo(
                            BleedResolver.ConflictingResidualThresholdDb,
                            evidence.ResidualToTargetDb,
                            scenario.Name);
                    }
                    else
                    {
                        Assert.IsGreaterThan(
                            BleedResolver.ConflictingResidualThresholdDb,
                            evidence.ResidualToTargetDb,
                            scenario.Name);
                    }
                }
            }
        }

        Assert.HasCount(11, scenarioNames);
    }

    [TestMethod]
    public void Phase10ResolverUsesOnlyCenteredSecondWhenOuterFingerprintDrifts()
    {
        using var scenario = Phase11BleedSyntheticCorpus.CenterWindowWithDriftingOuterFingerprint();

        var segment = ResolveTarget(scenario);

        Assert.AreEqual(AudioSegmentStatus.Bleed, segment.Status);
        Assert.AreEqual("confirmed-bleed-from-track-2", segment.Reason);
        Assert.IsNotNull(segment.BleedEvidence);
        Assert.AreEqual(4, Math.Abs(segment.BleedEvidence.LagMilliseconds));
        Assert.IsGreaterThan(48_000, segment.TimelineEndSample - segment.TimelineStartSample);
    }

    private static AnalyzedAudioSegment ResolveTarget(Phase11BleedSyntheticScenario scenario)
    {
        var resolved = new BleedResolver(new TimelinePcmAccessor(new PcmWaveSampleReader()))
            .Resolve(scenario.Sequence, scenario.Analyses, DialogueProcessingPreset.Balanced);
        return resolved
            .Single(analysis => analysis.TrackIndex == scenario.TargetTrackIndex)
            .Segments
            .Single();
    }
}
