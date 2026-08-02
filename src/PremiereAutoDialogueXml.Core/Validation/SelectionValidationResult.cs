namespace PremiereAutoDialogueXml.Core.Validation;

public sealed class SelectionValidationResult
{
    public SelectionValidationResult(IEnumerable<ValidationIssue> issues)
    {
        Issues = issues.ToArray();
    }

    public IReadOnlyList<ValidationIssue> Issues { get; }

    public bool IsValid => Issues.Count == 0;
}
