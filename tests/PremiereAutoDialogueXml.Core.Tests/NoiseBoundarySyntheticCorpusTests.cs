using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Audio.Pcm;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.ProjectModel;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class NoiseBoundarySyntheticCorpusTests
{
    private const int ObservationSamples = 1_536;

    [TestMethod]
    public void AnalyzeShadowKeepsCandidateSafeAcrossSyntheticCorpus()
    {
        var scenarios = new Dictionary<string, IReadOnlyList<AudioFrameObservation>>
        {
            ["stationary-room-tone"] = Observations(
                (0.01f, -60f, true),
                (0.01f, -60f, true),
                (0.01f, -60f, true),
                (0.01f, -60f, true),
                (0.01f, -60f, true),
                (0.01f, -60f, true)),
            ["floor-step-up-down"] = Observations(
                (0.01f, -65f, true),
                (0.01f, -65f, true),
                (0.01f, -65f, true),
                (0.01f, -48f, true),
                (0.01f, -48f, true),
                (0.01f, -48f, true),
                (0.01f, -68f, true),
                (0.01f, -68f, true),
                (0.01f, -68f, true)),
            ["silence"] = Observations(
                (0.01f, -120f, true),
                (0.01f, -120f, true),
                (0.01f, -120f, true),
                (0.01f, -120f, true)),
            ["loud-first-frame"] = Observations(
                (0.01f, -12f, true),
                (0.01f, -60f, true),
                (0.01f, -60f, true),
                (0.01f, -60f, true)),
            ["vad-negative-high-energy-conflict"] = Observations(
                (0.01f, -60f, true),
                (0.01f, -45f, true),
                (0.01f, -45f, true),
                (0.01f, -45f, true),
                (0.01f, -45f, true)),
            ["speech-at-threshold"] = Observations(
                (0.01f, -60f, true),
                (0.50f, -18f, true),
                (0.50f, -18f, true),
                (0.50f, -18f, true),
                (0.50f, -18f, true)),
            ["probability-jitter"] = Observations(
                (0.01f, -60f, true),
                (0.51f, -18f, true),
                (0.49f, -18f, true),
                (0.52f, -18f, true),
                (0.48f, -18f, true),
                (0.51f, -18f, true),
                (0.49f, -18f, true)),
            ["media-gap"] = Observations(
                (0.01f, -60f, true),
                (0.01f, -120f, false),
                (0.01f, -120f, false),
                (0.01f, -60f, true))
        };

        foreach (var scenario in scenarios)
        {
            using var fixture = TestAudioFixture.CreatePcm16(
                Enumerable.Repeat(0.25f, 38_400).ToArray());
            var track = new PremiereAudioTrack(
                1,
                1,
                [fixture.Clip(scenario.Key, 0, 20)]);

            var shadow = Analyzer().AnalyzeShadow(
                track,
                scenario.Value,
                DialogueProcessingPreset.Balanced);

            AssertShadowIsSafe(scenario.Key, scenario.Value.Count, shadow);
        }
    }

    [TestMethod]
    public void AnalyzeDefaultMatchesShadowBaselineWithoutCapturingTrace()
    {
        using var fixture = TestAudioFixture.CreatePcm16(
            Enumerable.Repeat(0.25f, 38_400).ToArray());
        var track = new PremiereAudioTrack(1, 1, [fixture.Clip("default-path", 0, 20)]);
        var observations = Observations(
            (0.01f, -60f, true),
            (0.90f, -18f, true),
            (0.90f, -18f, true),
            (0.90f, -18f, true),
            (0.90f, -18f, true));
        var analyzer = Analyzer();

        var production = analyzer.Analyze(track, observations, DialogueProcessingPreset.Balanced);
        var shadow = analyzer.AnalyzeShadow(track, observations, DialogueProcessingPreset.Balanced);

        Assert.IsNull(production.NoiseBoundaryTrace);
        CollectionAssert.AreEqual(production.Phrases.ToArray(), shadow.Baseline.Phrases.ToArray());
        CollectionAssert.AreEqual(production.Segments.ToArray(), shadow.Baseline.Segments.ToArray());
        Assert.AreEqual(production.LearnedDirectVoiceRmsDbfs, shadow.Baseline.LearnedDirectVoiceRmsDbfs);
        AssertShadowIsSafe("default-path", observations.Count, shadow);
    }

    [TestMethod]
    public void AnalyzeShadowRecordsPhase09LoudFirstFrameSelfTraining()
    {
        using var fixture = TestAudioFixture.CreatePcm16(
            Enumerable.Repeat(0.25f, 9_600).ToArray());
        var track = new PremiereAudioTrack(1, 1, [fixture.Clip("loud-first", 0, 5)]);
        var observations = Observations((0.01f, -12f, true));

        var shadow = Analyzer().AnalyzeShadow(
            track,
            observations,
            DialogueProcessingPreset.Balanced);

        var frame = shadow.Baseline.NoiseBoundaryTrace!.Frames.Single();
        Assert.AreEqual(-90f, frame.NoiseFloorBeforeDbfs, 0.001f);
        Assert.AreEqual(-12f, frame.NoiseFloorAfterDbfs, 0.001f);
        Assert.AreEqual(
            NoiseFloorTrainingDecision.EligibleVadNegative,
            frame.NoiseFloorTrainingDecision);
        Assert.IsFalse(frame.IsAboveDirectEnergyThreshold);
        Assert.AreEqual(NoiseBoundaryFrameState.VadNegative, frame.BoundaryState);
        var candidateFrame = shadow.Candidate.NoiseBoundaryTrace!.Frames.Single();
        Assert.AreEqual(-90f, candidateFrame.NoiseFloorBeforeDbfs, 0.001f);
        Assert.AreEqual(-90f, candidateFrame.NoiseFloorAfterDbfs, 0.001f);
        Assert.AreEqual(
            NoiseFloorTrainingDecision.WarmupCandidate,
            candidateFrame.NoiseFloorTrainingDecision);
        Assert.IsTrue(candidateFrame.IsWarmupUncertain);
        Assert.AreEqual(NoiseBoundaryFrameState.WarmupAmbiguous, candidateFrame.BoundaryState);
        Assert.IsTrue(shadow.Candidate.Segments.Any(segment =>
            segment.Status == AudioSegmentStatus.Ambiguous &&
            segment.Reason == "ambiguous-noise-floor-warmup"));
        AssertShadowIsSafe("loud-first", observations.Count, shadow);
    }

    [TestMethod]
    public void AnalyzeShadowRecordsPhase09HighEnergyConflictAsTrainingEligible()
    {
        using var fixture = TestAudioFixture.CreatePcm16(
            Enumerable.Repeat(0.01f, 19_200).ToArray());
        var track = new PremiereAudioTrack(1, 1, [fixture.Clip("energy-conflict", 0, 10)]);
        var observations = Observations(
            (0.01f, -60f, true),
            (0.01f, -45f, true),
            (0.01f, -45f, true),
            (0.01f, -45f, true),
            (0.01f, -45f, true));

        var shadow = Analyzer().AnalyzeShadow(
            track,
            observations,
            DialogueProcessingPreset.Balanced);

        var conflictFrames = shadow.Baseline.NoiseBoundaryTrace!.Frames
            .Where(frame => frame.RmsDbfs == -45f)
            .ToArray();
        Assert.HasCount(4, conflictFrames);
        Assert.IsTrue(conflictFrames.All(frame =>
            frame.NoiseFloorTrainingDecision == NoiseFloorTrainingDecision.EligibleVadNegative));
        Assert.IsTrue(conflictFrames.All(frame =>
            frame.BoundaryState == NoiseBoundaryFrameState.EnergyConflictAmbiguous));
        Assert.IsTrue(shadow.Candidate.NoiseBoundaryTrace!.Frames.All(frame =>
            frame.NoiseFloorTrainingDecision == NoiseFloorTrainingDecision.WarmupCandidate));
        AssertShadowIsSafe("energy-conflict", observations.Count, shadow);
    }

    [TestMethod]
    public void AnalyzeShadowRecordsMediaGapWithoutTrainingNoiseFloor()
    {
        using var fixture = TestAudioFixture.CreatePcm16(
            Enumerable.Repeat(0.01f, 9_600).ToArray());
        var track = new PremiereAudioTrack(1, 1, [fixture.Clip("media-gap", 0, 5)]);
        var observations = Observations(
            (0.01f, -60f, true),
            (0.01f, -120f, false),
            (0.01f, -60f, true));

        var shadow = Analyzer().AnalyzeShadow(
            track,
            observations,
            DialogueProcessingPreset.Balanced);

        var gap = shadow.Baseline.NoiseBoundaryTrace!.Frames[1];
        Assert.AreEqual(NoiseFloorTrainingDecision.NotEligibleNoMedia, gap.NoiseFloorTrainingDecision);
        Assert.AreEqual(NoiseBoundaryFrameState.NoMedia, gap.BoundaryState);
        Assert.AreEqual(gap.NoiseFloorBeforeDbfs, gap.NoiseFloorAfterDbfs);
        var candidateGap = shadow.Candidate.NoiseBoundaryTrace!.Frames[1];
        Assert.AreEqual(
            NoiseFloorTrainingDecision.NotEligibleNoMedia,
            candidateGap.NoiseFloorTrainingDecision);
        Assert.AreEqual(NoiseBoundaryFrameState.NoMedia, candidateGap.BoundaryState);
        AssertShadowIsSafe("media-gap", observations.Count, shadow);
    }

    [TestMethod]
    public void AnalyzeShadowKeepsDecisionsStableAcrossClipBoundary()
    {
        using var first = TestAudioFixture.CreatePcm16(
            Enumerable.Repeat(0.25f, 9_600).ToArray());
        using var second = TestAudioFixture.CreatePcm16(
            Enumerable.Repeat(0.25f, 9_600).ToArray());
        var track = new PremiereAudioTrack(
            2,
            2,
            [first.Clip("clip-a", 0, 5), second.Clip("clip-b", 5, 10)]);
        var observations = new[]
        {
            Observation(0, 0.01f, -60f, true),
            Observation(6_144, 0.90f, -18f, true),
            Observation(7_680, 0.90f, -18f, true),
            Observation(9_600, 0.90f, -18f, true),
            Observation(11_136, 0.90f, -18f, true)
        };

        var shadow = Analyzer().AnalyzeShadow(
            track,
            observations,
            DialogueProcessingPreset.Balanced);

        Assert.IsTrue(shadow.Baseline.Segments.Any(segment => segment.SourceClipId == "clip-a"));
        Assert.IsTrue(shadow.Baseline.Segments.Any(segment => segment.SourceClipId == "clip-b"));
        AssertShadowIsSafe("clip-boundary", observations.Length, shadow);
    }

    private static void AssertShadowIsSafe(
        string scenario,
        int observationCount,
        NoiseBoundaryShadowAnalysis shadow)
    {
        Assert.IsNotNull(shadow.Baseline.NoiseBoundaryTrace, scenario);
        Assert.IsNotNull(shadow.Candidate.NoiseBoundaryTrace, scenario);
        Assert.AreEqual(
            NoiseBoundaryAnalysisMode.Phase09Baseline,
            shadow.Baseline.NoiseBoundaryTrace.Mode,
            scenario);
        Assert.AreEqual(
            NoiseBoundaryAnalysisMode.NoiseBoundaryCandidate,
            shadow.Candidate.NoiseBoundaryTrace.Mode,
            scenario);
        Assert.AreEqual(
            "phase09-adaptive-p20-v1",
            shadow.Comparison.BaselinePolicyVersion,
            scenario);
        Assert.AreEqual(
            "phase10-background-eligible-p20-v1",
            shadow.Comparison.CandidatePolicyVersion,
            scenario);
        Assert.AreEqual(
            "phase09-vad-single-threshold-v1",
            shadow.Comparison.BaselineBoundaryPolicyVersion,
            scenario);
        Assert.AreEqual(
            "phase10-vad-start050-continue040-v1",
            shadow.Comparison.CandidateBoundaryPolicyVersion,
            scenario);
        Assert.AreEqual(observationCount, shadow.Comparison.ObservationCount, scenario);
        Assert.AreEqual(0, shadow.Comparison.BaselineEnabledCandidateDisabledCount, scenario);
        Assert.IsTrue(shadow.Baseline.NoiseBoundaryTrace.Frames.All(frame =>
            float.IsFinite(frame.NoiseFloorBeforeDbfs) &&
            float.IsFinite(frame.NoiseFloorAfterDbfs) &&
            frame.NoiseFloorBeforeDbfs is >= AudioMath.SilenceDbfs and <= 0 &&
            frame.NoiseFloorAfterDbfs is >= AudioMath.SilenceDbfs and <= 0), scenario);
        Assert.IsTrue(shadow.Candidate.NoiseBoundaryTrace.Frames.All(frame =>
            float.IsFinite(frame.NoiseFloorBeforeDbfs) &&
            float.IsFinite(frame.NoiseFloorAfterDbfs) &&
            frame.NoiseFloorBeforeDbfs is >= AudioMath.SilenceDbfs and <= 0 &&
            frame.NoiseFloorAfterDbfs is >= AudioMath.SilenceDbfs and <= 0), scenario);
    }

    private static TrackDialogueAnalyzer Analyzer() =>
        new(new TimelinePcmAccessor(new PcmWaveSampleReader()));

    private static IReadOnlyList<AudioFrameObservation> Observations(
        params (float Vad, float Rms, bool ContainsMedia)[] values) =>
        values.Select((value, index) =>
            Observation(index * ObservationSamples, value.Vad, value.Rms, value.ContainsMedia))
        .ToArray();

    private static AudioFrameObservation Observation(
        long start,
        float probability,
        float rms,
        bool containsMedia) => new(
            start,
            start + ObservationSamples,
            probability,
            rms,
            rms,
            containsMedia);
}
