using System.Security.Cryptography;
using System.Text.Json;
using PremiereAutoDialogueXml.App.Workflow;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class FailureDiagnosticWriterTests
{
    [TestMethod]
    public async Task WriteAsyncCreatesBoundedDiagnosticWithoutFullXmlPath()
    {
        using var fixture = new TemporaryDirectory();
        var time = new DateTimeOffset(2026, 8, 3, 5, 0, 0, TimeSpan.Zero);
        var writer = new FailureDiagnosticWriter(
            () => time,
            () => Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

        var path = await writer.WriteAsync(new(
            fixture.Path,
            "audio-analysis",
            @"F:\private\show.xml",
            "SOURCE-SHA",
            new InvalidOperationException(new string('x', 3_000))));

        Assert.IsTrue(File.Exists(path));
        Assert.HasCount(0, Directory.GetFiles(fixture.Path, "*.tmp"));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.AreEqual("show.xml", json.RootElement.GetProperty("xmlFileName").GetString());
        Assert.AreEqual(2_048, json.RootElement.GetProperty("message").GetString()!.Length);
        Assert.AreEqual("SOURCE-SHA", json.RootElement.GetProperty("sourceXmlSha256").GetString());
        Assert.IsFalse((await File.ReadAllTextAsync(path)).Contains(@"F:\private", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task WriteAsyncRefusesCollisionAndPreservesExistingDiagnostic()
    {
        using var fixture = new TemporaryDirectory();
        var writer = new FailureDiagnosticWriter(
            () => new DateTimeOffset(2026, 8, 3, 5, 0, 0, TimeSpan.Zero),
            () => Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        var request = new FailureDiagnosticRequest(
            fixture.Path,
            "output-write",
            "show.xml",
            "SOURCE-SHA",
            new IOException("write failed"));
        var first = await writer.WriteAsync(request);
        var firstHash = Hash(first);

        await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(request));

        Assert.AreEqual(firstHash, Hash(first));
        Assert.HasCount(1, Directory.GetFiles(fixture.Path, "*.json"));
        Assert.HasCount(0, Directory.GetFiles(fixture.Path, "*.tmp"));
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"padx-diagnostic-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
