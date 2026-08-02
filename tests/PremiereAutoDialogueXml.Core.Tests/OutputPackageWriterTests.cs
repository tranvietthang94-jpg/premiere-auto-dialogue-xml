using System.Security.Cryptography;
using System.Text.Json;
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
        Assert.AreEqual(sourceHashBefore, Hash(fixture.Project.SourceXmlPath));
        Assert.AreEqual(result.OutputXmlSha256, Hash(result.XmlPath));
        var reloaded = PremiereAutoDialogueXml.Core.Xml.PremiereXmlDocumentLoader.Load(
            result.XmlPath,
            result.OutputXmlSha256);
        Assert.AreEqual("xmeml", reloaded.Document.Root!.Name.LocalName);
        Assert.AreEqual(4, result.FragmentCount);
        Assert.AreEqual(1, result.MarkerCount);
        Assert.IsFalse(Directory.EnumerateFiles(result.RunDirectory).Any(path => path.EndsWith(".tmp", StringComparison.Ordinal)));

        using var audit = JsonDocument.Parse(await File.ReadAllTextAsync(result.AuditPath));
        var root = audit.RootElement;
        Assert.AreEqual(fixture.Project.SourceXmlSha256, root.GetProperty("sourceXmlSha256").GetString());
        Assert.AreEqual(result.OutputXmlSha256, root.GetProperty("outputXmlSha256").GetString());
        Assert.AreEqual("6.2.1", root.GetProperty("model").GetProperty("version").GetString());
        Assert.AreEqual(4, root.GetProperty("fragments").GetArrayLength());
        Assert.AreEqual("speech", root.GetProperty("fragments")[1].GetProperty("status").GetString());
        Assert.AreEqual(6d, root.GetProperty("fragments")[1].GetProperty("appliedGainDb").GetDouble(), 0.001);

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

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
