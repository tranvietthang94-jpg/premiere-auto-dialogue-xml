using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.ProjectModel;
using PremiereAutoDialogueXml.Output.Audit;
using PremiereAutoDialogueXml.Validation;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class BoundaryDiscontinuityScannerTests
{
    [TestMethod]
    public void ScanRanksHighAmplitudeMuteBoundaryAboveNearZeroBoundary()
    {
        using var fixture = TestAudioFixture.CreatePcm16(
        [
            0.5f, 0.5f, 0.5f, 0.5f,
            0.001f, 0.001f, 0.001f, 0.001f
        ]);
        var highClip = fixture.Clip("high", 0, 2, 0, 4);
        var lowClip = fixture.Clip("low", 2, 4, 4, 8);
        var project = Project([highClip, lowClip]);
        var fragments = new[]
        {
            Fragment(highClip, "high-enabled", 0, 1, 0, 2, AudioSegmentStatus.Ambiguous),
            Fragment(highClip, "high-disabled", 1, 2, 2, 4, AudioSegmentStatus.Noise),
            Fragment(lowClip, "low-enabled", 2, 3, 4, 6, AudioSegmentStatus.Ambiguous),
            Fragment(lowClip, "low-disabled", 3, 4, 6, 8, AudioSegmentStatus.Noise)
        };

        var report = new BoundaryDiscontinuityScanner().Scan(Audit(fragments), project);

        Assert.AreEqual(2, report.SourceClipCount);
        Assert.AreEqual(2, report.AppCreatedBoundaryCount);
        Assert.AreEqual(2, report.TransitionCount);
        Assert.AreEqual(2, report.EnabledToDisabledCount);
        Assert.AreEqual(1, report.ScreeningCandidateCount);
        Assert.HasCount(2, report.TopSamples);
        Assert.AreEqual("high", report.TopSamples[0].SourceClipId);
        Assert.IsGreaterThan(-7, report.TopSamples[0].ExcessStepDbfs);
        Assert.IsLessThan(-55, report.TopSamples[1].ExcessStepDbfs);
    }

    [TestMethod]
    public void ScanReportsMuteEnableAndGainTransitionsSeparately()
    {
        using var fixture = TestAudioFixture.CreatePcm16(Enumerable.Repeat(0.25f, 10).ToArray());
        var clip = fixture.Clip("clip", 0, 5, 0, 10);
        var project = Project([clip]);
        var fragments = new[]
        {
            Fragment(clip, "enabled-left", 0, 1, 0, 2, AudioSegmentStatus.Ambiguous),
            Fragment(clip, "disabled", 1, 2, 2, 4, AudioSegmentStatus.Noise),
            Fragment(clip, "enabled-right", 2, 3, 4, 6, AudioSegmentStatus.Speech, 0),
            Fragment(clip, "gain", 3, 4, 6, 8, AudioSegmentStatus.Speech, 6.020599913),
            Fragment(clip, "gain-continuous", 4, 5, 8, 10, AudioSegmentStatus.Speech, 6.020599913)
        };

        var report = new BoundaryDiscontinuityScanner().Scan(Audit(fragments), project);

        Assert.AreEqual(4, report.AppCreatedBoundaryCount);
        Assert.AreEqual(1, report.IgnoredContinuousBoundaryCount);
        Assert.AreEqual(3, report.TransitionCount);
        Assert.AreEqual(1, report.EnabledToDisabledCount);
        Assert.AreEqual(1, report.DisabledToEnabledCount);
        Assert.AreEqual(1, report.GainChangeCount);
        Assert.IsTrue(report.TopSamples.Any(sample => sample.Kind == BoundaryTransitionKind.GainChange));
    }

    [TestMethod]
    public void ScanIsDeterministicAndDoesNotTreatSourceClipCutAsAppBoundary()
    {
        using var fixture = TestAudioFixture.CreatePcm16(Enumerable.Repeat(0.2f, 8).ToArray());
        var first = fixture.Clip("first", 0, 2, 0, 4);
        var second = fixture.Clip("second", 2, 4, 4, 8);
        var project = Project([first, second]);
        var fragments = new[]
        {
            Fragment(first, "first-only", 0, 2, 0, 4, AudioSegmentStatus.Ambiguous),
            Fragment(second, "second-only", 2, 4, 4, 8, AudioSegmentStatus.Noise)
        };
        var audit = Audit(fragments);
        var scanner = new BoundaryDiscontinuityScanner();

        var firstReport = scanner.Scan(audit, project);
        var secondReport = scanner.Scan(audit, project);

        Assert.AreEqual(0, firstReport.AppCreatedBoundaryCount);
        Assert.AreEqual(0, firstReport.TransitionCount);
        Assert.AreEqual(firstReport.TransitionStreamSha256, secondReport.TransitionStreamSha256);
        Assert.AreEqual(
            "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
            firstReport.TransitionStreamSha256);
    }

    [TestMethod]
    public void ScanRejectsSourceXmlHashMismatch()
    {
        using var fixture = TestAudioFixture.CreatePcm16(Enumerable.Repeat(0.2f, 4).ToArray());
        var clip = fixture.Clip("clip", 0, 2, 0, 4);
        var project = Project([clip]);
        var audit = Audit(
        [
            Fragment(clip, "left", 0, 1, 0, 2, AudioSegmentStatus.Ambiguous),
            Fragment(clip, "right", 1, 2, 2, 4, AudioSegmentStatus.Noise)
        ]) with
        {
            SourceXmlSha256 = "OTHER"
        };

        var exception = Assert.Throws<InvalidDataException>(
            () => new BoundaryDiscontinuityScanner().Scan(audit, project));

        StringAssert.Contains(exception.Message, "SHA-256 XML nguồn");
    }

    private static PremiereProject Project(IReadOnlyList<PremiereAudioClip> clips) => new(
        "source.xml",
        "SOURCE-HASH",
        new(
            "sequence",
            "uuid",
            "fixture",
            25,
            clips.Max(clip => clip.TimelineEndFrame),
            2,
            48_000,
            [new PremiereAudioTrack(1, 1, clips)]));

    private static OutputAudit Audit(IReadOnlyList<FragmentAudit> fragments) => new(
        "1.9",
        "phase14-synthetic",
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
            1_920,
            SequenceTimingAudit.ExactFrameGridPolicy)
    };

    private static FragmentAudit Fragment(
        PremiereAudioClip clip,
        string id,
        long timelineStartFrame,
        long timelineEndFrame,
        long sourceStartSample,
        long sourceEndSample,
        AudioSegmentStatus status,
        double? gainDb = null) => new(
            id,
            1,
            clip.Id,
            clip.SourceFileId,
            Path.GetFileName(clip.SourceMedia.LocalPath),
            timelineStartFrame,
            timelineEndFrame,
            timelineStartFrame - clip.TimelineStartFrame,
            timelineEndFrame - clip.TimelineStartFrame,
            0,
            0,
            sourceStartSample,
            sourceEndSample,
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
