using System.Buffers;
using PremiereAutoDialogueXml.Core.Media;

namespace PremiereAutoDialogueXml.Audio.Pcm;

public sealed class PcmWaveSampleReader
{
    private const int DefaultBufferFrames = 16_384;

    public void ReadRange(
        WaveFileInfo wave,
        long startSampleFrame,
        long sampleFrameCount,
        Action<ReadOnlyMemory<float>> consume,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wave);
        ArgumentNullException.ThrowIfNull(consume);

        if (startSampleFrame < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startSampleFrame));
        }

        if (sampleFrameCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleFrameCount));
        }

        var endSampleFrame = checked(startSampleFrame + sampleFrameCount);
        if (endSampleFrame > wave.SampleFrameCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleFrameCount),
                $"Vùng đọc [{startSampleFrame}, {endSampleFrame}) vượt quá {wave.SampleFrameCount} frame của WAV.");
        }

        if (sampleFrameCount == 0)
        {
            return;
        }

        ValidateWaveContract(wave);

        var byteBufferLength = checked(DefaultBufferFrames * wave.BlockAlign);
        var bytes = ArrayPool<byte>.Shared.Rent(byteBufferLength);
        var samples = ArrayPool<float>.Shared.Rent(DefaultBufferFrames);

        try
        {
            using var stream = new FileStream(
                wave.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);

            stream.Position = checked(wave.DataOffset + (startSampleFrame * wave.BlockAlign));
            var remainingFrames = sampleFrameCount;

            while (remainingFrames > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requestedFrames = checked((int)Math.Min(DefaultBufferFrames, remainingFrames));
                var requestedBytes = checked(requestedFrames * wave.BlockAlign);
                stream.ReadExactly(bytes.AsSpan(0, requestedBytes));

                DecodeMono(bytes.AsSpan(0, requestedBytes), samples.AsSpan(0, requestedFrames), wave);
                consume(samples.AsMemory(0, requestedFrames));
                remainingFrames -= requestedFrames;
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(samples, clearArray: true);
            ArrayPool<byte>.Shared.Return(bytes, clearArray: true);
        }
    }

    public static void DecodeMono(ReadOnlySpan<byte> source, Span<float> destination, WaveFileInfo wave)
    {
        ArgumentNullException.ThrowIfNull(wave);
        ValidateWaveContract(wave);

        var requiredBytes = checked(destination.Length * wave.BlockAlign);
        if (source.Length < requiredBytes)
        {
            throw new ArgumentException("Buffer PCM không đủ dữ liệu cho số frame cần giải mã.", nameof(source));
        }

        var bytesPerSample = wave.ContainerBitsPerSample / 8;
        var alignmentShift = wave.ContainerBitsPerSample - wave.ValidBitsPerSample;
        var scale = MathF.Pow(2, wave.ValidBitsPerSample - 1);

        for (var index = 0; index < destination.Length; index++)
        {
            var offset = checked(index * bytesPerSample);
            var signed = wave.ContainerBitsPerSample switch
            {
                16 => (short)(source[offset] | (source[offset + 1] << 8)),
                24 => SignExtend24(source, offset),
                32 => source[offset] |
                      (source[offset + 1] << 8) |
                      (source[offset + 2] << 16) |
                      (source[offset + 3] << 24),
                _ => throw new InvalidDataException($"PCM {wave.ContainerBitsPerSample}-bit chưa được hỗ trợ.")
            };

            destination[index] = (signed >> alignmentShift) / scale;
        }
    }

    private static int SignExtend24(ReadOnlySpan<byte> source, int offset)
    {
        var value = source[offset] | (source[offset + 1] << 8) | (source[offset + 2] << 16);
        return (value & 0x0080_0000) == 0 ? value : value | unchecked((int)0xFF00_0000);
    }

    private static void ValidateWaveContract(WaveFileInfo wave)
    {
        if (wave.ChannelCount != 1 || wave.SampleRate != 48_000)
        {
            throw new InvalidDataException("Bộ đọc Phase 03 chỉ nhận WAV mono PCM 48 kHz đã qua kiểm tra.");
        }

        if (wave.ContainerBitsPerSample is not (16 or 24 or 32) ||
            wave.ValidBitsPerSample is not (16 or 24 or 32) ||
            wave.ValidBitsPerSample > wave.ContainerBitsPerSample ||
            wave.BlockAlign != wave.ContainerBitsPerSample / 8)
        {
            throw new InvalidDataException("Thông số PCM không phù hợp với hợp đồng Phase 03.");
        }
    }
}
