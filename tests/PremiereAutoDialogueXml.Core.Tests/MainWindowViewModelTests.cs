using PremiereAutoDialogueXml.App.ViewModels;
using PremiereAutoDialogueXml.App.Workflow;
using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.Inspection;
using PremiereAutoDialogueXml.Core.ProjectModel;
using PremiereAutoDialogueXml.Output;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class MainWindowViewModelTests
{
    [TestMethod]
    public async Task ChangingSelectionInvalidatesSuccessfulInspection()
    {
        using var fixture = ViewModelFixture.Create();
        var service = new FakeWorkflowService(fixture.Project);
        var viewModel = fixture.CreateViewModel(service);

        await viewModel.InspectSelectionAsync();
        Assert.IsTrue(viewModel.CanProcess);

        var secondXml = Path.Combine(fixture.Directory, "second.xml");
        File.WriteAllText(secondXml, "<x />");
        viewModel.SetXmlPath(secondXml);

        Assert.IsFalse(viewModel.CanProcess);
        Assert.AreEqual("Đang chờ", viewModel.StepTwoStatus);
        Assert.AreEqual(string.Empty, viewModel.OutputXmlPath);
    }

    [TestMethod]
    public async Task InspectionErrorBlocksProcessing()
    {
        using var fixture = ViewModelFixture.Create();
        var service = new FakeWorkflowService(fixture.Project)
        {
            InspectionResult = new(
                null,
                [new("unsupported", InspectionSeverity.Error, "Không hỗ trợ fixture này.")])
        };
        var viewModel = fixture.CreateViewModel(service);

        await viewModel.InspectSelectionAsync();

        Assert.IsFalse(viewModel.CanProcess);
        Assert.AreEqual("Cần sửa đầu vào", viewModel.StepTwoStatus);
        StringAssert.Contains(viewModel.StatusMessage, "1 lỗi");
        Assert.AreEqual(0, service.AnalyzeCount);
    }

    [TestMethod]
    public async Task SuccessfulRunPublishesOutputSummary()
    {
        using var fixture = ViewModelFixture.Create();
        var service = new FakeWorkflowService(fixture.Project);
        var viewModel = fixture.CreateViewModel(service);
        await viewModel.InspectSelectionAsync();

        await viewModel.StartProcessingAsync();

        Assert.AreEqual(1, service.AnalyzeCount);
        Assert.AreEqual(1, service.WriteCount);
        Assert.AreEqual("Đã phân tích", viewModel.StepThreeStatus);
        Assert.AreEqual("Đã xuất", viewModel.StepFourStatus);
        Assert.AreEqual(service.Output.XmlPath, viewModel.OutputXmlPath);
        Assert.IsTrue(viewModel.CanOpenOutput);
        StringAssert.Contains(viewModel.OutputSummary, "4 fragment");
        StringAssert.Contains(viewModel.StatusMessage, "Đã xuất XML và audit");
    }

    [TestMethod]
    public async Task CancellationWaitsForAnalyzerAndNeverCallsWriter()
    {
        using var fixture = ViewModelFixture.Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeWorkflowService(fixture.Project)
        {
            AnalyzeHandler = async (_, _, _, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return FakeWorkflowService.Analysis;
            }
        };
        var viewModel = fixture.CreateViewModel(service);
        await viewModel.InspectSelectionAsync();

        var operation = viewModel.StartProcessingAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.RequestCancellation();
        await operation;

        Assert.IsFalse(viewModel.IsBusy);
        Assert.AreEqual(1, service.AnalyzeCount);
        Assert.AreEqual(0, service.WriteCount);
        Assert.AreEqual("Đã dừng", viewModel.StepThreeStatus);
        Assert.AreEqual("Không tạo output dở", viewModel.StepFourStatus);
        Assert.AreEqual(string.Empty, viewModel.OutputXmlPath);
    }

    [TestMethod]
    public async Task StartingTwiceWhileBusyUsesOneOperation()
    {
        using var fixture = ViewModelFixture.Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeWorkflowService(fixture.Project)
        {
            AnalyzeHandler = async (_, _, _, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return FakeWorkflowService.Analysis;
            }
        };
        var viewModel = fixture.CreateViewModel(service);
        await viewModel.InspectSelectionAsync();

        var first = viewModel.StartProcessingAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = viewModel.StartProcessingAsync();
        viewModel.RequestCancellation();
        await Task.WhenAll(first, second);

        Assert.AreSame(first, second);
        Assert.AreEqual(1, service.AnalyzeCount);
    }

    [TestMethod]
    public async Task FailureWritesDiagnosticAndDoesNotCallWriter()
    {
        using var fixture = ViewModelFixture.Create();
        var service = new FakeWorkflowService(fixture.Project)
        {
            AnalyzeHandler = (_, _, _, _) => throw new InvalidOperationException("model failed")
        };
        var viewModel = fixture.CreateViewModel(service);
        await viewModel.InspectSelectionAsync();

        await viewModel.StartProcessingAsync();

        Assert.AreEqual(0, service.WriteCount);
        Assert.AreEqual(1, service.DiagnosticCount);
        Assert.AreEqual("Gặp lỗi", viewModel.StepThreeStatus);
        StringAssert.Contains(viewModel.StatusMessage, "diagnostic.json");
        StringAssert.Contains(viewModel.InspectionDetails, "model failed");
    }

    private sealed class FakeWorkflowService(PremiereProject project) : IAppWorkflowService
    {
        public static readonly ProjectAudioAnalysis Analysis = new([], "6.2.1", "MODEL-SHA");

        public PremiereProjectInspectionResult InspectionResult { get; set; } = new(project, []);

        public Func<PremiereProject, DialogueProcessingPreset, IProgress<AudioAnalysisProgress>?, CancellationToken,
            Task<ProjectAudioAnalysis>> AnalyzeHandler
        { get; set; } =
            (_, _, _, _) => Task.FromResult(Analysis);

        public OutputPackageResult Output { get; } = new(
            Path.Combine(Path.GetTempPath(), "run"),
            Path.Combine(Path.GetTempPath(), "run", "result.xml"),
            Path.Combine(Path.GetTempPath(), "run", "result.audit.json"),
            "OUTPUT-SHA",
            4,
            1);

        public int AnalyzeCount { get; private set; }

        public int WriteCount { get; private set; }

        public int DiagnosticCount { get; private set; }

        public Task<PremiereProjectInspectionResult> InspectAsync(
            string xmlPath,
            CancellationToken cancellationToken) => Task.FromResult(InspectionResult);

        public Task<ProjectAudioAnalysis> AnalyzeAsync(
            PremiereProject inspectedProject,
            DialogueProcessingPreset preset,
            IProgress<AudioAnalysisProgress>? progress,
            CancellationToken cancellationToken)
        {
            AnalyzeCount++;
            return AnalyzeHandler(inspectedProject, preset, progress, cancellationToken);
        }

        public Task<OutputPackageResult> WriteOutputAsync(
            PremiereProject inspectedProject,
            ProjectAudioAnalysis analysis,
            DialogueProcessingPreset preset,
            string outputDirectory,
            CancellationToken cancellationToken)
        {
            WriteCount++;
            return Task.FromResult(Output);
        }

        public Task<string> WriteFailureDiagnosticAsync(
            FailureDiagnosticRequest request,
            CancellationToken cancellationToken)
        {
            DiagnosticCount++;
            return Task.FromResult(Path.Combine(request.OutputDirectory, "diagnostic.json"));
        }
    }

    private sealed class ViewModelFixture : IDisposable
    {
        private ViewModelFixture(string directory, string xmlPath, string outputDirectory, PremiereProject project)
        {
            Directory = directory;
            XmlPath = xmlPath;
            OutputDirectory = outputDirectory;
            Project = project;
        }

        public string Directory { get; }

        public string XmlPath { get; }

        public string OutputDirectory { get; }

        public PremiereProject Project { get; }

        public static ViewModelFixture Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"padx-ui-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            var output = Path.Combine(directory, "output");
            System.IO.Directory.CreateDirectory(output);
            var xml = Path.Combine(directory, "fixture.xml");
            File.WriteAllText(xml, "<x />");
            var project = new PremiereProject(
                xml,
                "SOURCE-SHA",
                new("sequence", "uuid", "Show", 25, 100, 2, 48_000, []));
            return new(directory, xml, output, project);
        }

        public MainWindowViewModel CreateViewModel(IAppWorkflowService service)
        {
            var viewModel = new MainWindowViewModel(service);
            viewModel.SetXmlPath(XmlPath);
            viewModel.SetOutputDirectory(OutputDirectory);
            return viewModel;
        }

        public void Dispose()
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
