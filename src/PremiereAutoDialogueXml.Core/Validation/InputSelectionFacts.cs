namespace PremiereAutoDialogueXml.Core.Validation;

public sealed record InputSelectionFacts(
    string? XmlPath,
    bool XmlExists,
    long XmlLength,
    string? OutputDirectory,
    bool OutputDirectoryExists);
