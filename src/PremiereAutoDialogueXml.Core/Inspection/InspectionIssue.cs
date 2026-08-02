namespace PremiereAutoDialogueXml.Core.Inspection;

public sealed record InspectionIssue(
    string Code,
    InspectionSeverity Severity,
    string Message);
