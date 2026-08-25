using System.IO;
using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.Inspection;
using PremiereAutoDialogueXml.Core.ProjectModel;
using PremiereAutoDialogueXml.Core.Xml;
using PremiereAutoDialogueXml.Output;
using PremiereAutoDialogueXml.Validation;

namespace PremiereAutoDialogueXml.App.Workflow;

public sealed class AppWorkflowService : IAppWorkflowService
{
    private readonly PremiereXmlInspector _inspector;
    private readonly AudioProjectAnalyzer _analyzer;
    private readonly OutputPackageWriter _writer;
    private readonly FailureDiagnosticWriter _diagnosticWriter;
    private readonly ProductionTransitionPackageAdopter _transitionAdopter;

    public AppWorkflowService()
        : this(
            new PremiereXmlInspector(),
            new AudioProjectAnalyzer(),
            new OutputPackageWriter(),
            new FailureDiagnosticWriter(),
            new ProductionTransitionPackageAdopter())
    {
    }

    public AppWorkflowService(
        PremiereXmlInspector inspector,
        AudioProjectAnalyzer analyzer,
        OutputPackageWriter writer,
        FailureDiagnosticWriter diagnosticWriter,
        ProductionTransitionPackageAdopter? transitionAdopter = null)
    {
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _diagnosticWriter = diagnosticWriter ?? throw new ArgumentNullException(nameof(diagnosticWriter));
        _transitionAdopter = transitionAdopter ?? new ProductionTransitionPackageAdopter();
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

    public async Task<OutputPackageResult> WriteOutputAsync(
        PremiereProject project,
        ProjectAudioAnalysis analysis,
        DialogueProcessingPreset preset,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var package = await _writer.WriteAsync(
            new(project, analysis, preset, outputDirectory),
            cancellationToken);
        try
        {
            return await _transitionAdopter.AdoptAsync(project, package, cancellationToken);
        }
        catch
        {
            DeleteFailedRun(package, outputDirectory);
            throw;
        }
    }

    public Task<string> WriteFailureDiagnosticAsync(
        FailureDiagnosticRequest request,
        CancellationToken cancellationToken) =>
        _diagnosticWriter.WriteAsync(request, cancellationToken);

    private static void DeleteFailedRun(OutputPackageResult package, string outputDirectory)
    {
        var parent = Path.GetFullPath(outputDirectory);
        var run = Path.GetFullPath(package.RunDirectory);
        if (Directory.Exists(run) &&
            string.Equals(Path.GetDirectoryName(run), parent, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(run, recursive: true);
        }
    }
}
