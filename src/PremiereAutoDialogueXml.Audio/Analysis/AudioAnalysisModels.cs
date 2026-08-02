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
    float LearnedDirectVoiceRmsDbfs);

public sealed record ProjectAudioAnalysis(
    IReadOnlyList<TrackAudioAnalysis> Tracks,
    string ModelVersion,
    string ModelSha256);

internal readonly record struct TimelineInterval(long StartSample, long EndSample)
{
    public long Length => EndSample - StartSample;

    public bool Contains(long sample) => sample >= StartSample && sample < EndSample;

    public bool Overlaps(long start, long end) => StartSample < end && EndSample > start;
}
