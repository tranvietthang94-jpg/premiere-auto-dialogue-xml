using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Output.Audit;
using PremiereAutoDialogueXml.Validation;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class PremierePcmBoundaryScannerTests
{
    private const int SamplesPerFrame = 1_920;

    [TestMethod]
    public void ScanMeasuresRenderedMuteEnableAndGainTransients()
    {
        var samples = new float[4 * SamplesPerFrame];
        Array.Fill(samples, 0.5f, SamplesPerFrame, SamplesPerFrame);
        Array.Fill(samples, 0.25f, 2 * SamplesPerFrame, SamplesPerFrame);
        using var fixture = TestAudioFixture.CreatePcm16(samples);
        var fragments = new[]
        {
            Fragment("clip", "noise", 0, 1, AudioSegmentStatus.Noise),
            Fragment("clip", "enabled", 1, 2, AudioSegmentStatus.Ambiguous, 0),
            Fragment("clip", "gain", 2, 3, AudioSegmentStatus.Speech, 6.020599913),
            Fragment("clip", "muted", 3, 4, AudioSegmentStatus.Noise)
        };

        var report = new PremierePcmBoundaryScanner().Scan(Audit(fragments), fixture.Wave, 1);

        Assert.AreEqual(3, report.AppCreatedBoundaryCount);
        Assert.AreEqual(3, report.TransitionCount);
        Assert.AreEqual(1, report.EnabledToDisabledCount);
        Assert.AreEqual(1, report.DisabledToEnabledCount);
        Assert.AreEqual(1, report.GainChangeCount);
        Assert.AreEqual(3, report.TransientScreeningCandidateCount);
        Assert.HasCount(3, report.TopSamples);
        Assert.AreEqual(BoundaryTransitionKind.DisabledToEnabled, report.TopSamples[0].Kind);
        Assert.AreEqual("00:00:00:01", report.TopSamples[0].BoundaryTimecode);
        Assert.IsGreaterThan(-7, report.TopSamples[0].ActualStepDbfs);
        Assert.IsGreaterThan(100, report.TopSamples[0].StepAboveLocalP99Db);
    }

    [TestMethod]
    public void ScanIgnoresBoundaryBetweenSourceClipsAndIsDeterministic()
    {
        using var fixture = TestAudioFixture.CreatePcm16(new float[2 * SamplesPerFrame]);
        var audit = Audit(
        [
            Fragment("first", "first", 0, 1, AudioSegmentStatus.Ambiguous),
            Fragment("second", "second", 1, 2, AudioSegmentStatus.Noise)
        ]);
        var scanner = new PremierePcmBoundaryScanner();

        var first = scanner.Scan(audit, fixture.Wave, 1);
        var second = scanner.Scan(audit, fixture.Wave, 1);

        Assert.AreEqual(2, first.SourceClipCount);
        Assert.AreEqual(0, first.AppCreatedBoundaryCount);
        Assert.AreEqual(0, first.TransitionCount);
        Assert.AreEqual(first.TransitionStreamSha256, second.TransitionStreamSha256);
        Assert.AreEqual(
            "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
            first.TransitionStreamSha256);
    }

    [TestMethod]
    public void ScanReturnsRequestedBoundaryEvenWhenItIsOutsideBoundedTopSamples()
    {
        var samples = new float[4 * SamplesPerFrame];
        Array.Fill(samples, 0.5f, SamplesPerFrame, SamplesPerFrame);
        Array.Fill(samples, 0.25f, 2 * SamplesPerFrame, SamplesPerFrame);
        using var fixture = TestAudioFixture.CreatePcm16(samples);
        var fragments = new[]
        {
            Fragment("clip", "noise", 0, 1, AudioSegmentStatus.Noise),
            Fragment("clip", "enabled", 1, 2, AudioSegmentStatus.Ambiguous, 0),
            Fragment("clip", "gain", 2, 3, AudioSegmentStatus.Speech, 6.020599913),
            Fragment("clip", "muted", 3, 4, AudioSegmentStatus.Noise)
        };

        var report = new PremierePcmBoundaryScanner().Scan(
            Audit(fragments),
            fixture.Wave,
            1,
            maximumCapturedSamples: 1,
            focusedBoundaryFrame: 2);

        Assert.AreEqual("1.1", report.SchemaVersion);
        Assert.HasCount(1, report.TopSamples);
        Assert.AreNotEqual(2, report.TopSamples[0].BoundaryFrame);
        Assert.AreEqual(2, report.FocusedBoundaryFrame);
        Assert.IsNotNull(report.FocusedSample);
        Assert.AreEqual(2, report.FocusedSample.BoundaryFrame);
        Assert.AreEqual(BoundaryTransitionKind.GainChange, report.FocusedSample.Kind);
    }

    [TestMethod]
    public void ScanRejectsPcmThatEndsBeforeAuditTimeline()
    {
        using var fixture = TestAudioFixture.CreatePcm16(new float[SamplesPerFrame]);
        var audit = Audit(
        [
            Fragment("clip", "left", 0, 1, AudioSegmentStatus.Ambiguous),
            Fragment("clip", "right", 1, 2, AudioSegmentStatus.Noise)
        ]);

        var exception = Assert.Throws<InvalidDataException>(
            () => new PremierePcmBoundaryScanner().Scan(audit, fixture.Wave, 1));

        StringAssert.Contains(exception.Message, "kết thúc trước timeline audit");
    }

    private static OutputAudit Audit(IReadOnlyList<FragmentAudit> fragments) => new(
        "1.9",
        "phase14-pcm-synthetic",
        DateTimeOffset.UnixEpoch,
        "source.xml",
        "SOURCE-HASH",
        "output.xml",
        "OUTPUT-HASH",
        "sequence",
        "sequence-output",
        "uuid-output",
        "fixture - AUTO AUDIO",
        new("6.2.1", "MODEL-HASH"),
        PresetAudit.From(DialogueProcessingPreset.Balanced),
        fragments,
        [])
    {
        SequenceTiming = new(
            25,
            false,
            48_000,
            SamplesPerFrame,
            SequenceTimingAudit.ExactFrameGridPolicy)
    };

    private static FragmentAudit Fragment(
        string sourceClipId,
        string id,
        long timelineStartFrame,
        long timelineEndFrame,
        AudioSegmentStatus status,
        double? gainDb = null) => new(
            id,
            1,
            sourceClipId,
            $"file-{sourceClipId}",
            $"{sourceClipId}.wav",
            timelineStartFrame,
            timelineEndFrame,
            timelineStartFrame,
            timelineEndFrame,
            0,
            0,
            timelineStartFrame * SamplesPerFrame,
            timelineEndFrame * SamplesPerFrame,
            status,
            status is AudioSegmentStatus.Speech or AudioSegmentStatus.Ambiguous,
            status == AudioSegmentStatus.Speech ? $"phrase-{id}" : null,
            null,
            null,
            gainDb,
            false,
            $"reason-{id}",
            null);
}
