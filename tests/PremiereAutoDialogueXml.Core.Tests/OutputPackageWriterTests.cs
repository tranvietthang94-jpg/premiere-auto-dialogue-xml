using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Output;
using PremiereAutoDialogueXml.Output.Xml;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class OutputPackageWriterTests
{
    [TestMethod]
    public async Task WriteAsyncCreatesAtomicXmlAndAuditWithoutChangingSource()
    {
        using var fixture = PremiereXmlGeneratorTests.WriterFixture.Create();
        var sourceHashBefore = Hash(fixture.Project.SourceXmlPath);
        var writer = new OutputPackageWriter();

        var result = await writer.WriteAsync(new(
            fixture.Project,
            fixture.Analysis,
            DialogueProcessingPreset.Balanced,
            fixture.Directory));

        Assert.IsTrue(File.Exists(result.XmlPath));
        Assert.IsTrue(File.Exists(result.AuditPath));
        Assert.IsNotNull(result.ReviewCsvPath);
        Assert.IsTrue(File.Exists(result.ReviewCsvPath));
        Assert.AreEqual(sourceHashBefore, Hash(fixture.Project.SourceXmlPath));
        Assert.AreEqual(result.OutputXmlSha256, Hash(result.XmlPath));
        var reloaded = PremiereAutoDialogueXml.Core.Xml.PremiereXmlDocumentLoader.Load(
            result.XmlPath,
            result.OutputXmlSha256);
        Assert.AreEqual("xmeml", reloaded.Document.Root!.Name.LocalName);
        Assert.AreEqual(4, result.FragmentCount);
        Assert.AreEqual(1, result.MarkerCount);
        Assert.AreEqual(1, result.ReviewGroupCount);
        Assert.IsFalse(Directory.EnumerateFiles(result.RunDirectory).Any(path => path.EndsWith(".tmp", StringComparison.Ordinal)));

        using var audit = JsonDocument.Parse(await File.ReadAllTextAsync(result.AuditPath));
        var root = audit.RootElement;
        Assert.AreEqual("1.4", root.GetProperty("schemaVersion").GetString());
        Assert.AreEqual(fixture.Project.SourceXmlSha256, root.GetProperty("sourceXmlSha256").GetString());
        Assert.AreEqual(result.OutputXmlSha256, root.GetProperty("outputXmlSha256").GetString());
        Assert.AreEqual("6.2.1", root.GetProperty("model").GetProperty("version").GetString());
        Assert.AreEqual(
            "mono-center-equal-power-to-stereo",
            root.GetProperty("preset").GetProperty("premiereRoutingProfile").GetString());
        Assert.AreEqual(
            3.010299956639812,
            root.GetProperty("preset").GetProperty("premiereCenterPanCompensationDb").GetDouble(),
            0.000001);
        Assert.AreEqual(
            "max-direct-speech-and-frame-aligned-enabled-phrase-peak",
            root.GetProperty("preset").GetProperty("gainReferencePeakPolicy").GetString());
        Assert.IsTrue(root.GetProperty("preset").GetProperty("preserveVadNegativeHighEnergyConflicts").GetBoolean());
        Assert.AreEqual(4, root.GetProperty("fragments").GetArrayLength());
        Assert.AreEqual("speech", root.GetProperty("fragments")[1].GetProperty("status").GetString());
        Assert.AreEqual(
            9.010299956639812,
            root.GetProperty("fragments")[1].GetProperty("appliedGainDb").GetDouble(),
            0.001);
        var review = root.GetProperty("review");
        Assert.AreEqual(Path.GetFileName(result.ReviewCsvPath), review.GetProperty("fileName").GetString());
        Assert.AreEqual(Hash(result.ReviewCsvPath), review.GetProperty("sha256").GetString());
        Assert.AreEqual("sequence-relative-ndf", review.GetProperty("timecodeBasis").GetString());
        Assert.AreEqual(1, review.GetProperty("ambiguousMarkerCount").GetInt32());
        Assert.AreEqual(1, review.GetProperty("groupCount").GetInt32());
        Assert.AreEqual(0, review.GetProperty("shadowEvidence").GetArrayLength());
        Assert.AreEqual("low", review.GetProperty("groups")[0].GetProperty("priority").GetString());

        var reviewBytes = await File.ReadAllBytesAsync(result.ReviewCsvPath);
        CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }, reviewBytes[..3]);
        var reviewCsv = await File.ReadAllTextAsync(result.ReviewCsvPath);
        StringAssert.Contains(reviewCsv, "TC tương đối vào");
        StringAssert.Contains(reviewCsv, "voice.wav");
        Assert.IsFalse(reviewCsv.Contains(fixture.Directory, StringComparison.OrdinalIgnoreCase));

        var xmlStart = await File.ReadAllTextAsync(result.XmlPath);
        StringAssert.Contains(xmlStart, "<!DOCTYPE xmeml>");
        StringAssert.Contains(xmlStart, "Show - AUTO AUDIO");
    }

    [TestMethod]
    public async Task WriteAsyncRefusesRunDirectoryCollisionWithoutOverwriting()
    {
        using var fixture = PremiereXmlGeneratorTests.WriterFixture.Create();
        var fixedTime = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var fixedGuid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var writer = new OutputPackageWriter(
            new PremiereXmlGenerator(),
            () => fixedTime,
            () => fixedGuid);
        var request = new OutputPackageRequest(
            fixture.Project,
            fixture.Analysis,
            DialogueProcessingPreset.Balanced,
            fixture.Directory);
        var first = await writer.WriteAsync(request);
        var firstXmlHash = Hash(first.XmlPath);

        await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(request));

        Assert.AreEqual(firstXmlHash, Hash(first.XmlPath));
        Assert.IsTrue(File.Exists(first.AuditPath));
        Assert.IsTrue(File.Exists(first.ReviewCsvPath));
    }

    [TestMethod]
    public async Task WriteAsyncPersistsShadowEvidenceWithoutChangingXml()
    {
        using var fixture = PremiereXmlGeneratorTests.WriterFixture.Create();
        var track = fixture.Analysis.Tracks.Single();
        var segments = track.Segments
            .Select(segment => segment.Status == AudioSegmentStatus.Ambiguous
                ? segment with { Reason = "ambiguous-energy-vad-conflict" }
                : segment)
            .ToArray();
        var comparison = new BleedEvidence(2, 8, 0.94f, 3, -24, -16, -8);
        var analysis = fixture.Analysis with
        {
            Tracks = [track with { Segments = segments }],
            ShadowEvidence =
            [
                new(
                    1,
                    "clip-source",
                    11_520,
                    15_360,
                    "ambiguous-energy-vad-conflict",
                    CrossTrackShadowOutcome.LikelyBleed,
                    11_520,
                    15_360,
                    comparison)
            ]
        };
        var baseline = await new OutputPackageWriter(DeterministicGenerator()).WriteAsync(new(
            fixture.Project,
            analysis with { ShadowEvidence = [] },
            DialogueProcessingPreset.Balanced,
            fixture.Directory));
        var result = await new OutputPackageWriter(DeterministicGenerator()).WriteAsync(new(
            fixture.Project,
            analysis,
            DialogueProcessingPreset.Balanced,
            fixture.Directory));

        Assert.AreEqual(baseline.OutputXmlSha256, result.OutputXmlSha256);
        using var audit = JsonDocument.Parse(await File.ReadAllTextAsync(result.AuditPath));
        var review = audit.RootElement.GetProperty("review");
        Assert.AreEqual(1, review.GetProperty("shadowEvidence").GetArrayLength());
        Assert.AreEqual("likelyBleed", review.GetProperty("groups")[0].GetProperty("representativeShadowOutcome").GetString());
        Assert.AreEqual("medium", review.GetProperty("groups")[0].GetProperty("priority").GetString());
        StringAssert.Contains(await File.ReadAllTextAsync(result.ReviewCsvPath!), "Có khả năng bleed");
    }

    [TestMethod]
    public async Task WriteAsyncStreamingXmlMatchesDomGenerator()
    {
        using var fixture = PremiereXmlGeneratorTests.WriterFixture.Create(gainWasCapped: true);
        var expected = DeterministicGenerator().Generate(fixture.Project, fixture.Analysis);

        var result = await new OutputPackageWriter(DeterministicGenerator()).WriteAsync(new(
            fixture.Project,
            fixture.Analysis,
            DialogueProcessingPreset.Balanced,
            fixture.Directory));
        var actual = PremiereAutoDialogueXml.Core.Xml.PremiereXmlDocumentLoader.Load(
            result.XmlPath,
            result.OutputXmlSha256).Document;

        Assert.IsTrue(XNode.DeepEquals(
            WithoutInsignificantWhitespace(expected.Document.Root!),
            WithoutInsignificantWhitespace(actual.Root!)));
        var hashException = Assert.ThrowsExactly<PremiereAutoDialogueXml.Core.Xml.PremiereXmlLoadException>(() =>
            PremiereAutoDialogueXml.Core.Xml.PremiereXmlDocumentLoader.ValidateGeneratedOutputSyntax(
                result.XmlPath,
                "WRONG-SHA256"));
        Assert.AreEqual("xml-source-changed", hashException.Code);
    }

    [TestMethod]
    public async Task WriteAsyncPreCancelledLeavesNoRunDirectory()
    {
        using var fixture = PremiereXmlGeneratorTests.WriterFixture.Create();
        var before = Directory.GetDirectories(fixture.Directory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => new OutputPackageWriter().WriteAsync(
            new(
                fixture.Project,
                fixture.Analysis,
                DialogueProcessingPreset.Balanced,
                fixture.Directory),
            cancellation.Token));

        CollectionAssert.AreEqual(before, Directory.GetDirectories(fixture.Directory));
    }

    [TestMethod]
    public async Task WriteAsyncStaleSourceLeavesNoRunDirectory()
    {
        using var fixture = PremiereXmlGeneratorTests.WriterFixture.Create();
        File.AppendAllText(fixture.Project.SourceXmlPath, " ");
        var before = Directory.GetDirectories(fixture.Directory);

        await Assert.ThrowsExactlyAsync<PremiereAutoDialogueXml.Core.Xml.PremiereXmlLoadException>(
            () => new OutputPackageWriter().WriteAsync(new(
                fixture.Project,
                fixture.Analysis,
                DialogueProcessingPreset.Balanced,
                fixture.Directory)));

        CollectionAssert.AreEqual(before, Directory.GetDirectories(fixture.Directory));
    }

    [TestMethod]
    public async Task WriteAsyncSanitizesReservedAndTrailingWindowsFileName()
    {
        using var fixture = PremiereXmlGeneratorTests.WriterFixture.Create();
        var project = fixture.Project with
        {
            Sequence = fixture.Project.Sequence with { Name = "CON. " }
        };

        var result = await new OutputPackageWriter().WriteAsync(new(
            project,
            fixture.Analysis,
            DialogueProcessingPreset.Balanced,
            fixture.Directory));

        Assert.AreEqual("_CON_AutoAudio.xml", Path.GetFileName(result.XmlPath));
        Assert.IsTrue(Path.GetFileName(result.RunDirectory).StartsWith("_CON_AutoAudio_", StringComparison.Ordinal));
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static PremiereXmlGenerator DeterministicGenerator()
    {
        var counter = 0;
        return new(() => Guid.Parse($"00000000-0000-0000-0000-{++counter:D12}"));
    }

    private static XElement WithoutInsignificantWhitespace(XElement element)
    {
        var clone = new XElement(element);
        foreach (var whitespace in clone.DescendantNodes().OfType<XText>()
                     .Where(text => string.IsNullOrWhiteSpace(text.Value)).ToArray())
        {
            whitespace.Remove();
        }

        return clone;
    }
}
