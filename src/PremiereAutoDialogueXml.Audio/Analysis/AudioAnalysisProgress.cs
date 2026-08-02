namespace PremiereAutoDialogueXml.Audio.Analysis;

public sealed record AudioAnalysisProgress(
    int CompletedTracks,
    int TotalTracks,
    int? CurrentTrackIndex,
    string Message);
