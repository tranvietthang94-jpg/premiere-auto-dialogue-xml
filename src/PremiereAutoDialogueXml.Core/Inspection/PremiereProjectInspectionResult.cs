using PremiereAutoDialogueXml.Core.ProjectModel;

namespace PremiereAutoDialogueXml.Core.Inspection;

public sealed record PremiereProjectInspectionResult(
    PremiereProject? Project,
    IReadOnlyList<InspectionIssue> Issues)
{
    public bool CanProceed =>
        Project is not null &&
        Issues.All(issue => issue.Severity != InspectionSeverity.Error);

    public int ErrorCount => Issues.Count(issue => issue.Severity == InspectionSeverity.Error);

    public int WarningCount => Issues.Count(issue => issue.Severity == InspectionSeverity.Warning);
}
