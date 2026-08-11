using PremiereAutoDialogueXml.Audio.Analysis;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class ConservativeNoiseBoundaryAnalysisMergerTests
{
    [TestMethod]
    public void MergeKeepsWholeBaselinePhraseAndAuditsFinalSafetyDecision()
    {
        var baselinePhrase = Phrase("T01-P000001", 0, 4_800, -12, 9.01f);
        var candidatePhrase = Phrase("T01-P000001", 0, 2_400, -9, 6.01f);
        var baseline = Analysis(
            [baselinePhrase],
            [Segment(0, 4_800, AudioSegmentStatus.Speech, baselinePhrase.Id, baselinePhrase.AppliedGainDb, "confirmed-direct-speech")]);
        var candidate = Analysis(
            [candidatePhrase],
            [
                Segment(0, 2_400, AudioSegmentStatus.Speech, candidatePhrase.Id, candidatePhrase.AppliedGainDb, "confirmed-direct-speech"),
                Segment(2_400, 4_800, AudioSegmentStatus.Noise, null, null, "vad-negative-noise")
            ]);
        var rawDifference = new NoiseBoundaryDecisionDifference(
            2_400,
            4_800,
            "clip",
            AudioSegmentStatus.Speech,
            "confirmed-direct-speech",
            true,
            "clip",
            AudioSegmentStatus.Noise,
            "vad-negative-noise",
            false)
        {
            BaselinePhraseId = baselinePhrase.Id,
            BaselineGainDb = baselinePhrase.AppliedGainDb
        };
        var comparison = Comparison(rawDifference);

        var (merged, finalComparison) = ConservativeNoiseBoundaryAnalysisMerger.Merge(
            baseline,
            candidate,
            comparison);

        var phrase = merged.Phrases.Single();
        Assert.AreEqual("T01-P000001-legacy-safety", phrase.Id);
        Assert.AreEqual(baselinePhrase.AppliedGainDb, phrase.AppliedGainDb);
        Assert.IsTrue(merged.Segments.All(segment => segment.PhraseId == phrase.Id));
        Assert.IsTrue(merged.Segments.All(segment => segment.GainDb == baselinePhrase.AppliedGainDb));
        var safety = finalComparison.FinalDifferences.Single(difference =>
            difference.TimelineStartSample == 2_400);
        Assert.IsTrue(safety.BaselineEnabled);
        Assert.IsFalse(safety.CandidateEnabled);
        Assert.IsTrue(safety.FinalEnabled);
        Assert.AreEqual(AudioSegmentStatus.Ambiguous, safety.FinalStatus);
        Assert.AreEqual("ambiguous-noise-boundary-disagreement", safety.FinalReason);
        Assert.AreEqual(0, finalComparison.BaselineEnabledFinalDisabledCount);
    }

    private static NoiseBoundaryTrackComparison Comparison(
        NoiseBoundaryDecisionDifference difference) => new(
            1,
            NoiseBoundaryAnalysisMode.Phase09Baseline,
            NoiseBoundaryAnalysisMode.NoiseBoundaryCandidate,
            "phase09-adaptive-p20-v1",
            "phase10-background-eligible-p20-v1",
            "phase09-vad-single-threshold-v1",
            "phase10-vad-start050-continue040-v1",
            4,
            0,
            1,
            1,
            1,
            0,
            [],
            [difference]);

    private static TrackAudioAnalysis Analysis(
        IReadOnlyList<DialoguePhrase> phrases,
        IReadOnlyList<AnalyzedAudioSegment> segments) =>
        new(1, 4, phrases, segments, -12);

    private static DialoguePhrase Phrase(
        string id,
        long start,
        long end,
        float peak,
        float gain) =>
        new(id, 1, start, end, start, end, peak, gain, gain, false);

    private static AnalyzedAudioSegment Segment(
        long start,
        long end,
        AudioSegmentStatus status,
        string? phraseId,
        float? gain,
        string reason) =>
        new(1, "clip", start, end, start, end, status, phraseId, gain, reason);
}
