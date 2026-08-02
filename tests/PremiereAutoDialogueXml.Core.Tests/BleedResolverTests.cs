using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Audio.Pcm;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.ProjectModel;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class BleedResolverTests
{
    [TestMethod]
    public void ResolveClassifiesWeakCorrelatedCopyAsBleed()
    {
        using var targetFixture = TestAudioFixture.CreatePcm16(Sine(19_200, amplitude: 0.03f));
        using var otherFixture = TestAudioFixture.CreatePcm16(Sine(19_200, amplitude: 0.40f));
        var targetTrack = new PremiereAudioTrack(1, 1, [targetFixture.Clip("target", 0, 10)]);
        var otherTrack = new PremiereAudioTrack(2, 2, [otherFixture.Clip("other", 0, 10)]);
        var target = TargetAnalysis(AudioSegmentStatus.Ambiguous, targetTrack);
        var other = OtherAnalysis(otherTrack);

        var resolved = Resolve([targetTrack, otherTrack], [target, other]);

        var segment = resolved.Single(result => result.TrackIndex == 1).Segments[0];
        Assert.AreEqual(AudioSegmentStatus.Bleed, segment.Status);
        Assert.IsNotNull(segment.BleedEvidence);
        Assert.AreEqual(2, segment.BleedEvidence.OtherTrackIndex);
        Assert.IsGreaterThanOrEqualTo(0.80f, segment.BleedEvidence.WaveformCorrelation);
        Assert.IsGreaterThanOrEqualTo(12f, segment.BleedEvidence.OtherMicAdvantageDb);
    }

    [TestMethod]
    public void ResolveClassifiesCleanCorrelatedCopyEvenWhenVadInitiallyCalledItSpeech()
    {
        using var targetFixture = TestAudioFixture.CreatePcm16(Sine(19_200, amplitude: 0.03f));
        using var otherFixture = TestAudioFixture.CreatePcm16(Sine(19_200, amplitude: 0.40f));
        var targetTrack = new PremiereAudioTrack(1, 1, [targetFixture.Clip("target", 0, 10)]);
        var otherTrack = new PremiereAudioTrack(2, 2, [otherFixture.Clip("other", 0, 10)]);
        var target = TargetAnalysis(AudioSegmentStatus.Speech, targetTrack);
        var other = OtherAnalysis(otherTrack);

        var resolved = Resolve([targetTrack, otherTrack], [target, other]);

        var segment = resolved.Single(result => result.TrackIndex == 1).Segments[0];
        Assert.AreEqual(AudioSegmentStatus.Bleed, segment.Status);
        Assert.IsNull(segment.GainDb);
    }

    [TestMethod]
    public void ResolveKeepsOverlappingIndependentVoiceAmbiguous()
    {
        var targetSamples = Sine(19_200, amplitude: 0.03f);
        var independent = Sine(19_200, amplitude: 0.02f, frequency: 997);
        for (var index = 0; index < targetSamples.Length; index++)
        {
            targetSamples[index] += independent[index];
        }

        using var targetFixture = TestAudioFixture.CreatePcm16(targetSamples);
        using var otherFixture = TestAudioFixture.CreatePcm16(Sine(19_200, amplitude: 0.40f));
        var targetTrack = new PremiereAudioTrack(1, 1, [targetFixture.Clip("target", 0, 10)]);
        var otherTrack = new PremiereAudioTrack(2, 2, [otherFixture.Clip("other", 0, 10)]);
        var target = TargetAnalysis(AudioSegmentStatus.Speech, targetTrack);
        var other = OtherAnalysis(otherTrack);

        var resolved = Resolve([targetTrack, otherTrack], [target, other]);

        var segment = resolved.Single(result => result.TrackIndex == 1).Segments[0];
        Assert.AreEqual(AudioSegmentStatus.Ambiguous, segment.Status);
        Assert.AreEqual("conflicting-direct-and-bleed-evidence", segment.Reason);
        Assert.AreEqual(6f, segment.GainDb);
        Assert.IsNotNull(segment.BleedEvidence);
        Assert.IsGreaterThan(-10f, segment.BleedEvidence.ResidualToTargetDb);
    }

    [TestMethod]
    public void ResolveDoesNotDisableWhenOtherMicIsNotTwelveDbLouder()
    {
        using var targetFixture = TestAudioFixture.CreatePcm16(Sine(19_200, amplitude: 0.20f));
        using var otherFixture = TestAudioFixture.CreatePcm16(Sine(19_200, amplitude: 0.40f));
        var targetTrack = new PremiereAudioTrack(1, 1, [targetFixture.Clip("target", 0, 10)]);
        var otherTrack = new PremiereAudioTrack(2, 2, [otherFixture.Clip("other", 0, 10)]);
        var target = TargetAnalysis(AudioSegmentStatus.Ambiguous, targetTrack);
        var other = OtherAnalysis(otherTrack);

        var resolved = Resolve([targetTrack, otherTrack], [target, other]);

        Assert.AreEqual(AudioSegmentStatus.Ambiguous, resolved[0].Segments[0].Status);
        Assert.IsNull(resolved[0].Segments[0].BleedEvidence);
    }

    private static IReadOnlyList<TrackAudioAnalysis> Resolve(
        IReadOnlyList<PremiereAudioTrack> tracks,
        IReadOnlyList<TrackAudioAnalysis> analyses)
    {
        var sequence = new PremiereSequence("sequence", "uuid", "test", 25, 10, 2, 48_000, tracks);
        return new BleedResolver(new TimelinePcmAccessor(new PcmWaveSampleReader()))
            .Resolve(sequence, analyses, DialogueProcessingPreset.Balanced);
    }

    private static TrackAudioAnalysis TargetAnalysis(
        AudioSegmentStatus status,
        PremiereAudioTrack track)
    {
        var clip = track.Clips[0];
        var segment = new AnalyzedAudioSegment(
            track.Index,
            clip.Id,
            0,
            9_600,
            0,
            9_600,
            status,
            status == AudioSegmentStatus.Speech ? "T01-P000001" : null,
            status == AudioSegmentStatus.Speech ? 6f : 0f,
            status == AudioSegmentStatus.Speech ? "confirmed-direct-speech" : "ambiguous-independent");
        return new(track.Index, 0, [], [segment], LearnedDirectVoiceRmsDbfs: -15f);
    }

    private static TrackAudioAnalysis OtherAnalysis(PremiereAudioTrack track)
    {
        var phrase = new DialoguePhrase(
            "T02-P000001",
            track.Index,
            0,
            9_600,
            0,
            9_600,
            -8f,
            2f,
            2f,
            false);
        return new(track.Index, 0, [phrase], [], LearnedDirectVoiceRmsDbfs: -8f);
    }

    private static float[] Sine(int sampleCount, float amplitude, float frequency = 440) =>
        Enumerable.Range(0, sampleCount)
            .Select(index => amplitude * MathF.Sin(2 * MathF.PI * frequency * index / 48_000))
            .ToArray();
}
