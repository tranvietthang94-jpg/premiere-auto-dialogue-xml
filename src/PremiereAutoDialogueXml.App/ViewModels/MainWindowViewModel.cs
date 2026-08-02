using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.Validation;

namespace PremiereAutoDialogueXml.App.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private string _xmlPath = string.Empty;
    private string _outputDirectory = string.Empty;
    private string _statusMessage = "Chọn XML và thư mục lưu kết quả để bắt đầu.";
    private WorkflowStep _currentStep = WorkflowStep.SelectXml;

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

    public bool CanInspect => !string.IsNullOrWhiteSpace(XmlPath) && !string.IsNullOrWhiteSpace(OutputDirectory);

    public string StepOneStatus => _currentStep >= WorkflowStep.SelectXml && CanInspect ? "Đã chọn" : "Chưa hoàn tất";

    public string StepTwoStatus => _currentStep >= WorkflowStep.Inspect ? "Đã kiểm tra nền tảng" : "Đang chờ";

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
        if (result.IsValid)
        {
            _currentStep = WorkflowStep.Inspect;
            StatusMessage = "Lựa chọn hợp lệ. Phase 02 sẽ đọc cấu trúc XML và đối chiếu media trước khi cho phép phân tích.";
        }
        else
        {
            _currentStep = WorkflowStep.SelectXml;
            StatusMessage = string.Join(" ", result.Issues.Select(issue => issue.Message));
        }

        NotifyStepStatuses();
    }

    private void ResetInspectionState()
    {
        _currentStep = WorkflowStep.SelectXml;
        StatusMessage = "Lựa chọn đã thay đổi. Hãy kiểm tra lại trước khi phân tích.";
        NotifyStepStatuses();
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
