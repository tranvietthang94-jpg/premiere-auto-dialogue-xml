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
                Frames:
                [
                    new(
                        TimelineStartSample: 0,
                        TimelineEndSample: 1_536,
                        VadProbability: 0.01f,
                        RmsDbfs: -60f,
                        NoiseFloorBeforeDbfs: -90f,
                        NoiseFloorAfterDbfs: -60f,
                        NoiseFloorTrainingDecision:
                            NoiseFloorTrainingDecision.EligibleVadNegative,
                        IsVadSpeech: false,
                        IsDirectEvidence: false,
                        IsAboveDirectEnergyThreshold: false,
                        BoundaryState: NoiseBoundaryFrameState.VadNegative)
                ])
        };
}
