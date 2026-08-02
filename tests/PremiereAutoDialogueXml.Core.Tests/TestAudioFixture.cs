using System.Buffers.Binary;
using PremiereAutoDialogueXml.Core.Media;
using PremiereAutoDialogueXml.Core.ProjectModel;

namespace PremiereAutoDialogueXml.Core.Tests;

internal sealed class TestAudioFixture : IDisposable
{
    private TestAudioFixture(string path, WaveFileInfo wave)
    {
        Path = path;
        Wave = wave;
    }

    public string Path { get; }

    public WaveFileInfo Wave { get; }

    public static TestAudioFixture CreatePcm16(IReadOnlyList<float> samples)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"padx-audio-{Guid.NewGuid():N}.pcm");
        var bytes = new byte[checked(samples.Count * sizeof(short))];
        for (var index = 0; index < samples.Count; index++)
        {
            var value = Math.Clamp(samples[index], -1f, short.MaxValue / 32768f);
            var integer = checked((short)Math.Round(value * 32768f));
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * sizeof(short)), integer);
        }

        File.WriteAllBytes(path, bytes);
        var wave = new WaveFileInfo(
            path,
            WaveEncodingKind.Pcm,
            ChannelCount: 1,
            SampleRate: 48_000,
            ContainerBitsPerSample: 16,
            ValidBitsPerSample: 16,
            BlockAlign: 2,
            DataOffset: 0,
            DeclaredDataBytes: bytes.Length,
            UsableDataBytes: bytes.Length,
            SampleFrameCount: samples.Count,
            PartialFrameBytes: 0,
            MissingTailBytes: 0);
        return new(path, wave);
    }

    public PremiereAudioClip Clip(
        string id,
        long timelineStartFrame,
        long timelineEndFrame,
        long sourceStartSample = 0,
        long? sourceEndSample = null) => new(
            Id: id,
            Name: id,
            Enabled: true,
            TimelineStartFrame: timelineStartFrame,
            TimelineEndFrame: timelineEndFrame,
            SourceInFrame: 0,
            SourceOutFrame: timelineEndFrame - timelineStartFrame,
            PproTicksIn: 0,
            PproTicksOut: 0,
            SourceStartSample: sourceStartSample,
            SourceEndSample: sourceEndSample ?? Wave.SampleFrameCount,
            SourceFileId: $"file-{id}",
            SourceMedia: new(
                Id: $"media-{id}",
                Name: id,
                PathUrl: new Uri(Path).AbsoluteUri,
                LocalPath: Path,
                Wave: Wave));

    public void Dispose()
    {
        File.Delete(Path);
    }
}
