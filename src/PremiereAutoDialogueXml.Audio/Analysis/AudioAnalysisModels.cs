namespace PremiereAutoDialogueXml.Audio.Analysis;

public enum AudioSegmentStatus
{
    Speech,
    Noise,
    Bleed,
    Ambiguous
}

public readonly record struct AudioFrameObservation(
    long TimelineStartSample,
    long TimelineEndSample,
    float VadProbability,
    float RmsDbfs,
    float SamplePeakDbfs,
    bool ContainsMedia);

public sealed record DialoguePhrase(
    string Id,
    int TrackIndex,
    long CoreStartSample,
    long CoreEndSample,
    long PaddedStartSample,
    long PaddedEndSample,
    float MeasuredPeakDbfs,
    float RequiredGainDb,
    float AppliedGainDb,
    bool GainWasCapped);

public sealed record AnalyzedAudioSegment(
    int TrackIndex,
    string SourceClipId,
    long TimelineStartSample,
    long TimelineEndSample,
    long SourceStartSample,
    long SourceEndSample,
    AudioSegmentStatus Status,
    string? PhraseId,
    float? GainDb,
    string Reason,
    BleedEvidence? BleedEvidence = null);

public sealed record BleedEvidence(
    int OtherTrackIndex,
    float OtherMicAdvantageDb,
    float WaveformCorrelation,
    int LagMilliseconds,
    float TargetRmsDbfs,
    float LearnedDirectVoiceRmsDbfs,
    float ResidualToTargetDb);

public sealed record TrackAudioAnalysis(
    int TrackIndex,
    long AnalyzedFrameCount,
    IReadOnlyList<DialoguePhrase> Phrases,
    IReadOnlyList<AnalyzedAudioSegment> Segments,
    float LearnedDirectVoiceRmsDbfs)
{
    public NoiseBoundaryTrackTrace? NoiseBoundaryTrace { get; init; }
}

public enum CrossTrackShadowOutcome
{
    NoComparableSpeech,
    BelowThreshold,
    ConflictingEvidence,
    LikelyBleed
}

public sealed record CrossTrackShadowEvidence(
    int TrackIndex,
    string SourceClipId,
    long TimelineStartSample,
    long TimelineEndSample,
    string SegmentReason,
    CrossTrackShadowOutcome Outcome,
    long? ComparedStartSample,
    long? ComparedEndSample,
    BleedEvidence? BestComparison);

public sealed record ProjectAudioAnalysis(
    IReadOnlyList<TrackAudioAnalysis> Tracks,
    string ModelVersion,
    string ModelSha256)
{
    public IReadOnlyList<CrossTrackShadowEvidence> ShadowEvidence { get; init; } = [];

    public VadFrontEndComparison? VadFrontEndComparison { get; init; }

    public NoiseBoundaryProjectComparison? NoiseBoundaryComparison { get; init; }
}

public sealed record VadDecisionDifference(
    int TrackIndex,
    string SourceClipId,
    long TimelineStartSample,
    long TimelineEndSample,
    AudioSegmentStatus LegacyStatus,
    string LegacyReason,
    AudioSegmentStatus CandidateStatus,
    string CandidateReason,
    AudioSegmentStatus FinalStatus,
    string FinalReason);

public sealed record VadFrontEndTrackComparison(
    int TrackIndex,
    int ObservationCount,
    int ChangedObservationCount,
    float MaximumProbabilityDelta,
    int SegmentDifferenceCount,
    int LegacyEnabledCandidateDisabledCount,
    int LegacyDisabledCandidateEnabledCount,
    IReadOnlyList<VadDecisionDifference> Differences);

public sealed record VadFrontEndComparison(
    string LegacyResampling,
    string CandidateResampling,
    IReadOnlyList<VadFrontEndTrackComparison> Tracks)
{
    public int ObservationCount => Tracks.Sum(track => track.ObservationCount);

    public int ChangedObservationCount => Tracks.Sum(track => track.ChangedObservationCount);

    public int SegmentDifferenceCount => Tracks.Sum(track => track.SegmentDifferenceCount);

    public int LegacyEnabledCandidateDisabledCount =>
        Tracks.Sum(track => track.LegacyEnabledCandidateDisabledCount);

    public int LegacyDisabledCandidateEnabledCount =>
        Tracks.Sum(track => track.LegacyDisabledCandidateEnabledCount);
}

internal readonly record struct TimelineInterval(long StartSample, long EndSample)
{
    public long Length => EndSample - StartSample;

    public bool Contains(long sample) => sample >= StartSample && sample < EndSample;

    public bool Overlaps(long start, long end) => StartSample < end && EndSample > start;
}
