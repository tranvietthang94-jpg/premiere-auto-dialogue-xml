using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Output.Xml;

namespace PremiereAutoDialogueXml.Output.Audit;

public sealed record OutputAudit(
    string SchemaVersion,
    string RunId,
    DateTimeOffset CreatedAtUtc,
    string SourceXmlFileName,
    string SourceXmlSha256,
    string OutputXmlFileName,
    string OutputXmlSha256,
    string SourceSequenceId,
    string OutputSequenceId,
    string OutputSequenceUuid,
    string OutputSequenceName,
    ModelAudit Model,
    PresetAudit Preset,
    IReadOnlyList<FragmentAudit> Fragments,
    IReadOnlyList<MarkerAudit> Markers);

public sealed record ModelAudit(string Version, string Sha256);

public sealed record PresetAudit(
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
    string GainReferencePeakPolicy,
    double MaximumBoostDb,
    int MaximumWorkers)
{
    public static PresetAudit From(DialogueProcessingPreset preset) => new(
        preset.Name,
        preset.VadThreshold,
        preset.MinimumSpeechMilliseconds,
        preset.PhraseBreakMilliseconds,
        preset.PaddingBeforeMilliseconds,
        preset.PaddingAfterMilliseconds,
        preset.DirectVoiceAboveNoiseDb,
        preset.BleedOtherMicAdvantageDb,
        preset.BleedCorrelationThreshold,
        preset.BleedMaximumLagMilliseconds,
        preset.TargetSamplePeakDbfs,
        preset.PremiereRoutingProfile,
        preset.PremiereCenterPanCompensationDb,
        preset.GainReferencePeakPolicy,
        preset.MaximumBoostDb,
        preset.MaximumWorkers);
}

public sealed record FragmentAudit(
    string ClipItemId,
    int TrackIndex,
    string SourceClipId,
    string SourceFileId,
    string SourceFileName,
    long TimelineStartFrame,
    long TimelineEndFrame,
    long SourceInFrame,
    long SourceOutFrame,
    long PproTicksIn,
    long PproTicksOut,
    long SourceStartSample,
    long SourceEndSample,
    AudioSegmentStatus Status,
    bool Enabled,
    string? PhraseId,
    double? MeasuredPeakDbfs,
    double? RequiredGainDb,
    double? AppliedGainDb,
    bool GainWasCapped,
    string Reason,
    BleedEvidence? BleedEvidence)
{
    public static FragmentAudit From(
        GeneratedAudioFragment fragment,
        IReadOnlyDictionary<string, DialoguePhrase> phrases)
    {
        var phrase = fragment.PhraseId is not null && phrases.TryGetValue(fragment.PhraseId, out var match)
            ? match
            : null;
        return new(
            fragment.ClipItemId,
            fragment.TrackIndex,
            fragment.SourceClipId,
            fragment.SourceFileId,
            fragment.SourceFileName,
            fragment.TimelineStartFrame,
            fragment.TimelineEndFrame,
            fragment.SourceInFrame,
            fragment.SourceOutFrame,
            fragment.PproTicksIn,
            fragment.PproTicksOut,
            fragment.SourceStartSample,
            fragment.SourceEndSample,
            fragment.Status,
            fragment.Enabled,
            fragment.PhraseId,
            phrase?.MeasuredPeakDbfs,
            phrase?.RequiredGainDb,
            fragment.GainDb,
            phrase?.GainWasCapped ?? false,
            fragment.Reason,
            fragment.BleedEvidence);
    }
}

public sealed record MarkerAudit(
    string Name,
    string Comment,
    long InFrame,
    long OutFrame,
    int TrackIndex,
    string Reason)
{
    public static MarkerAudit From(GeneratedSequenceMarker marker) => new(
        marker.Name,
        marker.Comment,
        marker.InFrame,
        marker.OutFrame,
        marker.TrackIndex,
        marker.Reason);
}
