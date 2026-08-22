using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Audio.Pcm;
using PremiereAutoDialogueXml.Audio.Vad;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.ProjectModel;
using PremiereAutoDialogueXml.Core.Timing;

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

    [TestMethod]
    [DataRow(24, 2_000)]
    [DataRow(25, 1_920)]
    [DataRow(30, 1_600)]
    public void ScanUsesProjectFrameGridForTimelineCoverage(int frameRate, int samplesPerFrame)
    {
        var samples = Enumerable.Repeat(0.1f, samplesPerFrame).ToArray();
        using var fixture = TestAudioFixture.CreatePcm16(samples);
        var track = new PremiereAudioTrack(1, 1, [fixture.Clip("clip-1", 0, 1)]);
        using var detector = new CapturingDetector();
        var frameGrid = PremiereNdfFrameGrid.Create(frameRate);

        var observations = new TrackAudioScanner(new PcmWaveSampleReader(), frameGrid)
            .Scan(track, detector, DialogueProcessingPreset.Balanced);

        Assert.AreEqual(0L, observations[0].TimelineStartSample);
        Assert.AreEqual(samplesPerFrame, observations[^1].TimelineEndSample);
        Assert.AreEqual(samplesPerFrame, observations.Sum(observation =>
            observation.TimelineEndSample - observation.TimelineStartSample));
    }

    [TestMethod]
    public void ScanPairMatchesTwoIndependentScansExactly()
    {
        var firstSamples = Enumerable.Range(0, 4_100)
            .Select(index => 0.31f * MathF.Sin(2 * MathF.PI * (300 + (index % 3_000)) * index / 48_000))
            .ToArray();
        var secondSamples = Enumerable.Range(0, 3_700)
            .Select(index => 0.17f * MathF.Cos(2 * MathF.PI * 1_200 * index / 48_000))
            .ToArray();
        using var first = TestAudioFixture.CreatePcm16(firstSamples);
        using var second = TestAudioFixture.CreatePcm16(secondSamples);
        var track = new PremiereAudioTrack(
            1,
            1,
            [first.Clip("clip-1", 0, 2), second.Clip("clip-2", 12, 14)]);
        var scanner = new TrackAudioScanner(new PcmWaveSampleReader());
        using var independentLegacy = new WeightedDetector();
        using var independentAntiAlias = new WeightedDetector();
        using var pairedLegacy = new WeightedDetector();
        using var pairedAntiAlias = new WeightedDetector();

        var expectedLegacy = scanner.Scan(
            track,
            independentLegacy,
            DialogueProcessingPreset.Balanced,
            VadResamplingMode.LegacyStride3);
        var expectedAntiAlias = scanner.Scan(
            track,
            independentAntiAlias,
            DialogueProcessingPreset.Balanced,
            VadResamplingMode.AntiAliasFir);
        var actual = scanner.ScanPair(
            track,
            pairedLegacy,
            pairedAntiAlias,
            DialogueProcessingPreset.Balanced);

        CollectionAssert.AreEqual(expectedLegacy.ToArray(), actual.Legacy.ToArray());
        CollectionAssert.AreEqual(expectedAntiAlias.ToArray(), actual.AntiAlias.ToArray());
        Assert.AreEqual(independentLegacy.ResetCount, pairedLegacy.ResetCount);
        Assert.AreEqual(independentAntiAlias.ResetCount, pairedAntiAlias.ResetCount);
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

    private sealed class WeightedDetector : IVoiceActivityDetector
    {
        public int SampleRate => 16_000;

        public int ChunkSampleCount => 512;

        public int ResetCount { get; private set; }

        public float ProcessChunk(ReadOnlySpan<float> samples)
        {
            double weighted = 0;
            for (var index = 0; index < samples.Length; index++)
            {
                weighted += samples[index] * ((index % 17) + 1);
            }

            return (float)Math.Clamp(Math.Abs(weighted) / 1_000, 0, 1);
        }

        public void Reset() => ResetCount++;

        public void Dispose()
        {
        }
    }
}
