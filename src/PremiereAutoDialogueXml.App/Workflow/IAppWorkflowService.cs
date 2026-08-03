using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.Inspection;
using PremiereAutoDialogueXml.Core.ProjectModel;
using PremiereAutoDialogueXml.Output;

namespace PremiereAutoDialogueXml.App.Workflow;

public interface IAppWorkflowService
{
    Task<PremiereProjectInspectionResult> InspectAsync(
        string xmlPath,
        CancellationToken cancellationToken);

    Task<ProjectAudioAnalysis> AnalyzeAsync(
        PremiereProject project,
        DialogueProcessingPreset preset,
        IProgress<AudioAnalysisProgress>? progress,
        CancellationToken cancellationToken);

    Task<OutputPackageResult> WriteOutputAsync(
        PremiereProject project,
        ProjectAudioAnalysis analysis,
        DialogueProcessingPreset preset,
        string outputDirectory,
        CancellationToken cancellationToken);

    Task<string> WriteFailureDiagnosticAsync(
        FailureDiagnosticRequest request,
        CancellationToken cancellationToken);
}

public sealed record FailureDiagnosticRequest(
    string OutputDirectory,
    string Stage,
    string XmlFileName,
    string? SourceXmlSha256,
    Exception Exception);
