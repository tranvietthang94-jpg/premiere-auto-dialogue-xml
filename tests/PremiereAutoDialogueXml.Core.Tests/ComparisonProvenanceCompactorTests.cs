using PremiereAutoDialogueXml.Audio.Analysis;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class ComparisonProvenanceCompactorTests
{
    [TestMethod]
    public void CompactBoundsVadDifferencesAndKeepsFullCountDigestAndSafetyCategories()
    {
        var differences = Enumerable.Range(0, 240)
            .Select(index => new VadDecisionDifference(
                1,
                "clip-1",
                index * 100,
                (index + 1) * 100,
                index % 5 == 0 ? AudioSegmentStatus.Speech : AudioSegmentStatus.Noise,
                "legacy",
                index % 7 == 0 ? AudioSegmentStatus.Ambiguous : AudioSegmentStatus.Noise,
                "candidate",
                AudioSegmentStatus.Ambiguous,
                "ambiguous-vad-front-end-disagreement"))
            .ToArray();
        var comparison = new VadFrontEndTrackComparison(
            1,
            1_000,
            900,
            0.75f,
            differences.Length,
            differences.Count(difference => Enabled(difference.LegacyStatus) && !Enabled(difference.CandidateStatus)),
            differences.Count(difference => !Enabled(difference.LegacyStatus) && Enabled(difference.CandidateStatus)),
            differences);

        var compacted = ComparisonProvenanceCompactor.Compact(comparison);
        var repeated = ComparisonProvenanceCompactor.Compact(comparison);

        Assert.AreEqual(differences.Length, compacted.SegmentDifferenceCount);
        Assert.IsLessThanOrEqualTo(ComparisonProvenanceCompactor.SampleLimit, compacted.CapturedDifferenceCount);
        Assert.IsLessThan(compacted.SegmentDifferenceCount, compacted.CapturedDifferenceCount);
        Assert.AreEqual(64, compacted.DifferenceTraceSha256.Length);
        Assert.AreEqual(compacted.DifferenceTraceSha256, repeated.DifferenceTraceSha256);
        Assert.IsTrue(compacted.Differences.Any(difference =>
            Enabled(difference.LegacyStatus) && !Enabled(difference.CandidateStatus)));
        Assert.IsTrue(compacted.Differences.Any(difference =>
            !Enabled(difference.LegacyStatus) && Enabled(difference.CandidateStatus)));
    }

    [TestMethod]
    public void CompactBoundsNoiseBoundaryListsAndKeepsFullCountsAndDigests()
    {
        var phrases = Enumerable.Range(0, 180)
            .Select(index => new NoiseBoundaryPhraseDifference(
                (index % 4) switch
                {
                    0 => "candidate-only",
                    1 => "baseline-only",
                    2 => "split",
                    _ => "merge"
                },
                [Phrase("baseline", index)],
                [Phrase("candidate", index)]))
            .ToArray();
        var decisions = Enumerable.Range(0, 220)
            .Select(index => new NoiseBoundaryDecisionDifference(
                index * 100,
                (index + 1) * 100,
                "clip-1",
                index % 5 == 0 ? AudioSegmentStatus.Speech : AudioSegmentStatus.Noise,
                "baseline",
                index % 5 == 0,
                "clip-1",
                index % 7 == 0 ? AudioSegmentStatus.Ambiguous : AudioSegmentStatus.Noise,
                "candidate",
                index % 7 == 0))
            .ToArray();
        var finals = decisions.Select(difference => new NoiseBoundaryFinalDecisionDifference(
                difference.TimelineStartSample,
                difference.TimelineEndSample,
                "clip-1",
                difference.BaselineStatus!.Value,
                difference.BaselineReason!,
                difference.BaselineEnabled,
                null,
                null,
                difference.CandidateStatus!.Value,
                difference.CandidateReason!,
                difference.CandidateEnabled,
                null,
                null,
                AudioSegmentStatus.Ambiguous,
                difference.BaselineEnabled && !difference.CandidateEnabled
                    ? "ambiguous-noise-boundary-disagreement"
                    : "final",
                true,
                null,
                0))
            .ToArray();
        var comparison = new NoiseBoundaryTrackComparison(
            1,
            NoiseBoundaryAnalysisMode.Phase09Baseline,
            NoiseBoundaryAnalysisMode.NoiseBoundaryCandidate,
            "phase09-adaptive-p20-v1",
            "phase10-background-eligible-p20-v1",
            "phase09-vad-single-threshold-v1",
            "phase10-vad-start050-continue040-v1",
            1_000,
            900,
            phrases.Length,
            decisions.Length,
            decisions.Count(difference => difference.BaselineEnabled && !difference.CandidateEnabled),
            decisions.Count(difference => !difference.BaselineEnabled && difference.CandidateEnabled),
            [],
            decisions)
        {
            PhraseDifferences = phrases,
            FinalDifferenceCount = finals.Length,
            FinalDifferences = finals
        };

        var compacted = ComparisonProvenanceCompactor.Compact(comparison);

        Assert.AreEqual(phrases.Length, compacted.PhraseDifferenceCount);
        Assert.AreEqual(decisions.Length, compacted.SegmentDifferenceCount);
        Assert.AreEqual(finals.Length, compacted.FinalDifferenceCount);
        Assert.IsLessThanOrEqualTo(ComparisonProvenanceCompactor.SampleLimit, compacted.CapturedPhraseDifferenceCount);
        Assert.IsLessThanOrEqualTo(ComparisonProvenanceCompactor.SampleLimit, compacted.CapturedDecisionDifferenceCount);
        Assert.IsLessThanOrEqualTo(ComparisonProvenanceCompactor.SampleLimit, compacted.CapturedFinalDifferenceCount);
        Assert.AreEqual(64, compacted.PhraseDifferenceTraceSha256.Length);
        Assert.AreEqual(64, compacted.DecisionDifferenceTraceSha256.Length);
        Assert.AreEqual(64, compacted.FinalDifferenceTraceSha256.Length);
        Assert.IsTrue(compacted.DecisionDifferences.Any(difference =>
            difference.BaselineEnabled && !difference.CandidateEnabled));
        Assert.IsTrue(compacted.DecisionDifferences.Any(difference =>
            !difference.BaselineEnabled && difference.CandidateEnabled));
    }

    private static NoiseBoundaryPhraseSnapshot Phrase(string prefix, int index) => new(
        $"{prefix}-{index}",
        index * 100,
        (index + 1) * 100,
        index * 100,
        (index + 1) * 100,
        -12,
        9.01f,
        9.01f,
        false);

    private static bool Enabled(AudioSegmentStatus status) =>
        status is AudioSegmentStatus.Speech or AudioSegmentStatus.Ambiguous;
}
