using System.Buffers.Binary;
using PremiereAutoDialogueXml.Core.Inspection;

namespace PremiereAutoDialogueXml.Core.Media;

public sealed class WaveFileInspector
{
    private const ushort WaveFormatPcm = 0x0001;
    private const ushort WaveFormatExtensible = 0xFFFE;
    private const int MaximumFormatChunkBytes = 1_024;
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");

    public WaveInspectionResult Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            using var stream = new FileStream(
                path,
                new FileStreamOptions
                {
                    Access = FileAccess.Read,
                    Mode = FileMode.Open,
                    Share = FileShare.Read,
                    Options = FileOptions.RandomAccess
                });
            return Inspect(stream, path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure("wav-open-failed", $"Không thể đọc WAV '{Path.GetFileName(path)}': {exception.Message}");
        }
    }

    public WaveInspectionResult Inspect(Stream stream, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        if (!stream.CanRead || !stream.CanSeek)
        {
            return Failure("wav-stream-unsupported", "Luồng WAV phải hỗ trợ đọc và seek 64-bit.");
        }

        try
        {
            return InspectCore(stream, sourceName);
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException or ArgumentException or OverflowException)
        {
            return Failure("wav-invalid", $"Header WAV '{Path.GetFileName(sourceName)}' không hợp lệ: {exception.Message}");
        }
    }

    private static WaveInspectionResult InspectCore(Stream stream, string sourceName)
    {
        var issues = new List<InspectionIssue>();
        if (stream.Length < 12)
        {
            return Failure("wav-header-truncated", "Tệp quá ngắn để chứa header RIFF/WAVE.");
        }

        stream.Position = 0;
        Span<byte> riffHeader = stackalloc byte[12];
        ReadExactly(stream, riffHeader);
        if (!riffHeader[..4].SequenceEqual("RIFF"u8) || !riffHeader[8..12].SequenceEqual("WAVE"u8))
        {
            return Failure("wav-signature-invalid", "MVP chỉ hỗ trợ tệp RIFF/WAVE PCM.");
        }

        FormatChunk? format = null;
        long dataOffset = -1;
        long declaredDataBytes = -1;

        Span<byte> chunkHeader = stackalloc byte[8];
        while (stream.Position <= stream.Length - 8)
        {
            ReadExactly(stream, chunkHeader);
            var chunkId = chunkHeader[..4];
            var chunkSize = (long)BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
            var chunkDataOffset = stream.Position;

            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (format is not null)
                {
                    return Failure("wav-fmt-duplicate", "WAV chứa nhiều hơn một chunk fmt.");
                }

                if (chunkSize is < 16 or > MaximumFormatChunkBytes || chunkDataOffset + chunkSize > stream.Length)
                {
                    return Failure("wav-fmt-invalid", "Chunk fmt bị thiếu hoặc có kích thước không hợp lệ.");
                }

                var bytes = new byte[checked((int)chunkSize)];
                ReadExactly(stream, bytes);
                format = ParseFormat(bytes);
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                if (dataOffset >= 0)
                {
                    return Failure("wav-data-duplicate", "WAV chứa nhiều hơn một chunk data.");
                }

                dataOffset = chunkDataOffset;
                declaredDataBytes = chunkSize;
            }

            if (format is not null && dataOffset >= 0)
            {
                break;
            }

            var nextChunk = checked(chunkDataOffset + chunkSize + (chunkSize & 1));
            if (nextChunk > stream.Length)
            {
                return Failure("wav-chunk-truncated", "Một chunk WAV kết thúc ngoài kích thước tệp.");
            }

            stream.Position = nextChunk;
        }

        if (format is null)
        {
            return Failure("wav-fmt-missing", "Không tìm thấy chunk fmt trong WAV.");
        }

        if (dataOffset < 0)
        {
            return Failure("wav-data-missing", "Không tìm thấy chunk data trong WAV.");
        }

        var formatIssue = ValidateSupportedFormat(format);
        if (formatIssue is not null)
        {
            return new(null, [formatIssue]);
        }

        var availableAfterOffset = Math.Max(0, stream.Length - dataOffset);
        var actualDataBytes = Math.Min(declaredDataBytes, availableAfterOffset);
        var missingTailByteCount = declaredDataBytes - actualDataBytes;
        if (missingTailByteCount >= format.BlockAlign)
        {
            return Failure(
                "wav-data-truncated",
                $"Chunk data thiếu {missingTailByteCount} byte, tương đương ít nhất một PCM frame.");
        }

        var missingTailBytes = checked((int)missingTailByteCount);
        var partialFrameBytes = checked((int)(actualDataBytes % format.BlockAlign));
        if (missingTailBytes > 0 || partialFrameBytes > 0)
        {
            issues.Add(new(
                "wav-partial-tail",
                InspectionSeverity.Warning,
                $"WAV có phần đuôi PCM chưa đủ một frame (thiếu {missingTailBytes} byte, dư {partialFrameBytes} byte); phần frame hoàn chỉnh vẫn được dùng."));
        }

        var usableDataBytes = actualDataBytes - partialFrameBytes;
        var sampleFrameCount = usableDataBytes / format.BlockAlign;
        var file = new WaveFileInfo(
            sourceName,
            format.Encoding,
            format.ChannelCount,
            format.SampleRate,
            format.ContainerBitsPerSample,
            format.ValidBitsPerSample,
            format.BlockAlign,
            dataOffset,
            declaredDataBytes,
            usableDataBytes,
            sampleFrameCount,
            partialFrameBytes,
            missingTailBytes);

        return new(file, issues);
    }

    private static FormatChunk ParseFormat(ReadOnlySpan<byte> bytes)
    {
        var formatTag = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        var averageBytesPerSecond = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]);
        var containerBits = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]);

        if (formatTag == WaveFormatPcm)
        {
            return new(
                WaveEncodingKind.Pcm,
                channels,
                checked((int)sampleRate),
                containerBits,
                containerBits,
                blockAlign,
                averageBytesPerSecond);
        }

        if (formatTag != WaveFormatExtensible || bytes.Length < 40)
        {
            throw new ArgumentException($"Mã định dạng WAV 0x{formatTag:X4} không được hỗ trợ.");
        }

        var extensionBytes = BinaryPrimitives.ReadUInt16LittleEndian(bytes[16..]);
        if (extensionBytes < 22 || bytes.Length < 18 + extensionBytes)
        {
            throw new ArgumentException("WAVE_FORMAT_EXTENSIBLE thiếu phần mở rộng 22 byte.");
        }

        var validBits = BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..]);
        var subFormat = new Guid(bytes[24..40]);
        if (subFormat != PcmSubFormat)
        {
            throw new ArgumentException("WAVE_FORMAT_EXTENSIBLE không chứa PCM integer.");
        }

        return new(
            WaveEncodingKind.ExtensiblePcm,
            channels,
            checked((int)sampleRate),
            containerBits,
            validBits,
            blockAlign,
            averageBytesPerSecond);
    }

    private static InspectionIssue? ValidateSupportedFormat(FormatChunk format)
    {
        if (format.ChannelCount != 1)
        {
            return Error("wav-channel-unsupported", $"WAV phải là mono; header báo {format.ChannelCount} kênh.");
        }

        if (format.SampleRate != 48_000)
        {
            return Error("wav-sample-rate-unsupported", $"WAV phải là 48 kHz; header báo {format.SampleRate} Hz.");
        }

        if (format.ContainerBitsPerSample is not (16 or 24 or 32) ||
            format.ValidBitsPerSample is not (16 or 24 or 32) ||
            format.ValidBitsPerSample > format.ContainerBitsPerSample)
        {
            return Error(
                "wav-bit-depth-unsupported",
                $"WAV phải là PCM 16/24/32-bit; header báo {format.ValidBitsPerSample}-bit trong container {format.ContainerBitsPerSample}-bit.");
        }

        var expectedBlockAlign = checked(format.ChannelCount * format.ContainerBitsPerSample / 8);
        if (format.ContainerBitsPerSample % 8 != 0 || format.BlockAlign != expectedBlockAlign)
        {
            return Error("wav-block-align-invalid", "Block align của WAV không khớp channel count và bit depth.");
        }

        var expectedAverage = checked((long)format.SampleRate * format.BlockAlign);
        if (format.AverageBytesPerSecond != expectedAverage)
        {
            return Error("wav-byte-rate-invalid", "Byte rate của WAV không khớp sample rate và block align.");
        }

        return null;
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer[read..]);
            if (count == 0)
            {
                throw new EndOfStreamException();
            }

            read += count;
        }
    }

    private static WaveInspectionResult Failure(string code, string message) => new(null, [Error(code, message)]);

    private static InspectionIssue Error(string code, string message) => new(code, InspectionSeverity.Error, message);

    private sealed record FormatChunk(
        WaveEncodingKind Encoding,
        int ChannelCount,
        int SampleRate,
        int ContainerBitsPerSample,
        int ValidBitsPerSample,
        int BlockAlign,
        long AverageBytesPerSecond);
}
