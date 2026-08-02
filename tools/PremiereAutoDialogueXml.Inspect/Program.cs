using System.Text.Json;
using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.Xml;

var analyzeAudio = args.Length > 0 && args[0].Equals("--analyze", StringComparison.OrdinalIgnoreCase);
var xmlPaths = analyzeAudio ? args.Skip(1).ToArray() : args;
if (xmlPaths.Length == 0)
{
    Console.Error.WriteLine("Usage: PremiereAutoDialogueXml.Inspect [--analyze] <premiere.xml> [more.xml]");
    return 2;
}

var inspector = new PremiereXmlInspector();
var failed = false;
foreach (var xmlPath in xmlPaths)
{
    var startedAt = DateTimeOffset.UtcNow;
    var result = inspector.Inspect(xmlPath);
    var project = result.Project;
    var clips = project?.Sequence.AudioTracks.SelectMany(track => track.Clips).ToList() ?? [];
    object? audioSummary = null;
    if (analyzeAudio && result.CanProceed && project is not null)
    {
        var progress = new InlineProgress<AudioAnalysisProgress>(update =>
            Console.Error.WriteLine($"[{update.CompletedTracks}/{update.TotalTracks}] {update.Message}"));
        var analysis = await new AudioProjectAnalyzer().AnalyzeAsync(
            project,
            DialogueProcessingPreset.Balanced,
            progress);
        audioSummary = new
        {
            analysis.ModelVersion,
            analysis.ModelSha256,
            PeakWorkingSetMegabytes = Math.Round(
                System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1024d / 1024d,
                1),
            ManagedMemoryMegabytes = Math.Round(GC.GetTotalMemory(forceFullCollection: false) / 1024d / 1024d, 1),
            Phrases = analysis.Tracks.Sum(track => track.Phrases.Count),
            GainCappedPhrases = analysis.Tracks.Sum(track => track.Phrases.Count(phrase => phrase.GainWasCapped)),
            Segments = Enum.GetValues<AudioSegmentStatus>().ToDictionary(
                status => status.ToString(),
                status => analysis.Tracks.Sum(track => track.Segments.Count(segment => segment.Status == status))),
            DurationSeconds = Enum.GetValues<AudioSegmentStatus>().ToDictionary(
                status => status.ToString(),
                status => analysis.Tracks.Sum(track => track.Segments
                    .Where(segment => segment.Status == status)
                    .Sum(segment => segment.TimelineEndSample - segment.TimelineStartSample)) / 48_000d)
        };
    }

    var summary = new
    {
        Xml = Path.GetFileName(xmlPath),
        result.CanProceed,
        Sequence = project?.Sequence.Name,
        FrameRate = project?.Sequence.FrameRate,
        DurationFrames = project?.Sequence.DurationFrames,
        AudioTracks = project?.Sequence.AudioTracks.Count,
        Clips = clips.Count,
        Media = clips.Select(clip => clip.SourceFileId).Distinct(StringComparer.Ordinal).Count(),
        result.WarningCount,
        result.ErrorCount,
        AudioAnalysis = audioSummary,
        Issues = result.Issues.Select(issue => new
        {
            Severity = issue.Severity.ToString(),
            issue.Code,
            issue.Message
        }),
        ElapsedMilliseconds = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds
    };

    Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    failed |= !result.CanProceed;
}

return failed ? 1 : 0;

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
