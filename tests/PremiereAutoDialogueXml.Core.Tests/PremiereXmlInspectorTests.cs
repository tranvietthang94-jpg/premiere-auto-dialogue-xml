using System.Buffers.Binary;
using System.Security;
using PremiereAutoDialogueXml.Core.Inspection;
using PremiereAutoDialogueXml.Core.Xml;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class PremiereXmlInspectorTests
{
    private readonly PremiereXmlInspector _inspector = new();

    [TestMethod]
    public void Inspect_AcceptsUnicodePathSourceTrimAndTimelineGap()
    {
        using var fixture = XmlFixture.Create();

        var result = _inspector.Inspect(fixture.XmlPath);

        Assert.IsTrue(result.CanProceed, FormatIssues(result.Issues));
        Assert.IsNotNull(result.Project);
        Assert.HasCount(1, result.Project.Sequence.AudioTracks);
        Assert.HasCount(2, result.Project.Sequence.AudioTracks[0].Clips);
        var first = result.Project.Sequence.AudioTracks[0].Clips[0];
        var second = result.Project.Sequence.AudioTracks[0].Clips[1];
        Assert.AreEqual(48_000, first.SourceStartSample);
        Assert.AreEqual(96_000, first.SourceEndSample);
        Assert.AreEqual(25, second.TimelineStartFrame - first.TimelineEndFrame);
        Assert.AreEqual(64, result.Project.SourceXmlSha256.Length);
    }

    [TestMethod]
    public void Inspect_UsesWaveHeaderWhenXmlMetadataDiffers()
    {
        using var fixture = XmlFixture.Create(waveBits: 24, xmlDepth: 16);

        var result = _inspector.Inspect(fixture.XmlPath);

        Assert.IsTrue(result.CanProceed, FormatIssues(result.Issues));
        Assert.IsTrue(result.Issues.Any(issue =>
            issue.Code == "xml-wav-metadata-mismatch" &&
            issue.Severity == InspectionSeverity.Warning));
        Assert.AreEqual(24, result.Project!.Sequence.AudioTracks[0].Clips[0].SourceMedia.Wave.ValidBitsPerSample);
    }

    [TestMethod]
    public void Inspect_RejectsOverlapOnSameTrack()
    {
        using var fixture = XmlFixture.Create(overlap: true);

        var result = _inspector.Inspect(fixture.XmlPath);

        Assert.IsFalse(result.CanProceed);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "audio-overlap-unsupported"));
    }

    [TestMethod]
    public void Inspect_RejectsEffectsBeforeReadingMedia()
    {
        using var fixture = XmlFixture.Create(includeFilter: true, createMedia: false);

        var result = _inspector.Inspect(fixture.XmlPath);

        Assert.IsFalse(result.CanProceed);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "filter-unsupported"));
        Assert.IsFalse(result.Issues.Any(issue => issue.Code == "media-missing"));
    }

    [TestMethod]
    public void Inspect_RejectsExternalDocumentType()
    {
        using var fixture = XmlFixture.Create(documentType: "<!DOCTYPE xmeml SYSTEM \"https://example.invalid/evil.dtd\">");

        var result = _inspector.Inspect(fixture.XmlPath);

        Assert.IsFalse(result.CanProceed);
        Assert.AreEqual("doctype-not-allowed", result.Issues.Single().Code);
    }

    [TestMethod]
    public void Inspect_RejectsNestedSequenceBeforeReadingMedia()
    {
        using var fixture = XmlFixture.Create(includeNestedSequence: true, createMedia: false);

        var result = _inspector.Inspect(fixture.XmlPath);

        Assert.IsFalse(result.CanProceed);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "nested-sequence-unsupported"));
        Assert.IsFalse(result.Issues.Any(issue => issue.Code == "media-missing"));
    }

    [TestMethod]
    public void Inspect_RejectsMissingMedia()
    {
        using var fixture = XmlFixture.Create(createMedia: false);

        var result = _inspector.Inspect(fixture.XmlPath);

        Assert.IsFalse(result.CanProceed);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "media-missing"));
    }

    [TestMethod]
    public void Inspect_RejectsDuplicateClipIds()
    {
        using var fixture = XmlFixture.Create(duplicateClipId: true);

        var result = _inspector.Inspect(fixture.XmlPath);

        Assert.IsFalse(result.CanProceed);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "clip-id-duplicate"));
    }

    [TestMethod]
    public void Inspect_RejectsSourceTrimOutsideCompleteWaveFrames()
    {
        using var fixture = XmlFixture.Create(waveSampleFrames: 95_999);

        var result = _inspector.Inspect(fixture.XmlPath);

        Assert.IsFalse(result.CanProceed);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "source-range-outside-media"));
    }

    [TestMethod]
    public void Inspect_FallsBackToFrameTrimWhenPproTicksAreMissing()
    {
        using var fixture = XmlFixture.Create(includePproTicks: false);

        var result = _inspector.Inspect(fixture.XmlPath);

        Assert.IsTrue(result.CanProceed, FormatIssues(result.Issues));
        Assert.AreEqual(2, result.Issues.Count(issue => issue.Code == "ppro-ticks-missing"));
        Assert.AreEqual(48_000, result.Project!.Sequence.AudioTracks[0].Clips[0].SourceStartSample);
    }

    [TestMethod]
    public void Inspect_AcceptsPremiereSubframeAudioRounding()
    {
        using var fixture = XmlFixture.Create(subframeRounding: true);

        var result = _inspector.Inspect(fixture.XmlPath);

        Assert.IsTrue(result.CanProceed, FormatIssues(result.Issues));
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "clip-frame-rounding"));
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "ppro-frame-rounding"));
    }

    [TestMethod]
    public void Inspect_RejectsDurationDifferenceAboveOneFrame()
    {
        using var fixture = XmlFixture.Create(retime: true);

        var result = _inspector.Inspect(fixture.XmlPath);

        Assert.IsFalse(result.CanProceed);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "clip-retime-unsupported"));
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "ppro-duration-mismatch"));
    }

    [TestMethod]
    [DataRow(24)]
    [DataRow(30)]
    public void Inspect_AcceptsSupportedIntegerNdfRateWithExplicitMetadata(int frameRate)
    {
        using var fixture = XmlFixture.Create(
            sequenceFrameRate: frameRate,
            sequenceNtsc: false);

        var result = _inspector.Inspect(fixture.XmlPath);

        Assert.IsTrue(result.CanProceed, FormatIssues(result.Issues));
        Assert.AreEqual(frameRate, result.Project!.Sequence.FrameRate);
        Assert.AreEqual(48_000, result.Project.Sequence.AudioSampleRate);
    }

    [TestMethod]
    [DataRow(24)]
    [DataRow(30)]
    public void Inspect_RequiresExplicitNdfForNewRateBeforeReadingMedia(int frameRate)
    {
        using var fixture = XmlFixture.Create(
            createMedia: false,
            sequenceFrameRate: frameRate,
            sequenceNtsc: null);

        var result = _inspector.Inspect(fixture.XmlPath);

        Assert.IsFalse(result.CanProceed);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "ntsc-rate-required"));
        Assert.IsFalse(result.Issues.Any(issue => issue.Code == "media-missing"));
    }

    [TestMethod]
    [DataRow(24)]
    [DataRow(30)]
    public void Inspect_RequiresExplicitNdfOnClipsAtNewRatesBeforeReadingMedia(int frameRate)
    {
        using var fixture = XmlFixture.Create(
            createMedia: false,
            sequenceFrameRate: frameRate,
            sequenceNtsc: false,
            clipNtsc: null);

        var result = _inspector.Inspect(fixture.XmlPath);

        Assert.IsFalse(result.CanProceed);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "ntsc-rate-required"));
        Assert.IsFalse(result.Issues.Any(issue => issue.Code == "media-missing"));
    }

    [TestMethod]
    public void Inspect_RejectsRateOutsidePhase12GateBeforeReadingMedia()
    {
        using var fixture = XmlFixture.Create(
            createMedia: false,
            sequenceFrameRate: 29,
            sequenceNtsc: false);

        var result = _inspector.Inspect(fixture.XmlPath);

        Assert.IsFalse(result.CanProceed);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "sequence-rate-unsupported"));
        Assert.IsFalse(result.Issues.Any(issue => issue.Code == "media-missing"));
    }

    [TestMethod]
    public void Inspect_KeepsMissingNtscCompatibleForExisting25FpsInput()
    {
        using var fixture = XmlFixture.Create(sequenceNtsc: null, clipNtsc: null);

        var result = _inspector.Inspect(fixture.XmlPath);

        Assert.IsTrue(result.CanProceed, FormatIssues(result.Issues));
        Assert.AreEqual(25, result.Project!.Sequence.FrameRate);
    }

    [TestMethod]
    public void Inspect_RejectsNtscTrueBeforeReadingMedia()
    {
        using var fixture = XmlFixture.Create(createMedia: false, sequenceNtsc: true);

        var result = _inspector.Inspect(fixture.XmlPath);

        Assert.IsFalse(result.CanProceed);
        Assert.AreEqual("ntsc-rate-unsupported", result.Issues.Single().Code);
        Assert.IsFalse(result.Issues.Any(issue => issue.Code == "media-missing"));
    }

    [TestMethod]
    public void Inspect_RejectsClipRateDifferentFromSequence()
    {
        using var fixture = XmlFixture.Create(clipFrameRate: 24, clipNtsc: false);

        var result = _inspector.Inspect(fixture.XmlPath);

        Assert.IsFalse(result.CanProceed);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "clip-rate-unsupported"));
    }

    [TestMethod]
    public void TimeMath_ScalesLargeTickValuesWithoutInt64MultiplicationOverflow()
    {
        var samples = PremiereTimeMath.ScaleFloor(
            PremiereTimeMath.TicksPerSecond * 1_000_000,
            48_000,
            PremiereTimeMath.TicksPerSecond);

        Assert.AreEqual(48_000_000_000L, samples);
    }

    private static string FormatIssues(IEnumerable<InspectionIssue> issues) =>
        string.Join(Environment.NewLine, issues.Select(issue => $"{issue.Code}: {issue.Message}"));

    private sealed class XmlFixture : IDisposable
    {
        private XmlFixture(string rootDirectory, string xmlPath)
        {
            RootDirectory = rootDirectory;
            XmlPath = xmlPath;
        }

        public string RootDirectory { get; }

        public string XmlPath { get; }

        public static XmlFixture Create(
            bool overlap = false,
            bool includeFilter = false,
            bool includeNestedSequence = false,
            bool createMedia = true,
            bool includePproTicks = true,
            bool subframeRounding = false,
            bool retime = false,
            bool duplicateClipId = false,
            int waveSampleFrames = 192_000,
            int waveBits = 16,
            int xmlDepth = 16,
            string documentType = "<!DOCTYPE xmeml>",
            int sequenceFrameRate = 25,
            int? clipFrameRate = null,
            bool? sequenceNtsc = false,
            bool? clipNtsc = false)
        {
            var root = Path.Combine(Path.GetTempPath(), $"premiere-auto-xml-{Guid.NewGuid():N}");
            var mediaDirectory = Path.Combine(root, "Nghệ sĩ Ánh");
            Directory.CreateDirectory(mediaDirectory);
            var mediaPath = Path.Combine(mediaDirectory, "giọng & mic.wav");
            if (createMedia)
            {
                WritePcmWave(mediaPath, waveBits, sampleFrames: waveSampleFrames);
            }

            var xmlPath = Path.Combine(root, "chương trình.xml");
            var encodedLocalPath = string.Join(
                '/',
                mediaPath.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
            var fileUrl = SecurityElement.Escape($"file://localhost/{encodedLocalPath}")!;
            var secondStart = overlap ? 20 : 50;
            var firstEnd = retime ? 28 : subframeRounding ? 26 : 25;
            var effectiveClipFrameRate = clipFrameRate ?? sequenceFrameRate;
            var firstTickOffset = subframeRounding
                ? PremiereTimeMath.TicksPerSecond / effectiveClipFrameRate * 2 / 5
                : 0;
            var firstTicks = includePproTicks
                ? PproTicks(25, 50, effectiveClipFrameRate, firstTickOffset)
                : string.Empty;
            var secondTicks = includePproTicks
                ? PproTicks(50, 75, effectiveClipFrameRate)
                : string.Empty;
            var filter = includeFilter ? "<filter><effect><effectid>audiolevels</effectid></effect></filter>" : string.Empty;
            var nestedSequence = includeNestedSequence ? "<sequence id=\"nested-1\"><name>Nested</name></sequence>" : string.Empty;
            var secondClipId = duplicateClipId ? "clipitem-1" : "clipitem-2";
            var sequenceNtscElement = NtscElement(sequenceNtsc);
            var clipNtscElement = NtscElement(clipNtsc);
            var xml = $$"""
                <?xml version="1.0" encoding="UTF-8"?>
                {{documentType}}
                <xmeml version="4">
                  <sequence id="sequence-1">
                    <uuid>0d4640f7-401d-4631-8e39-ad861b953b29</uuid>
                    <duration>100</duration>
                    <rate><timebase>{{sequenceFrameRate}}</timebase>{{sequenceNtscElement}}</rate>
                    <name>Kiểm thử Unicode</name>
                    <media>
                      <video><track><enabled>TRUE</enabled><locked>FALSE</locked>{{nestedSequence}}</track></video>
                      <audio>
                        <numOutputChannels>2</numOutputChannels>
                        <format><samplecharacteristics><depth>16</depth><samplerate>48000</samplerate></samplecharacteristics></format>
                        <outputs>
                          <group><index>1</index><numchannels>1</numchannels><downmix>0</downmix><channel><index>1</index></channel></group>
                          <group><index>2</index><numchannels>1</numchannels><downmix>0</downmix><channel><index>2</index></channel></group>
                        </outputs>
                        <track premiereTrackType="Stereo">
                          <clipitem id="clipitem-1" premiereChannelType="mono">
                            <name>Giọng một</name><enabled>TRUE</enabled><duration>100</duration>
                            <rate><timebase>{{effectiveClipFrameRate}}</timebase>{{clipNtscElement}}</rate>
                            <start>0</start><end>{{firstEnd}}</end><in>25</in><out>50</out>
                            {{firstTicks}}
                            <file id="file-1">
                              <name>giọng &amp; mic.wav</name><pathurl>{{fileUrl}}</pathurl><duration>100</duration>
                              <media><audio><samplecharacteristics><depth>{{xmlDepth}}</depth><samplerate>48000</samplerate></samplecharacteristics><channelcount>1</channelcount></audio></media>
                            </file>
                            <sourcetrack><mediatype>audio</mediatype><trackindex>1</trackindex></sourcetrack>
                            {{filter}}
                          </clipitem>
                          <clipitem id="{{secondClipId}}" premiereChannelType="mono">
                            <name>Giọng hai</name><enabled>TRUE</enabled><duration>100</duration>
                            <rate><timebase>{{effectiveClipFrameRate}}</timebase>{{clipNtscElement}}</rate>
                            <start>{{secondStart}}</start><end>{{secondStart + 25}}</end><in>50</in><out>75</out>
                            {{secondTicks}}
                            <file id="file-1" />
                            <sourcetrack><mediatype>audio</mediatype><trackindex>1</trackindex></sourcetrack>
                          </clipitem>
                          <enabled>TRUE</enabled><locked>FALSE</locked><outputchannelindex>1</outputchannelindex>
                        </track>
                      </audio>
                    </media>
                  </sequence>
                </xmeml>
                """;
            File.WriteAllText(xmlPath, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return new(root, xmlPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, recursive: true);
            }
        }

        private static string NtscElement(bool? ntsc) =>
            ntsc.HasValue ? $"<ntsc>{(ntsc.Value ? "TRUE" : "FALSE")}</ntsc>" : string.Empty;

        private static string PproTicks(
            long sourceInFrame,
            long sourceOutFrame,
            int frameRate,
            long outTickOffset = 0)
        {
            var ticksIn = PremiereTimeMath.ScaleFloor(
                sourceInFrame,
                PremiereTimeMath.TicksPerSecond,
                frameRate);
            var ticksOut = checked(
                PremiereTimeMath.ScaleFloor(
                    sourceOutFrame,
                    PremiereTimeMath.TicksPerSecond,
                    frameRate) +
                outTickOffset);
            return $"<pproTicksIn>{ticksIn}</pproTicksIn><pproTicksOut>{ticksOut}</pproTicksOut>";
        }

        private static void WritePcmWave(string path, int bitsPerSample, int sampleFrames)
        {
            var blockAlign = checked(bitsPerSample / 8);
            var dataBytes = checked(sampleFrames * blockAlign);
            var bytes = new byte[44 + dataBytes];
            "RIFF"u8.CopyTo(bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), checked((uint)(bytes.Length - 8)));
            "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 16);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 48_000);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), checked((uint)(48_000 * blockAlign)));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), checked((ushort)blockAlign));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), checked((ushort)bitsPerSample));
            "data"u8.CopyTo(bytes.AsSpan(36));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), checked((uint)dataBytes));
            File.WriteAllBytes(path, bytes);
        }
    }
}
