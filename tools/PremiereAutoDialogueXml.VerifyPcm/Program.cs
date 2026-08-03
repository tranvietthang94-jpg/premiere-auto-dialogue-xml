using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PremiereAutoDialogueXml.Core.Media;
using PremiereAutoDialogueXml.Output.Audit;
using PremiereAutoDialogueXml.Validation;

if (args.Length != 4)
{
    Console.Error.WriteLine(
        "Cách dùng: PremiereAutoDialogueXml.VerifyPcm <audit.json> <track-number> <full-sequence-mono-48k.wav> <new-report.json>");
    return 2;
}

var auditPath = Path.GetFullPath(args[0]);
var wavPath = Path.GetFullPath(args[2]);
var reportPath = Path.GetFullPath(args[3]);
if (!int.TryParse(args[1], out var trackIndex) || trackIndex <= 0)
{
    Console.Error.WriteLine("track-number phải là số nguyên bắt đầu từ 1.");
    return 2;
}

if (!File.Exists(auditPath) || !File.Exists(wavPath))
{
    Console.Error.WriteLine("Không tìm thấy audit hoặc WAV pilot.");
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

    var validation = new PcmTrackPeakValidator().Validate(audit, waveInspection.File, trackIndex);
    var envelope = new PcmPilotEvidence(
        "1.0",
        DateTimeOffset.UtcNow,
        Path.GetFileName(auditPath),
        await ComputeSha256Async(auditPath),
        Path.GetFileName(wavPath),
        await ComputeSha256Async(wavPath),
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
        validation.PhraseCount,
        validation.PassedPhraseCount,
        validation.FailedPhraseCount,
        validation.AllWithinTolerance
    }, jsonOptions));
    return validation.AllWithinTolerance ? 0 : 1;
}
catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or ArgumentException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
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
    string RunId,
    ModelAudit Model,
    PresetAudit Preset,
    PcmTrackPeakValidationReport Validation);
