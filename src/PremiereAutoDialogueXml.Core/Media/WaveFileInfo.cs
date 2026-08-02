namespace PremiereAutoDialogueXml.Core.Media;

public sealed record WaveFileInfo(
    string Path,
    WaveEncodingKind Encoding,
    int ChannelCount,
    int SampleRate,
    int ContainerBitsPerSample,
    int ValidBitsPerSample,
    int BlockAlign,
    long DataOffset,
    long DeclaredDataBytes,
    long UsableDataBytes,
    long SampleFrameCount,
    int PartialFrameBytes,
    int MissingTailBytes);
