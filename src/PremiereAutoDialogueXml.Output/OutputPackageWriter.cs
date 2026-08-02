using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using PremiereAutoDialogueXml.Output.Audit;
using PremiereAutoDialogueXml.Output.Xml;

namespace PremiereAutoDialogueXml.Output;

public sealed class OutputPackageWriter
{
    private static readonly JsonSerializerOptions AuditJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly PremiereXmlGenerator _xmlGenerator;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<Guid> _guidFactory;

    public OutputPackageWriter(
        PremiereXmlGenerator? xmlGenerator = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<Guid>? guidFactory = null)
    {
        _xmlGenerator = xmlGenerator ?? new PremiereXmlGenerator();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _guidFactory = guidFactory ?? Guid.NewGuid;
    }

    public async Task<OutputPackageResult> WriteAsync(
        OutputPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Project);
        ArgumentNullException.ThrowIfNull(request.Analysis);
        ArgumentNullException.ThrowIfNull(request.Preset);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputParentDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        var parent = Path.GetFullPath(request.OutputParentDirectory);
        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException("Thư mục lưu kết quả không tồn tại.");
        }

        var generatedAt = _utcNow().ToUniversalTime();
        var runId = $"{generatedAt:yyyyMMdd-HHmmss}-{_guidFactory():N}";
        var safeSequenceName = MakeSafeFileName(request.Project.Sequence.Name);
        var runDirectory = Path.Combine(parent, $"{safeSequenceName}_AutoAudio_{runId}");
        if (Directory.Exists(runDirectory) || File.Exists(runDirectory))
        {
            throw new IOException("Thư mục run đã tồn tại; app không ghi đè kết quả cũ.");
        }

        var generated = _xmlGenerator.Generate(request.Project, request.Analysis, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var xmlFileName = $"{safeSequenceName}_AutoAudio.xml";
        var auditFileName = $"{safeSequenceName}_AutoAudio.audit.json";
        var xmlPath = Path.Combine(runDirectory, xmlFileName);
        var auditPath = Path.Combine(runDirectory, auditFileName);
        var xmlTempPath = Path.Combine(runDirectory, $".{xmlFileName}.{_guidFactory():N}.tmp");
        var auditTempPath = Path.Combine(runDirectory, $".{auditFileName}.{_guidFactory():N}.tmp");
        var createdDirectory = false;

        try
        {
            Directory.CreateDirectory(runDirectory);
            createdDirectory = true;
            cancellationToken.ThrowIfCancellationRequested();

            await WriteXmlAsync(generated.Document, xmlTempPath, cancellationToken);
            var outputXmlSha256 = await ComputeSha256Async(xmlTempPath, cancellationToken);
            var phrases = request.Analysis.Tracks
                .SelectMany(track => track.Phrases)
                .ToDictionary(phrase => phrase.Id, StringComparer.Ordinal);
            var audit = new OutputAudit(
                SchemaVersion: "1.0",
                RunId: runId,
                CreatedAtUtc: generatedAt,
                SourceXmlFileName: Path.GetFileName(request.Project.SourceXmlPath),
                SourceXmlSha256: request.Project.SourceXmlSha256,
                OutputXmlFileName: xmlFileName,
                OutputXmlSha256: outputXmlSha256,
                SourceSequenceId: request.Project.Sequence.Id,
                OutputSequenceId: generated.SequenceId,
                OutputSequenceUuid: generated.SequenceUuid,
                OutputSequenceName: generated.SequenceName,
                Model: new(request.Analysis.ModelVersion, request.Analysis.ModelSha256),
                Preset: PresetAudit.From(request.Preset),
                Fragments: generated.AudioFragments.Select(fragment => FragmentAudit.From(fragment, phrases)).ToArray(),
                Markers: generated.Markers.Select(MarkerAudit.From).ToArray());

            await WriteAuditAsync(audit, auditTempPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(xmlTempPath, xmlPath);
            File.Move(auditTempPath, auditPath);

            return new(
                runDirectory,
                xmlPath,
                auditPath,
                outputXmlSha256,
                generated.AudioFragments.Count,
                generated.Markers.Count);
        }
        catch
        {
            DeleteIfExists(xmlTempPath);
            DeleteIfExists(auditTempPath);
            DeleteIfExists(xmlPath);
            DeleteIfExists(auditPath);
            if (createdDirectory && Directory.Exists(runDirectory) && !Directory.EnumerateFileSystemEntries(runDirectory).Any())
            {
                Directory.Delete(runDirectory);
            }

            throw;
        }
    }

    private static async Task WriteXmlAsync(
        System.Xml.Linq.XDocument document,
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            });
        var settings = new XmlWriterSettings
        {
            Async = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            IndentChars = "\t",
            NewLineChars = "\r\n",
            NewLineHandling = NewLineHandling.Replace,
            OmitXmlDeclaration = false,
            CloseOutput = false
        };
        await using var writer = XmlWriter.Create(stream, settings);
        await writer.WriteStartDocumentAsync();
        await writer.WriteRawAsync("\r\n<!DOCTYPE xmeml>\r\n");
        await (document.Root ?? throw new InvalidDataException("XML kết quả không có root xmeml."))
            .WriteToAsync(writer, cancellationToken);
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task WriteAuditAsync(
        OutputAudit audit,
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            });
        await JsonSerializer.SerializeAsync(stream, audit, AuditJsonOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Access = FileAccess.Read,
                Mode = FileMode.Open,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value
            .Trim()
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "Sequence";
        }

        return sanitized.Length <= 80 ? sanitized : sanitized[..80];
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
