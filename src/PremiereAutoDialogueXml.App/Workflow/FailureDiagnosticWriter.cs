using System.IO;
using System.Text.Json;

namespace PremiereAutoDialogueXml.App.Workflow;

public sealed class FailureDiagnosticWriter
{
    private const int MaximumMessageCharacters = 2_048;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<Guid> _guidFactory;

    public FailureDiagnosticWriter(
        Func<DateTimeOffset>? utcNow = null,
        Func<Guid>? guidFactory = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _guidFactory = guidFactory ?? Guid.NewGuid;
    }

    public async Task<string> WriteAsync(
        FailureDiagnosticRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Stage);
        ArgumentNullException.ThrowIfNull(request.Exception);
        cancellationToken.ThrowIfCancellationRequested();

        var parent = Path.GetFullPath(request.OutputDirectory);
        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException("Thư mục lưu diagnostic không tồn tại.");
        }

        var createdAt = _utcNow().ToUniversalTime();
        var stem = $"PremiereAutoDialogueXml-diagnostic-{createdAt:yyyyMMdd-HHmmss}-{_guidFactory():N}";
        var finalPath = Path.Combine(parent, $"{stem}.json");
        var tempPath = Path.Combine(parent, $".{stem}.{_guidFactory():N}.tmp");
        var diagnostic = new FailureDiagnostic(
            SchemaVersion: "1.0",
            CreatedAtUtc: createdAt,
            Stage: Limit(request.Stage),
            XmlFileName: Limit(SafeFileName(request.XmlFileName)),
            SourceXmlSha256: request.SourceXmlSha256,
            ErrorType: request.Exception.GetType().FullName ?? request.Exception.GetType().Name,
            Message: Limit(request.Exception.Message));

        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             new FileStreamOptions
                             {
                                 Access = FileAccess.Write,
                                 Mode = FileMode.CreateNew,
                                 Share = FileShare.None,
                                 Options = FileOptions.Asynchronous | FileOptions.WriteThrough
                             }))
            {
                await JsonSerializer.SerializeAsync(stream, diagnostic, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, finalPath);
            return finalPath;
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
    }

    private static string Limit(string? value)
    {
        var safe = value?.Trim() ?? string.Empty;
        return safe.Length <= MaximumMessageCharacters
            ? safe
            : safe[..MaximumMessageCharacters];
    }

    private static string SafeFileName(string value)
    {
        try
        {
            return Path.GetFileName(value);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return string.Empty;
        }
    }

    private sealed record FailureDiagnostic(
        string SchemaVersion,
        DateTimeOffset CreatedAtUtc,
        string Stage,
        string XmlFileName,
        string? SourceXmlSha256,
        string ErrorType,
        string Message);
}
