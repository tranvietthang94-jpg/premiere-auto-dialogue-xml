using System.Globalization;
using System.Text;
using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Output.Audit;

namespace PremiereAutoDialogueXml.Output.Review;

internal static class ReviewCsvWriter
{
    private static readonly string[] Headers =
    [
        "Mã",
        "Ưu tiên",
        "Track",
        "TC tương đối vào",
        "TC tương đối ra",
        "Frame vào",
        "Frame ra",
        "Số marker",
        "Lý do",
        "Đánh giá shadow",
        "Track đối chiếu",
        "Chênh lệch dB",
        "Tương quan",
        "Độ trễ ms",
        "Residual dB",
        "Tệp nguồn"
    ];

    public static async Task WriteAsync(
        IReadOnlyList<ReviewGroupAudit> groups,
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
        await using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            bufferSize: 16_384,
            leaveOpen: true)
        {
            NewLine = "\r\n"
        };

        await writer.WriteLineAsync(CsvRow(Headers));
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var comparison = group.BestComparison;
            var values = new[]
            {
                group.Id,
                PriorityText(group.Priority),
                $"A{group.TrackIndex}",
                group.InTimecode,
                group.OutTimecode,
                group.InFrame.ToString(CultureInfo.InvariantCulture),
                group.OutFrame.ToString(CultureInfo.InvariantCulture),
                group.MarkerCount.ToString(CultureInfo.InvariantCulture),
                string.Join(" | ", group.Reasons),
                ShadowOutcomeText(group.RepresentativeShadowOutcome),
                comparison is null ? string.Empty : $"A{comparison.OtherTrackIndex}",
                FormatNumber(comparison?.OtherMicAdvantageDb),
                FormatNumber(comparison?.WaveformCorrelation),
                comparison?.LagMilliseconds.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                FormatNumber(comparison?.ResidualToTargetDb),
                SpreadsheetSafeText(string.Join(" | ", group.SourceFileNames))
            };
            await writer.WriteLineAsync(CsvRow(values));
        }

        await writer.FlushAsync(cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static string CsvRow(IEnumerable<string> values) =>
        string.Join(",", values.Select(Escape));

    private static string Escape(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string FormatNumber(float? value) =>
        value?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string SpreadsheetSafeText(string value) =>
        value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            ? $"'{value}"
            : value;

    private static string PriorityText(ReviewPriority priority) => priority switch
    {
        ReviewPriority.High => "Cao",
        ReviewPriority.Medium => "Vừa",
        _ => "Thấp"
    };

    private static string ShadowOutcomeText(CrossTrackShadowOutcome? outcome) => outcome switch
    {
        CrossTrackShadowOutcome.LikelyBleed => "Có khả năng bleed",
        CrossTrackShadowOutcome.ConflictingEvidence => "Bằng chứng xung đột",
        CrossTrackShadowOutcome.BelowThreshold => "Chưa đạt ngưỡng",
        CrossTrackShadowOutcome.NoComparableSpeech => "Không có lời đối chiếu",
        _ => string.Empty
    };
}
