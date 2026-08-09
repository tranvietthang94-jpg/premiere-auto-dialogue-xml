using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Output.Audit;
using PremiereAutoDialogueXml.Output.Review;
using PremiereAutoDialogueXml.Output.Xml;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class ReviewGroupBuilderTests
{
    [TestMethod]
    public void BuildGroupsNearbyMarkersPerTrackWithoutLosingCoverage()
    {
        var markers = new[]
        {
            Marker(1, 10, 12, "ambiguous-energy-vad-conflict"),
            Marker(1, 20, 22, "ambiguous-energy-vad-conflict-residual"),
            Marker(1, 40, 42, "ambiguous-near-speech"),
            Marker(2, 11, 13, "ambiguous-independent"),
            Marker(2, 60, 61, "gain-capped")
        };
        var fragments = new[]
        {
            Fragment(1, 10, 22, @"F:\media\clip1.wav", "ambiguous-energy-vad-conflict"),
            Fragment(1, 40, 42, "clip1.wav", "ambiguous-near-speech"),
            Fragment(2, 11, 13, "clip2.wav", "ambiguous-independent")
        };
        var likely = Comparison(2, correlation: 0.95f);
        var below = Comparison(3, correlation: 0.82f);
        var evidence = new[]
        {
            Shadow(1, 10, 12, CrossTrackShadowOutcome.LikelyBleed, likely),
            Shadow(1, 20, 22, CrossTrackShadowOutcome.BelowThreshold, below)
        };

        var groups = new ReviewGroupBuilder().Build(fragments, markers, evidence, 25, 48_000);

        Assert.HasCount(3, groups);
        Assert.AreEqual(4, groups.Sum(group => group.MarkerCount));
        Assert.AreEqual(4, ReviewGroupBuilder.CountAmbiguousMarkers(markers));
        var energyGroup = groups[0];
        Assert.AreEqual("R-T01-000001", energyGroup.Id);
        Assert.AreEqual(ReviewPriority.High, energyGroup.Priority);
        Assert.AreEqual(2, energyGroup.MarkerCount);
        Assert.AreEqual(2, energyGroup.ShadowEvidenceCount);
        Assert.AreEqual(CrossTrackShadowOutcome.BelowThreshold, energyGroup.RepresentativeShadowOutcome);
        Assert.AreEqual(3, energyGroup.BestComparison!.OtherTrackIndex);
        CollectionAssert.AreEqual(new[] { "clip1.wav" }, energyGroup.SourceFileNames.ToArray());

        Assert.AreEqual(ReviewPriority.Low, groups[1].Priority);
        Assert.AreEqual("00:00:01:15", groups[1].InTimecode);
        Assert.AreEqual("00:00:01:17", groups[1].OutTimecode);
        Assert.AreEqual(ReviewPriority.Medium, groups[2].Priority);
        Assert.AreEqual(2, groups[2].TrackIndex);
    }

    [TestMethod]
    public void BuildAssignsMediumPriorityWhenAllEnergyEvidenceLooksLikeBleed()
    {
        var marker = Marker(1, 10, 12, "ambiguous-energy-vad-conflict");
        var fragment = Fragment(1, 10, 12, "voice.wav", "ambiguous-energy-vad-conflict");
        var evidence = Shadow(1, 10, 12, CrossTrackShadowOutcome.LikelyBleed, Comparison(2, 0.9f));

        var group = new ReviewGroupBuilder().Build([fragment], [marker], [evidence], 25, 48_000).Single();

        Assert.AreEqual(ReviewPriority.Medium, group.Priority);
        Assert.AreEqual(CrossTrackShadowOutcome.LikelyBleed, group.RepresentativeShadowOutcome);
    }

    [TestMethod]
    public void BuildRejectsMarkerWithoutMatchingAmbiguousFragment()
    {
        var marker = Marker(1, 10, 12, "ambiguous-independent");

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new ReviewGroupBuilder().Build([], [marker], [], 25, 48_000));
    }

    private static GeneratedSequenceMarker Marker(int track, long start, long end, string reason) =>
        new("Cần kiểm tra", reason, start, end, track, reason);

    private static GeneratedAudioFragment Fragment(
        int track,
        long start,
        long end,
        string sourceFileName,
        string reason) => new(
            $"fragment-{track}-{start}",
            track,
            $"clip-{track}",
            $"file-{track}",
            sourceFileName,
            start,
            end,
            start,
            end,
            start,
            end,
            start * 1_920,
            end * 1_920,
            AudioSegmentStatus.Ambiguous,
            true,
            null,
            null,
            reason,
            null);

    private static CrossTrackShadowEvidence Shadow(
        int track,
        long startFrame,
        long endFrame,
        CrossTrackShadowOutcome outcome,
        BleedEvidence comparison) => new(
            track,
            $"clip-{track}",
            startFrame * 1_920,
            endFrame * 1_920,
            "ambiguous-energy-vad-conflict",
            outcome,
            startFrame * 1_920,
            endFrame * 1_920,
            comparison);

    private static BleedEvidence Comparison(int otherTrack, float correlation) =>
        new(otherTrack, 8, correlation, 3, -24, -16, -8);
}
