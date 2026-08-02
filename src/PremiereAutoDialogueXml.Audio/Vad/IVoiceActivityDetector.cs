namespace PremiereAutoDialogueXml.Audio.Vad;

public interface IVoiceActivityDetector : IDisposable
{
    int SampleRate { get; }

    int ChunkSampleCount { get; }

    float ProcessChunk(ReadOnlySpan<float> samples);

    void Reset();
}
