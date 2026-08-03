using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.Inspection;
using PremiereAutoDialogueXml.Core.ProjectModel;
using PremiereAutoDialogueXml.Core.Xml;
using PremiereAutoDialogueXml.Output;

namespace PremiereAutoDialogueXml.App.Workflow;

public sealed class AppWorkflowService : IAppWorkflowService
{
    private readonly PremiereXmlInspector _inspector;
    private readonly AudioProjectAnalyzer _analyzer;
    private readonly OutputPackageWriter _writer;
    private readonly FailureDiagnosticWriter _diagnosticWriter;

    public AppWorkflowService()
        : this(
            new PremiereXmlInspector(),
            new AudioProjectAnalyzer(),
            new OutputPackageWriter(),
            new FailureDiagnosticWriter())
    {
    }

    public AppWorkflowService(
        PremiereXmlInspector inspector,
        AudioProjectAnalyzer analyzer,
        OutputPackageWriter writer,
        FailureDiagnosticWriter diagnosticWriter)
    {
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _diagnosticWriter = diagnosticWriter ?? throw new ArgumentNullException(nameof(diagnosticWriter));
    }

    public Task<PremiereProjectInspectionResult> InspectAsync(
        string xmlPath,
        CancellationToken cancellationToken) =>
        Task.Run(() => _inspector.Inspect(xmlPath), cancellationToken);

    public Task<ProjectAudioAnalysis> AnalyzeAsync(
        PremiereProject project,
        DialogueProcessingPreset preset,
        IProgress<AudioAnalysisProgress>? progress,
        CancellationToken cancellationToken) =>
        _analyzer.AnalyzeAsync(project, preset, progress, cancellationToken);

    public Task<OutputPackageResult> WriteOutputAsync(
        PremiereProject project,
        ProjectAudioAnalysis analysis,
        DialogueProcessingPreset preset,
        string outputDirectory,
        CancellationToken cancellationToken) =>
        _writer.WriteAsync(
            new(project, analysis, preset, outputDirectory),
            cancellationToken);

    public Task<string> WriteFailureDiagnosticAsync(
        FailureDiagnosticRequest request,
        CancellationToken cancellationToken) =>
        _diagnosticWriter.WriteAsync(request, cancellationToken);
}
