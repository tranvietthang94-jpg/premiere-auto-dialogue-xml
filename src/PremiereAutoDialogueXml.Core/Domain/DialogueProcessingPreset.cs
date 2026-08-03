using PremiereAutoDialogueXml.Core.Validation;

namespace PremiereAutoDialogueXml.Core.Domain;

public sealed record DialogueProcessingPreset(
    string Name,
    double VadThreshold,
    int MinimumSpeechMilliseconds,
    int PhraseBreakMilliseconds,
    int PaddingBeforeMilliseconds,
    int PaddingAfterMilliseconds,
    double DirectVoiceAboveNoiseDb,
    double BleedOtherMicAdvantageDb,
    double BleedCorrelationThreshold,
    int BleedMaximumLagMilliseconds,
    double TargetSamplePeakDbfs,
    string PremiereRoutingProfile,
    double PremiereCenterPanCompensationDb,
    double MaximumBoostDb,
    int MaximumWorkers)
{
    public static DialogueProcessingPreset Balanced { get; } = new(
        Name: "Cân bằng",
        VadThreshold: 0.50,
        MinimumSpeechMilliseconds: 120,
        PhraseBreakMilliseconds: 350,
        PaddingBeforeMilliseconds: 200,
        PaddingAfterMilliseconds: 300,
        DirectVoiceAboveNoiseDb: 10.0,
        BleedOtherMicAdvantageDb: 12.0,
        BleedCorrelationThreshold: 0.80,
        BleedMaximumLagMilliseconds: 12,
        TargetSamplePeakDbfs: -6.0,
        PremiereRoutingProfile: "mono-center-equal-power-to-stereo",
        PremiereCenterPanCompensationDb: 3.010299956639812,
        MaximumBoostDb: 18.0,
        MaximumWorkers: 4);

    public IReadOnlyList<ValidationIssue> Validate()
    {
        var issues = new List<ValidationIssue>();

        if (string.IsNullOrWhiteSpace(Name))
        {
            issues.Add(new("preset-name-required", "Preset phải có tên."));
        }

        if (VadThreshold is <= 0 or >= 1)
        {
            issues.Add(new("vad-threshold-out-of-range", "VAD threshold phải lớn hơn 0 và nhỏ hơn 1."));
        }

        if (MinimumSpeechMilliseconds <= 0 || PhraseBreakMilliseconds <= 0)
        {
            issues.Add(new("speech-timing-invalid", "Thời lượng lời tối thiểu và khoảng nghỉ phải lớn hơn 0."));
        }

        if (PaddingBeforeMilliseconds < 0 || PaddingAfterMilliseconds < 0)
        {
            issues.Add(new("padding-negative", "Padding không được âm."));
        }

        if (DirectVoiceAboveNoiseDb <= 0 || BleedOtherMicAdvantageDb <= 0)
        {
            issues.Add(new("voice-threshold-invalid", "Ngưỡng direct voice và bleed phải lớn hơn 0 dB."));
        }

        if (BleedCorrelationThreshold is < 0 or > 1)
        {
            issues.Add(new("correlation-out-of-range", "Ngưỡng tương quan phải nằm trong khoảng 0 đến 1."));
        }

        if (BleedMaximumLagMilliseconds < 0)
        {
            issues.Add(new("lag-negative", "Độ lệch bleed tối đa không được âm."));
        }

        if (TargetSamplePeakDbfs > 0)
        {
            issues.Add(new("target-peak-positive", "Sample peak mục tiêu không được lớn hơn 0 dBFS."));
        }

        if (string.IsNullOrWhiteSpace(PremiereRoutingProfile))
        {
            issues.Add(new("routing-profile-required", "Preset phải ghi rõ routing Premiere đã xác nhận."));
        }

        if (!double.IsFinite(PremiereCenterPanCompensationDb) ||
            PremiereCenterPanCompensationDb is < 0 or > 6.1 ||
            TargetSamplePeakDbfs + PremiereCenterPanCompensationDb > 0)
        {
            issues.Add(new(
                "routing-compensation-invalid",
                "Bù center-pan phải nằm trong 0–6,1 dB và không đẩy target trước routing vượt 0 dBFS."));
        }

        if (MaximumBoostDb is < 0 or > 18)
        {
            issues.Add(new("boost-out-of-range", "Boost tối đa phải nằm trong khoảng 0 đến 18 dB."));
        }

        if (MaximumWorkers is < 1 or > 4)
        {
            issues.Add(new("worker-count-out-of-range", "Số worker phải nằm trong khoảng 1 đến 4."));
        }

        return issues;
    }
}
