using System.Security.Cryptography;
using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.Xml;
using PremiereAutoDialogueXml.Output.Audit;
using PremiereAutoDialogueXml.Validation;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class PremiereConstantGainCandidateWriterTests
{
    [TestMethod]
    public async Task WriteAsyncEmitsOneFrameTransitionWithoutPreNormalizingAdjacentClips()
    {
        using var fixture = CandidateFixture.Create();
        var inputHash = Sha256(fixture.InputPath);

        var result = await new PremiereConstantGainCandidateWriter().WriteAsync(
            fixture.InputPath,
            inputHash,
            Audit(inputHash),
            new string('A', 64),
            fixture.OutputPath,
            trackIndex: 1,
            boundaryFrame: 1);

        Assert.AreEqual(inputHash, Sha256(fixture.InputPath));
        Assert.AreEqual(result.OutputXmlSha256, Sha256(fixture.OutputPath));
        Assert.AreEqual(25, result.FrameRate);
        Assert.AreEqual(10_160_640_000, result.FrameTicks);
        Assert.AreEqual(5_080_320_000, result.HalfFrameTicks);
        Assert.IsFalse(result.LeftEnabled);
        Assert.IsTrue(result.RightEnabled);
        Assert.AreEqual(PremiereConstantGainCandidateWriter.EffectId, result.EffectId);

        var document = PremiereXmlDocumentLoader.LoadGeneratedOutput(
            fixture.OutputPath,
            result.OutputXmlSha256).Document;
        var track = document.Root!.Element("sequence")!.Element("media")!.Element("audio")!.Element("track")!;
        var clips = track.Elements("clipitem").ToArray();
        Assert.HasCount(2, clips);
        Assert.AreEqual("1", clips[0].Element("end")!.Value);
        Assert.AreEqual("10160640000", clips[0].Element("pproTicksOut")!.Value);
        Assert.AreEqual("1", clips[1].Element("start")!.Value);
        Assert.AreEqual("10160640000", clips[1].Element("pproTicksIn")!.Value);

        var transition = track.Elements("transitionitem").Single();
        Assert.AreEqual("1", transition.Element("start")!.Value);
        Assert.AreEqual("1", transition.Element("end")!.Value);
        Assert.AreEqual("5080320000", transition.Element("pproTicksIn")!.Value);
        Assert.AreEqual("15240960000", transition.Element("pproTicksOut")!.Value);
        Assert.AreEqual("center", transition.Element("alignment")!.Value);
        Assert.AreEqual("5080320000", transition.Element("cutPointTicks")!.Value);
        Assert.AreEqual(
            PremiereConstantGainCandidateWriter.EffectName,
            transition.Element("effect")!.Element("name")!.Value);
        Assert.AreEqual(
            PremiereConstantGainCandidateWriter.EffectId,
            transition.Element("effect")!.Element("effectid")!.Value);
    }

    [TestMethod]
    public async Task WriteAsyncRejectsDifferentMediaAndLeavesNoOutput()
    {
        using var fixture = CandidateFixture.Create();
        var inputHash = Sha256(fixture.InputPath);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PremiereConstantGainCandidateWriter().WriteAsync(
                fixture.InputPath,
                inputHash,
                Audit(inputHash, rightSourceClipId: "source-2"),
                new string('A', 64),
                fixture.OutputPath,
                trackIndex: 1,
                boundaryFrame: 1));

        StringAssert.Contains(exception.Message, "cùng source clip/media");
        Assert.IsFalse(File.Exists(fixture.OutputPath));
    }

    [TestMethod]
    public async Task WriteAsyncRejectsChangedInputHashAndLeavesNoOutput()
    {
        using var fixture = CandidateFixture.Create();

        var exception = await Assert.ThrowsAsync<PremiereXmlLoadException>(() =>
            new PremiereConstantGainCandidateWriter().WriteAsync(
                fixture.InputPath,
                new string('0', 64),
                Audit(Sha256(fixture.InputPath)),
                new string('A', 64),
                fixture.OutputPath,
                trackIndex: 1,
                boundaryFrame: 1));

        Assert.AreEqual("xml-source-changed", exception.Code);
        Assert.IsFalse(File.Exists(fixture.OutputPath));
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static OutputAudit Audit(string outputXmlSha256, string rightSourceClipId = "source-1") => new(
        "1.9",
        "phase14-test-run",
        DateTimeOffset.UnixEpoch,
        "source.xml",
        new string('B', 64),
        "input.xml",
        outputXmlSha256,
        "sequence-source",
        "sequence-test",
        "uuid-test",
        "Phase 14 test",
        new("6.2.1", new string('C', 64)),
        PresetAudit.From(DialogueProcessingPreset.Balanced),
        [
            new(
                "clip-left",
                1,
                "source-1",
                "file-1",
                "media.wav",
                0,
                1,
                0,
                1,
                0,
                10_160_640_000,
                0,
                1_920,
                AudioSegmentStatus.Noise,
                false,
                null,
                null,
                null,
                null,
                false,
                "noise",
                null),
            new(
                "clip-right",
                1,
                rightSourceClipId,
                "file-1",
                "media.wav",
                1,
                3,
                1,
                3,
                10_160_640_000,
                30_481_920_000,
                1_920,
                5_760,
                AudioSegmentStatus.Ambiguous,
                true,
                null,
                null,
                null,
                null,
                false,
                "ambiguous",
                null)
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

    private sealed class CandidateFixture : IDisposable
    {
        private CandidateFixture(string root, string inputPath, string outputPath)
        {
            Root = root;
            InputPath = inputPath;
            OutputPath = outputPath;
        }

        public string Root { get; }
        public string InputPath { get; }
        public string OutputPath { get; }

        public static CandidateFixture Create(string rightFileId = "file-1")
        {
            var root = Path.Combine(Path.GetTempPath(), $"padx-phase14-candidate-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var input = Path.Combine(root, "input.xml");
            var output = Path.Combine(root, "candidate.xml");
            File.WriteAllText(input, $$"""
                <?xml version="1.0" encoding="utf-8"?>
                <!DOCTYPE xmeml>
                <xmeml version="4">
                  <sequence id="sequence-test">
                    <duration>3</duration>
                    <rate><timebase>25</timebase><ntsc>FALSE</ntsc></rate>
                    <name>Phase 14 test</name>
                    <media>
                      <audio>
                        <numOutputChannels>2</numOutputChannels>
                        <format><samplecharacteristics><depth>16</depth><samplerate>48000</samplerate></samplecharacteristics></format>
                        <track>
                          <clipitem id="clip-left">
                            <masterclipid>masterclip-1</masterclipid>
                            <enabled>FALSE</enabled>
                            <duration>3</duration>
                            <rate><timebase>25</timebase><ntsc>FALSE</ntsc></rate>
                            <start>0</start><end>1</end><in>0</in><out>1</out>
                            <pproTicksIn>0</pproTicksIn><pproTicksOut>10160640000</pproTicksOut>
                            <file id="file-1" />
                            <sourcetrack><mediatype>audio</mediatype><trackindex>1</trackindex></sourcetrack>
                          </clipitem>
                          <clipitem id="clip-right">
                            <masterclipid>masterclip-1</masterclipid>
                            <enabled>TRUE</enabled>
                            <duration>3</duration>
                            <rate><timebase>25</timebase><ntsc>FALSE</ntsc></rate>
                            <start>1</start><end>3</end><in>1</in><out>3</out>
                            <pproTicksIn>10160640000</pproTicksIn><pproTicksOut>30481920000</pproTicksOut>
                            <file id="{{rightFileId}}" />
                            <sourcetrack><mediatype>audio</mediatype><trackindex>1</trackindex></sourcetrack>
                          </clipitem>
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
