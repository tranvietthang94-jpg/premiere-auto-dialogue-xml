using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Audio.Pcm;
using PremiereAutoDialogueXml.Audio.Vad;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.ProjectModel;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class TrackAudioScannerTests
{
    [TestMethod]
    public void ScanLegacyDecimates48KhzByThreeAndFlushesPartialChunk()
    {
        var samples = Enumerable.Range(0, 1_920).Select(index => (index % 1_000) / 2_000f).ToArray();
        using var fixture = TestAudioFixture.CreatePcm16(samples);
        var track = new PremiereAudioTrack(1, 1, [fixture.Clip("clip-1", 0, 1)]);
        using var detector = new CapturingDetector();

        var observations = new TrackAudioScanner(new PcmWaveSampleReader())
            .Scan(track, detector, DialogueProcessingPreset.Balanced);

        Assert.HasCount(2, observations);
        Assert.HasCount(2, detector.Chunks);
        Assert.AreEqual(samples[0], detector.Chunks[0][0], 0.00004f);
        Assert.AreEqual(samples[3], detector.Chunks[0][1], 0.00004f);
        Assert.AreEqual(1_920L, observations[^1].TimelineEndSample);
        Assert.AreEqual(1, detector.ResetCount);
    }

    [TestMethod]
    public void ScanAntiAliasProducesExactVadChunkAndTimelineCoverage()
    {
        var samples = Enumerable.Range(0, 1_920)
            .Select(index => 0.25f * MathF.Sin(2 * MathF.PI * 1_000 * index / 48_000))
            .ToArray();
        using var fixture = TestAudioFixture.CreatePcm16(samples);
        var track = new PremiereAudioTrack(1, 1, [fixture.Clip("clip-1", 0, 1)]);
        using var detector = new CapturingDetector();

        var observations = new TrackAudioScanner(new PcmWaveSampleReader())
            .Scan(
                track,
                detector,
                DialogueProcessingPreset.Balanced,
                VadResamplingMode.AntiAliasFir);

        Assert.HasCount(2, observations);
        Assert.IsTrue(detector.Chunks.All(chunk => chunk.Length == 512));
        Assert.AreEqual(0L, observations[0].TimelineStartSample);
        Assert.AreEqual(1_920L, observations[^1].TimelineEndSample);
        Assert.AreEqual(1, detector.ResetCount);
    }

    [TestMethod]
    public void ScanKeepsVadStateAcrossAdjacentWaveFiles()
    {
        using var first = TestAudioFixture.CreatePcm16(Enumerable.Repeat(0.1f, 1_920).ToArray());
        using var second = TestAudioFixture.CreatePcm16(Enumerable.Repeat(0.2f, 1_920).ToArray());
        var track = new PremiereAudioTrack(
            1,
            1,
            [first.Clip("clip-1", 0, 1), second.Clip("clip-2", 1, 2)]);
        using var detector = new CapturingDetector();

        var observations = new TrackAudioScanner(new PcmWaveSampleReader())
            .Scan(track, detector, DialogueProcessingPreset.Balanced);

        Assert.HasCount(3, observations);
        Assert.AreEqual(1, detector.ResetCount);
        Assert.AreEqual(0.1f, detector.Chunks[1][0], 0.00004f);
        Assert.AreEqual(0.2f, detector.Chunks[1][128], 0.00004f);
    }

    [TestMethod]
    public void ScanResetsStateAcrossPhraseBreakingTimelineGap()
    {
        using var first = TestAudioFixture.CreatePcm16(Enumerable.Repeat(0.1f, 1_920).ToArray());
        using var second = TestAudioFixture.CreatePcm16(Enumerable.Repeat(0.1f, 1_920).ToArray());
        var track = new PremiereAudioTrack(
            1,
            1,
            [first.Clip("clip-1", 0, 1), second.Clip("clip-2", 11, 12)]);
        using var detector = new CapturingDetector();

        var observations = new TrackAudioScanner(new PcmWaveSampleReader())
            .Scan(track, detector, DialogueProcessingPreset.Balanced);

        Assert.HasCount(4, observations);
        Assert.AreEqual(2, detector.ResetCount);
        Assert.AreEqual(0L, observations[0].TimelineStartSample);
        Assert.AreEqual(21_120L, observations[2].TimelineStartSample);
    }

    private sealed class CapturingDetector : IVoiceActivityDetector
    {
        public int SampleRate => 16_000;

        public int ChunkSampleCount => 512;

        public int ResetCount { get; private set; }

        public List<float[]> Chunks { get; } = [];

        public float ProcessChunk(ReadOnlySpan<float> samples)
        {
            Chunks.Add(samples.ToArray());
            return 0.01f;
        }

        public void Reset() => ResetCount++;

        public void Dispose()
        {
        }
    }
}
