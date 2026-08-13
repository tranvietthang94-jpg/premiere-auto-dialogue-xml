using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Audio.Pcm;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.ProjectModel;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class DirectionalBleedCalibratorTests
{
    [TestMethod]
    public void BuildLearnsStableDirectionalFingerprintFromIndependentPhrases()
    {
        using var corpus = CalibrationCorpus.Create(
        [
            new(3, 0.08f),
            new(4, 0.075f),
            new(4, 0.085f),
            new(5, 0.08f)
        ]);
        var originalTargetSegments = corpus.TargetAnalysis.Segments.ToArray();

        var shadow = Build(corpus);

        Assert.AreEqual(
            DirectionalBleedCalibrationPolicy.CandidateVersion,
            shadow.Policy.Version);
        Assert.AreEqual(3, shadow.Policy.MinimumAnchorCount);
        Assert.AreEqual(64, shadow.Policy.MaximumRetainedAnchorCount);
        Assert.AreEqual(16, shadow.Policy.MaximumAnchorSampleCount);
        var fingerprint = Pair(shadow);
        Assert.AreEqual(DirectionalBleedCalibrationStatus.Stable, fingerprint.Status);
        Assert.AreEqual("stable-directional-fingerprint", fingerprint.Reason);
        Assert.AreEqual(4, fingerprint.SourcePhraseCount);
        Assert.AreEqual(4, fingerprint.AcceptedAnchorCount);
        Assert.AreEqual(4, fingerprint.RetainedAnchorCount);
        Assert.AreEqual(4, fingerprint.ConsistentAnchorCount);
        Assert.AreEqual(0, fingerprint.OutlierAnchorCount);
        Assert.AreEqual(-4f, fingerprint.MedianLagMilliseconds);
        Assert.IsLessThanOrEqualTo(2f, fingerprint.LagSpreadMilliseconds!.Value);
        Assert.IsLessThanOrEqualTo(3f, fingerprint.AttenuationSpreadDb!.Value);
        Assert.AreEqual(64, fingerprint.EvidenceSha256.Length);
        Assert.HasCount(4, fingerprint.AnchorSamples);
        Assert.AreEqual(0, fingerprint.Rejections.Total);
        CollectionAssert.AreEqual(originalTargetSegments, corpus.TargetAnalysis.Segments.ToArray());

        var reverse = shadow.Fingerprints.Single(item =>
            item.SourceTrackIndex == 1 && item.TargetTrackIndex == 2);
        Assert.AreEqual(DirectionalBleedCalibrationStatus.InsufficientSupport, reverse.Status);
        Assert.AreEqual(0, reverse.AcceptedAnchorCount);
    }

    [TestMethod]
    public void BuildRejectsRobustOutlierButKeepsStableFingerprint()
    {
        using var corpus = CalibrationCorpus.Create(
        [
            new(4, 0.08f),
            new(4, 0.08f),
            new(4, 0.08f),
            new(10, 0.16f)
        ]);

        var fingerprint = Pair(Build(corpus));

        Assert.AreEqual(DirectionalBleedCalibrationStatus.Stable, fingerprint.Status);
        Assert.AreEqual(4, fingerprint.AcceptedAnchorCount);
        Assert.AreEqual(3, fingerprint.ConsistentAnchorCount);
        Assert.AreEqual(1, fingerprint.OutlierAnchorCount);
        Assert.AreEqual(-4f, fingerprint.MedianLagMilliseconds);
        Assert.AreEqual(0f, fingerprint.LagSpreadMilliseconds);
    }

    [TestMethod]
    public void BuildMarksTwoCompetingFingerprintsUnstable()
    {
        using var corpus = CalibrationCorpus.Create(
        [
            new(0, 0.08f),
            new(0, 0.08f),
            new(10, 0.08f),
            new(10, 0.08f)
        ]);

        var fingerprint = Pair(Build(corpus));

        Assert.AreEqual(
            DirectionalBleedCalibrationStatus.UnstableFingerprint,
            fingerprint.Status);
        Assert.AreEqual("insufficient-consistent-anchors", fingerprint.Reason);
        Assert.AreEqual(4, fingerprint.AcceptedAnchorCount);
        Assert.AreEqual(0, fingerprint.ConsistentAnchorCount);
        Assert.AreEqual(4, fingerprint.OutlierAnchorCount);
    }

    [TestMethod]
    public void BuildAppliesLeaveOneRegionOutBeforeMinimumSupport()
    {
        using var corpus = CalibrationCorpus.Create(
        [
            new(4, 0.08f),
            new(4, 0.08f),
            new(4, 0.08f)
        ]);
        var complete = Pair(Build(corpus));
        var excludedRange = corpus.AnchorRanges[1];
        var exclusion = new DirectionalBleedCalibrationExclusion(
            corpus.TargetAnalysis.TrackIndex,
            excludedRange.StartSample,
            excludedRange.EndSample);

        var leaveOneOut = Pair(Build(corpus, exclusion));

        Assert.AreEqual(DirectionalBleedCalibrationStatus.Stable, complete.Status);
        Assert.AreEqual(3, complete.AcceptedAnchorCount);
        Assert.AreEqual(
            DirectionalBleedCalibrationStatus.InsufficientSupport,
            leaveOneOut.Status);
        Assert.AreEqual("insufficient-eligible-anchors", leaveOneOut.Reason);
        Assert.AreEqual(2, leaveOneOut.AcceptedAnchorCount);
        Assert.AreEqual(1, leaveOneOut.Rejections.ExcludedCandidateRegion);
        Assert.AreNotEqual(complete.EvidenceSha256, leaveOneOut.EvidenceSha256);
    }

    [TestMethod]
    public void BuildRejectsClippingDirectConflictAndIncompleteMedia()
    {
        using var corpus = CalibrationCorpus.Create(
        [
            new(4, 0.08f),
            new(4, 0.08f),
            new(4, 0.08f),
            new(4, 0.08f, IsClipped: true),
            new(4, 0.08f, HasDirectConflict: true),
            new(4, 0.08f, IsPolarityInverted: true),
            new(4, 0.08f, HasConfirmedSourceSpeech: false),
            new(4, 0.08f, HasMatchingBaselineBleed: false),
            new(4, 0.30f),
            new(4, 0.08f, IsIndependentCopy: true),
            new(4, 0.08f)
        ], sourceClipEndFrame: 124);

        var fingerprint = Pair(Build(corpus));

        Assert.AreEqual(DirectionalBleedCalibrationStatus.Stable, fingerprint.Status);
        Assert.AreEqual(3, fingerprint.AcceptedAnchorCount);
        Assert.AreEqual(1, fingerprint.Rejections.Clipped);
        Assert.AreEqual(1, fingerprint.Rejections.ConflictingResidual);
        Assert.AreEqual(1, fingerprint.Rejections.PolarityInverted);
        Assert.AreEqual(1, fingerprint.Rejections.NoConfirmedSourceSpeech);
        Assert.AreEqual(1, fingerprint.Rejections.NoMatchingBaselineBleed);
        Assert.AreEqual(1, fingerprint.Rejections.BelowAdvantage);
        Assert.AreEqual(1, fingerprint.Rejections.BelowCorrelation);
        Assert.AreEqual(1, fingerprint.Rejections.IncompleteMedia);
        Assert.AreEqual(8, fingerprint.Rejections.Total);
    }

    [TestMethod]
    public void BuildIsDeterministicAcrossAnalysisOrder()
    {
        using var corpus = CalibrationCorpus.Create(
        [
            new(3, 0.08f),
            new(4, 0.08f),
            new(5, 0.08f),
            new(4, 0.08f)
        ]);

        var first = Pair(Build(corpus));
        var reversed = new DirectionalBleedCalibrator(
                new TimelinePcmAccessor(new PcmWaveSampleReader()))
            .Build(
                corpus.Sequence,
                corpus.Analyses.Reverse().ToArray(),
                DialogueProcessingPreset.Balanced);
        var second = Pair(reversed);

        Assert.AreEqual(first.Status, second.Status);
        Assert.AreEqual(first.EvidenceSha256, second.EvidenceSha256);
        Assert.AreEqual(first.MedianLagMilliseconds, second.MedianLagMilliseconds);
        Assert.AreEqual(first.MedianAttenuationDb, second.MedianAttenuationDb);
        CollectionAssert.AreEqual(first.AnchorSamples.ToArray(), second.AnchorSamples.ToArray());
    }

    [TestMethod]
    public void BuildBoundsRetainedAnchorsAndAuditSamples()
    {
        var anchors = Enumerable.Range(0, 65)
            .Select(_ => new CalibrationAnchorSpec(0, 0.08f))
            .ToArray();
        using var corpus = CalibrationCorpus.Create(anchors);
        var fastPreset = DialogueProcessingPreset.Balanced with
        {
            BleedMaximumLagMilliseconds = 1
        };

        var fingerprint = Pair(Build(corpus, preset: fastPreset));

        Assert.AreEqual(DirectionalBleedCalibrationStatus.Stable, fingerprint.Status);
        Assert.AreEqual(65, fingerprint.AcceptedAnchorCount);
        Assert.AreEqual(64, fingerprint.RetainedAnchorCount);
        Assert.AreEqual(64, fingerprint.ConsistentAnchorCount);
        Assert.HasCount(16, fingerprint.AnchorSamples);
        Assert.AreEqual(64, fingerprint.EvidenceSha256.Length);
    }

    [TestMethod]
    public void BuildHonorsCancellationBeforeReadingMedia()
    {
        using var corpus = CalibrationCorpus.Create(
        [
            new(4, 0.08f),
            new(4, 0.08f),
            new(4, 0.08f)
        ]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            new DirectionalBleedCalibrator(
                    new TimelinePcmAccessor(new PcmWaveSampleReader()))
                .Build(
                    corpus.Sequence,
                    corpus.Analyses,
                    DialogueProcessingPreset.Balanced,
                    cancellationToken: cancellation.Token));
    }

    private static DirectionalBleedCalibrationShadow Build(
        CalibrationCorpus corpus,
        DirectionalBleedCalibrationExclusion? exclusion = null,
        DialogueProcessingPreset? preset = null) =>
        new DirectionalBleedCalibrator(new TimelinePcmAccessor(new PcmWaveSampleReader()))
            .Build(
                corpus.Sequence,
                corpus.Analyses,
                preset ?? DialogueProcessingPreset.Balanced,
                exclusion);

    private static DirectionalBleedFingerprint Pair(
        DirectionalBleedCalibrationShadow shadow) =>
        shadow.Fingerprints.Single(fingerprint =>
            fingerprint.SourceTrackIndex == 2 && fingerprint.TargetTrackIndex == 1);
}

internal sealed class CalibrationCorpus : IDisposable
{
    private const int SamplesPerFrame = 1_920;
    private const int FramesPerAnchorSlot = 12;
    private const int AnchorDurationFrames = 8;
    private readonly TestAudioFixture _targetFixture;
    private readonly TestAudioFixture _sourceFixture;

    private CalibrationCorpus(
        TestAudioFixture targetFixture,
        TestAudioFixture sourceFixture,
        PremiereSequence sequence,
        IReadOnlyList<TrackAudioAnalysis> analyses,
        IReadOnlyList<CalibrationAnchorRange> anchorRanges)
    {
        _targetFixture = targetFixture;
        _sourceFixture = sourceFixture;
        Sequence = sequence;
        Analyses = analyses;
        AnchorRanges = anchorRanges;
    }

    public PremiereSequence Sequence { get; }

    public IReadOnlyList<TrackAudioAnalysis> Analyses { get; }

    public TrackAudioAnalysis TargetAnalysis =>
        Analyses.Single(analysis => analysis.TrackIndex == 1);

    public IReadOnlyList<CalibrationAnchorRange> AnchorRanges { get; }

    public static CalibrationCorpus Create(
        IReadOnlyList<CalibrationAnchorSpec> anchorSpecs,
        int? sourceClipEndFrame = null)
    {
        ArgumentNullException.ThrowIfNull(anchorSpecs);
        if (anchorSpecs.Count == 0)
        {
            throw new ArgumentException("Calibration corpus phải có anchor.", nameof(anchorSpecs));
        }

        var durationFrames = checked(anchorSpecs.Count * FramesPerAnchorSlot);
        var sampleCount = checked(durationFrames * SamplesPerFrame);
        var sourceSamples = BroadbandVoice(sampleCount, seed: 0x11B00001u);
        var independentSamples = BroadbandVoice(sampleCount, seed: 0x11B00002u);
        var targetSamples = new float[sampleCount];
        var ranges = new List<CalibrationAnchorRange>(anchorSpecs.Count);
        for (var index = 0; index < anchorSpecs.Count; index++)
        {
            var spec = anchorSpecs[index];
            var startSample = checked(index * FramesPerAnchorSlot * SamplesPerFrame);
            var endSample = checked(startSample + (AnchorDurationFrames * SamplesPerFrame));
            ranges.Add(new(startSample, endSample));
            if (spec.IsClipped)
            {
                for (var sample = startSample; sample < endSample; sample++)
                {
                    sourceSamples[sample] = sample % 2 == 0 ? 1f : -1f;
                }
            }

            var delaySamples = checked(spec.DelayMilliseconds * 48);
            for (var sample = startSample; sample < endSample; sample++)
            {
                var sourceIndex = sample - delaySamples;
                var transferSource = spec.IsIndependentCopy
                    ? independentSamples
                    : sourceSamples;
                var bleed = sourceIndex >= startSample && sourceIndex < endSample
                    ? transferSource[sourceIndex] * spec.Scale
                    : 0f;
                if (spec.IsPolarityInverted)
                {
                    bleed = -bleed;
                }

                var direct = spec.HasDirectConflict ? independentSamples[sample] * 0.04f : 0f;
                targetSamples[sample] = bleed + direct;
            }
        }

        var targetFixture = TestAudioFixture.CreatePcm16(targetSamples);
        var sourceFixture = TestAudioFixture.CreatePcm16(sourceSamples);
        var targetClip = targetFixture.Clip("calibration-target", 0, durationFrames);
        var sourceClip = sourceFixture.Clip(
            "calibration-source",
            0,
            sourceClipEndFrame ?? durationFrames);
        var targetTrack = new PremiereAudioTrack(1, 1, [targetClip]);
        var sourceTrack = new PremiereAudioTrack(2, 2, [sourceClip]);
        var targetSegments = new List<AnalyzedAudioSegment>(anchorSpecs.Count);
        var sourceSegments = new List<AnalyzedAudioSegment>(anchorSpecs.Count);
        var sourcePhrases = new List<DialoguePhrase>(anchorSpecs.Count);
        for (var index = 0; index < anchorSpecs.Count; index++)
        {
            var spec = anchorSpecs[index];
            var range = ranges[index];
            var phraseId = $"T02-P{index + 1:000000}";
            sourcePhrases.Add(new(
                phraseId,
                2,
                range.StartSample,
                range.EndSample,
                range.StartSample,
                range.EndSample,
                -8f,
                2f,
                2f,
                false));
            sourceSegments.Add(new(
                2,
                sourceClip.Id,
                range.StartSample,
                range.EndSample,
                range.StartSample,
                range.EndSample,
                spec.HasConfirmedSourceSpeech
                    ? AudioSegmentStatus.Speech
                    : AudioSegmentStatus.Ambiguous,
                spec.HasConfirmedSourceSpeech ? phraseId : null,
                spec.HasConfirmedSourceSpeech ? 2f : 0f,
                spec.HasConfirmedSourceSpeech
                    ? "confirmed-direct-speech"
                    : "ambiguous-independent"));
            targetSegments.Add(new(
                1,
                targetClip.Id,
                range.StartSample,
                range.EndSample,
                range.StartSample,
                range.EndSample,
                spec.HasMatchingBaselineBleed
                    ? AudioSegmentStatus.Bleed
                    : AudioSegmentStatus.Ambiguous,
                null,
                null,
                spec.HasMatchingBaselineBleed
                    ? "confirmed-bleed-from-track-2"
                    : "ambiguous-independent",
                spec.HasMatchingBaselineBleed
                    ? new BleedEvidence(2, 20f, 1f, spec.DelayMilliseconds, -34f, -15f, -60f)
                    : null));
        }

        var targetAnalysis = new TrackAudioAnalysis(
            1,
            0,
            [],
            targetSegments,
            LearnedDirectVoiceRmsDbfs: -15f);
        var sourceAnalysis = new TrackAudioAnalysis(
            2,
            0,
            sourcePhrases,
            sourceSegments,
            LearnedDirectVoiceRmsDbfs: -8f);
        var sequence = new PremiereSequence(
            "phase11-calibration",
            "phase11-calibration-uuid",
            "phase11-calibration",
            25,
            durationFrames,
            2,
            48_000,
            [targetTrack, sourceTrack]);
        return new(
            targetFixture,
            sourceFixture,
            sequence,
            [targetAnalysis, sourceAnalysis],
            ranges);
    }

    public void Dispose()
    {
        _sourceFixture.Dispose();
        _targetFixture.Dispose();
    }

    private static float[] BroadbandVoice(int sampleCount, uint seed)
    {
        var result = new float[sampleCount];
        var state = seed;
        for (var index = 0; index < result.Length; index++)
        {
            state = unchecked((state * 1_664_525u) + 1_013_904_223u);
            var noise = ((state >> 8) / 8_388_607.5f) - 1f;
            var envelope = 0.70f + (0.30f * MathF.Sin(
                2f * MathF.PI * 3.1f * index / 48_000));
            result[index] = noise * envelope * 0.42f;
        }

        return result;
    }
}

internal sealed record CalibrationAnchorSpec(
    int DelayMilliseconds,
    float Scale,
    bool IsClipped = false,
    bool HasDirectConflict = false,
    bool IsPolarityInverted = false,
    bool IsIndependentCopy = false,
    bool HasConfirmedSourceSpeech = true,
    bool HasMatchingBaselineBleed = true);

internal readonly record struct CalibrationAnchorRange(long StartSample, long EndSample);
