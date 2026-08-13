using PremiereAutoDialogueXml.Core.Domain;

namespace PremiereAutoDialogueXml.Audio.Analysis;

public enum CalibratedBleedShadowOutcome
{
    NoCalibration,
    BelowCalibratedThreshold,
    ConflictingEvidence,
    CalibratedLikelyBleed
}

public enum CalibratedBleedWindowDisposition
{
    Pass,
    BelowCalibration,
    ConflictingResidual,
    IncompleteMedia,
    Clipped,
    PolarityInverted
}

public sealed record CalibratedBleedScoringPolicy(
    string Version,
    int MaximumWindowMilliseconds,
    int MinimumWindowMilliseconds,
    int MinimumPassingWindowCount,
    int MaximumRetainedWindowCount,
    double MinimumPassingWindowRatio,
    double MaximumLagDeviationMilliseconds,
    double MaximumAttenuationDeviationDb,
    double ClippingPeakDbfs,
    double MinimumCorrelation,
    double MaximumResidualToTargetDb)
{
    public const string CandidateVersion = "phase11-calibrated-multi-window-shadow-v1";

    public static CalibratedBleedScoringPolicy From(
        DialogueProcessingPreset preset,
        DirectionalBleedCalibrationPolicy calibrationPolicy)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(calibrationPolicy);
        return new(
            CandidateVersion,
            MaximumWindowMilliseconds: calibrationPolicy.MaximumAnchorWindowMilliseconds,
            MinimumWindowMilliseconds: preset.MinimumSpeechMilliseconds,
            MinimumPassingWindowCount: 2,
            MaximumRetainedWindowCount: 16,
            MinimumPassingWindowRatio: 0.75,
            MaximumLagDeviationMilliseconds: calibrationPolicy.MaximumLagDeviationMilliseconds,
            MaximumAttenuationDeviationDb: calibrationPolicy.MaximumAttenuationDeviationDb,
            ClippingPeakDbfs: calibrationPolicy.ClippingPeakDbfs,
            MinimumCorrelation: calibrationPolicy.MinimumCorrelation,
            MaximumResidualToTargetDb: calibrationPolicy.MaximumResidualToTargetDb);
    }
}

public sealed record CalibratedBleedWindowEvidence(
    string SourcePhraseId,
    long TimelineStartSample,
    long TimelineEndSample,
    CalibratedBleedWindowDisposition Disposition,
    float OtherMicAdvantageDb,
    float WaveformCorrelation,
    int LagMilliseconds,
    float TransferScaleToTarget,
    float ResidualToTargetDb);

public sealed record CalibratedBleedCandidateEvidence(
    int TargetTrackIndex,
    string TargetSourceClipId,
    long TimelineStartSample,
    long TimelineEndSample,
    AudioSegmentStatus BaselineStatus,
    string BaselineReason,
    AudioSegmentStatus FinalStatus,
    string FinalReason,
    bool FinalEnabled,
    CalibratedBleedShadowOutcome Outcome,
    string OutcomeReason,
    int? SourceTrackIndex,
    string? FingerprintEvidenceSha256,
    int AvailableWindowCount,
    int EvaluatedWindowCount,
    int PassingWindowCount,
    int ConflictingWindowCount,
    string WindowEvidenceSha256,
    IReadOnlyList<CalibratedBleedWindowEvidence> WindowSamples);

public sealed record CalibratedBleedProjectShadow(
    DirectionalBleedCalibrationShadow Calibration,
    CalibratedBleedScoringPolicy ScoringPolicy,
    IReadOnlyList<CalibratedBleedCandidateEvidence> Candidates)
{
    public int ProductionChangedSegmentCount => Candidates.Count(candidate =>
        candidate.FinalStatus != candidate.BaselineStatus ||
        !string.Equals(candidate.FinalReason, candidate.BaselineReason, StringComparison.Ordinal) ||
        candidate.FinalEnabled != IsEnabled(candidate.BaselineStatus));

    private static bool IsEnabled(AudioSegmentStatus status) =>
        status is AudioSegmentStatus.Speech or AudioSegmentStatus.Ambiguous;
}
