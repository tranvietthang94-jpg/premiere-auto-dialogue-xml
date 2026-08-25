using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.ProjectModel;

namespace PremiereAutoDialogueXml.Output;

public sealed record OutputPackageRequest(
    PremiereProject Project,
    ProjectAudioAnalysis Analysis,
    DialogueProcessingPreset Preset,
    string OutputParentDirectory);

public sealed record OutputPackageResult(
    string RunDirectory,
    string XmlPath,
    string AuditPath,
    string OutputXmlSha256,
    int FragmentCount,
    int MarkerCount)
{
    public string? ReviewCsvPath { get; init; }

    public int ReviewGroupCount { get; init; }

    public int TransitionCount { get; init; }
}
