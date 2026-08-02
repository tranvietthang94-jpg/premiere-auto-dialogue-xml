using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.Inspection;
using PremiereAutoDialogueXml.Core.Validation;
using PremiereAutoDialogueXml.Core.Xml;

namespace PremiereAutoDialogueXml.App.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private string _xmlPath = string.Empty;
    private string _outputDirectory = string.Empty;
    private string _statusMessage = "Chọn XML và thư mục lưu kết quả để bắt đầu.";
    private string _inspectionDetails = "Chưa có kết quả kiểm tra XML/media.";
    private WorkflowStep _currentStep = WorkflowStep.SelectXml;
    private bool _inspectionAttempted;
    private readonly PremiereXmlInspector _xmlInspector = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public DialogueProcessingPreset Preset { get; } = DialogueProcessingPreset.Balanced;

    public string XmlPath
    {
        get => _xmlPath;
        private set
        {
            if (SetField(ref _xmlPath, value))
            {
                OnPropertyChanged(nameof(CanInspect));
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
                OnPropertyChanged(nameof(CanInspect));
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

    public bool CanInspect => !string.IsNullOrWhiteSpace(XmlPath) && !string.IsNullOrWhiteSpace(OutputDirectory);

    public string StepOneStatus => _currentStep >= WorkflowStep.SelectXml && CanInspect ? "Đã chọn" : "Chưa hoàn tất";

    public string StepTwoStatus => _currentStep >= WorkflowStep.Inspect
        ? "XML và media đạt"
        : _inspectionAttempted
            ? "Cần sửa đầu vào"
            : "Đang chờ";

    public string StepThreeStatus => "Có ở Phase 03";

    public string StepFourStatus => "Có ở Phase 04";

    public string PresetSummary =>
        $"VAD {Preset.VadThreshold:0.00} · lời tối thiểu {Preset.MinimumSpeechMilliseconds} ms · " +
        $"nghỉ tách câu {Preset.PhraseBreakMilliseconds} ms · padding {Preset.PaddingBeforeMilliseconds}/{Preset.PaddingAfterMilliseconds} ms · " +
        $"peak {Preset.TargetSamplePeakDbfs:0.#} dBFS · boost tối đa +{Preset.MaximumBoostDb:0.#} dB · {Preset.MaximumWorkers} worker";

    public void SetXmlPath(string path)
    {
        XmlPath = path?.Trim() ?? string.Empty;
    }

    public void SetOutputDirectory(string path)
    {
        OutputDirectory = path?.Trim() ?? string.Empty;
    }

    public void InspectSelection()
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
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            xmlExists = false;
        }

        var outputExists = false;
        try
        {
            outputExists = Directory.Exists(OutputDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            outputExists = false;
        }

        var facts = new InputSelectionFacts(XmlPath, xmlExists, xmlLength, OutputDirectory, outputExists);
        var result = InputSelectionValidator.Validate(facts);
        if (!result.IsValid)
        {
            _inspectionAttempted = true;
            _currentStep = WorkflowStep.SelectXml;
            StatusMessage = string.Join(" ", result.Issues.Select(issue => issue.Message));
            InspectionDetails = "Chưa đọc XML vì lựa chọn tệp hoặc thư mục kết quả chưa hợp lệ.";
        }
        else
        {
            InspectXmlAndMedia();
        }

        NotifyStepStatuses();
    }

    private void ResetInspectionState()
    {
        _inspectionAttempted = false;
        _currentStep = WorkflowStep.SelectXml;
        StatusMessage = "Lựa chọn đã thay đổi. Hãy kiểm tra lại trước khi phân tích.";
        InspectionDetails = "Chưa có kết quả kiểm tra XML/media.";
        NotifyStepStatuses();
    }

    private void InspectXmlAndMedia()
    {
        _inspectionAttempted = true;
        var inspection = _xmlInspector.Inspect(XmlPath);
        if (inspection.CanProceed && inspection.Project is not null)
        {
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
                $"Có {inspection.WarningCount} cảnh báo; chưa tạo hoặc sửa tệp nào.";
        }
        else
        {
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
    }

    private void NotifyStepStatuses()
    {
        OnPropertyChanged(nameof(StepOneStatus));
        OnPropertyChanged(nameof(StepTwoStatus));
        OnPropertyChanged(nameof(StepThreeStatus));
        OnPropertyChanged(nameof(StepFourStatus));
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
