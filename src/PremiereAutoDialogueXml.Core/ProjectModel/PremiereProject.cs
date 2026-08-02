using PremiereAutoDialogueXml.Core.Media;

namespace PremiereAutoDialogueXml.Core.ProjectModel;

public sealed record PremiereProject(
    string SourceXmlPath,
    string SourceXmlSha256,
    PremiereSequence Sequence);

public sealed record PremiereSequence(
    string Id,
    string Uuid,
    string Name,
    int FrameRate,
    long DurationFrames,
    int OutputChannelCount,
    int AudioSampleRate,
    IReadOnlyList<PremiereAudioTrack> AudioTracks);

public sealed record PremiereAudioTrack(
    int Index,
    int OutputChannelIndex,
    IReadOnlyList<PremiereAudioClip> Clips);

public sealed record PremiereAudioClip(
    string Id,
    string Name,
    bool Enabled,
    long TimelineStartFrame,
    long TimelineEndFrame,
    long SourceInFrame,
    long SourceOutFrame,
    long PproTicksIn,
    long PproTicksOut,
    long SourceStartSample,
    long SourceEndSample,
    string SourceFileId,
    PremiereSourceMedia SourceMedia);

public sealed record PremiereSourceMedia(
    string Id,
    string Name,
    string PathUrl,
    string LocalPath,
    WaveFileInfo Wave);
