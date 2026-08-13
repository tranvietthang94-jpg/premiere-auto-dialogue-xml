using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.Media;
using PremiereAutoDialogueXml.Core.ProjectModel;
using PremiereAutoDialogueXml.Output.Xml;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class PremiereXmlGeneratorTests
{
    [TestMethod]
    public void GenerateClonesSequenceAndCreatesSafeAudioFragments()
    {
        using var fixture = WriterFixture.Create();
        var generated = new PremiereXmlGenerator().Generate(fixture.Project, fixture.Analysis);
        var sequence = generated.Document.Root!.Element("sequence")!;
        var clipItems = sequence.Element("media")!.Element("audio")!
            .Elements("track").Single().Elements("clipitem").ToArray();

        Assert.AreEqual("Show - AUTO AUDIO", generated.SequenceName);
        Assert.AreNotEqual("sequence-source", generated.SequenceId);
        Assert.AreNotEqual("11111111-1111-1111-1111-111111111111", generated.SequenceUuid);
        Assert.AreEqual(generated.SequenceId, (string?)sequence.Attribute("id"));
        Assert.AreEqual(generated.SequenceUuid, sequence.Element("uuid")!.Value);
        Assert.AreEqual("Show - AUTO AUDIO", sequence.Element("name")!.Value);
        Assert.IsNotNull(generated.Document.DocumentType);
        Assert.AreEqual("xmeml", generated.Document.DocumentType.Name);

        Assert.HasCount(3, clipItems);
        Assert.AreEqual(3, clipItems.Select(item => (string)item.Attribute("id")!).Distinct().Count());
        Assert.IsFalse(clipItems.Any(item => (string?)item.Attribute("id") == "clip-source"));
        CollectionAssert.AreEqual(new[] { "FALSE", "TRUE", "FALSE" },
            clipItems.Select(item => item.Element("enabled")!.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "0", "2", "8" },
            clipItems.Select(item => item.Element("start")!.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "2", "8", "10" },
            clipItems.Select(item => item.Element("end")!.Value).ToArray());

        Assert.IsNotNull(clipItems[0].Element("file")!.Element("pathurl"));
        Assert.IsNull(clipItems[1].Element("file")!.Element("pathurl"));
        Assert.IsTrue(clipItems.All(item => (string?)item.Element("file")!.Attribute("id") == "file-source"));
        Assert.HasCount(0, clipItems[0].Elements("filter"));
        Assert.HasCount(1, clipItems[1].Elements("filter"));
        Assert.HasCount(0, clipItems[2].Elements("filter"));
        Assert.AreEqual(
            "2.821726937",
            clipItems[1].Elements("filter").Single().Descendants("value").Single().Value);

        Assert.AreEqual(fixture.VideoFingerprint, WriterFixture.Fingerprint(sequence.Element("media")!.Element("video")!));
        Assert.HasCount(1, generated.Markers.Where(marker => marker.Name == "Cần kiểm tra"));
        Assert.HasCount(3, generated.AudioFragments);
        Assert.AreEqual(AudioSegmentStatus.Ambiguous, generated.AudioFragments[1].Status);
        Assert.AreEqual("speech | ambiguous-near-speech", generated.AudioFragments[1].Reason);
        Assert.AreEqual(AudioSegmentStatus.Bleed, generated.AudioFragments[^1].Status);
        Assert.IsFalse(generated.AudioFragments[^1].Enabled);
    }

    [TestMethod]
    public void GenerateFrameAlignmentNeverDisablesFrameTouchedBySpeech()
    {
        using var fixture = WriterFixture.Create();
        var phrase = fixture.Analysis.Tracks[0].Phrases[0];
        var segments = new[]
        {
            fixture.Segment(0, 1_000, AudioSegmentStatus.Noise, null, null, "noise"),
            fixture.Segment(1_000, 3_000, AudioSegmentStatus.Speech, phrase.Id, phrase.AppliedGainDb, "confirmed-direct-speech"),
            fixture.Segment(3_000, 19_200, AudioSegmentStatus.Noise, null, null, "noise")
        };
        var analysis = fixture.Analysis with
        {
            Tracks =
            [
                fixture.Analysis.Tracks[0] with { Segments = segments }
            ]
        };

        var generated = new PremiereXmlGenerator().Generate(fixture.Project, analysis);
        var fragments = generated.AudioFragments;

        Assert.AreEqual(0L, fragments[0].TimelineStartFrame);
        Assert.AreEqual(2L, fragments[0].TimelineEndFrame);
        Assert.IsTrue(fragments[0].Enabled);
        Assert.AreEqual(AudioSegmentStatus.Speech, fragments[0].Status);
    }

    [TestMethod]
    public void GenerateMergesAdjacentFrameRunsWithSameOutputDecisionAndCombinesReasons()
    {
        using var fixture = WriterFixture.Create();
        var phrase = fixture.Analysis.Tracks[0].Phrases[0];
        var segments = new[]
        {
            fixture.Segment(0, 3_840, AudioSegmentStatus.Noise, null, null, "noise"),
            fixture.Segment(3_840, 7_680, AudioSegmentStatus.Ambiguous, phrase.Id, phrase.AppliedGainDb, "ambiguous-first"),
            fixture.Segment(7_680, 11_520, AudioSegmentStatus.Ambiguous, phrase.Id, phrase.AppliedGainDb, "ambiguous-second"),
            fixture.Segment(11_520, 19_200, AudioSegmentStatus.Noise, null, null, "noise")
        };
        var analysis = fixture.Analysis with
        {
            Tracks =
            [
                fixture.Analysis.Tracks[0] with { Segments = segments }
            ]
        };

        var generated = new PremiereXmlGenerator().Generate(fixture.Project, analysis);

        Assert.HasCount(3, generated.AudioFragments);
        var ambiguous = generated.AudioFragments.Single(fragment => fragment.Status == AudioSegmentStatus.Ambiguous);
        Assert.AreEqual(2L, ambiguous.TimelineStartFrame);
        Assert.AreEqual(6L, ambiguous.TimelineEndFrame);
        Assert.AreEqual("ambiguous-first | ambiguous-second", ambiguous.Reason);
        var marker = generated.Markers.Single(item => item.Name == "Cần kiểm tra");
        Assert.AreEqual(ambiguous.Reason, marker.Reason);
    }

    [TestMethod]
    public void GenerateAddsMarkerWhenGainWasCapped()
    {
        using var fixture = WriterFixture.Create(gainWasCapped: true);

        var generated = new PremiereXmlGenerator().Generate(fixture.Project, fixture.Analysis);

        Assert.HasCount(1, generated.Markers.Where(marker => marker.Reason == "gain-capped"));
        var markerElements = generated.Document.Root!.Element("sequence")!.Elements("marker").ToArray();
        Assert.IsTrue(markerElements.Any(marker => marker.Element("name")!.Value == "Gain đã giới hạn +18 dB"));
        var speech = generated.Document.Descendants("clipitem")
            .Single(item => item.Element("start")!.Value == "2");
        CollectionAssert.AreEqual(
            new[] { PremiereAutoDialogueXml.Output.Gain.PremiereGainFilterFactory.PremiereGainEffectId, "audiolevels" },
            speech.Elements("filter").Select(filter => filter.Descendants("effectid").Single().Value).ToArray());
        CollectionAssert.AreEqual(
            new[] { "1.995262315", "3.981071706" },
            speech.Elements("filter").Select(filter => filter.Descendants("value").Single().Value).ToArray());
    }

    [TestMethod]
    public void GenerateRejectsSourceXmlChangedAfterInspection()
    {
        using var fixture = WriterFixture.Create();
        File.AppendAllText(fixture.Project.SourceXmlPath, " ");

        var exception = Assert.ThrowsExactly<PremiereAutoDialogueXml.Core.Xml.PremiereXmlLoadException>(
            () => new PremiereXmlGenerator().Generate(fixture.Project, fixture.Analysis));

        Assert.AreEqual("xml-source-changed", exception.Code);
    }

    [TestMethod]
    public void GenerateRejectsSegmentForUnknownSourceClip()
    {
        using var fixture = WriterFixture.Create();
        var extra = fixture.Segment(0, 1, AudioSegmentStatus.Noise, null, null, "invalid") with
        {
            SourceClipId = "clip-not-in-project"
        };
        var analysis = fixture.Analysis with
        {
            Tracks = [fixture.Analysis.Tracks[0] with { Segments = [.. fixture.Analysis.Tracks[0].Segments, extra] }]
        };

        Assert.ThrowsExactly<InvalidDataException>(() => new PremiereXmlGenerator().Generate(fixture.Project, analysis));
    }

    [TestMethod]
    public void GenerateRejectsPhraseFromAnotherTrack()
    {
        using var fixture = WriterFixture.Create();
        var analysis = fixture.Analysis with
        {
            Tracks =
            [
                fixture.Analysis.Tracks[0] with
                {
                    Phrases = [fixture.Analysis.Tracks[0].Phrases[0] with { TrackIndex = 2 }]
                }
            ]
        };

        Assert.ThrowsExactly<InvalidDataException>(() => new PremiereXmlGenerator().Generate(fixture.Project, analysis));
    }

    [TestMethod]
    public void GenerateRejectsSegmentWithUnknownPhrase()
    {
        using var fixture = WriterFixture.Create();
        var segments = fixture.Analysis.Tracks[0].Segments.ToArray();
        segments[1] = segments[1] with { PhraseId = "phrase-not-in-analysis" };
        var analysis = fixture.Analysis with
        {
            Tracks = [fixture.Analysis.Tracks[0] with { Segments = segments }]
        };

        Assert.ThrowsExactly<InvalidDataException>(() => new PremiereXmlGenerator().Generate(fixture.Project, analysis));
    }

    [TestMethod]
    public void GenerateRejectsPhraseWithoutAnySegment()
    {
        using var fixture = WriterFixture.Create();
        var unused = fixture.Analysis.Tracks[0].Phrases[0] with { Id = "unused-phrase" };
        var analysis = fixture.Analysis with
        {
            Tracks =
            [
                fixture.Analysis.Tracks[0] with
                {
                    Phrases = [.. fixture.Analysis.Tracks[0].Phrases, unused]
                }
            ]
        };

        Assert.ThrowsExactly<InvalidDataException>(() => new PremiereXmlGenerator().Generate(fixture.Project, analysis));
    }

    internal sealed class WriterFixture : IDisposable
    {
        private WriterFixture(
            string directory,
            PremiereProject project,
            ProjectAudioAnalysis analysis,
            string videoFingerprint)
        {
            Directory = directory;
            Project = project;
            Analysis = analysis;
            VideoFingerprint = videoFingerprint;
        }

        public string Directory { get; }

        public PremiereProject Project { get; }

        public ProjectAudioAnalysis Analysis { get; }

        public string VideoFingerprint { get; }

        public static WriterFixture Create(bool gainWasCapped = false)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"padx-writer-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            var xmlPath = Path.Combine(directory, "source.xml");
            File.WriteAllText(xmlPath, SourceXml, new UTF8Encoding(false));
            var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(xmlPath)));
            var wave = new WaveFileInfo(
                Path.Combine(directory, "voice.wav"),
                WaveEncodingKind.Pcm,
                1,
                48_000,
                24,
                24,
                3,
                44,
                57_600,
                57_600,
                19_200,
                0,
                0);
            var clip = new PremiereAudioClip(
                "clip-source",
                "voice.wav",
                true,
                0,
                10,
                100,
                110,
                1_016_064_000_000,
                1_117_670_400_000,
                192_000,
                211_200,
                "file-source",
                new("media-source", "voice.wav", "file://localhost/F%3a/demo/voice.wav", wave.Path, wave));
            var track = new PremiereAudioTrack(1, 1, [clip]);
            var project = new PremiereProject(
                xmlPath,
                sourceHash,
                new("sequence-source", "11111111-1111-1111-1111-111111111111", "Show", 25, 10, 2, 48_000, [track]));
            var compensatedGain = (float)(6 + DialogueProcessingPreset.Balanced.PremiereCenterPanCompensationDb);
            var measuredPeak = gainWasCapped
                ? (float)(DialogueProcessingPreset.Balanced.TargetSamplePeakDbfs +
                          DialogueProcessingPreset.Balanced.PremiereCenterPanCompensationDb -
                          24)
                : -12;
            var phrase = new DialoguePhrase(
                "T01-P000001",
                1,
                3_840,
                11_520,
                3_840,
                15_360,
                measuredPeak,
                gainWasCapped ? 24 : compensatedGain,
                gainWasCapped ? 18 : compensatedGain,
                gainWasCapped);
            var fixture = new WriterFixture(directory, project, null!, string.Empty);
            var segments = new[]
            {
                fixture.Segment(0, 3_840, AudioSegmentStatus.Noise, null, null, "vad-negative-noise"),
                fixture.Segment(3_840, 11_520, AudioSegmentStatus.Speech, phrase.Id, phrase.AppliedGainDb, "confirmed-direct-speech"),
                fixture.Segment(11_520, 15_360, AudioSegmentStatus.Ambiguous, phrase.Id, phrase.AppliedGainDb, "ambiguous-near-speech"),
                fixture.Segment(15_360, 19_200, AudioSegmentStatus.Bleed, null, null, "confirmed-bleed-from-track-2")
            };
            var analysis = new ProjectAudioAnalysis(
                [new(1, 7, [phrase], segments, -18)],
                "6.2.1",
                "MODEL-SHA256");
            var sourceDocument = XDocument.Load(xmlPath, LoadOptions.PreserveWhitespace);
            var videoFingerprint = Fingerprint(sourceDocument.Root!.Element("sequence")!.Element("media")!.Element("video")!);
            return new(directory, project, analysis, videoFingerprint);
        }

        public AnalyzedAudioSegment Segment(
            long start,
            long end,
            AudioSegmentStatus status,
            string? phraseId,
            float? gain,
            string reason) => new(
                1,
                "clip-source",
                start,
                end,
                192_000 + start,
                192_000 + end,
                status,
                phraseId,
                gain,
                reason);

        public static string Fingerprint(XElement element)
        {
            var clone = new XElement(element);
            foreach (var whitespace in clone.DescendantNodes().OfType<XText>()
                         .Where(text => string.IsNullOrWhiteSpace(text.Value)).ToArray())
            {
                whitespace.Remove();
            }

            return clone.ToString(SaveOptions.DisableFormatting);
        }

        public void Dispose()
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }

        private const string SourceXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE xmeml>
            <xmeml version="4">
              <sequence id="sequence-source">
                <uuid>11111111-1111-1111-1111-111111111111</uuid>
                <duration>10</duration>
                <rate><timebase>25</timebase><ntsc>FALSE</ntsc></rate>
                <name>Show</name>
                <media>
                  <video><format><samplecharacteristics><width>3840</width><height>2160</height></samplecharacteristics></format><track><enabled>TRUE</enabled><locked>FALSE</locked></track></video>
                  <audio>
                    <numOutputChannels>2</numOutputChannels>
                    <format><samplecharacteristics><depth>24</depth><samplerate>48000</samplerate></samplecharacteristics></format>
                    <outputs><group><index>1</index><numchannels>1</numchannels><downmix>0</downmix><channel><index>1</index></channel></group><group><index>2</index><numchannels>1</numchannels><downmix>0</downmix><channel><index>2</index></channel></group></outputs>
                    <track premiereTrackType="Stereo" customRouting="preserve">
                      <clipitem id="clip-source" premiereChannelType="mono">
                        <masterclipid>masterclip-source</masterclipid><name>voice.wav</name><enabled>TRUE</enabled><duration>300</duration>
                        <rate><timebase>25</timebase><ntsc>FALSE</ntsc></rate><start>0</start><end>10</end><in>100</in><out>110</out>
                        <pproTicksIn>1016064000000</pproTicksIn><pproTicksOut>1117670400000</pproTicksOut>
                        <file id="file-source"><name>voice.wav</name><pathurl>file://localhost/F%3a/demo/voice.wav</pathurl></file>
                        <sourcetrack><mediatype>audio</mediatype><trackindex>1</trackindex></sourcetrack>
                        <logginginfo><description>preserve first</description></logginginfo><labels><label2>Caribbean</label2></labels>
                      </clipitem>
                      <enabled>TRUE</enabled><locked>FALSE</locked><outputchannelindex>1</outputchannelindex>
                    </track>
                  </audio>
                </media>
              </sequence>
            </xmeml>
            """;
    }
}
