using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Audio.Pcm;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.ProjectModel;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class TrackDialogueAnalyzerTests
{
    [TestMethod]
    public void AnalyzeBuildsPhraseWithPaddingAndPeakBasedGain()
    {
        var samples = Enumerable.Repeat(0.001f, 57_600).ToArray();
        Array.Fill(samples, 0.25f, 15_360, 6_144);
        using var fixture = TestAudioFixture.CreatePcm16(samples);
        var track = new PremiereAudioTrack(1, 1, [fixture.Clip("clip-1", 0, 30)]);
        var observations = new List<AudioFrameObservation>
        {
            Observation(0, 1_536, 0.01f, -60f),
            Observation(1_536, 3_072, 0.01f, -60f),
            Observation(15_360, 16_896, 0.90f, -12f),
            Observation(16_896, 18_432, 0.90f, -12f),
            Observation(18_432, 19_968, 0.90f, -12f),
            Observation(19_968, 21_504, 0.90f, -12f)
        };

        var result = Analyze(track, observations);

        Assert.HasCount(1, result.Phrases);
        var phrase = result.Phrases[0];
        Assert.AreEqual(5_760L, phrase.PaddedStartSample);
        Assert.AreEqual(35_904L, phrase.PaddedEndSample);
        Assert.AreEqual(-12.0412f, phrase.MeasuredPeakDbfs, 0.002f);
        Assert.AreEqual(9.0515f, phrase.AppliedGainDb, 0.002f);
        Assert.AreEqual(
            -6f,
            phrase.MeasuredPeakDbfs + phrase.AppliedGainDb -
            (float)DialogueProcessingPreset.Balanced.PremiereCenterPanCompensationDb,
            0.002f);
        Assert.IsFalse(phrase.GainWasCapped);
        Assert.IsTrue(result.Segments.Any(segment => segment.Status == AudioSegmentStatus.Speech));
        Assert.IsTrue(result.Segments.Any(segment => segment.Status == AudioSegmentStatus.Noise));
    }

    [TestMethod]
    public void AnalyzeKeepsOneGainDecisionAcrossTwoWaveFiles()
    {
        var firstSamples = Enumerable.Repeat(0.001f, 9_600).ToArray();
        var secondSamples = Enumerable.Repeat(0.001f, 9_600).ToArray();
        Array.Fill(firstSamples, 0.125f, 6_144, 3_456);
        Array.Fill(secondSamples, 0.25f, 0, 3_072);
        using var first = TestAudioFixture.CreatePcm16(firstSamples);
        using var second = TestAudioFixture.CreatePcm16(secondSamples);
        var track = new PremiereAudioTrack(
            2,
            2,
            [first.Clip("clip-a", 0, 5), second.Clip("clip-b", 5, 10)]);
        var observations = new[]
        {
            Observation(0, 1_536, 0.01f, -60f),
            Observation(6_144, 7_680, 0.90f, -18f),
            Observation(7_680, 9_216, 0.90f, -18f),
            Observation(9_600, 11_136, 0.90f, -12f),
            Observation(11_136, 12_672, 0.90f, -12f)
        };

        var result = Analyze(track, observations);

        Assert.HasCount(1, result.Phrases);
        var phraseId = result.Phrases[0].Id;
        Assert.AreEqual(-12.0412f, result.Phrases[0].MeasuredPeakDbfs, 0.002f);
        Assert.IsTrue(result.Segments.Any(segment => segment.SourceClipId == "clip-a" && segment.PhraseId == phraseId));
        Assert.IsTrue(result.Segments.Any(segment => segment.SourceClipId == "clip-b" && segment.PhraseId == phraseId));
    }

    [TestMethod]
    public void AnalyzeCapsLargeBoostAndMarksPhrase()
    {
        var samples = Enumerable.Repeat(0.001f, 19_200).ToArray();
        using var fixture = TestAudioFixture.CreatePcm16(samples);
        var track = new PremiereAudioTrack(1, 1, [fixture.Clip("quiet", 0, 10)]);
        var observations = new[]
        {
            Observation(0, 1_536, 0.01f, -80f),
            Observation(3_072, 4_608, 0.90f, -40f),
            Observation(4_608, 6_144, 0.90f, -40f),
            Observation(6_144, 7_680, 0.90f, -40f),
            Observation(7_680, 9_216, 0.90f, -40f)
        };

        var result = Analyze(track, observations);

        Assert.HasCount(1, result.Phrases);
        Assert.AreEqual(18f, result.Phrases[0].AppliedGainDb, 0.0001f);
        Assert.IsTrue(result.Phrases[0].GainWasCapped);
        Assert.IsGreaterThan(18f, result.Phrases[0].RequiredGainDb);
    }

    [TestMethod]
    public void AnalyzeUsesHotterFrameAlignedPhrasePeakAsGainReference()
    {
        var samples = Enumerable.Repeat(0.001f, 38_400).ToArray();
        Array.Fill(samples, 0.25f, 10_000, 6_144);
        samples[7_000] = 0.5f;
        using var fixture = TestAudioFixture.CreatePcm16(samples);
        var track = new PremiereAudioTrack(1, 1, [fixture.Clip("frame-peak", 0, 20)]);
        var observations = new[]
        {
            Observation(0, 1_536, 0.01f, -60f),
            Observation(10_000, 11_536, 0.90f, -12f),
            Observation(11_536, 13_072, 0.90f, -12f),
            Observation(13_072, 14_608, 0.90f, -12f),
            Observation(14_608, 16_144, 0.90f, -12f)
        };

        var result = Analyze(track, observations);

        Assert.HasCount(1, result.Phrases);
        var phrase = result.Phrases[0];
        Assert.AreEqual(-6.0206f, phrase.MeasuredPeakDbfs, 0.002f);
        Assert.AreEqual(3.0309f, phrase.AppliedGainDb, 0.002f);
        Assert.AreEqual(
            -6f,
            phrase.MeasuredPeakDbfs + phrase.AppliedGainDb -
            (float)DialogueProcessingPreset.Balanced.PremiereCenterPanCompensationDb,
            0.002f);
    }

    [TestMethod]
    public void AnalyzePreservesShortVadEventAsAmbiguous()
    {
        using var fixture = TestAudioFixture.CreatePcm16(Enumerable.Repeat(0.1f, 9_600).ToArray());
        var track = new PremiereAudioTrack(1, 1, [fixture.Clip("short", 0, 5)]);
        var observations = new[]
        {
            Observation(0, 1_536, 0.01f, -60f),
            Observation(3_072, 4_608, 0.90f, -20f)
        };

        var result = Analyze(track, observations);

        Assert.HasCount(0, result.Phrases);
        Assert.IsTrue(result.Segments.Any(segment => segment.Status == AudioSegmentStatus.Ambiguous));
        Assert.IsFalse(result.Segments.Any(
            segment => segment.Status == AudioSegmentStatus.Noise &&
                       segment.TimelineStartSample < 4_608 &&
                       segment.TimelineEndSample > 3_072));
    }

    [TestMethod]
    public void AnalyzePreservesLaughterLikeBorderlineBurstsAsAmbiguous()
    {
        using var fixture = TestAudioFixture.CreatePcm16(Enumerable.Repeat(0.2f, 19_200).ToArray());
        var track = new PremiereAudioTrack(1, 1, [fixture.Clip("laugh", 0, 10)]);
        var observations = new[]
        {
            Observation(0, 1_536, 0.01f, -60f),
            Observation(4_608, 6_144, 0.40f, -14f),
            Observation(6_144, 7_680, 0.42f, -14f)
        };

        var result = Analyze(track, observations);

        Assert.HasCount(0, result.Phrases);
        Assert.IsTrue(result.Segments.Any(
            segment => segment.Status == AudioSegmentStatus.Ambiguous &&
                       segment.TimelineStartSample <= 4_608 &&
                       segment.TimelineEndSample >= 7_680));
    }

    [TestMethod]
    public void AnalyzeSplitsOverlappingPaddingAtMidpointAndAmbiguousInheritsGain()
    {
        var samples = Enumerable.Repeat(0.001f, 115_200).ToArray();
        Array.Fill(samples, 0.25f, 10_000, 6_144);
        Array.Fill(samples, 0.25f, 34_576, 6_144);
        using var fixture = TestAudioFixture.CreatePcm16(samples);
        var track = new PremiereAudioTrack(3, 1, [fixture.Clip("two-phrases", 0, 60)]);
        var observations = new[]
        {
            Observation(0, 1_536, 0.01f, -60f),
            Observation(10_000, 11_536, 0.90f, -12f),
            Observation(11_536, 13_072, 0.90f, -12f),
            Observation(13_072, 14_608, 0.90f, -12f),
            Observation(14_608, 16_144, 0.90f, -12f),
            Observation(17_000, 18_536, 0.40f, -14f),
            Observation(34_576, 36_112, 0.90f, -12f),
            Observation(36_112, 37_648, 0.90f, -12f),
            Observation(37_648, 39_184, 0.90f, -12f),
            Observation(39_184, 40_720, 0.90f, -12f)
        };

        var result = Analyze(track, observations);

        Assert.HasCount(2, result.Phrases);
        Assert.AreEqual(result.Phrases[0].PaddedEndSample, result.Phrases[1].PaddedStartSample);
        var inherited = result.Segments.Single(segment =>
            segment.Status == AudioSegmentStatus.Ambiguous &&
            segment.TimelineStartSample <= 17_000 &&
            segment.TimelineEndSample >= 18_536);
        Assert.AreEqual(result.Phrases[0].Id, inherited.PhraseId);
        Assert.AreEqual(result.Phrases[0].AppliedGainDb, inherited.GainDb);
    }

    private static TrackAudioAnalysis Analyze(
        PremiereAudioTrack track,
        IReadOnlyList<AudioFrameObservation> observations) =>
        new TrackDialogueAnalyzer(new TimelinePcmAccessor(new PcmWaveSampleReader()))
            .Analyze(track, observations, DialogueProcessingPreset.Balanced);

    private static AudioFrameObservation Observation(
        long start,
        long end,
        float probability,
        float rms) => new(start, end, probability, rms, rms, ContainsMedia: true);
}
