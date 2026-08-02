using System.Buffers.Binary;
using PremiereAutoDialogueXml.Core.Inspection;
using PremiereAutoDialogueXml.Core.Media;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class WaveFileInspectorTests
{
    private readonly WaveFileInspector _inspector = new();

    [TestMethod]
    [DataRow(16)]
    [DataRow(24)]
    [DataRow(32)]
    public void Inspect_AcceptsSupportedPcmDepths(int bitsPerSample)
    {
        using var stream = new MemoryStream(WaveFixture.CreatePcm(bitsPerSample, dataBytes: bitsPerSample / 8 * 8));

        var result = _inspector.Inspect(stream, "voice.wav");

        Assert.IsTrue(result.IsSupported);
        Assert.IsNotNull(result.File);
        Assert.AreEqual(bitsPerSample, result.File.ContainerBitsPerSample);
        Assert.AreEqual(8, result.File.SampleFrameCount);
        Assert.IsEmpty(result.Issues);
    }

    [TestMethod]
    public void Inspect_AcceptsExtensiblePcm()
    {
        using var stream = new MemoryStream(WaveFixture.CreateExtensiblePcm(containerBits: 32, validBits: 24, dataBytes: 32));

        var result = _inspector.Inspect(stream, "extensible.wav");

        Assert.IsTrue(result.IsSupported);
        Assert.IsNotNull(result.File);
        Assert.AreEqual(WaveEncodingKind.ExtensiblePcm, result.File.Encoding);
        Assert.AreEqual(24, result.File.ValidBitsPerSample);
        Assert.AreEqual(8, result.File.SampleFrameCount);
    }

    [TestMethod]
    public void Inspect_AllowsOnePartialPcmFrameAtTail()
    {
        using var stream = new MemoryStream(WaveFixture.CreatePcm(24, dataBytes: 4));

        var result = _inspector.Inspect(stream, "partial.wav");

        Assert.IsTrue(result.IsSupported);
        Assert.IsNotNull(result.File);
        Assert.AreEqual(1, result.File.SampleFrameCount);
        Assert.AreEqual(1, result.File.PartialFrameBytes);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "wav-partial-tail"));
    }

    [TestMethod]
    public void Inspect_RejectsMissingCompletePcmFrame()
    {
        using var stream = new MemoryStream(WaveFixture.CreatePcm(24, dataBytes: 3, declaredDataBytes: 6));

        var result = _inspector.Inspect(stream, "truncated.wav");

        Assert.IsFalse(result.IsSupported);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "wav-data-truncated"));
    }

    [TestMethod]
    public void Inspect_UsesLongOffsetsForNearFourGigabyteWave()
    {
        const uint declaredDataBytes = uint.MaxValue - 1;
        var prefix = WaveFixture.CreatePcmHeader(16, declaredDataBytes);
        using var stream = new SparsePrefixStream(prefix, prefix.Length + (long)declaredDataBytes);

        var result = _inspector.Inspect(stream, "near-4gb.wav");

        Assert.IsTrue(result.IsSupported);
        Assert.IsNotNull(result.File);
        Assert.AreEqual((long)declaredDataBytes, result.File.UsableDataBytes);
        Assert.AreEqual(2_147_483_647L, result.File.SampleFrameCount);
    }

    [TestMethod]
    public void Inspect_RejectsStereoAndNon48Khz()
    {
        using var stereo = new MemoryStream(WaveFixture.CreatePcm(16, dataBytes: 16, channels: 2));
        using var fortyFour = new MemoryStream(WaveFixture.CreatePcm(16, dataBytes: 8, sampleRate: 44_100));

        var stereoResult = _inspector.Inspect(stereo, "stereo.wav");
        var sampleRateResult = _inspector.Inspect(fortyFour, "44k.wav");

        Assert.AreEqual(InspectionSeverity.Error, stereoResult.Issues.Single().Severity);
        Assert.AreEqual("wav-channel-unsupported", stereoResult.Issues.Single().Code);
        Assert.AreEqual("wav-sample-rate-unsupported", sampleRateResult.Issues.Single().Code);
    }

    private static class WaveFixture
    {
        private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");

        public static byte[] CreatePcm(
            int bitsPerSample,
            int dataBytes,
            int? declaredDataBytes = null,
            int channels = 1,
            int sampleRate = 48_000)
        {
            var header = CreatePcmHeader(bitsPerSample, checked((uint)(declaredDataBytes ?? dataBytes)), channels, sampleRate);
            var result = new byte[header.Length + dataBytes];
            header.CopyTo(result, 0);
            return result;
        }

        public static byte[] CreatePcmHeader(
            int bitsPerSample,
            uint declaredDataBytes,
            int channels = 1,
            int sampleRate = 48_000)
        {
            var blockAlign = checked((ushort)(channels * bitsPerSample / 8));
            var header = new byte[44];
            "RIFF"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), uint.MaxValue);
            "WAVEfmt "u8.CopyTo(header.AsSpan(8));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), checked((ushort)channels));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), checked((uint)sampleRate));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), checked((uint)(sampleRate * blockAlign)));
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), blockAlign);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), checked((ushort)bitsPerSample));
            "data"u8.CopyTo(header.AsSpan(36));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), declaredDataBytes);
            return header;
        }

        public static byte[] CreateExtensiblePcm(int containerBits, int validBits, int dataBytes)
        {
            var blockAlign = checked((ushort)(containerBits / 8));
            var header = new byte[68];
            "RIFF"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), checked((uint)(header.Length + dataBytes - 8)));
            "WAVEfmt "u8.CopyTo(header.AsSpan(8));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 40);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 0xFFFE);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), 48_000);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), checked((uint)(48_000 * blockAlign)));
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), blockAlign);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), checked((ushort)containerBits));
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(36), 22);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(38), checked((ushort)validBits));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), 0);
            PcmSubFormat.ToByteArray().CopyTo(header, 44);
            "data"u8.CopyTo(header.AsSpan(60));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(64), checked((uint)dataBytes));

            var result = new byte[header.Length + dataBytes];
            header.CopyTo(result, 0);
            return result;
        }
    }

    private sealed class SparsePrefixStream : Stream
    {
        private readonly byte[] _prefix;
        private long _position;

        public SparsePrefixStream(byte[] prefix, long length)
        {
            _prefix = prefix;
            Length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length { get; }

        public override long Position
        {
            get => _position;
            set => _position = value >= 0 && value <= Length ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= Length)
            {
                return 0;
            }

            var count = checked((int)Math.Min(buffer.Length, Length - _position));
            buffer[..count].Clear();
            if (_position < _prefix.Length)
            {
                var prefixCount = Math.Min(count, _prefix.Length - checked((int)_position));
                _prefix.AsSpan(checked((int)_position), prefixCount).CopyTo(buffer);
            }

            _position += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            Position = target;
            return Position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
