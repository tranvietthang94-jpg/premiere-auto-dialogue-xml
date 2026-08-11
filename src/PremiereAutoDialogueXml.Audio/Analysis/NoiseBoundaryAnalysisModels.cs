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
    EligibleVadNegative
}

public enum NoiseBoundaryFrameState
{
    NoMedia,
    VadNegative,
    ConfirmedStartEvidence,
    UnconfirmedStartEvidence,
    BorderlineAmbiguous,
    EnergyConflictAmbiguous
}

public sealed record NoiseBoundaryFrameTrace(
    long TimelineStartSample,
    long TimelineEndSample,
    float VadProbability,
    float RmsDbfs,
    float NoiseFloorBeforeDbfs,
    float NoiseFloorAfterDbfs,
    NoiseFloorTrainingDecision NoiseFloorTrainingDecision,
    bool IsVadSpeech,
    bool IsDirectEvidence,
    bool IsAboveDirectEnergyThreshold,
    NoiseBoundaryFrameState BoundaryState);

public sealed record NoiseBoundaryTrackTrace(
    int TrackIndex,
    NoiseBoundaryAnalysisMode Mode,
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
    NoiseFloorTrainingDecision TrainingDecision);

internal sealed record AudioFrameEvidenceBuildResult(
    IReadOnlyList<AudioFrameEvidence> Evidence,
    IReadOnlyList<NoiseFloorFrameSnapshot> NoiseFloorTrace);
