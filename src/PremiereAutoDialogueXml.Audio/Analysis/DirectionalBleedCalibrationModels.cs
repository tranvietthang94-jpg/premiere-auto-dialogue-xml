using PremiereAutoDialogueXml.Core.Domain;

namespace PremiereAutoDialogueXml.Audio.Analysis;

public enum DirectionalBleedCalibrationStatus
{
    Stable,
    InsufficientSupport,
    UnstableFingerprint
}

public sealed record DirectionalBleedCalibrationPolicy(
    string Version,
    int MaximumAnchorWindowMilliseconds,
    int MinimumAnchorCount,
    int MaximumRetainedAnchorCount,
    int MaximumAnchorSampleCount,
    double MinimumConsistentSupportRatio,
    double MaximumLagDeviationMilliseconds,
    double MaximumAttenuationDeviationDb,
    double ClippingPeakDbfs,
    double MinimumOtherMicAdvantageDb,
    double MinimumCorrelation,
    int MaximumLagMilliseconds,
    double MaximumResidualToTargetDb)
{
    public const string CandidateVersion = "phase11-directional-calibration-shadow-v1";

    public static DirectionalBleedCalibrationPolicy From(DialogueProcessingPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return new(
            CandidateVersion,
            MaximumAnchorWindowMilliseconds: 250,
            MinimumAnchorCount: 3,
            MaximumRetainedAnchorCount: 64,
            MaximumAnchorSampleCount: 16,
            MinimumConsistentSupportRatio: 0.75,
            MaximumLagDeviationMilliseconds: 2.0,
            MaximumAttenuationDeviationDb: 3.0,
            ClippingPeakDbfs: -0.05,
            MinimumOtherMicAdvantageDb: preset.BleedOtherMicAdvantageDb,
            MinimumCorrelation: preset.BleedCorrelationThreshold,
            MaximumLagMilliseconds: preset.BleedMaximumLagMilliseconds,
            MaximumResidualToTargetDb: BleedResolver.ConflictingResidualThresholdDb);
    }
}

public sealed record DirectionalBleedCalibrationExclusion(
    int TargetTrackIndex,
    long TimelineStartSample,
    long TimelineEndSample);

public sealed record DirectionalBleedCalibrationRejectionCounts(
    int NoConfirmedSourceSpeech,
    int NoMatchingBaselineBleed,
    int TooShort,
    int ExcludedCandidateRegion,
    int IncompleteMedia,
    int Clipped,
    int PolarityInverted,
    int BelowAdvantage,
    int BelowCorrelation,
    int LagOutOfRange,
    int ConflictingResidual)
{
    public int Total =>
        NoConfirmedSourceSpeech +
        NoMatchingBaselineBleed +
        TooShort +
        ExcludedCandidateRegion +
        IncompleteMedia +
        Clipped +
        PolarityInverted +
        BelowAdvantage +
        BelowCorrelation +
        LagOutOfRange +
        ConflictingResidual;
}

public sealed record DirectionalBleedAnchorEvidence(
    string SourcePhraseId,
    string TargetSourceClipId,
    long TimelineStartSample,
    long TimelineEndSample,
    float OtherMicAdvantageDb,
    float WaveformCorrelation,
    int LagMilliseconds,
    float TransferScaleToTarget,
    float ResidualToTargetDb);

public sealed record DirectionalBleedFingerprint(
    int SourceTrackIndex,
    int TargetTrackIndex,
    DirectionalBleedCalibrationStatus Status,
    string Reason,
    int SourcePhraseCount,
    int AcceptedAnchorCount,
    int RetainedAnchorCount,
    int ConsistentAnchorCount,
    int OutlierAnchorCount,
    float? MedianLagMilliseconds,
    float? LagSpreadMilliseconds,
    float? MedianAttenuationDb,
    float? AttenuationSpreadDb,
    float? MedianCorrelation,
    float? MaximumResidualToTargetDb,
    string EvidenceSha256,
    DirectionalBleedCalibrationRejectionCounts Rejections,
    IReadOnlyList<DirectionalBleedAnchorEvidence> AnchorSamples);

public sealed record DirectionalBleedCalibrationShadow(
    DirectionalBleedCalibrationPolicy Policy,
    IReadOnlyList<DirectionalBleedFingerprint> Fingerprints);
