namespace PremiereAutoDialogueXml.Audio.Analysis;

public enum NoiseBoundaryAnalysisMode
{
    Phase09Baseline,
    NoiseBoundaryCandidate
}

public enum NoiseFloorTrainingDecision
{
    NotEligibleNoMedia,
    NotEligibleVadSpeech,
    NotEligibleInvalidLevel,
    WarmupCandidate,
    EligibleVadNegative,
    EligibleBackground,
    NotEligibleHighEnergyConflict,
    EligibleStableFloorStep
}

public enum NoiseBoundaryFrameState
{
    NoMedia,
    VadNegative,
    ConfirmedStartEvidence,
    UnconfirmedStartEvidence,
    ConfirmedContinueEvidence,
    UnconfirmedContinueEvidence,
    ContinueAmbiguous,
    BorderlineAmbiguous,
    WarmupAmbiguous,
    EnergyConflictAmbiguous
}

public sealed record NoiseFloorPolicyDescriptor(
    string Version,
    int WindowFrameCount,
    double Percentile,
    float InitialFloorDbfs,
    int WarmupFrameCount,
    float RiseSmoothing,
    float FallSmoothing,
    int StableStepFrameCount,
    float StableStepMaximumSpreadDb);

public sealed record VadBoundaryPolicyDescriptor(
    string Version,
    double StartThreshold,
    double ContinueThreshold,
    int PhraseBreakMilliseconds,
    int MinimumStartEvidenceMilliseconds,
    int MinimumDirectEvidenceMilliseconds);

public readonly record struct NoiseBoundaryFrameTrace(
    long TimelineStartSample,
    long TimelineEndSample,
    float VadProbability,
    float RmsDbfs,
    float NoiseFloorBeforeDbfs,
    float NoiseFloorAfterDbfs,
    bool NoiseFloorReadyBefore,
    bool NoiseFloorReadyAfter,
    NoiseFloorTrainingDecision NoiseFloorTrainingDecision,
    bool IsVadSpeech,
    bool IsDirectEvidence,
    bool IsAboveDirectEnergyThreshold,
    bool IsWarmupUncertain,
    NoiseBoundaryFrameState BoundaryState);

public sealed record NoiseBoundaryTrackTrace(
    int TrackIndex,
    NoiseBoundaryAnalysisMode Mode,
    NoiseFloorPolicyDescriptor Policy,
    VadBoundaryPolicyDescriptor BoundaryPolicy,
    IReadOnlyList<NoiseBoundaryFrameTrace> Frames);

public sealed record NoiseBoundaryFrameDifference(
    NoiseBoundaryFrameTrace Baseline,
    NoiseBoundaryFrameTrace Candidate);

public sealed record NoiseBoundaryFrameDifferenceSummary(
    int NoiseFloorBeforeDifferenceCount,
    int NoiseFloorAfterDifferenceCount,
    int NoiseFloorReadyBeforeDifferenceCount,
    int NoiseFloorReadyAfterDifferenceCount,
    int TrainingDecisionDifferenceCount,
    int VadSpeechDifferenceCount,
    int DirectEvidenceDifferenceCount,
    int DirectEnergyThresholdDifferenceCount,
    int WarmupUncertainDifferenceCount,
    int BoundaryStateDifferenceCount);

public sealed record NoiseBoundaryPhraseSnapshot(
    string Id,
    long CoreStartSample,
    long CoreEndSample,
    long PaddedStartSample,
    long PaddedEndSample,
    float MeasuredPeakDbfs,
    float RequiredGainDb,
    float AppliedGainDb,
    bool GainWasCapped);

public sealed record NoiseBoundaryPhraseDifference(
    string ChangeKind,
    IReadOnlyList<NoiseBoundaryPhraseSnapshot> BaselinePhrases,
    IReadOnlyList<NoiseBoundaryPhraseSnapshot> CandidatePhrases);

public sealed record NoiseBoundaryDecisionDifference(
    long TimelineStartSample,
    long TimelineEndSample,
    string? BaselineSourceClipId,
    AudioSegmentStatus? BaselineStatus,
    string? BaselineReason,
    bool BaselineEnabled,
    string? CandidateSourceClipId,
    AudioSegmentStatus? CandidateStatus,
    string? CandidateReason,
    bool CandidateEnabled)
{
    public string? BaselinePhraseId { get; init; }

    public float? BaselineGainDb { get; init; }

    public string? CandidatePhraseId { get; init; }

    public float? CandidateGainDb { get; init; }
}

public sealed record NoiseBoundaryFinalDecisionDifference(
    long TimelineStartSample,
    long TimelineEndSample,
    string SourceClipId,
    AudioSegmentStatus BaselineStatus,
    string BaselineReason,
    bool BaselineEnabled,
    string? BaselinePhraseId,
    float? BaselineGainDb,
    AudioSegmentStatus CandidateStatus,
    string CandidateReason,
    bool CandidateEnabled,
    string? CandidatePhraseId,
    float? CandidateGainDb,
    AudioSegmentStatus FinalStatus,
    string FinalReason,
    bool FinalEnabled,
    string? FinalPhraseId,
    float? FinalGainDb);

public sealed record NoiseBoundaryTrackComparison(
    int TrackIndex,
    NoiseBoundaryAnalysisMode BaselineMode,
    NoiseBoundaryAnalysisMode CandidateMode,
    string BaselinePolicyVersion,
    string CandidatePolicyVersion,
    string BaselineBoundaryPolicyVersion,
    string CandidateBoundaryPolicyVersion,
    int ObservationCount,
    int ChangedFrameCount,
    int PhraseDifferenceCount,
    int SegmentDifferenceCount,
    int BaselineEnabledCandidateDisabledCount,
    int BaselineDisabledCandidateEnabledCount,
    IReadOnlyList<NoiseBoundaryFrameDifference> FrameDifferenceSamples,
    IReadOnlyList<NoiseBoundaryDecisionDifference> DecisionDifferences)
{
    public NoiseFloorPolicyDescriptor? BaselinePolicy { get; init; }

    public NoiseFloorPolicyDescriptor? CandidatePolicy { get; init; }

    public VadBoundaryPolicyDescriptor? BaselineBoundaryPolicy { get; init; }

    public VadBoundaryPolicyDescriptor? CandidateBoundaryPolicy { get; init; }

    public float MaximumNoiseFloorDeltaDb { get; init; }

    public int TrainingEligibilityDifferenceCount { get; init; }

    public string FrameTraceSha256 { get; init; } =
        "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855";

    public NoiseBoundaryFrameDifferenceSummary FrameDifferenceSummary { get; init; } =
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public int CapturedFrameDifferenceCount => FrameDifferenceSamples.Count;

    public int BaselineEnabledFinalDisabledCount { get; init; }

    public IReadOnlyList<NoiseBoundaryPhraseDifference> PhraseDifferences { get; init; } = [];

    public IReadOnlyList<NoiseBoundaryFinalDecisionDifference> FinalDifferences { get; init; } = [];
}

public sealed record NoiseBoundaryResamplingComparison(
    string Resampling,
    IReadOnlyList<NoiseBoundaryTrackComparison> Tracks)
{
    public int ObservationCount => Tracks.Sum(track => track.ObservationCount);

    public int ChangedFrameCount => Tracks.Sum(track => track.ChangedFrameCount);

    public int PhraseDifferenceCount => Tracks.Sum(track => track.PhraseDifferenceCount);

    public int SegmentDifferenceCount => Tracks.Sum(track => track.SegmentDifferenceCount);

    public int BaselineEnabledCandidateDisabledCount =>
        Tracks.Sum(track => track.BaselineEnabledCandidateDisabledCount);

    public int BaselineDisabledCandidateEnabledCount =>
        Tracks.Sum(track => track.BaselineDisabledCandidateEnabledCount);

    public int BaselineEnabledFinalDisabledCount =>
        Tracks.Sum(track => track.BaselineEnabledFinalDisabledCount);
}

public sealed record NoiseBoundaryProjectComparison(
    IReadOnlyList<NoiseBoundaryResamplingComparison> FrontEnds)
{
    public int ObservationCount => FrontEnds.Sum(frontEnd => frontEnd.ObservationCount);

    public int ChangedFrameCount => FrontEnds.Sum(frontEnd => frontEnd.ChangedFrameCount);

    public int PhraseDifferenceCount => FrontEnds.Sum(frontEnd => frontEnd.PhraseDifferenceCount);

    public int SegmentDifferenceCount => FrontEnds.Sum(frontEnd => frontEnd.SegmentDifferenceCount);

    public int BaselineEnabledCandidateDisabledCount =>
        FrontEnds.Sum(frontEnd => frontEnd.BaselineEnabledCandidateDisabledCount);

    public int BaselineDisabledCandidateEnabledCount =>
        FrontEnds.Sum(frontEnd => frontEnd.BaselineDisabledCandidateEnabledCount);

    public int BaselineEnabledFinalDisabledCount =>
        FrontEnds.Sum(frontEnd => frontEnd.BaselineEnabledFinalDisabledCount);
}

public sealed record NoiseBoundaryShadowAnalysis(
    TrackAudioAnalysis Baseline,
    TrackAudioAnalysis Candidate,
    NoiseBoundaryTrackComparison Comparison);

internal readonly record struct NoiseFloorFrameSnapshot(
    float NoiseFloorBeforeDbfs,
    float NoiseFloorAfterDbfs,
    bool NoiseFloorReadyBefore,
    bool NoiseFloorReadyAfter,
    NoiseFloorTrainingDecision TrainingDecision);

internal sealed record AudioFrameEvidenceBuildResult(
    IReadOnlyList<AudioFrameEvidence> Evidence,
    IReadOnlyList<NoiseFloorFrameSnapshot> NoiseFloorTrace,
    NoiseFloorPolicyDescriptor Policy);
