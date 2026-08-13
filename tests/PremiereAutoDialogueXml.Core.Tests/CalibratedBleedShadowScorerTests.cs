using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Audio.Pcm;
using PremiereAutoDialogueXml.Core.Domain;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class CalibratedBleedShadowScorerTests
{
    [TestMethod]
    public void BuildFindsLikelyBleedAcrossTwoIndependentWindowsWithoutChangingProduction()
    {
        using var corpus = CalibrationCorpus.Create(
        [
            new(4, 0.08f),
            new(4, 0.08f),
            new(4, 0.08f),
            new(4, 0.08f, HasMatchingBaselineBleed: false)
        ]);

        var shadow = Build(corpus);
        var candidate = TargetCandidate(shadow);

        Assert.AreEqual(
            CalibratedBleedShadowOutcome.CalibratedLikelyBleed,
            candidate.Outcome);
        Assert.AreEqual(2, candidate.AvailableWindowCount);
        Assert.AreEqual(2, candidate.EvaluatedWindowCount);
        Assert.AreEqual(2, candidate.PassingWindowCount);
        Assert.AreEqual(0, candidate.ConflictingWindowCount);
        Assert.AreEqual(2, candidate.SourceTrackIndex);
        Assert.AreEqual(AudioSegmentStatus.Ambiguous, candidate.FinalStatus);
        Assert.IsTrue(candidate.FinalEnabled);
        Assert.AreEqual(candidate.BaselineReason, candidate.FinalReason);
        Assert.AreEqual(0, shadow.ProductionChangedSegmentCount);
    }

    [TestMethod]
    public void BuildKeepsDirectResidualConflictEnabledForReview()
    {
        using var corpus = CalibrationCorpus.Create(
        [
            new(4, 0.08f),
            new(4, 0.08f),
            new(4, 0.08f),
            new(
                4,
                0.08f,
                HasDirectConflict: true,
                HasMatchingBaselineBleed: false)
        ]);

        var candidate = TargetCandidate(Build(corpus));

        Assert.AreEqual(CalibratedBleedShadowOutcome.ConflictingEvidence, candidate.Outcome);
        Assert.IsGreaterThan(0, candidate.ConflictingWindowCount);
        Assert.AreEqual(AudioSegmentStatus.Ambiguous, candidate.FinalStatus);
        Assert.IsTrue(candidate.FinalEnabled);
    }

    [TestMethod]
    public void BuildRejectsCandidateWhenOnlyOneOfTwoWindowsMatchesCalibration()
    {
        using var corpus = CalibrationCorpus.Create(
        [
            new(4, 0.08f),
            new(4, 0.08f),
            new(4, 0.08f),
            new(
                4,
                0.08f,
                HasMatchingBaselineBleed: false,
                SecondHalfDelayMilliseconds: 10)
        ]);

        var candidate = TargetCandidate(Build(corpus));

        Assert.AreEqual(
            CalibratedBleedShadowOutcome.BelowCalibratedThreshold,
            candidate.Outcome);
        Assert.AreEqual(2, candidate.EvaluatedWindowCount);
        Assert.AreEqual(1, candidate.PassingWindowCount);
        Assert.IsTrue(candidate.FinalEnabled);
    }

    [TestMethod]
    public void BuildReportsNoCalibrationWhenPairHasFewerThanThreeAnchors()
    {
        using var corpus = CalibrationCorpus.Create(
        [
            new(4, 0.08f),
            new(4, 0.08f),
            new(4, 0.08f, HasMatchingBaselineBleed: false)
        ]);

        var candidate = TargetCandidate(Build(corpus));

        Assert.AreEqual(CalibratedBleedShadowOutcome.NoCalibration, candidate.Outcome);
        Assert.IsNull(candidate.SourceTrackIndex);
        Assert.HasCount(0, candidate.WindowSamples);
        Assert.IsTrue(candidate.FinalEnabled);
    }

    private static CalibratedBleedProjectShadow Build(CalibrationCorpus corpus)
    {
        var accessor = new TimelinePcmAccessor(new PcmWaveSampleReader());
        var calibration = new DirectionalBleedCalibrator(accessor).Build(
            corpus.Sequence,
            corpus.Analyses,
            DialogueProcessingPreset.Balanced);
        return new CalibratedBleedShadowScorer(accessor).Build(
            corpus.Sequence,
            corpus.Analyses,
            DialogueProcessingPreset.Balanced,
            calibration);
    }

    private static CalibratedBleedCandidateEvidence TargetCandidate(
        CalibratedBleedProjectShadow shadow) =>
        shadow.Candidates.Single(candidate =>
            candidate.TargetTrackIndex == 1 &&
            candidate.BaselineStatus == AudioSegmentStatus.Ambiguous);
}
