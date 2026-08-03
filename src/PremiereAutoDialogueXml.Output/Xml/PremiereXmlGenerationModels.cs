using System.Xml.Linq;
using PremiereAutoDialogueXml.Audio.Analysis;

namespace PremiereAutoDialogueXml.Output.Xml;

public sealed record GeneratedPremiereXml(
    XDocument Document,
    string SequenceId,
    string SequenceUuid,
    string SequenceName,
    IReadOnlyList<GeneratedAudioFragment> AudioFragments,
    IReadOnlyList<GeneratedSequenceMarker> Markers);

public sealed record GeneratedAudioFragment(
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
    double? GainDb,
    string Reason,
    BleedEvidence? BleedEvidence);

public sealed record GeneratedSequenceMarker(
    string Name,
    string Comment,
    long InFrame,
    long OutFrame,
    int TrackIndex,
    string Reason);
