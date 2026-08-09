using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Audio.Pcm;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.ProjectModel;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class CrossTrackShadowEvidenceBuilderTests
{
    [TestMethod]
    public void BuildMarksWeakCorrelatedEnergyConflictAsLikelyBleedWithoutChangingSegment()
    {
        using var targetFixture = TestAudioFixture.CreatePcm16(Sine(19_200, amplitude: 0.03f));
        using var otherFixture = TestAudioFixture.CreatePcm16(Sine(19_200, amplitude: 0.40f));
        var targetTrack = new PremiereAudioTrack(1, 1, [targetFixture.Clip("target", 0, 10)]);
        var otherTrack = new PremiereAudioTrack(2, 2, [otherFixture.Clip("other", 0, 10)]);
        var targetSegment = EnergyConflict(targetTrack);
        var target = TargetAnalysis(targetTrack, targetSegment, learnedDirectVoiceRmsDbfs: -15f);
        var other = OtherAnalysis(otherTrack);

        var evidence = Build([targetTrack, otherTrack], [target, other]);

        Assert.HasCount(1, evidence);
        Assert.AreEqual(CrossTrackShadowOutcome.LikelyBleed, evidence[0].Outcome);
        var comparison = evidence[0].BestComparison;
        Assert.IsNotNull(comparison);
        Assert.AreEqual(2, comparison.OtherTrackIndex);
        Assert.IsGreaterThanOrEqualTo(0.80f, comparison.WaveformCorrelation);
        Assert.IsGreaterThanOrEqualTo(12f, comparison.OtherMicAdvantageDb);
        Assert.AreEqual(AudioSegmentStatus.Ambiguous, targetSegment.Status);
        Assert.AreEqual("ambiguous-energy-vad-conflict", targetSegment.Reason);
        Assert.AreEqual(0f, targetSegment.GainDb);
    }

    [TestMethod]
    public void BuildMarksIndependentResidualAsConflictingEvidence()
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
        var target = TargetAnalysis(targetTrack, EnergyConflict(targetTrack), -15f);
        var other = OtherAnalysis(otherTrack);

        var evidence = Build([targetTrack, otherTrack], [target, other]);

        Assert.HasCount(1, evidence);
        Assert.AreEqual(CrossTrackShadowOutcome.ConflictingEvidence, evidence[0].Outcome);
        var comparison = evidence[0].BestComparison;
        Assert.IsNotNull(comparison);
        Assert.IsGreaterThan(
            -10f,
            comparison.ResidualToTargetDb);
    }

    [TestMethod]
    public void BuildReportsBelowThresholdWhenOtherMicIsNotTwelveDbLouder()
    {
        using var targetFixture = TestAudioFixture.CreatePcm16(Sine(19_200, amplitude: 0.20f));
        using var otherFixture = TestAudioFixture.CreatePcm16(Sine(19_200, amplitude: 0.40f));
        var targetTrack = new PremiereAudioTrack(1, 1, [targetFixture.Clip("target", 0, 10)]);
        var otherTrack = new PremiereAudioTrack(2, 2, [otherFixture.Clip("other", 0, 10)]);
        var target = TargetAnalysis(targetTrack, EnergyConflict(targetTrack), -10f);
        var other = OtherAnalysis(otherTrack);

        var evidence = Build([targetTrack, otherTrack], [target, other]);

        Assert.HasCount(1, evidence);
        Assert.AreEqual(CrossTrackShadowOutcome.BelowThreshold, evidence[0].Outcome);
        var comparison = evidence[0].BestComparison;
        Assert.IsNotNull(comparison);
        Assert.IsLessThan(12f, comparison.OtherMicAdvantageDb);
    }

    [TestMethod]
    public void BuildReportsNoComparableSpeechWhenOtherTracksHaveNoOverlappingPhrase()
    {
        using var targetFixture = TestAudioFixture.CreatePcm16(Sine(19_200, amplitude: 0.03f));
        using var otherFixture = TestAudioFixture.CreatePcm16(Sine(19_200, amplitude: 0.40f));
        var targetTrack = new PremiereAudioTrack(1, 1, [targetFixture.Clip("target", 0, 10)]);
        var otherTrack = new PremiereAudioTrack(2, 2, [otherFixture.Clip("other", 0, 10)]);
        var target = TargetAnalysis(targetTrack, EnergyConflict(targetTrack), -15f);
        var other = new TrackAudioAnalysis(2, 0, [], [], -8f);

        var evidence = Build([targetTrack, otherTrack], [target, other]);

        Assert.HasCount(1, evidence);
        Assert.AreEqual(CrossTrackShadowOutcome.NoComparableSpeech, evidence[0].Outcome);
        Assert.IsNull(evidence[0].BestComparison);
        Assert.IsNull(evidence[0].ComparedStartSample);
        Assert.IsNull(evidence[0].ComparedEndSample);
    }

    [TestMethod]
    public void BuildIgnoresAmbiguousSegmentsThatAreNotEnergyVadConflicts()
    {
        using var targetFixture = TestAudioFixture.CreatePcm16(Sine(19_200, amplitude: 0.03f));
        using var otherFixture = TestAudioFixture.CreatePcm16(Sine(19_200, amplitude: 0.40f));
        var targetTrack = new PremiereAudioTrack(1, 1, [targetFixture.Clip("target", 0, 10)]);
        var otherTrack = new PremiereAudioTrack(2, 2, [otherFixture.Clip("other", 0, 10)]);
        var clip = targetTrack.Clips[0];
        var segment = new AnalyzedAudioSegment(
            1,
            clip.Id,
            0,
            9_600,
            0,
            9_600,
            AudioSegmentStatus.Ambiguous,
            null,
            0f,
            "ambiguous-independent");
        var target = TargetAnalysis(targetTrack, segment, -15f);
        var other = OtherAnalysis(otherTrack);

        var evidence = Build([targetTrack, otherTrack], [target, other]);

        Assert.IsEmpty(evidence);
    }

    [TestMethod]
    public void BuildHonorsCancellationBeforeReadingComparisonMedia()
    {
        using var targetFixture = TestAudioFixture.CreatePcm16(Sine(19_200, amplitude: 0.03f));
        using var otherFixture = TestAudioFixture.CreatePcm16(Sine(19_200, amplitude: 0.40f));
        var targetTrack = new PremiereAudioTrack(1, 1, [targetFixture.Clip("target", 0, 10)]);
        var otherTrack = new PremiereAudioTrack(2, 2, [otherFixture.Clip("other", 0, 10)]);
        var target = TargetAnalysis(targetTrack, EnergyConflict(targetTrack), -15f);
        var other = OtherAnalysis(otherTrack);
        var sequence = new PremiereSequence(
            "sequence",
            "uuid",
            "test",
            25,
            10,
            2,
            48_000,
            [targetTrack, otherTrack]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            new CrossTrackShadowEvidenceBuilder(
                    new TimelinePcmAccessor(new PcmWaveSampleReader()))
                .Build(
                    sequence,
                    [target, other],
                    DialogueProcessingPreset.Balanced,
                    cancellation.Token));
    }

    private static IReadOnlyList<CrossTrackShadowEvidence> Build(
        IReadOnlyList<PremiereAudioTrack> tracks,
        IReadOnlyList<TrackAudioAnalysis> analyses)
    {
        var sequence = new PremiereSequence("sequence", "uuid", "test", 25, 10, 2, 48_000, tracks);
        return new CrossTrackShadowEvidenceBuilder(
                new TimelinePcmAccessor(new PcmWaveSampleReader()))
            .Build(sequence, analyses, DialogueProcessingPreset.Balanced);
    }

    private static AnalyzedAudioSegment EnergyConflict(PremiereAudioTrack track)
    {
        var clip = track.Clips[0];
        return new(
            track.Index,
            clip.Id,
            0,
            9_600,
            0,
            9_600,
            AudioSegmentStatus.Ambiguous,
            null,
            0f,
            "ambiguous-energy-vad-conflict");
    }

    private static TrackAudioAnalysis TargetAnalysis(
        PremiereAudioTrack track,
        AnalyzedAudioSegment segment,
        float learnedDirectVoiceRmsDbfs) =>
        new(track.Index, 0, [], [segment], learnedDirectVoiceRmsDbfs);

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
