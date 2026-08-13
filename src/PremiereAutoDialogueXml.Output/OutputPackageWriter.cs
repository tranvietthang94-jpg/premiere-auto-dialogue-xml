using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Output.Audit;
using PremiereAutoDialogueXml.Output.Review;
using PremiereAutoDialogueXml.Output.Validation;
using PremiereAutoDialogueXml.Output.Xml;

namespace PremiereAutoDialogueXml.Output;

public sealed class OutputPackageWriter
{
    private static readonly HashSet<string> ReservedWindowsFileNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

    private static readonly JsonSerializerOptions AuditJsonOptions = new()
    {
        WriteIndented = false,
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

        var generated = _xmlGenerator.GeneratePlan(request.Project, request.Analysis, cancellationToken);
        new OutputDecisionContractValidator().Validate(
            request.Project,
            request.Analysis,
            request.Preset,
            generated);
        cancellationToken.ThrowIfCancellationRequested();

        var xmlFileName = $"{safeSequenceName}_AutoAudio.xml";
        var auditFileName = $"{safeSequenceName}_AutoAudio.audit.json";
        var reviewFileName = $"{safeSequenceName}_AutoAudio.review.csv";
        var xmlPath = Path.Combine(runDirectory, xmlFileName);
        var auditPath = Path.Combine(runDirectory, auditFileName);
        var reviewPath = Path.Combine(runDirectory, reviewFileName);
        var xmlTempPath = Path.Combine(runDirectory, $".{xmlFileName}.{_guidFactory():N}.tmp");
        var auditTempPath = Path.Combine(runDirectory, $".{auditFileName}.{_guidFactory():N}.tmp");
        var reviewTempPath = Path.Combine(runDirectory, $".{reviewFileName}.{_guidFactory():N}.tmp");
        var createdDirectory = false;

        try
        {
            Directory.CreateDirectory(runDirectory);
            createdDirectory = true;
            cancellationToken.ThrowIfCancellationRequested();

            await PremiereXmlStreamingWriter.WriteAsync(
                request.Project,
                generated,
                xmlTempPath,
                cancellationToken);
            var outputXmlSha256 = await ComputeSha256Async(xmlTempPath, cancellationToken);
            PremiereAutoDialogueXml.Core.Xml.PremiereXmlDocumentLoader.ValidateGeneratedOutputSyntax(
                xmlTempPath,
                outputXmlSha256);
            var reviewGroups = new ReviewGroupBuilder().Build(
                generated.AudioFragments,
                generated.Markers,
                request.Analysis.ShadowEvidence,
                request.Project.Sequence.FrameRate,
                request.Project.Sequence.AudioSampleRate);
            await ReviewCsvWriter.WriteAsync(reviewGroups, reviewTempPath, cancellationToken);
            var reviewSha256 = await ComputeSha256Async(reviewTempPath, cancellationToken);
            var phrases = request.Analysis.Tracks
                .SelectMany(track => track.Phrases)
                .ToDictionary(phrase => phrase.Id, StringComparer.Ordinal);
            var audit = new OutputAudit(
                SchemaVersion: "1.7",
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
                Markers: generated.Markers.Select(MarkerAudit.From).ToArray())
            {
                VadFrontEndComparison = request.Analysis.VadFrontEndComparison,
                NoiseBoundaryComparison = request.Analysis.NoiseBoundaryComparison,
                CalibratedBleedShadow = request.Analysis.CalibratedBleedShadow,
                Review = new(
                    FileName: reviewFileName,
                    Sha256: reviewSha256,
                    TimecodeBasis: ReviewGroupBuilder.TimecodeBasis,
                    FrameRate: request.Project.Sequence.FrameRate,
                    GroupingGapFrames: ReviewGroupBuilder.DefaultGroupingGapFrames,
                    AmbiguousMarkerCount: ReviewGroupBuilder.CountAmbiguousMarkers(generated.Markers),
                    GroupCount: reviewGroups.Count,
                    ShadowEvidence: request.Analysis.ShadowEvidence.ToArray(),
                    Groups: reviewGroups)
            };

            var expectedAuditSha256 = await WriteAuditAsync(audit, auditTempPath, cancellationToken);
            await ValidateWrittenAuditAsync(expectedAuditSha256, auditTempPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(xmlTempPath, xmlPath);
            File.Move(reviewTempPath, reviewPath);
            File.Move(auditTempPath, auditPath);

            return new(
                runDirectory,
                xmlPath,
                auditPath,
                outputXmlSha256,
                generated.AudioFragments.Count,
                generated.Markers.Count)
            {
                ReviewCsvPath = reviewPath,
                ReviewGroupCount = reviewGroups.Count
            };
        }
        catch
        {
            DeleteIfExists(xmlTempPath);
            DeleteIfExists(auditTempPath);
            DeleteIfExists(reviewTempPath);
            DeleteIfExists(xmlPath);
            DeleteIfExists(auditPath);
            DeleteIfExists(reviewPath);
            if (createdDirectory && Directory.Exists(runDirectory) && !Directory.EnumerateFileSystemEntries(runDirectory).Any())
            {
                Directory.Delete(runDirectory);
            }

            throw;
        }
    }

    private static async Task<string> WriteAuditAsync(
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
        using var sha256 = SHA256.Create();
        await using (var hashingStream = new CryptoStream(
                         stream,
                         sha256,
                         CryptoStreamMode.Write,
                         leaveOpen: true))
        {
            await JsonSerializer.SerializeAsync(hashingStream, audit, AuditJsonOptions, cancellationToken);
        }

        await stream.FlushAsync(cancellationToken);
        return Convert.ToHexString(
            sha256.Hash ?? throw new CryptographicException("Không thể tính SHA-256 của audit vừa ghi."));
    }

    private static async Task ValidateWrittenAuditAsync(
        string expectedSha256,
        string path,
        CancellationToken cancellationToken)
    {
        var actualSha256 = await ComputeSha256Async(path, cancellationToken);
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Audit đọc lại không khớp dữ liệu đã ghi.");
        }
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
            .ToArray())
            .TrimEnd(' ', '.');
        if (sanitized.Length > 80)
        {
            sanitized = sanitized[..80].TrimEnd(' ', '.');
        }

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "Sequence";
        }

        var baseName = Path.GetFileNameWithoutExtension(sanitized);
        if (ReservedWindowsFileNames.Contains(baseName))
        {
            sanitized = $"_{sanitized}";
        }

        return sanitized;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
