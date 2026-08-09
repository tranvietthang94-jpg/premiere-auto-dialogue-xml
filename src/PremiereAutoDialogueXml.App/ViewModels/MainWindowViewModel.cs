using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using PremiereAutoDialogueXml.App.Workflow;
using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.Inspection;
using PremiereAutoDialogueXml.Core.ProjectModel;
using PremiereAutoDialogueXml.Core.Validation;
using PremiereAutoDialogueXml.Output;

namespace PremiereAutoDialogueXml.App.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly IAppWorkflowService _workflow;
    private string _xmlPath = string.Empty;
    private string _outputDirectory = string.Empty;
    private string _statusMessage = "Chọn XML và thư mục lưu kết quả để bắt đầu.";
    private string _inspectionDetails = "Chưa có kết quả kiểm tra XML/media.";
    private string _progressMessage = "Chưa bắt đầu phân tích.";
    private string _outputSummary = "Chưa có kết quả xuất.";
    private string _outputXmlPath = string.Empty;
    private string _outputRunDirectory = string.Empty;
    private double _progressPercent;
    private WorkflowStep _currentStep = WorkflowStep.SelectXml;
    private PremiereProject? _inspectedProject;
    private bool _inspectionAttempted;
    private bool _analysisCompleted;
    private bool _exportCompleted;
    private bool _wasCancelled;
    private WorkflowStep? _failureStep;
    private bool _isBusy;
    private CancellationTokenSource? _operationCancellation;
    private Task _activeOperation = Task.CompletedTask;

    public MainWindowViewModel()
        : this(new AppWorkflowService())
    {
    }

    public MainWindowViewModel(IAppWorkflowService workflow)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DialogueProcessingPreset Preset { get; } = DialogueProcessingPreset.Balanced;

    public string XmlPath
    {
        get => _xmlPath;
        private set
        {
            if (SetField(ref _xmlPath, value))
            {
                ResetInspectionState();
            }
        }
    }

    public string OutputDirectory
    {
        get => _outputDirectory;
        private set
        {
            if (SetField(ref _outputDirectory, value))
            {
                ResetInspectionState();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public string InspectionDetails
    {
        get => _inspectionDetails;
        private set => SetField(ref _inspectionDetails, value);
    }

    public string ProgressMessage
    {
        get => _progressMessage;
        private set => SetField(ref _progressMessage, value);
    }

    public string OutputSummary
    {
        get => _outputSummary;
        private set => SetField(ref _outputSummary, value);
    }

    public string OutputXmlPath
    {
        get => _outputXmlPath;
        private set
        {
            if (SetField(ref _outputXmlPath, value))
            {
                OnPropertyChanged(nameof(CanOpenOutput));
            }
        }
    }

    public string OutputRunDirectory
    {
        get => _outputRunDirectory;
        private set => SetField(ref _outputRunDirectory, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetField(ref _progressPercent, Math.Clamp(value, 0, 100));
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                NotifyActionStates();
                NotifyStepStatuses();
            }
        }
    }

    public bool CanChooseFiles => !IsBusy;

    public bool CanInspect =>
        !IsBusy &&
        !string.IsNullOrWhiteSpace(XmlPath) &&
        !string.IsNullOrWhiteSpace(OutputDirectory);

    public bool CanProcess => !IsBusy && _inspectedProject is not null;

    public bool CanCancel => IsBusy;

    public bool CanOpenOutput => !IsBusy && !string.IsNullOrWhiteSpace(OutputXmlPath);

    public string StepOneStatus =>
        !string.IsNullOrWhiteSpace(XmlPath) && !string.IsNullOrWhiteSpace(OutputDirectory)
            ? "Đã chọn"
            : "Chưa hoàn tất";

    public string StepTwoStatus => _inspectedProject is not null
        ? "XML và media đạt"
        : IsBusy && _currentStep == WorkflowStep.Inspect
            ? "Đang kiểm tra"
            : _inspectionAttempted
                ? _failureStep == WorkflowStep.Inspect ? "Gặp lỗi" : "Cần sửa đầu vào"
                : "Đang chờ";

    public string StepThreeStatus => _analysisCompleted
        ? "Đã phân tích"
        : IsBusy && _currentStep == WorkflowStep.Analyze
            ? "Đang chạy"
            : _wasCancelled ? "Đã dừng" : _failureStep == WorkflowStep.Analyze ? "Gặp lỗi" : "Đang chờ";

    public string StepFourStatus => _exportCompleted
        ? "Đã xuất"
        : IsBusy && _currentStep == WorkflowStep.Export
            ? "Đang ghi kết quả"
            : _wasCancelled ? "Không tạo output dở" : _failureStep == WorkflowStep.Export ? "Gặp lỗi" : "Đang chờ";

    public string PresetSummary =>
        $"VAD {Preset.VadThreshold:0.00} · lời tối thiểu {Preset.MinimumSpeechMilliseconds} ms · " +
        $"nghỉ tách câu {Preset.PhraseBreakMilliseconds} ms · padding {Preset.PaddingBeforeMilliseconds}/{Preset.PaddingAfterMilliseconds} ms · " +
        $"peak hậu routing {Preset.TargetSamplePeakDbfs:0.#} dBFS · bù center-pan +{Preset.PremiereCenterPanCompensationDb:0.00} dB · " +
        $"boost tối đa +{Preset.MaximumBoostDb:0.#} dB · {Preset.MaximumWorkers} worker";

    public void SetXmlPath(string path)
    {
        if (!IsBusy)
        {
            XmlPath = path?.Trim() ?? string.Empty;
        }
    }

    public void SetOutputDirectory(string path)
    {
        if (!IsBusy)
        {
            OutputDirectory = path?.Trim() ?? string.Empty;
        }
    }

    public Task InspectSelectionAsync()
    {
        if (IsBusy)
        {
            return _activeOperation;
        }

        if (!CanInspect)
        {
            StatusMessage = "Hãy chọn XML và thư mục kết quả trước khi kiểm tra.";
            return Task.CompletedTask;
        }

        return BeginOperation(InspectSelectionCoreAsync);
    }

    public Task StartProcessingAsync()
    {
        if (IsBusy)
        {
            return _activeOperation;
        }

        if (!CanProcess)
        {
            StatusMessage = "Hãy kiểm tra XML và media thành công trước khi phân tích.";
            return Task.CompletedTask;
        }

        return BeginOperation(ProcessCoreAsync);
    }

    public void RequestCancellation()
    {
        if (_operationCancellation is null || _operationCancellation.IsCancellationRequested)
        {
            return;
        }

        StatusMessage = "Đang dừng an toàn… App sẽ chờ worker đóng và không giữ output dở dang.";
        ProgressMessage = "Đang dừng an toàn…";
        _operationCancellation.Cancel();
    }

    public async Task CancelAndWaitAsync()
    {
        RequestCancellation();
        await _activeOperation;
    }

    private Task BeginOperation(Func<CancellationToken, Task> operation)
    {
        var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        IsBusy = true;
        _activeOperation = ExecuteOperationAsync(operation, cancellation);
        return _activeOperation;
    }

    private async Task ExecuteOperationAsync(
        Func<CancellationToken, Task> operation,
        CancellationTokenSource cancellation)
    {
        try
        {
            await operation(cancellation.Token);
        }
        finally
        {
            if (ReferenceEquals(_operationCancellation, cancellation))
            {
                _operationCancellation = null;
            }

            cancellation.Dispose();
            IsBusy = false;
        }
    }

    private async Task InspectSelectionCoreAsync(CancellationToken cancellationToken)
    {
        _inspectionAttempted = true;
        _failureStep = null;
        _wasCancelled = false;
        _currentStep = WorkflowStep.Inspect;
        StatusMessage = "Đang kiểm tra XML, đường dẫn media và header WAV…";
        ProgressMessage = "Kiểm tra chỉ đọc; chưa phân tích âm thanh.";
        NotifyStepStatuses();

        var selection = ReadSelectionFacts();
        var validation = InputSelectionValidator.Validate(selection);
        if (!validation.IsValid)
        {
            _currentStep = WorkflowStep.SelectXml;
            StatusMessage = string.Join(" ", validation.Issues.Select(issue => issue.Message));
            InspectionDetails = "Chưa đọc XML vì lựa chọn tệp hoặc thư mục kết quả chưa hợp lệ.";
            NotifyStepStatuses();
            return;
        }

        try
        {
            var inspection = await _workflow.InspectAsync(XmlPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ApplyInspectionResult(inspection);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _currentStep = WorkflowStep.SelectXml;
            _wasCancelled = true;
            StatusMessage = "Đã dừng kiểm tra an toàn; chưa tạo hoặc sửa tệp nào.";
            ProgressMessage = "Đã dừng.";
            NotifyStepStatuses();
        }
        catch (Exception exception)
        {
            _currentStep = WorkflowStep.SelectXml;
            await RecordFailureAsync("inspect", exception, null);
        }
    }

    private async Task ProcessCoreAsync(CancellationToken cancellationToken)
    {
        var project = _inspectedProject
            ?? throw new InvalidOperationException("Không có project đã kiểm tra.");
        _analysisCompleted = false;
        _exportCompleted = false;
        _wasCancelled = false;
        _failureStep = null;
        OutputXmlPath = string.Empty;
        OutputRunDirectory = string.Empty;
        OutputSummary = "Chưa có kết quả xuất.";
        ProgressPercent = 0;
        _currentStep = WorkflowStep.Analyze;
        StatusMessage = "Đang phân tích âm thanh hoàn toàn trên máy này…";
        ProgressMessage = "Đang khởi tạo model nhận diện lời thoại…";
        NotifyStepStatuses();

        var stage = "audio-analysis";
        try
        {
            var progress = new Progress<AudioAnalysisProgress>(ApplyAudioProgress);
            var analysis = await _workflow.AnalyzeAsync(
                project,
                Preset,
                progress,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            _analysisCompleted = true;
            _currentStep = WorkflowStep.Export;
            ProgressPercent = 100;
            ProgressMessage = "Phân tích xong. Đang kiểm tra và ghi XML/audit…";
            StatusMessage = "Phân tích hoàn tất; đang tạo bản XML mới, XML/WAV nguồn vẫn bất biến.";
            NotifyStepStatuses();

            stage = "output-write";
            var output = await _workflow.WriteOutputAsync(
                project,
                analysis,
                Preset,
                OutputDirectory,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            _exportCompleted = true;
            OutputXmlPath = output.XmlPath;
            OutputRunDirectory = output.RunDirectory;
            var phraseCount = analysis.Tracks.Sum(track => track.Phrases.Count);
            var reviewSummary = string.IsNullOrWhiteSpace(output.ReviewCsvPath)
                ? string.Empty
                : $"\nReview: {Path.GetFileName(output.ReviewCsvPath)} ({output.ReviewGroupCount:N0} mục)";
            OutputSummary =
                $"{phraseCount:N0} cụm lời · {output.FragmentCount:N0} fragment · {output.MarkerCount:N0} marker\n" +
                $"XML: {Path.GetFileName(output.XmlPath)}\nAudit: {Path.GetFileName(output.AuditPath)}{reviewSummary}";
            StatusMessage = "Đã xuất XML, audit và danh sách review vào thư mục run mới. Bạn có thể mở Explorer để import XML vào Premiere.";
            ProgressMessage = "Hoàn tất.";
            InspectionDetails +=
                $"{Environment.NewLine}{Environment.NewLine}MODEL {analysis.ModelVersion} · SHA-256 {analysis.ModelSha256}" +
                $"{Environment.NewLine}OUTPUT SHA-256 {output.OutputXmlSha256}";
            NotifyStepStatuses();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _wasCancelled = true;
            _exportCompleted = false;
            StatusMessage = "Đã dừng an toàn. Không có XML/audit kết quả dở dang được giữ lại.";
            ProgressMessage = "Đã dừng an toàn.";
            NotifyStepStatuses();
        }
        catch (Exception exception)
        {
            await RecordFailureAsync(stage, exception, project.SourceXmlSha256);
        }
    }

    private void ApplyAudioProgress(AudioAnalysisProgress progress)
    {
        ProgressPercent = progress.TotalTracks <= 0
            ? 0
            : progress.CompletedTracks * 100d / progress.TotalTracks;
        ProgressMessage = progress.Message;
        StatusMessage = progress.Message;
    }

    private void ApplyInspectionResult(PremiereProjectInspectionResult inspection)
    {
        if (inspection.CanProceed && inspection.Project is not null)
        {
            _inspectedProject = inspection.Project;
            _currentStep = WorkflowStep.Inspect;
            var sequence = inspection.Project.Sequence;
            var clipCount = sequence.AudioTracks.Sum(track => track.Clips.Count);
            var mediaCount = sequence.AudioTracks
                .SelectMany(track => track.Clips)
                .Select(clip => clip.SourceMedia.LocalPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            StatusMessage =
                $"Kiểm tra đạt: sequence “{sequence.Name}”, {sequence.AudioTracks.Count} track, {clipCount} clip, {mediaCount} WAV. " +
                $"Có {inspection.WarningCount} cảnh báo; sẵn sàng phân tích.";
            ProgressMessage = "Sẵn sàng phân tích âm thanh.";
        }
        else
        {
            _inspectedProject = null;
            _currentStep = WorkflowStep.SelectXml;
            StatusMessage =
                $"Chưa thể phân tích: có {inspection.ErrorCount} lỗi và {inspection.WarningCount} cảnh báo. " +
                "Mở Chi tiết kỹ thuật để xem nguyên nhân.";
        }

        InspectionDetails = inspection.Issues.Count == 0
            ? "Không có cảnh báo compatibility. Header WAV thật khớp metadata XML và mọi source range đều nằm trong media."
            : string.Join(
                Environment.NewLine,
                inspection.Issues.Select(issue =>
                    $"{(issue.Severity == InspectionSeverity.Error ? "LỖI" : "CẢNH BÁO")} [{issue.Code}] {issue.Message}"));
        NotifyActionStates();
        NotifyStepStatuses();
    }

    private async Task RecordFailureAsync(string stage, Exception exception, string? sourceXmlSha256)
    {
        _failureStep = stage switch
        {
            "inspect" => WorkflowStep.Inspect,
            "output-write" => WorkflowStep.Export,
            _ => WorkflowStep.Analyze
        };
        _exportCompleted = false;
        var diagnosticName = string.Empty;
        try
        {
            var path = await _workflow.WriteFailureDiagnosticAsync(
                new(
                    OutputDirectory,
                    stage,
                    SafeFileName(XmlPath),
                    sourceXmlSha256,
                    exception),
                CancellationToken.None);
            diagnosticName = Path.GetFileName(path);
        }
        catch (Exception diagnosticException)
        {
            InspectionDetails +=
                $"{Environment.NewLine}Không thể ghi diagnostic: {diagnosticException.Message}";
        }

        StatusMessage = string.IsNullOrEmpty(diagnosticName)
            ? "Có lỗi khi xử lý. Không tạo output dở dang; mở Chi tiết kỹ thuật để xem nguyên nhân."
            : $"Có lỗi khi xử lý. Không tạo output dở dang; diagnostic: {diagnosticName}.";
        ProgressMessage = "Xử lý thất bại.";
        InspectionDetails +=
            $"{Environment.NewLine}{Environment.NewLine}LỖI [{stage}] {exception.Message}";
        NotifyStepStatuses();
    }

    private InputSelectionFacts ReadSelectionFacts()
    {
        var xmlExists = false;
        long xmlLength = 0;
        try
        {
            var file = new FileInfo(XmlPath);
            xmlExists = file.Exists;
            if (xmlExists)
            {
                xmlLength = file.Length;
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            xmlExists = false;
        }

        var outputExists = false;
        try
        {
            outputExists = Directory.Exists(OutputDirectory);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            outputExists = false;
        }

        return new(XmlPath, xmlExists, xmlLength, OutputDirectory, outputExists);
    }

    private void ResetInspectionState()
    {
        _inspectedProject = null;
        _inspectionAttempted = false;
        _analysisCompleted = false;
        _exportCompleted = false;
        _wasCancelled = false;
        _failureStep = null;
        _currentStep = WorkflowStep.SelectXml;
        ProgressPercent = 0;
        ProgressMessage = "Chưa bắt đầu phân tích.";
        OutputXmlPath = string.Empty;
        OutputRunDirectory = string.Empty;
        OutputSummary = "Chưa có kết quả xuất.";
        StatusMessage = "Lựa chọn đã thay đổi. Hãy kiểm tra lại trước khi phân tích.";
        InspectionDetails = "Chưa có kết quả kiểm tra XML/media.";
        NotifyActionStates();
        NotifyStepStatuses();
    }

    private void NotifyActionStates()
    {
        OnPropertyChanged(nameof(CanChooseFiles));
        OnPropertyChanged(nameof(CanInspect));
        OnPropertyChanged(nameof(CanProcess));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanOpenOutput));
    }

    private void NotifyStepStatuses()
    {
        OnPropertyChanged(nameof(StepOneStatus));
        OnPropertyChanged(nameof(StepTwoStatus));
        OnPropertyChanged(nameof(StepThreeStatus));
        OnPropertyChanged(nameof(StepFourStatus));
    }

    private static string SafeFileName(string path)
    {
        try
        {
            return Path.GetFileName(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return string.Empty;
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new(propertyName));
    }
}
