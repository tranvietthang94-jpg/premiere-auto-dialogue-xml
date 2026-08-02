using System.Text.Json;
using PremiereAutoDialogueXml.Core.Xml;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: PremiereAutoDialogueXml.Inspect <premiere.xml> [more.xml]");
    return 2;
}

var inspector = new PremiereXmlInspector();
var failed = false;
foreach (var xmlPath in args)
{
    var startedAt = DateTimeOffset.UtcNow;
    var result = inspector.Inspect(xmlPath);
    var project = result.Project;
    var clips = project?.Sequence.AudioTracks.SelectMany(track => track.Clips).ToList() ?? [];
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
