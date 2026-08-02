using PremiereAutoDialogueXml.Core.Inspection;

namespace PremiereAutoDialogueXml.Core.Media;

public sealed record WaveInspectionResult(
    WaveFileInfo? File,
    IReadOnlyList<InspectionIssue> Issues)
{
    public bool IsSupported => File is not null && Issues.All(issue => issue.Severity != InspectionSeverity.Error);
}
