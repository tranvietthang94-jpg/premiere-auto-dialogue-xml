using System.Security.Cryptography;
using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.Xml;
using PremiereAutoDialogueXml.Output.Audit;
using PremiereAutoDialogueXml.Validation;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class PremiereConstantGainBatchPlannerTests
{
    [TestMethod]
    public void PlanKeepsAllKindsAndSkipsAdjacentLowerPriorityBoundary()
    {
        var scan = Scan(
            Sample(1, 10, BoundaryTransitionKind.DisabledToEnabled, -5, 40),
            Sample(1, 11, BoundaryTransitionKind.EnabledToDisabled, -6, 39),
            Sample(1, 20, BoundaryTransitionKind.GainChange, -10, 30),
            Sample(2, 3, BoundaryTransitionKind.EnabledToDisabled, -15, 25));

        var plan = new PremiereConstantGainBatchPlanner().Plan(scan, maximumTransitions: 3);

        Assert.AreEqual(3, plan.SelectedCount);
        Assert.AreEqual(1, plan.EnabledToDisabledSelectedCount);
        Assert.AreEqual(1, plan.DisabledToEnabledSelectedCount);
        Assert.AreEqual(1, plan.GainChangeSelectedCount);
        CollectionAssert.AreEqual(
            new[] { (1, 10L), (1, 20L), (2, 3L) },
            plan.Selected.Select(item => (item.TrackIndex, item.BoundaryFrame)).ToArray());
        var skipped = plan.Decisions.Single(item => item.Request.BoundaryFrame == 11);
        Assert.AreEqual(PremiereConstantGainPlanDecisionKind.SkippedConflict, skipped.Decision);
        Assert.AreEqual(10L, skipped.ConflictingBoundaryFrame);
    }

    [TestMethod]
    public void PlanIsDeterministicWhenCapturedOrderChanges()
    {
        var samples = new[]
        {
            Sample(1, 20, BoundaryTransitionKind.GainChange, -10, 30),
            Sample(2, 3, BoundaryTransitionKind.EnabledToDisabled, -15, 25),
            Sample(1, 10, BoundaryTransitionKind.DisabledToEnabled, -5, 40)
        };

        var first = new PremiereConstantGainBatchPlanner().Plan(Scan(samples), 3);
        var second = new PremiereConstantGainBatchPlanner().Plan(Scan(samples.Reverse().ToArray()), 3);

        Assert.AreEqual(first.SelectionStreamSha256, second.SelectionStreamSha256);
        CollectionAssert.AreEqual(first.Selected.ToArray(), second.Selected.ToArray());
    }

    private static BoundaryDiscontinuityScanReport Scan(params BoundaryDiscontinuitySample[] samples) => new(
        "1.1",
        BoundaryDiscontinuityScanner.Policy,
        -40,
        1,
        samples.Length,
        0,
        samples.Length,
        samples.Count(item => item.Kind == BoundaryTransitionKind.EnabledToDisabled),
        samples.Count(item => item.Kind == BoundaryTransitionKind.DisabledToEnabled),
        samples.Count(item => item.Kind == BoundaryTransitionKind.GainChange),
        samples.Length,
        samples.Length,
        samples.Max(item => item.RenderedStepDbfs),
        samples.Max(item => item.ExcessStepDbfs),
        samples.Max(item => item.ExcessStepDbfs),
        samples.Max(item => item.RenderedStepAboveLocalP99Db),
        new string('A', 64),
        samples.Length,
        samples);

    private static BoundaryDiscontinuitySample Sample(
        int track,
        long frame,
        BoundaryTransitionKind kind,
        double stepDbfs,
        double aboveLocalP99Db) => new(
            track,
            "source-1",
            "file-1",
            "media.wav",
            frame,
            kind,
            kind == BoundaryTransitionKind.DisabledToEnabled
                ? AudioSegmentStatus.Noise
                : AudioSegmentStatus.Ambiguous,
            kind == BoundaryTransitionKind.EnabledToDisabled
                ? AudioSegmentStatus.Noise
                : AudioSegmentStatus.Ambiguous,
            kind != BoundaryTransitionKind.DisabledToEnabled,
            kind != BoundaryTransitionKind.EnabledToDisabled,
            6,
            12,
            1,
            2,
            0.1f,
            0.2f,
            0.1,
            0.2,
            0.1,
            0.1,
            0.1,
            -20,
            stepDbfs,
            stepDbfs,
            true,
            stepDbfs - aboveLocalP99Db,
            aboveLocalP99Db,
            true);
}

[TestClass]
public sealed class PremiereConstantGainBatchCandidateWriterTests
{
    [TestMethod]
    public async Task WriteAsyncEmitsThreeKindsWithoutChangingClipGeometry()
    {
        using var fixture = BatchFixture.Create();
        var inputHash = Sha256(fixture.InputPath);
        var requests = new[]
        {
            Request(1, 2, BoundaryTransitionKind.DisabledToEnabled),
            Request(1, 4, BoundaryTransitionKind.GainChange),
            Request(1, 6, BoundaryTransitionKind.EnabledToDisabled)
        };

        var result = await new PremiereConstantGainBatchCandidateWriter().WriteAsync(
            fixture.InputPath,
            inputHash,
            Audit(inputHash),
            new string('B', 64),
            fixture.OutputPath,
            requests);

        Assert.AreEqual(inputHash, Sha256(fixture.InputPath));
        Assert.AreEqual(result.OutputXmlSha256, Sha256(fixture.OutputPath));
        Assert.AreEqual(3, result.TransitionCount);
        CollectionAssert.AreEqual(
            new[]
            {
                BoundaryTransitionKind.DisabledToEnabled,
                BoundaryTransitionKind.GainChange,
                BoundaryTransitionKind.EnabledToDisabled
            },
            result.Boundaries.Select(item => item.Kind).ToArray());

        var input = PremiereXmlDocumentLoader.LoadGeneratedOutput(fixture.InputPath, inputHash).Document;
        var output = PremiereXmlDocumentLoader.LoadGeneratedOutput(
            fixture.OutputPath,
            result.OutputXmlSha256).Document;
        var inputClips = Track(input).Elements("clipitem").ToArray();
        var outputTrack = Track(output);
        var outputClips = outputTrack.Elements("clipitem").ToArray();
        Assert.HasCount(inputClips.Length, outputClips);
        for (var index = 0; index < inputClips.Length; index++)
        {
            Assert.IsTrue(
                System.Xml.Linq.XNode.DeepEquals(
                    WithoutInsignificantWhitespace(inputClips[index]),
                    WithoutInsignificantWhitespace(outputClips[index])),
                $"Clip {index} đã bị đổi ngoài transition.");
        }

        CollectionAssert.AreEqual(
            new[] { "2", "4", "6" },
            outputTrack.Elements("transitionitem").Select(item => item.Element("start")!.Value).ToArray());
    }

    [TestMethod]
    public async Task WriteAsyncRejectsAdjacentRequestsBeforeWritingOutput()
    {
        using var fixture = BatchFixture.Create();
        var inputHash = Sha256(fixture.InputPath);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PremiereConstantGainBatchCandidateWriter().WriteAsync(
                fixture.InputPath,
                inputHash,
                Audit(inputHash),
                new string('B', 64),
                fixture.OutputPath,
                [
                    Request(1, 2, BoundaryTransitionKind.DisabledToEnabled),
                    Request(1, 3, BoundaryTransitionKind.EnabledToDisabled)
                ]));

        StringAssert.Contains(exception.Message, "xung đột");
        Assert.IsFalse(File.Exists(fixture.OutputPath));
    }

    [TestMethod]
    public async Task WriteAsyncRejectsKindMismatchAtomically()
    {
        using var fixture = BatchFixture.Create();
        var inputHash = Sha256(fixture.InputPath);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PremiereConstantGainBatchCandidateWriter().WriteAsync(
                fixture.InputPath,
                inputHash,
                Audit(inputHash),
                new string('B', 64),
                fixture.OutputPath,
                [Request(1, 2, BoundaryTransitionKind.EnabledToDisabled)]));

        StringAssert.Contains(exception.Message, "không khớp scan");
        Assert.IsFalse(File.Exists(fixture.OutputPath));
    }

    private static PremiereConstantGainBoundaryRequest Request(
        int track,
        long frame,
        BoundaryTransitionKind kind) => new(track, frame, kind, -6, 30);

    private static System.Xml.Linq.XElement Track(System.Xml.Linq.XDocument document) =>
        document.Root!.Element("sequence")!.Element("media")!.Element("audio")!.Element("track")!;

    private static System.Xml.Linq.XElement WithoutInsignificantWhitespace(
        System.Xml.Linq.XElement source)
    {
        var copy = new System.Xml.Linq.XElement(source);
        foreach (var whitespace in copy.DescendantNodes()
                     .OfType<System.Xml.Linq.XText>()
                     .Where(text => string.IsNullOrWhiteSpace(text.Value))
                     .ToArray())
        {
            whitespace.Remove();
        }

        return copy;
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static OutputAudit Audit(string outputXmlSha256) => new(
        "1.9",
        "phase15-test-run",
        DateTimeOffset.UnixEpoch,
        "source.xml",
        new string('C', 64),
        "input.xml",
        outputXmlSha256,
        "sequence-source",
        "sequence-test",
        "uuid-test",
        "Phase 15 test",
        new("6.2.1", new string('D', 64)),
        PresetAudit.From(DialogueProcessingPreset.Balanced),
        [
            Fragment("clip-0", 0, 2, AudioSegmentStatus.Noise, false, null),
            Fragment("clip-1", 2, 4, AudioSegmentStatus.Ambiguous, true, 6),
            Fragment("clip-2", 4, 6, AudioSegmentStatus.Speech, true, 12),
            Fragment("clip-3", 6, 8, AudioSegmentStatus.Noise, false, null)
        ],
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
        string id,
        long start,
        long end,
        AudioSegmentStatus status,
        bool enabled,
        double? gain) => new(
            id,
            1,
            "source-1",
            "file-1",
            "media.wav",
            start,
            end,
            start,
            end,
            checked(start * 10_160_640_000),
            checked(end * 10_160_640_000),
            checked(start * 1_920),
            checked(end * 1_920),
            status,
            enabled,
            null,
            null,
            null,
            gain,
            false,
            status.ToString(),
            null);

    private sealed class BatchFixture : IDisposable
    {
        private BatchFixture(string root, string inputPath, string outputPath)
        {
            Root = root;
            InputPath = inputPath;
            OutputPath = outputPath;
        }

        public string Root { get; }
        public string InputPath { get; }
        public string OutputPath { get; }

        public static BatchFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"padx-phase15-batch-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var input = Path.Combine(root, "input.xml");
            var output = Path.Combine(root, "candidate.xml");
            File.WriteAllText(input, """
                <?xml version="1.0" encoding="utf-8"?>
                <!DOCTYPE xmeml>
                <xmeml version="4">
                  <sequence id="sequence-test">
                    <duration>8</duration>
                    <rate><timebase>25</timebase><ntsc>FALSE</ntsc></rate>
                    <name>Phase 15 test</name>
                    <media>
                      <audio>
                        <numOutputChannels>2</numOutputChannels>
                        <format><samplecharacteristics><depth>16</depth><samplerate>48000</samplerate></samplecharacteristics></format>
                        <track>
                          <clipitem id="clip-0"><masterclipid>masterclip-1</masterclipid><enabled>FALSE</enabled><duration>8</duration><rate><timebase>25</timebase><ntsc>FALSE</ntsc></rate><start>0</start><end>2</end><in>0</in><out>2</out><pproTicksIn>0</pproTicksIn><pproTicksOut>20321280000</pproTicksOut><file id="file-1" /><sourcetrack><mediatype>audio</mediatype><trackindex>1</trackindex></sourcetrack></clipitem>
                          <clipitem id="clip-1"><masterclipid>masterclip-1</masterclipid><enabled>TRUE</enabled><duration>8</duration><rate><timebase>25</timebase><ntsc>FALSE</ntsc></rate><start>2</start><end>4</end><in>2</in><out>4</out><pproTicksIn>20321280000</pproTicksIn><pproTicksOut>40642560000</pproTicksOut><file id="file-1" /><sourcetrack><mediatype>audio</mediatype><trackindex>1</trackindex></sourcetrack></clipitem>
                          <clipitem id="clip-2"><masterclipid>masterclip-1</masterclipid><enabled>TRUE</enabled><duration>8</duration><rate><timebase>25</timebase><ntsc>FALSE</ntsc></rate><start>4</start><end>6</end><in>4</in><out>6</out><pproTicksIn>40642560000</pproTicksIn><pproTicksOut>60963840000</pproTicksOut><file id="file-1" /><sourcetrack><mediatype>audio</mediatype><trackindex>1</trackindex></sourcetrack></clipitem>
                          <clipitem id="clip-3"><masterclipid>masterclip-1</masterclipid><enabled>FALSE</enabled><duration>8</duration><rate><timebase>25</timebase><ntsc>FALSE</ntsc></rate><start>6</start><end>8</end><in>6</in><out>8</out><pproTicksIn>60963840000</pproTicksIn><pproTicksOut>81285120000</pproTicksOut><file id="file-1" /><sourcetrack><mediatype>audio</mediatype><trackindex>1</trackindex></sourcetrack></clipitem>
                        </track>
                      </audio>
                    </media>
                  </sequence>
                </xmeml>
                """, new System.Text.UTF8Encoding(false));
            return new(root, input, output);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
