using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PremiereAutoDialogueXml.Core.Media;
using PremiereAutoDialogueXml.Output.Audit;
using PremiereAutoDialogueXml.Validation;
using PremiereAutoDialogueXml.Core.Xml;

if (args.Length > 0 && args[0].Equals("--boundary", StringComparison.OrdinalIgnoreCase))
{
    return await WriteBoundaryReportAsync(args);
}

if (args.Length is not (4 or 5))
{
    Console.Error.WriteLine(
        "Cách dùng: PremiereAutoDialogueXml.VerifyPcm <audit.json> <track-number> <full-sequence-mono-48k.wav> <new-report.json> [source.xml]");
    Console.Error.WriteLine(
        "          PremiereAutoDialogueXml.VerifyPcm --boundary <audit.json> <track-number> <full-sequence-mono-48k.wav> <source.xml> <new-report.json>");
    return 2;
}

var auditPath = Path.GetFullPath(args[0]);
var wavPath = Path.GetFullPath(args[2]);
var reportPath = Path.GetFullPath(args[3]);
var sourceXmlPath = args.Length == 5 ? Path.GetFullPath(args[4]) : null;
if (!int.TryParse(args[1], out var trackIndex) || trackIndex <= 0)
{
    Console.Error.WriteLine("track-number phải là số nguyên bắt đầu từ 1.");
    return 2;
}

if (!File.Exists(auditPath) || !File.Exists(wavPath) || (sourceXmlPath is not null && !File.Exists(sourceXmlPath)))
{
    Console.Error.WriteLine("Không tìm thấy audit, WAV pilot hoặc XML nguồn.");
    return 2;
}

var reportDirectory = Path.GetDirectoryName(reportPath);
if (string.IsNullOrWhiteSpace(reportDirectory) || !Directory.Exists(reportDirectory))
{
    Console.Error.WriteLine("Thư mục chứa report phải tồn tại.");
    return 2;
}

if (File.Exists(reportPath))
{
    Console.Error.WriteLine("Report đã tồn tại; hãy chọn tên mới để không ghi đè bằng chứng.");
    return 2;
}

if (string.Equals(reportPath, auditPath, StringComparison.OrdinalIgnoreCase) ||
    string.Equals(reportPath, wavPath, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Report không được trùng audit hoặc WAV nguồn.");
    return 2;
}

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
};

try
{
    await using var auditStream = new FileStream(
        auditPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 64 * 1024,
        FileOptions.SequentialScan | FileOptions.Asynchronous);
    var audit = await JsonSerializer.DeserializeAsync<OutputAudit>(auditStream, jsonOptions)
        ?? throw new InvalidDataException("Audit JSON rỗng hoặc không hợp lệ.");

    var waveInspection = new WaveFileInspector().Inspect(wavPath);
    if (waveInspection.File is null)
    {
        foreach (var issue in waveInspection.Issues)
        {
            Console.Error.WriteLine($"{issue.Code}: {issue.Message}");
        }

        return 1;
    }

    IReadOnlyDictionary<string, WaveFileInfo>? sourceMediaByFileId = null;
    string? sourceXmlSha256 = null;
    if (sourceXmlPath is not null)
    {
        sourceXmlSha256 = await ComputeSha256Async(sourceXmlPath);
        if (!string.Equals(sourceXmlSha256, audit.SourceXmlSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("SHA-256 XML nguồn không khớp audit; từ chối đối chiếu media.");
        }

        var sourceInspection = new PremiereXmlInspector().Inspect(sourceXmlPath);
        if (!sourceInspection.CanProceed || sourceInspection.Project is null)
        {
            throw new InvalidDataException("XML nguồn không qua được inspector; từ chối đối chiếu media.");
        }

        sourceMediaByFileId = sourceInspection.Project.Sequence.AudioTracks
            .SelectMany(track => track.Clips)
            .GroupBy(clip => clip.SourceFileId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().SourceMedia.Wave, StringComparer.Ordinal);
    }

    var validation = new PcmTrackPeakValidator().Validate(
        audit,
        waveInspection.File,
        trackIndex,
        sourceMediaByFileId);
    var envelope = new PcmPilotEvidence(
        "1.2",
        DateTimeOffset.UtcNow,
        Path.GetFileName(auditPath),
        await ComputeSha256Async(auditPath),
        Path.GetFileName(wavPath),
        await ComputeSha256Async(wavPath),
        sourceXmlPath is null ? null : Path.GetFileName(sourceXmlPath),
        sourceXmlSha256,
        audit.RunId,
        audit.Model,
        audit.Preset,
        validation);

    var tempPath = Path.Combine(reportDirectory, $".{Path.GetFileName(reportPath)}.{Guid.NewGuid():N}.tmp");
    try
    {
        await using (var reportStream = new FileStream(
                         tempPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 64 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(reportStream, envelope, jsonOptions);
            await reportStream.FlushAsync();
        }

        File.Move(tempPath, reportPath);
    }
    finally
    {
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Report = Path.GetFileName(reportPath),
        validation.TrackIndex,
        validation.FrameRate,
        validation.SamplesPerFrame,
        validation.PhraseCount,
        validation.PassedPhraseCount,
        validation.FailedPhraseCount,
        validation.UncappedPhraseCount,
        validation.TargetPassedPhraseCount,
        validation.TargetFailedPhraseCount,
        validation.MedianObservedOffsetDb,
        validation.PhrasesMatchingMedianOffset,
        validation.PhrasesOutsideMedianOffset,
        validation.UsedSourceMedia,
        validation.SourceDerivedMedianOffsetDb,
        validation.SourceDerivedPhrasesMatchingMedianOffset,
        validation.SourceDerivedPhrasesOutsideMedianOffset,
        validation.AllWithinTolerance
    }, jsonOptions));
    return validation.AllWithinTolerance ? 0 : 1;
}
catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or ArgumentException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static async Task<int> WriteBoundaryReportAsync(string[] arguments)
{
    if (arguments.Length != 6)
    {
        Console.Error.WriteLine(
            "Cách dùng: --boundary <audit.json> <track-number> <full-sequence-mono-48k.wav> <source.xml> <new-report.json>");
        return 2;
    }

    var auditPath = Path.GetFullPath(arguments[1]);
    var wavPath = Path.GetFullPath(arguments[3]);
    var sourceXmlPath = Path.GetFullPath(arguments[4]);
    var reportPath = Path.GetFullPath(arguments[5]);
    if (!int.TryParse(arguments[2], out var trackIndex) || trackIndex <= 0)
    {
        Console.Error.WriteLine("track-number phải là số nguyên bắt đầu từ 1.");
        return 2;
    }

    if (!File.Exists(auditPath) || !File.Exists(wavPath) || !File.Exists(sourceXmlPath))
    {
        Console.Error.WriteLine("Không tìm thấy audit, WAV pilot hoặc XML nguồn.");
        return 2;
    }

    var reportDirectory = Path.GetDirectoryName(reportPath);
    if (string.IsNullOrWhiteSpace(reportDirectory) || !Directory.Exists(reportDirectory))
    {
        Console.Error.WriteLine("Thư mục chứa report phải tồn tại.");
        return 2;
    }

    if (File.Exists(reportPath) ||
        string.Equals(reportPath, auditPath, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(reportPath, wavPath, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(reportPath, sourceXmlPath, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Boundary report phải là file mới và không được trùng input.");
        return 2;
    }

    var jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    try
    {
        await using var auditStream = new FileStream(
            auditPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
        var audit = await JsonSerializer.DeserializeAsync<OutputAudit>(auditStream, jsonOptions)
            ?? throw new InvalidDataException("Audit JSON rỗng hoặc không hợp lệ.");
        var sourceXmlSha256 = await ComputeSha256Async(sourceXmlPath);
        if (!string.Equals(sourceXmlSha256, audit.SourceXmlSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("SHA-256 XML nguồn không khớp audit; từ chối quét PCM boundary.");
        }

        var sourceInspection = new PremiereXmlInspector().Inspect(sourceXmlPath);
        if (!sourceInspection.CanProceed || sourceInspection.Project is null)
        {
            throw new InvalidDataException("XML nguồn không qua inspector; từ chối quét PCM boundary.");
        }

        var waveInspection = new WaveFileInspector().Inspect(wavPath);
        if (waveInspection.File is null)
        {
            throw new InvalidDataException(string.Join(
                " | ",
                waveInspection.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
        }

        var scan = new PremierePcmBoundaryScanner().Scan(
            audit,
            waveInspection.File,
            trackIndex);
        var envelope = new PcmBoundaryEvidence(
            "1.0",
            DateTimeOffset.UtcNow,
            Path.GetFileName(auditPath),
            await ComputeSha256Async(auditPath),
            Path.GetFileName(wavPath),
            await ComputeSha256Async(wavPath),
            Path.GetFileName(sourceXmlPath),
            sourceXmlSha256,
            audit.RunId,
            scan);
        var tempPath = Path.Combine(
            reportDirectory,
            $".{Path.GetFileName(reportPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var reportStream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(reportStream, envelope, jsonOptions);
                await reportStream.FlushAsync();
            }

            File.Move(tempPath, reportPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Report = Path.GetFileName(reportPath),
            scan.Policy,
            scan.TrackIndex,
            scan.TransitionCount,
            scan.TransientScreeningCandidateCount,
            scan.EnabledToDisabledCandidateCount,
            scan.DisabledToEnabledCandidateCount,
            scan.GainChangeCandidateCount,
            scan.MaximumActualStepDbfs,
            scan.P95ActualStepDbfs,
            scan.MaximumStepAboveLocalP99Db,
            scan.TransitionStreamSha256,
            scan.CapturedSampleCount
        }, jsonOptions));
        return 0;
    }
    catch (Exception exception) when (
        exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or
        ArgumentException or OverflowException)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}

static async Task<string> ComputeSha256Async(string path)
{
    await using var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 64 * 1024,
        FileOptions.SequentialScan | FileOptions.Asynchronous);
    var hash = await SHA256.HashDataAsync(stream);
    return Convert.ToHexString(hash);
}

internal sealed record PcmPilotEvidence(
    string SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    string AuditFileName,
    string AuditSha256,
    string WavFileName,
    string WavSha256,
    string? SourceXmlFileName,
    string? SourceXmlSha256,
    string RunId,
    ModelAudit Model,
    PresetAudit Preset,
    PcmTrackPeakValidationReport Validation);

internal sealed record PcmBoundaryEvidence(
    string SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    string AuditFileName,
    string AuditSha256,
    string WavFileName,
    string WavSha256,
    string SourceXmlFileName,
    string SourceXmlSha256,
    string RunId,
    PremierePcmBoundaryScanReport Scan);
