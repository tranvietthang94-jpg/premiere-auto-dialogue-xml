using System.Buffers.Binary;
using PremiereAutoDialogueXml.Audio.Pcm;
using PremiereAutoDialogueXml.Core.Media;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class PcmWaveSampleReaderTests
{
    [TestMethod]
    public void DecodeMonoConvertsPcm16Boundaries()
    {
        short[] values = [short.MinValue, -16_384, 0, 16_384, short.MaxValue];
        var bytes = new byte[values.Length * sizeof(short)];
        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * sizeof(short)), values[index]);
        }

        var decoded = new float[values.Length];
        PcmWaveSampleReader.DecodeMono(bytes, decoded, Wave(bits: 16, frames: values.Length));

        CollectionAssert.AreEqual(
            new[] { -1f, -0.5f, 0f, 0.5f, short.MaxValue / 32768f },
            decoded);
    }

    [TestMethod]
    public void DecodeMonoSignExtendsPcm24()
    {
        int[] values = [-8_388_608, -4_194_304, 0, 4_194_304, 8_388_607];
        var bytes = EncodePcm24(values);
        var decoded = new float[values.Length];

        PcmWaveSampleReader.DecodeMono(bytes, decoded, Wave(bits: 24, frames: values.Length));

        CollectionAssert.AreEqual(
            new[] { -1f, -0.5f, 0f, 0.5f, 8_388_607 / 8_388_608f },
            decoded);
    }

    [TestMethod]
    public void DecodeMonoUsesValidBitsForExtensiblePcm()
    {
        int[] leftAlignedValues = [-1_073_741_824, 0, 1_073_741_824];
        var bytes = new byte[leftAlignedValues.Length * sizeof(int)];
        for (var index = 0; index < leftAlignedValues.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(index * sizeof(int)), leftAlignedValues[index]);
        }

        var decoded = new float[leftAlignedValues.Length];
        var wave = Wave(bits: 32, frames: leftAlignedValues.Length) with
        {
            Encoding = WaveEncodingKind.ExtensiblePcm,
            ValidBitsPerSample = 24
        };

        PcmWaveSampleReader.DecodeMono(bytes, decoded, wave);

        CollectionAssert.AreEqual(new[] { -0.5f, 0f, 0.5f }, decoded);
    }

    [TestMethod]
    public void ReadRangeReadsOnlyRequestedSourceTrim()
    {
        var path = Path.Combine(Path.GetTempPath(), $"padx-{Guid.NewGuid():N}.wav");
        short[] values = [-32_768, -24_576, -16_384, -8_192, 0, 8_192, 16_384, 24_576, 30_000, 32_767];
        var bytes = new byte[44 + (values.Length * sizeof(short))];
        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44 + (index * sizeof(short))), values[index]);
        }

        File.WriteAllBytes(path, bytes);
        try
        {
            var wave = Wave(bits: 16, frames: values.Length) with { Path = path, DataOffset = 44 };
            var result = new List<float>();

            new PcmWaveSampleReader().ReadRange(
                wave,
                startSampleFrame: 3,
                sampleFrameCount: 4,
                block => result.AddRange(block.Span.ToArray()));

            CollectionAssert.AreEqual(new[] { -0.25f, 0f, 0.25f, 0.5f }, result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void ReadRangeRejectsOutOfBoundsWithoutOpeningFile()
    {
        var wave = Wave(bits: 16, frames: 100) with { Path = "missing.wav" };

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new PcmWaveSampleReader().ReadRange(wave, 99, 2, _ => { }));
    }

    private static WaveFileInfo Wave(int bits, long frames) => new(
        Path: "fixture.wav",
        Encoding: WaveEncodingKind.Pcm,
        ChannelCount: 1,
        SampleRate: 48_000,
        ContainerBitsPerSample: bits,
        ValidBitsPerSample: bits,
        BlockAlign: bits / 8,
        DataOffset: 0,
        DeclaredDataBytes: frames * (bits / 8),
        UsableDataBytes: frames * (bits / 8),
        SampleFrameCount: frames,
        PartialFrameBytes: 0,
        MissingTailBytes: 0);

    private static byte[] EncodePcm24(IEnumerable<int> values)
    {
        var materialized = values.ToArray();
        var bytes = new byte[materialized.Length * 3];
        for (var index = 0; index < materialized.Length; index++)
        {
            var value = materialized[index];
            bytes[(index * 3) + 0] = (byte)value;
            bytes[(index * 3) + 1] = (byte)(value >> 8);
            bytes[(index * 3) + 2] = (byte)(value >> 16);
        }

        return bytes;
    }
}
