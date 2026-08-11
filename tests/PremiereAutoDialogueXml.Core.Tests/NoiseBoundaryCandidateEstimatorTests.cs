using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Core.Domain;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class NoiseBoundaryCandidateEstimatorTests
{
    private const int ObservationSamples = 1_536;

    [TestMethod]
    public void CandidateWarmupUsesEightFramesAndRejectsLoudFirstOutlier()
    {
        var observations = Levels(-12f, -60f, -60f, -60f, -60f, -60f, -60f, -60f, -60f);

        var result = BuildCandidate(observations);

        Assert.AreEqual(8, result.Policy.WarmupFrameCount);
        var first = result.NoiseFloorTrace[0];
        Assert.AreEqual(-90f, first.NoiseFloorBeforeDbfs, 0.001f);
        Assert.AreEqual(-90f, first.NoiseFloorAfterDbfs, 0.001f);
        Assert.AreEqual(NoiseFloorTrainingDecision.WarmupCandidate, first.TrainingDecision);
        Assert.IsTrue(result.Evidence[0].IsWarmupUncertain);

        var warmupComplete = result.NoiseFloorTrace[7];
        Assert.IsFalse(warmupComplete.NoiseFloorReadyBefore);
        Assert.IsTrue(warmupComplete.NoiseFloorReadyAfter);
        Assert.AreEqual(-60f, warmupComplete.NoiseFloorAfterDbfs, 0.001f);

        var firstTrusted = result.NoiseFloorTrace[8];
        Assert.IsTrue(firstTrusted.NoiseFloorReadyBefore);
        Assert.AreEqual(NoiseFloorTrainingDecision.EligibleBackground, firstTrusted.TrainingDecision);
        Assert.AreEqual(-60f, firstTrusted.NoiseFloorBeforeDbfs, 0.001f);
        Assert.AreEqual(-60f, firstTrusted.NoiseFloorAfterDbfs, 0.001f);
    }

    [TestMethod]
    public void CandidateDoesNotTrainFromHighEnergyConflictBeforeStableStepEvidence()
    {
        var observations = Levels(
            Enumerable.Repeat(-60f, 8)
                .Concat(Enumerable.Repeat(-45f, 4))
                .ToArray());

        var result = BuildCandidate(observations);
        var conflictTrace = result.NoiseFloorTrace.Skip(8).ToArray();
        var conflictEvidence = result.Evidence.Skip(8).ToArray();

        Assert.HasCount(4, conflictTrace);
        Assert.IsTrue(conflictTrace.All(frame =>
            frame.TrainingDecision == NoiseFloorTrainingDecision.NotEligibleHighEnergyConflict));
        Assert.IsTrue(conflictTrace.All(frame =>
            MathF.Abs(frame.NoiseFloorBeforeDbfs - -60f) < 0.001f &&
            MathF.Abs(frame.NoiseFloorAfterDbfs - -60f) < 0.001f));
        Assert.IsTrue(conflictEvidence.All(frame => frame.IsAboveDirectEnergyThreshold));
    }

    [TestMethod]
    public void CandidateStepResponseIsMonotonicBoundedAndUsesAsymmetricSmoothing()
    {
        var levels = Enumerable.Repeat(-65f, 8)
            .Concat(Enumerable.Repeat(-48f, 512))
            .Concat(Enumerable.Repeat(-68f, 128))
            .ToArray();

        var result = BuildCandidate(Levels(levels));
        var rise = result.NoiseFloorTrace.Skip(8).Take(512).ToArray();
        var fall = result.NoiseFloorTrace.Skip(520).Take(128).ToArray();

        Assert.AreEqual(0.05f, result.Policy.RiseSmoothing, 0.0001f);
        Assert.AreEqual(0.20f, result.Policy.FallSmoothing, 0.0001f);
        Assert.AreEqual(16, result.Policy.StableStepFrameCount);
        Assert.AreEqual(3f, result.Policy.StableStepMaximumSpreadDb, 0.0001f);
        Assert.IsTrue(rise.Take(15).All(frame =>
            frame.TrainingDecision == NoiseFloorTrainingDecision.NotEligibleHighEnergyConflict));
        Assert.AreEqual(
            NoiseFloorTrainingDecision.EligibleStableFloorStep,
            rise[15].TrainingDecision);
        var promotedEvidence = result.Evidence[8 + 15];
        Assert.IsTrue(promotedEvidence.IsStableFloorStep);
        Assert.AreEqual(
            rise[15].NoiseFloorBeforeDbfs,
            promotedEvidence.AdaptiveNoiseFloorDbfs,
            0.0001f);
        Assert.IsGreaterThan(
            rise[15].NoiseFloorBeforeDbfs,
            rise[15].NoiseFloorAfterDbfs);
        AssertMonotonic(rise.Select(frame => frame.NoiseFloorAfterDbfs), increasing: true);
        AssertMonotonic(fall.Select(frame => frame.NoiseFloorAfterDbfs), increasing: false);
        Assert.IsGreaterThan(-48.01f, rise[^1].NoiseFloorAfterDbfs);
        Assert.IsLessThan(-67.5f, fall[^1].NoiseFloorAfterDbfs);
        AssertFiniteAndBounded(result.NoiseFloorTrace);
    }

    [TestMethod]
    public void CandidateIgnoresNonFiniteLevelsAndKeepsFiniteFloor()
    {
        var observations = Levels(
            Enumerable.Repeat(-60f, 8)
                .Concat([float.NaN, float.PositiveInfinity, float.NegativeInfinity])
                .ToArray());

        var result = BuildCandidate(observations);
        var invalid = result.NoiseFloorTrace.Skip(8).ToArray();

        Assert.IsTrue(invalid.All(frame =>
            frame.TrainingDecision == NoiseFloorTrainingDecision.NotEligibleInvalidLevel));
        Assert.IsTrue(invalid.All(frame =>
            frame.NoiseFloorBeforeDbfs == -60f && frame.NoiseFloorAfterDbfs == -60f));
        Assert.IsTrue(result.Evidence.Skip(8).All(frame =>
            !frame.IsAboveDirectEnergyThreshold && !frame.IsDirectEvidence));
        AssertFiniteAndBounded(result.NoiseFloorTrace);
    }

    [TestMethod]
    public void CandidateTraceIsDeterministicForSameObservations()
    {
        var observations = Levels(
            Enumerable.Repeat(-62f, 8)
                .Concat(Enumerable.Repeat(-47f, 24))
                .Concat(Enumerable.Repeat(-70f, 24))
                .ToArray());

        var first = BuildCandidate(observations);
        var second = BuildCandidate(observations);

        CollectionAssert.AreEqual(first.Evidence.ToArray(), second.Evidence.ToArray());
        CollectionAssert.AreEqual(first.NoiseFloorTrace.ToArray(), second.NoiseFloorTrace.ToArray());
        Assert.AreEqual(first.Policy, second.Policy);
    }

    [TestMethod]
    public void CandidateClampsFloorToSupportedDbfsRange()
    {
        var observations = Levels(
            Enumerable.Repeat(-200f, 8)
                .Concat(Enumerable.Repeat(20f, 32))
                .ToArray());

        var result = BuildCandidate(observations);

        Assert.AreEqual(AudioMath.SilenceDbfs, result.NoiseFloorTrace[7].NoiseFloorAfterDbfs);
        AssertFiniteAndBounded(result.NoiseFloorTrace);
    }

    private static AudioFrameEvidenceBuildResult BuildCandidate(
        IReadOnlyList<AudioFrameObservation> observations) =>
        new AudioFrameEvidenceBuilder().BuildWithTrace(
            observations,
            DialogueProcessingPreset.Balanced,
            NoiseBoundaryAnalysisMode.NoiseBoundaryCandidate);

    private static IReadOnlyList<AudioFrameObservation> Levels(params float[] levels) =>
        levels.Select((level, index) => new AudioFrameObservation(
            TimelineStartSample: index * ObservationSamples,
            TimelineEndSample: (index + 1) * ObservationSamples,
            VadProbability: 0.01f,
            RmsDbfs: level,
            SamplePeakDbfs: level,
            ContainsMedia: true))
        .ToArray();

    private static void AssertMonotonic(IEnumerable<float> values, bool increasing)
    {
        var ordered = values.ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (increasing)
            {
                Assert.IsGreaterThanOrEqualTo(ordered[index - 1], ordered[index]);
            }
            else
            {
                Assert.IsLessThanOrEqualTo(ordered[index - 1], ordered[index]);
            }
        }
    }

    private static void AssertFiniteAndBounded(IEnumerable<NoiseFloorFrameSnapshot> trace) =>
        Assert.IsTrue(trace.All(frame =>
            float.IsFinite(frame.NoiseFloorBeforeDbfs) &&
            float.IsFinite(frame.NoiseFloorAfterDbfs) &&
            frame.NoiseFloorBeforeDbfs is >= AudioMath.SilenceDbfs and <= 0 &&
            frame.NoiseFloorAfterDbfs is >= AudioMath.SilenceDbfs and <= 0));
}
