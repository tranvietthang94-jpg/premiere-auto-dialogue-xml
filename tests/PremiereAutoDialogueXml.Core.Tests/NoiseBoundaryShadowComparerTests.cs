using PremiereAutoDialogueXml.Audio.Analysis;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class NoiseBoundaryShadowComparerTests
{
    [TestMethod]
    public void CompareReportsBaselineEnabledCandidateDisabledInterval()
    {
        var baseline = Analysis(
            NoiseBoundaryAnalysisMode.Phase09Baseline,
            AudioSegmentStatus.Ambiguous,
            "ambiguous-energy-vad-conflict");
        var candidate = Analysis(
            NoiseBoundaryAnalysisMode.NoiseBoundaryCandidate,
            AudioSegmentStatus.Noise,
            "candidate-noise");

        var comparison = NoiseBoundaryShadowComparer.Compare(baseline, candidate);

        Assert.AreEqual(1, comparison.SegmentDifferenceCount);
        Assert.AreEqual(1, comparison.BaselineEnabledCandidateDisabledCount);
        Assert.AreEqual(0, comparison.BaselineDisabledCandidateEnabledCount);
        Assert.HasCount(1, comparison.DecisionDifferences);
        Assert.IsTrue(comparison.DecisionDifferences[0].BaselineEnabled);
        Assert.IsFalse(comparison.DecisionDifferences[0].CandidateEnabled);
    }

    [TestMethod]
    public void CompareRejectsTraceWithWrongModes()
    {
        var baseline = Analysis(
            NoiseBoundaryAnalysisMode.NoiseBoundaryCandidate,
            AudioSegmentStatus.Ambiguous,
            "baseline");
        var candidate = Analysis(
            NoiseBoundaryAnalysisMode.NoiseBoundaryCandidate,
            AudioSegmentStatus.Ambiguous,
            "candidate");

        Assert.ThrowsExactly<InvalidDataException>(() =>
            NoiseBoundaryShadowComparer.Compare(baseline, candidate));
    }

    [TestMethod]
    public void CompareRejectsCandidateTraceWithBaselinePolicy()
    {
        var baseline = Analysis(
            NoiseBoundaryAnalysisMode.Phase09Baseline,
            AudioSegmentStatus.Ambiguous,
            "baseline");
        var candidate = Analysis(
            NoiseBoundaryAnalysisMode.NoiseBoundaryCandidate,
            AudioSegmentStatus.Ambiguous,
            "candidate");
        candidate = candidate with
        {
            NoiseBoundaryTrace = candidate.NoiseBoundaryTrace! with
            {
                Policy = NoiseFloorPolicyCatalog.Phase09Baseline
            }
        };

        Assert.ThrowsExactly<InvalidDataException>(() =>
            NoiseBoundaryShadowComparer.Compare(baseline, candidate));
    }

    [TestMethod]
    public void CompareRejectsCandidateTraceWithBaselineBoundaryPolicy()
    {
        var baseline = Analysis(
            NoiseBoundaryAnalysisMode.Phase09Baseline,
            AudioSegmentStatus.Ambiguous,
            "baseline");
        var candidate = Analysis(
            NoiseBoundaryAnalysisMode.NoiseBoundaryCandidate,
            AudioSegmentStatus.Ambiguous,
            "candidate");
        candidate = candidate with
        {
            NoiseBoundaryTrace = candidate.NoiseBoundaryTrace! with
            {
                BoundaryPolicy = baseline.NoiseBoundaryTrace!.BoundaryPolicy
            }
        };

        Assert.ThrowsExactly<InvalidDataException>(() =>
            NoiseBoundaryShadowComparer.Compare(baseline, candidate));
    }

    [TestMethod]
    public void CompareRecordsFloorEligibilityAndPhraseSplitDetails()
    {
        var baseline = Analysis(
            NoiseBoundaryAnalysisMode.Phase09Baseline,
            AudioSegmentStatus.Ambiguous,
            "same") with
        {
            Phrases = [Phrase("baseline", 0, 4_800)]
        };
        var candidate = Analysis(
            NoiseBoundaryAnalysisMode.NoiseBoundaryCandidate,
            AudioSegmentStatus.Ambiguous,
            "same");
        candidate = candidate with
        {
            Phrases = [Phrase("candidate-1", 0, 2_400), Phrase("candidate-2", 2_400, 4_800)],
            NoiseBoundaryTrace = candidate.NoiseBoundaryTrace! with
            {
                Frames =
                [
                    candidate.NoiseBoundaryTrace.Frames[0] with
                    {
                        NoiseFloorAfterDbfs = -75f,
                        NoiseFloorTrainingDecision =
                            NoiseFloorTrainingDecision.NotEligibleHighEnergyConflict
                    }
                ]
            }
        };

        var comparison = NoiseBoundaryShadowComparer.Compare(baseline, candidate);

        Assert.AreEqual(15f, comparison.MaximumNoiseFloorDeltaDb, 0.0001f);
        Assert.AreEqual(1, comparison.TrainingEligibilityDifferenceCount);
        Assert.AreEqual(1, comparison.PhraseDifferenceCount);
        Assert.HasCount(1, comparison.PhraseDifferences);
        Assert.AreEqual("split", comparison.PhraseDifferences[0].ChangeKind);
        Assert.HasCount(1, comparison.PhraseDifferences[0].BaselinePhrases);
        Assert.HasCount(2, comparison.PhraseDifferences[0].CandidatePhrases);
    }

    [TestMethod]
    public void CompareBoundsFrameSamplesAndKeepsDeterministicFullTraceDigest()
    {
        const int frameCount = 200;
        var baseline = Analysis(
            NoiseBoundaryAnalysisMode.Phase09Baseline,
            AudioSegmentStatus.Noise,
            "same");
        var candidate = Analysis(
            NoiseBoundaryAnalysisMode.NoiseBoundaryCandidate,
            AudioSegmentStatus.Noise,
            "same");
        var baselineTemplate = baseline.NoiseBoundaryTrace!.Frames[0];
        var candidateTemplate = candidate.NoiseBoundaryTrace!.Frames[0];
        baseline = baseline with
        {
            NoiseBoundaryTrace = baseline.NoiseBoundaryTrace with
            {
                Frames = Enumerable.Range(0, frameCount).Select(index => baselineTemplate with
                {
                    TimelineStartSample = index * 1_536L,
                    TimelineEndSample = (index + 1) * 1_536L
                }).ToArray()
            }
        };
        candidate = candidate with
        {
            NoiseBoundaryTrace = candidate.NoiseBoundaryTrace with
            {
                Frames = Enumerable.Range(0, frameCount).Select(index => candidateTemplate with
                {
                    TimelineStartSample = index * 1_536L,
                    TimelineEndSample = (index + 1) * 1_536L,
                    NoiseFloorAfterDbfs = -80f + (index % 10)
                }).ToArray()
            }
        };

        var first = NoiseBoundaryShadowComparer.Compare(baseline, candidate);
        var second = NoiseBoundaryShadowComparer.Compare(baseline, candidate);

        Assert.AreEqual(frameCount, first.ChangedFrameCount);
        Assert.IsGreaterThan(0, first.CapturedFrameDifferenceCount);
        Assert.IsLessThanOrEqualTo(64, first.CapturedFrameDifferenceCount);
        Assert.IsLessThan(first.ChangedFrameCount, first.CapturedFrameDifferenceCount);
        Assert.AreEqual(frameCount, first.FrameDifferenceSummary.NoiseFloorAfterDifferenceCount);
        Assert.AreEqual(64, first.FrameTraceSha256.Length);
        Assert.AreEqual(first.FrameTraceSha256, second.FrameTraceSha256);
        CollectionAssert.AreEqual(
            first.FrameDifferenceSamples.ToArray(),
            second.FrameDifferenceSamples.ToArray());
    }

    private static DialoguePhrase Phrase(string id, long start, long end) =>
        new(id, 1, start, end, start, end, -12, 9.01f, 9.01f, false);

    private static TrackAudioAnalysis Analysis(
        NoiseBoundaryAnalysisMode mode,
        AudioSegmentStatus status,
        string reason) => new(
            TrackIndex: 1,
            AnalyzedFrameCount: 1,
            Phrases: [],
            Segments:
            [
                new(
                    TrackIndex: 1,
                    SourceClipId: "clip-1",
                    TimelineStartSample: 0,
                    TimelineEndSample: 1_536,
                    SourceStartSample: 0,
                    SourceEndSample: 1_536,
                    Status: status,
                    PhraseId: null,
                    GainDb: null,
                    Reason: reason)
            ],
            LearnedDirectVoiceRmsDbfs: -60f)
        {
            NoiseBoundaryTrace = new(
                TrackIndex: 1,
                Mode: mode,
                Policy: NoiseFloorPolicyCatalog.For(mode),
                BoundaryPolicy: VadBoundaryPolicyCatalog.For(
                    mode,
                    PremiereAutoDialogueXml.Core.Domain.DialogueProcessingPreset.Balanced),
                Frames:
                [
                    new(
                        TimelineStartSample: 0,
                        TimelineEndSample: 1_536,
                        VadProbability: 0.01f,
                        RmsDbfs: -60f,
                        NoiseFloorBeforeDbfs: -90f,
                        NoiseFloorAfterDbfs: -60f,
                        NoiseFloorReadyBefore: false,
                        NoiseFloorReadyAfter: true,
                        NoiseFloorTrainingDecision:
                            NoiseFloorTrainingDecision.EligibleVadNegative,
                        IsVadSpeech: false,
                        IsDirectEvidence: false,
                        IsAboveDirectEnergyThreshold: false,
                        IsWarmupUncertain: false,
                        BoundaryState: NoiseBoundaryFrameState.VadNegative)
                ])
        };
}
