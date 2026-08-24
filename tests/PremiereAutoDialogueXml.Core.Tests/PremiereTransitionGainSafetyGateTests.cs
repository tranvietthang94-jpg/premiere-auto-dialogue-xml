using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.ProjectModel;
using PremiereAutoDialogueXml.Output.Audit;
using PremiereAutoDialogueXml.Validation;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class PremiereTransitionGainSafetyGateTests
{
    private const int SamplesPerFrame = 1_920;

    [TestMethod]
    public void EvaluateRejectsBoundaryWhenPhrasePeakExistsOnlyInsideGuardFrame()
    {
        var samples = Enumerable.Repeat(0.05f, SamplesPerFrame * 4).ToArray();
        samples[SamplesPerFrame * 2] = 0.9f;
        samples[SamplesPerFrame * 3] = 0.5f;
        using var fixture = TestAudioFixture.CreatePcm16(samples);
        var context = Context(fixture, measuredPeakDbfs: Dbfs(0.9), phraseId: "phrase-risk");

        var report = new PremiereTransitionGainSafetyGate().Evaluate(
            context.Scan,
            context.Audit,
            context.Project);

        Assert.AreEqual(1, report.RejectedExpectedPeakLossCount);
        var decision = report.Decisions.Single();
        Assert.AreEqual(
            PremiereTransitionGainSafetyDecisionKind.RejectedExpectedPeakLoss,
            decision.Decision);
        var evidence = decision.PhraseEvidence.Single();
        Assert.AreEqual(SamplesPerFrame, evidence.ExcludedSampleCount);
        Assert.IsLessThan(-5, evidence.RetainedPeakDeltaDb!.Value);
    }

    [TestMethod]
    public void EvaluateKeepsBoundaryWhenEquivalentPeakRemainsOutsideGuardFrame()
    {
        var samples = Enumerable.Repeat(0.05f, SamplesPerFrame * 4).ToArray();
        samples[SamplesPerFrame * 2] = 0.9f;
        samples[SamplesPerFrame * 3] = 0.895f;
        using var fixture = TestAudioFixture.CreatePcm16(samples);
        var context = Context(fixture, measuredPeakDbfs: Dbfs(0.9), phraseId: "phrase-safe");

        var first = new PremiereTransitionGainSafetyGate().Evaluate(
            context.Scan,
            context.Audit,
            context.Project);
        var second = new PremiereTransitionGainSafetyGate().Evaluate(
            context.Scan,
            context.Audit,
            context.Project);

        Assert.AreEqual(1, first.EligibleCount);
        Assert.AreEqual(
            PremiereTransitionGainSafetyDecisionKind.Eligible,
            first.Decisions.Single().Decision);
        Assert.AreEqual(first.DecisionStreamSha256, second.DecisionStreamSha256);
        Assert.IsGreaterThanOrEqualTo(-0.1, first.Decisions.Single().PhraseEvidence.Single().RetainedPeakDeltaDb!.Value);
    }

    [TestMethod]
    public void EvaluateRejectsMissingPhraseAndSourcePeakMismatch()
    {
        var samples = Enumerable.Repeat(0.05f, SamplesPerFrame * 4).ToArray();
        samples[SamplesPerFrame * 2] = 0.9f;
        samples[SamplesPerFrame * 3] = 0.895f;
        using var fixture = TestAudioFixture.CreatePcm16(samples);
        var missing = Context(fixture, measuredPeakDbfs: Dbfs(0.9), phraseId: null);
        var mismatch = Context(fixture, measuredPeakDbfs: -20, phraseId: "phrase-stale");
        var gate = new PremiereTransitionGainSafetyGate();

        var missingReport = gate.Evaluate(missing.Scan, missing.Audit, missing.Project);
        var mismatchReport = gate.Evaluate(mismatch.Scan, mismatch.Audit, mismatch.Project);

        Assert.AreEqual(
            PremiereTransitionGainSafetyDecisionKind.RejectedMissingPhraseEvidence,
            missingReport.Decisions.Single().Decision);
        Assert.AreEqual(
            PremiereTransitionGainSafetyDecisionKind.RejectedSourcePeakMismatch,
            mismatchReport.Decisions.Single().Decision);
    }

    [TestMethod]
    public void EvaluateGainChangeChecksBothEnabledPhraseSides()
    {
        var samples = Enumerable.Repeat(0.05f, SamplesPerFrame * 4).ToArray();
        samples[0] = 0.9f;
        samples[SamplesPerFrame * 2] = 0.8f;
        samples[SamplesPerFrame * 3] = 0.7f;
        using var fixture = TestAudioFixture.CreatePcm16(samples);
        var clip = fixture.Clip("source-gain", 0, 4, 0, SamplesPerFrame * 4);
        var project = Project(clip);
        var audit = Audit(
            project,
            [
                Fragment(
                    "left",
                    clip,
                    0,
                    2,
                    0,
                    SamplesPerFrame * 2,
                    AudioSegmentStatus.Speech,
                    true,
                    "phrase-left",
                    Dbfs(0.9),
                    0),
                Fragment(
                    "right",
                    clip,
                    2,
                    4,
                    SamplesPerFrame * 2,
                    SamplesPerFrame * 4,
                    AudioSegmentStatus.Speech,
                    true,
                    "phrase-right",
                    Dbfs(0.8),
                    6)
            ]);
        var scan = Scan(
            clip,
            BoundaryTransitionKind.GainChange,
            AudioSegmentStatus.Speech,
            AudioSegmentStatus.Speech,
            true,
            true,
            0,
            6);

        var report = new PremiereTransitionGainSafetyGate().Evaluate(scan, audit, project);

        var decision = report.Decisions.Single();
        Assert.AreEqual(
            PremiereTransitionGainSafetyDecisionKind.RejectedExpectedPeakLoss,
            decision.Decision);
        Assert.HasCount(2, decision.PhraseEvidence);
        Assert.AreEqual(
            "retained-peak-within-tolerance",
            decision.PhraseEvidence.Single(item => item.PhraseId == "phrase-left").Reason);
        Assert.AreEqual(
            "retained-peak-loss-exceeds-tolerance",
            decision.PhraseEvidence.Single(item => item.PhraseId == "phrase-right").Reason);
    }

    private static SafetyContext Context(
        TestAudioFixture fixture,
        double measuredPeakDbfs,
        string? phraseId)
    {
        var clip = fixture.Clip("source-1", 0, 4, 0, SamplesPerFrame * 4);
        var project = Project(clip);
        var fragments = new[]
        {
            Fragment(
                "disabled",
                clip,
                0,
                2,
                0,
                SamplesPerFrame * 2,
                AudioSegmentStatus.Noise,
                false,
                null,
                null),
            Fragment(
                "enabled",
                clip,
                2,
                4,
                SamplesPerFrame * 2,
                SamplesPerFrame * 4,
                AudioSegmentStatus.Speech,
                true,
                phraseId,
                measuredPeakDbfs)
        };
        var audit = Audit(project, fragments);
        var scan = Scan(
            clip,
            BoundaryTransitionKind.DisabledToEnabled,
            AudioSegmentStatus.Noise,
            AudioSegmentStatus.Speech,
            false,
            true,
            0,
            0);
        return new(project, audit, scan);
    }

    private static PremiereProject Project(PremiereAudioClip clip) => new(
        "source.xml",
        "SOURCE-HASH",
        new(
            "sequence",
            "uuid",
            "fixture",
            25,
            4,
            2,
            48_000,
            [new PremiereAudioTrack(1, 1, [clip])]));

    private static OutputAudit Audit(
        PremiereProject project,
        IReadOnlyList<FragmentAudit> fragments) => new(
            "1.9",
            "phase16-synthetic",
            DateTimeOffset.UnixEpoch,
            Path.GetFileName(project.SourceXmlPath),
            project.SourceXmlSha256,
            "output.xml",
            "OUTPUT-HASH",
            project.Sequence.Id,
            "sequence-output",
            "uuid-output",
            "fixture - AUTO AUDIO",
            new("6.2.1", "MODEL-HASH"),
            PresetAudit.From(DialogueProcessingPreset.Balanced),
            fragments,
            [])
        {
            SequenceTiming = new(
                project.Sequence.FrameRate,
                false,
                project.Sequence.AudioSampleRate,
                project.Sequence.AudioSampleRate / project.Sequence.FrameRate,
                SequenceTimingAudit.ExactFrameGridPolicy)
        };

    private static BoundaryDiscontinuityScanReport Scan(
        PremiereAudioClip clip,
        BoundaryTransitionKind kind,
        AudioSegmentStatus leftStatus,
        AudioSegmentStatus rightStatus,
        bool leftEnabled,
        bool rightEnabled,
        double leftGainDb,
        double rightGainDb)
    {
        var sample = new BoundaryDiscontinuitySample(
            1,
            clip.Id,
            clip.SourceFileId,
            Path.GetFileName(clip.SourceMedia.LocalPath),
            2,
            kind,
            leftStatus,
            rightStatus,
            leftEnabled,
            rightEnabled,
            leftGainDb,
            rightGainDb,
            SamplesPerFrame * 2 - 1,
            SamplesPerFrame * 2,
            0.05f,
            0.9f,
            0,
            0.9,
            0.9,
            0.85,
            -120,
            Dbfs(0.9),
            Dbfs(0.9),
            Dbfs(0.85),
            true,
            -40,
            39,
            true);
        return new BoundaryDiscontinuityScanReport(
            "1.1",
            BoundaryDiscontinuityScanner.Policy,
            -40,
            1,
            1,
            0,
            1,
            0,
            1,
            0,
            1,
            1,
            Dbfs(0.9),
            Dbfs(0.9),
            Dbfs(0.9),
            39,
            new string('A', 64),
            1,
            [sample]);
    }

    private static FragmentAudit Fragment(
        string id,
        PremiereAudioClip clip,
        long timelineStart,
        long timelineEnd,
        long sourceStart,
        long sourceEnd,
        AudioSegmentStatus status,
        bool enabled,
        string? phraseId,
        double? measuredPeakDbfs,
        double appliedGainDb = 0) => new(
            id,
            1,
            clip.Id,
            clip.SourceFileId,
            Path.GetFileName(clip.SourceMedia.LocalPath),
            timelineStart,
            timelineEnd,
            timelineStart,
            timelineEnd,
            checked(timelineStart * 10_160_640_000),
            checked(timelineEnd * 10_160_640_000),
            sourceStart,
            sourceEnd,
            status,
            enabled,
            phraseId,
            measuredPeakDbfs,
            measuredPeakDbfs is null ? null : -6 - measuredPeakDbfs + 3.0102999566,
            enabled ? appliedGainDb : null,
            false,
            status.ToString(),
            null);

    private static double Dbfs(double amplitude) => 20 * Math.Log10(amplitude);

    private sealed record SafetyContext(
        PremiereProject Project,
        OutputAudit Audit,
        BoundaryDiscontinuityScanReport Scan);
}

[TestClass]
public sealed class PremiereConstantGainSafeBatchPlannerTests
{
    [TestMethod]
    public void PlanSkipsUnsafeAndSecondBoundaryForSamePhrase()
    {
        var safety = Report(
            Decision(1, 10, BoundaryTransitionKind.DisabledToEnabled, "phrase-a", eligible: true, priority: 40),
            Decision(1, 20, BoundaryTransitionKind.EnabledToDisabled, "phrase-a", eligible: true, priority: 35),
            Decision(2, 30, BoundaryTransitionKind.GainChange, "phrase-b", eligible: false, priority: 30),
            Decision(3, 40, BoundaryTransitionKind.EnabledToDisabled, "phrase-c", eligible: true, priority: 25));

        var plan = new PremiereConstantGainSafeBatchPlanner().Plan(safety, 3);

        CollectionAssert.AreEqual(
            new[] { (1, 10L), (3, 40L) },
            plan.Selected.Select(item => (item.TrackIndex, item.BoundaryFrame)).ToArray());
        Assert.AreEqual(
            PremiereConstantGainSafePlanDecisionKind.SkippedPhraseConflict,
            plan.Decisions.Single(item => item.Request.BoundaryFrame == 20).Decision);
        Assert.AreEqual(
            PremiereConstantGainSafePlanDecisionKind.SkippedUnsafe,
            plan.Decisions.Single(item => item.Request.BoundaryFrame == 30).Decision);
    }

    private static PremiereTransitionGainSafetyReport Report(
        params PremiereTransitionGainSafetyDecision[] decisions) => new(
            "1.0",
            PremiereTransitionGainSafetyGate.Policy,
            BoundaryDiscontinuityScanner.Policy,
            1,
            0.1,
            decisions.Length,
            decisions.Length,
            0,
            decisions.Count(item => item.Decision == PremiereTransitionGainSafetyDecisionKind.Eligible),
            decisions.Count(item => item.Decision != PremiereTransitionGainSafetyDecisionKind.Eligible),
            decisions.Count(item => item.Decision == PremiereTransitionGainSafetyDecisionKind.RejectedMissingPhraseEvidence),
            0,
            0,
            decisions.Count(item => item.Decision == PremiereTransitionGainSafetyDecisionKind.RejectedExpectedPeakLoss),
            new string('A', 64),
            decisions);

    private static PremiereTransitionGainSafetyDecision Decision(
        int track,
        long frame,
        BoundaryTransitionKind kind,
        string phraseId,
        bool eligible,
        double priority) => new(
            new(track, frame, kind, -6, priority),
            eligible
                ? PremiereTransitionGainSafetyDecisionKind.Eligible
                : PremiereTransitionGainSafetyDecisionKind.RejectedExpectedPeakLoss,
            eligible ? "all-affected-phrases-retain-peak" : "expected-peak-loss",
            [phraseId],
            []);
}
