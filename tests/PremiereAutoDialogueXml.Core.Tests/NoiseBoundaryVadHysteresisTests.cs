using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Audio.Pcm;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.ProjectModel;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class NoiseBoundaryVadHysteresisTests
{
    private const int ObservationSamples = 1_536;

    [TestMethod]
    public void CandidateStartsAtPointFiveAndContinuesThroughPointFour()
    {
        var observations = Warmup()
            .Concat(Frames(
                (0.50f, -18f),
                (0.50f, -18f),
                (0.50f, -18f),
                (0.50f, -18f),
                (0.44f, -45f),
                (0.42f, -45f),
                (0.40f, -45f),
                (0.39f, -60f)))
            .ToArray();

        using var fixture = Fixture();
        var shadow = Analyze(fixture, observations);

        Assert.AreEqual(0.50, shadow.Candidate.NoiseBoundaryTrace!.BoundaryPolicy.StartThreshold);
        Assert.AreEqual(0.40, shadow.Candidate.NoiseBoundaryTrace.BoundaryPolicy.ContinueThreshold);
        Assert.HasCount(1, shadow.Baseline.Phrases);
        Assert.HasCount(1, shadow.Candidate.Phrases);
        Assert.AreEqual(12L * ObservationSamples, shadow.Baseline.Phrases[0].CoreEndSample);
        Assert.AreEqual(15L * ObservationSamples, shadow.Candidate.Phrases[0].CoreEndSample);
        Assert.IsTrue(shadow.Candidate.NoiseBoundaryTrace.Frames
            .Skip(12)
            .Take(3)
            .All(frame => frame.BoundaryState == NoiseBoundaryFrameState.ConfirmedContinueEvidence));
        Assert.AreEqual(
            NoiseBoundaryFrameState.VadNegative,
            shadow.Candidate.NoiseBoundaryTrace.Frames[15].BoundaryState);
        AssertNoEnabledLoss(shadow);
    }

    [TestMethod]
    public void CandidateDoesNotCreatePhraseWithoutValidStart()
    {
        var observations = Warmup()
            .Concat(Frames(Enumerable.Repeat((Vad: 0.45f, Rms: -45f), 8).ToArray()))
            .ToArray();

        using var fixture = Fixture();
        var shadow = Analyze(fixture, observations);

        Assert.HasCount(0, shadow.Candidate.Phrases);
        Assert.IsTrue(shadow.Candidate.Segments.Any(segment =>
            segment.Status == AudioSegmentStatus.Ambiguous &&
            segment.Reason.StartsWith("ambiguous-energy-vad-conflict", StringComparison.Ordinal)));
        AssertNoEnabledLoss(shadow);
    }

    [TestMethod]
    public void CandidateDoesNotLowerMinimumStartOrDirectEvidence()
    {
        var observations = Warmup()
            .Concat(Frames(
                Enumerable.Repeat((Vad: 0.60f, Rms: -18f), 3)
                    .Concat(Enumerable.Repeat((Vad: 0.45f, Rms: -60f), 8))
                    .ToArray()))
            .ToArray();

        using var fixture = Fixture();
        var shadow = Analyze(fixture, observations);

        Assert.HasCount(0, shadow.Candidate.Phrases);
        Assert.IsTrue(shadow.Candidate.NoiseBoundaryTrace!.Frames
            .Skip(11)
            .All(frame => frame.BoundaryState == NoiseBoundaryFrameState.UnconfirmedContinueEvidence));
        Assert.IsTrue(shadow.Candidate.Segments.Any(segment =>
            segment.Status == AudioSegmentStatus.Ambiguous &&
            segment.TimelineStartSample <= 8L * ObservationSamples &&
            segment.TimelineEndSample >= observations.Length * ObservationSamples));
        AssertNoEnabledLoss(shadow);
    }

    [TestMethod]
    public void CandidateKeepsContinueContextWithoutDirectEnergyAmbiguous()
    {
        var observations = Warmup()
            .Concat(Frames(
                (0.60f, -18f),
                (0.60f, -18f),
                (0.60f, -18f),
                (0.60f, -18f),
                (0.49f, -60f),
                (0.42f, -60f),
                (0.40f, -60f)))
            .ToArray();

        using var fixture = Fixture();
        var shadow = Analyze(fixture, observations);
        var tailStart = 12L * ObservationSamples;
        var tailEnd = 15L * ObservationSamples;

        Assert.HasCount(1, shadow.Candidate.Phrases);
        Assert.AreEqual(tailEnd, shadow.Candidate.Phrases[0].CoreEndSample);
        Assert.IsTrue(shadow.Candidate.NoiseBoundaryTrace!.Frames
            .Skip(12)
            .All(frame => frame.BoundaryState == NoiseBoundaryFrameState.ContinueAmbiguous));
        var tailSegments = shadow.Candidate.Segments
            .Where(segment =>
                segment.TimelineStartSample < tailEnd &&
                segment.TimelineEndSample > tailStart)
            .ToArray();
        Assert.IsNotEmpty(tailSegments);
        Assert.IsTrue(tailSegments.All(segment =>
            segment.Status == AudioSegmentStatus.Ambiguous &&
            segment.Reason == "ambiguous-vad-continue-context-near-speech" &&
            segment.PhraseId == shadow.Candidate.Phrases[0].Id &&
            segment.GainDb == shadow.Candidate.Phrases[0].AppliedGainDb));
        AssertNoEnabledLoss(shadow);
    }

    [TestMethod]
    public void CandidateHandlesProbabilityJitterWithoutAcceptingBelowContinueThreshold()
    {
        var observations = Warmup()
            .Concat(Frames(
                (0.60f, -18f),
                (0.60f, -18f),
                (0.60f, -18f),
                (0.60f, -18f),
                (0.49f, -60f),
                (0.38f, -60f),
                (0.48f, -60f),
                (0.39f, -60f),
                (0.40f, -60f)))
            .ToArray();

        using var fixture = Fixture();
        var shadow = Analyze(fixture, observations);
        var trace = shadow.Candidate.NoiseBoundaryTrace!.Frames;

        Assert.AreEqual(17L * ObservationSamples, shadow.Candidate.Phrases.Single().CoreEndSample);
        Assert.AreEqual(NoiseBoundaryFrameState.ContinueAmbiguous, trace[12].BoundaryState);
        Assert.AreEqual(NoiseBoundaryFrameState.VadNegative, trace[13].BoundaryState);
        Assert.AreEqual(NoiseBoundaryFrameState.ContinueAmbiguous, trace[14].BoundaryState);
        Assert.AreEqual(NoiseBoundaryFrameState.VadNegative, trace[15].BoundaryState);
        Assert.AreEqual(NoiseBoundaryFrameState.ContinueAmbiguous, trace[16].BoundaryState);
        AssertNoEnabledLoss(shadow);
    }

    [TestMethod]
    public void CandidateDoesNotBridgeGapAtPhraseBreakThreshold()
    {
        var observations = Warmup().ToList();
        observations.AddRange(Frames(
            (0.60f, -18f),
            (0.60f, -18f),
            (0.60f, -18f),
            (0.60f, -18f)));
        var firstEnd = observations[^1].TimelineEndSample;
        var phraseBreakSamples = AudioMath.MillisecondsToSamples(
            DialogueProcessingPreset.Balanced.PhraseBreakMilliseconds);
        var ignoredContinueStart = firstEnd + phraseBreakSamples;
        observations.Add(Observation(ignoredContinueStart, 0.45f, -60f, containsMedia: true));
        observations.AddRange(Enumerable.Range(0, 4).Select(index =>
            Observation(
                ignoredContinueStart + ((index + 1L) * ObservationSamples),
                0.60f,
                -18f,
                containsMedia: true)));

        using var fixture = Fixture();
        var shadow = Analyze(fixture, observations);

        Assert.HasCount(2, shadow.Candidate.Phrases);
        Assert.AreEqual(firstEnd, shadow.Candidate.Phrases[0].CoreEndSample);
        Assert.AreEqual(
            ignoredContinueStart + ObservationSamples,
            shadow.Candidate.Phrases[1].CoreStartSample);
        Assert.IsGreaterThanOrEqualTo(
            phraseBreakSamples,
            shadow.Candidate.Phrases[1].CoreStartSample - shadow.Candidate.Phrases[0].CoreEndSample);
        AssertNoEnabledLoss(shadow);
    }

    [TestMethod]
    public void CandidateClosesActiveContextAtMediaGap()
    {
        var observations = Warmup().ToList();
        observations.AddRange(Frames(
            (0.60f, -18f),
            (0.60f, -18f),
            (0.60f, -18f),
            (0.60f, -18f)));
        var gapStart = observations[^1].TimelineEndSample;
        observations.Add(Observation(gapStart, 0.01f, -144f, containsMedia: false));
        observations.Add(Observation(
            gapStart + ObservationSamples,
            0.45f,
            -60f,
            containsMedia: true));
        observations.AddRange(Enumerable.Range(0, 4).Select(index =>
            Observation(
                gapStart + ((index + 2L) * ObservationSamples),
                0.60f,
                -18f,
                containsMedia: true)));

        using var fixture = Fixture();
        var shadow = Analyze(fixture, observations);

        Assert.HasCount(2, shadow.Candidate.Phrases);
        Assert.AreEqual(gapStart, shadow.Candidate.Phrases[0].CoreEndSample);
        Assert.AreEqual(
            gapStart + (2L * ObservationSamples),
            shadow.Candidate.Phrases[1].CoreStartSample);
        Assert.AreEqual(
            NoiseBoundaryFrameState.NoMedia,
            shadow.Candidate.NoiseBoundaryTrace!.Frames[12].BoundaryState);
        Assert.AreEqual(
            NoiseBoundaryFrameState.VadNegative,
            shadow.Candidate.NoiseBoundaryTrace.Frames[13].BoundaryState);
        AssertNoEnabledLoss(shadow);
    }

    [TestMethod]
    public void CandidateRejectsUnprovenStartThresholdChange()
    {
        var changedPreset = DialogueProcessingPreset.Balanced with { VadThreshold = 0.55 };

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            VadBoundaryPolicyCatalog.For(
                NoiseBoundaryAnalysisMode.NoiseBoundaryCandidate,
                changedPreset));
    }

    [TestMethod]
    public void CandidateRejectsUnprovenBoundaryTimingChange()
    {
        var changedPresets = new[]
        {
            DialogueProcessingPreset.Balanced with { PhraseBreakMilliseconds = 400 },
            DialogueProcessingPreset.Balanced with { MinimumSpeechMilliseconds = 100 }
        };

        foreach (var changedPreset in changedPresets)
        {
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                VadBoundaryPolicyCatalog.For(
                    NoiseBoundaryAnalysisMode.NoiseBoundaryCandidate,
                    changedPreset));
        }
    }

    private static NoiseBoundaryShadowAnalysis Analyze(
        TestAudioFixture fixture,
        IReadOnlyList<AudioFrameObservation> observations)
    {
        var track = new PremiereAudioTrack(1, 1, [fixture.Clip("hysteresis", 0, 40)]);
        return new TrackDialogueAnalyzer(new TimelinePcmAccessor(new PcmWaveSampleReader()))
            .AnalyzeShadow(track, observations, DialogueProcessingPreset.Balanced);
    }

    private static TestAudioFixture Fixture() =>
        TestAudioFixture.CreatePcm16(Enumerable.Repeat(0.25f, 76_800).ToArray());

    private static IReadOnlyList<AudioFrameObservation> Warmup() =>
        Enumerable.Range(0, 8)
            .Select(index => Observation(
                index * ObservationSamples,
                0.01f,
                -60f,
                containsMedia: true))
            .ToArray();

    private static IEnumerable<AudioFrameObservation> Frames(
        params (float Vad, float Rms)[] values)
    {
        var start = 8L * ObservationSamples;
        return values.Select((value, index) =>
            Observation(start + (index * ObservationSamples), value.Vad, value.Rms, containsMedia: true));
    }

    private static AudioFrameObservation Observation(
        long start,
        float vad,
        float rms,
        bool containsMedia) =>
        new(start, start + ObservationSamples, vad, rms, rms, containsMedia);

    private static void AssertNoEnabledLoss(NoiseBoundaryShadowAnalysis shadow) =>
        Assert.AreEqual(0, shadow.Comparison.BaselineEnabledCandidateDisabledCount);
}
