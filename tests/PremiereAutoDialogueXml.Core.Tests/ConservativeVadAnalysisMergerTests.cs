using PremiereAutoDialogueXml.Audio.Analysis;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class ConservativeVadAnalysisMergerTests
{
    [TestMethod]
    public void MergeKeepsLegacyEnabledRangeWhenCandidateWouldDisableIt()
    {
        var legacyPhrase = Phrase("T01-P000001", 0, 4_800, -12, 9.01f);
        var legacy = Analysis(
            [legacyPhrase],
            [Segment(0, 4_800, AudioSegmentStatus.Speech, legacyPhrase.Id, legacyPhrase.AppliedGainDb, "confirmed-direct-speech")]);
        var candidate = Analysis(
            [],
            [Segment(0, 4_800, AudioSegmentStatus.Noise, null, null, "vad-negative-noise")]);

        var (merged, comparison) = ConservativeVadAnalysisMerger.Merge(
            legacy,
            candidate,
            observationCount: 4,
            changedObservationCount: 4,
            maximumProbabilityDelta: 0.8f);

        var segment = merged.Segments.Single();
        Assert.AreEqual(AudioSegmentStatus.Ambiguous, segment.Status);
        Assert.AreEqual("ambiguous-vad-front-end-disagreement", segment.Reason);
        Assert.IsNotNull(segment.PhraseId);
        Assert.AreEqual(legacyPhrase.AppliedGainDb, segment.GainDb);
        Assert.HasCount(1, merged.Phrases);
        Assert.AreEqual(1, comparison.LegacyEnabledCandidateDisabledCount);
        Assert.AreEqual(0, comparison.LegacyDisabledCandidateEnabledCount);
    }

    [TestMethod]
    public void MergeAcceptsCandidateEnabledRangeWhenLegacyDisabledIt()
    {
        var candidatePhrase = Phrase("T01-P000001", 0, 4_800, -12, 9.01f);
        var legacy = Analysis(
            [],
            [Segment(0, 4_800, AudioSegmentStatus.Noise, null, null, "vad-negative-noise")]);
        var candidate = Analysis(
            [candidatePhrase],
            [Segment(0, 4_800, AudioSegmentStatus.Speech, candidatePhrase.Id, candidatePhrase.AppliedGainDb, "confirmed-direct-speech")]);

        var (merged, comparison) = ConservativeVadAnalysisMerger.Merge(
            legacy,
            candidate,
            observationCount: 4,
            changedObservationCount: 4,
            maximumProbabilityDelta: 0.8f);

        Assert.AreEqual(AudioSegmentStatus.Speech, merged.Segments.Single().Status);
        Assert.AreEqual(candidatePhrase.Id, merged.Segments.Single().PhraseId);
        Assert.AreEqual(0, comparison.LegacyEnabledCandidateDisabledCount);
        Assert.AreEqual(1, comparison.LegacyDisabledCandidateEnabledCount);
    }

    [TestMethod]
    public void MergeKeepsWholeLegacyPhraseWhenOnlyItsTailNeedsSafetyFallback()
    {
        var legacyPhrase = Phrase("T01-P000001", 0, 4_800, -12, 9.01f);
        var candidatePhrase = Phrase("T01-P000001", 0, 2_400, -9, 6.01f);
        var legacy = Analysis(
            [legacyPhrase],
            [Segment(0, 4_800, AudioSegmentStatus.Speech, legacyPhrase.Id, legacyPhrase.AppliedGainDb, "confirmed-direct-speech")]);
        var candidate = Analysis(
            [candidatePhrase],
            [
                Segment(0, 2_400, AudioSegmentStatus.Speech, candidatePhrase.Id, candidatePhrase.AppliedGainDb, "confirmed-direct-speech"),
                Segment(2_400, 4_800, AudioSegmentStatus.Noise, null, null, "vad-negative-noise")
            ]);

        var (merged, comparison) = ConservativeVadAnalysisMerger.Merge(
            legacy,
            candidate,
            observationCount: 4,
            changedObservationCount: 4,
            maximumProbabilityDelta: 0.8f);

        var phrase = merged.Phrases.Single();
        Assert.AreEqual("T01-P000001-legacy-safety", phrase.Id);
        Assert.AreEqual(legacyPhrase.MeasuredPeakDbfs, phrase.MeasuredPeakDbfs);
        Assert.IsTrue(merged.Segments.All(segment => segment.PhraseId == phrase.Id));
        Assert.IsTrue(merged.Segments.All(segment => segment.GainDb == legacyPhrase.AppliedGainDb));
        Assert.AreEqual(AudioSegmentStatus.Speech, merged.Segments[0].Status);
        Assert.AreEqual(AudioSegmentStatus.Ambiguous, merged.Segments[1].Status);
        Assert.AreEqual(1, comparison.LegacyEnabledCandidateDisabledCount);
    }

    [TestMethod]
    public void MergeKeepsCandidateOnlyAudioAsUnityAmbiguousInsideProtectedComponent()
    {
        var legacyPhrase = Phrase("T01-P000001", 0, 2_400, -12, 9.01f);
        var candidatePhrase = Phrase("T01-P000001", 1_200, 4_800, -9, 6.01f);
        var legacy = Analysis(
            [legacyPhrase],
            [
                Segment(0, 2_400, AudioSegmentStatus.Speech, legacyPhrase.Id, legacyPhrase.AppliedGainDb, "confirmed-direct-speech"),
                Segment(2_400, 4_800, AudioSegmentStatus.Noise, null, null, "vad-negative-noise")
            ]);
        var candidate = Analysis(
            [candidatePhrase],
            [
                Segment(0, 1_200, AudioSegmentStatus.Noise, null, null, "vad-negative-noise"),
                Segment(1_200, 4_800, AudioSegmentStatus.Speech, candidatePhrase.Id, candidatePhrase.AppliedGainDb, "confirmed-direct-speech")
            ]);

        var (merged, comparison) = ConservativeVadAnalysisMerger.Merge(
            legacy,
            candidate,
            observationCount: 4,
            changedObservationCount: 4,
            maximumProbabilityDelta: 0.8f);

        var candidateOnly = merged.Segments.Single(segment => segment.TimelineStartSample == 2_400);
        Assert.AreEqual(AudioSegmentStatus.Ambiguous, candidateOnly.Status);
        Assert.AreEqual("ambiguous-vad-front-end-candidate-only-in-legacy-fallback", candidateOnly.Reason);
        Assert.IsNull(candidateOnly.PhraseId);
        Assert.AreEqual(0f, candidateOnly.GainDb);
        Assert.AreEqual(1, comparison.LegacyEnabledCandidateDisabledCount);
        Assert.AreEqual(1, comparison.LegacyDisabledCandidateEnabledCount);
        Assert.HasCount(1, merged.Phrases);
        Assert.AreEqual("T01-P000001-legacy-safety", merged.Phrases[0].Id);
    }

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
