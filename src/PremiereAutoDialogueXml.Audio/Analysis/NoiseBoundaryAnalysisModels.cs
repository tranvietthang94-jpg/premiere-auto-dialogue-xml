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

public sealed record NoiseBoundaryFrameTrace(
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
    bool CandidateEnabled);

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
    IReadOnlyList<NoiseBoundaryFrameDifference> FrameDifferences,
    IReadOnlyList<NoiseBoundaryDecisionDifference> DecisionDifferences);

public sealed record NoiseBoundaryShadowAnalysis(
    TrackAudioAnalysis Baseline,
    TrackAudioAnalysis Candidate,
    NoiseBoundaryTrackComparison Comparison);

internal sealed record NoiseFloorFrameSnapshot(
    float NoiseFloorBeforeDbfs,
    float NoiseFloorAfterDbfs,
    bool NoiseFloorReadyBefore,
    bool NoiseFloorReadyAfter,
    NoiseFloorTrainingDecision TrainingDecision);

internal sealed record AudioFrameEvidenceBuildResult(
    IReadOnlyList<AudioFrameEvidence> Evidence,
    IReadOnlyList<NoiseFloorFrameSnapshot> NoiseFloorTrace,
    NoiseFloorPolicyDescriptor Policy);
