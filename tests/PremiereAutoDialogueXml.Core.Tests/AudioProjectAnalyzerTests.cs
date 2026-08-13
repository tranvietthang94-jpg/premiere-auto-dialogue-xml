using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Audio.Pcm;
using PremiereAutoDialogueXml.Audio.Vad;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.ProjectModel;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class AudioProjectAnalyzerTests
{
    [TestMethod]
    public async Task AnalyzeAsyncNeverExceedsFourWorkers()
    {
        using var fixture = TestAudioFixture.CreatePcm16(Enumerable.Repeat(0.01f, 1_920).ToArray());
        var tracks = Enumerable.Range(1, 6)
            .Select(index => new PremiereAudioTrack(
                index,
                index % 2 == 0 ? 2 : 1,
                [fixture.Clip($"clip-{index}", 0, 1)]))
            .ToArray();
        var project = Project(tracks);
        using var probe = new ConcurrencyProbe(expectedParallelWorkers: 4);
        var analyzer = new AudioProjectAnalyzer(
            () => new ProbedDetector(probe),
            new PcmWaveSampleReader());

        var result = await analyzer.AnalyzeAsync(project, DialogueProcessingPreset.Balanced);

        Assert.HasCount(6, result.Tracks);
        Assert.IsLessThanOrEqualTo(4, probe.MaximumActive);
        Assert.IsGreaterThanOrEqualTo(2, probe.MaximumActive);
    }

    [TestMethod]
    public async Task AnalyzeAsyncLimitsLongTimelineToOneWorkerForMemorySafety()
    {
        using var fixture = TestAudioFixture.CreatePcm16(Enumerable.Repeat(0.01f, 1_920).ToArray());
        var tracks = Enumerable.Range(1, 6)
            .Select(index => new PremiereAudioTrack(
                index,
                index % 2 == 0 ? 2 : 1,
                [fixture.Clip($"clip-{index}", 0, 1)]))
            .ToArray();
        var project = Project(tracks, durationFrames: 1_200_000);
        using var probe = new ConcurrencyProbe(expectedParallelWorkers: 1);
        var analyzer = new AudioProjectAnalyzer(
            () => new ProbedDetector(probe),
            new PcmWaveSampleReader());

        var result = await analyzer.AnalyzeAsync(project, DialogueProcessingPreset.Balanced);

        Assert.HasCount(6, result.Tracks);
        Assert.AreEqual(1, probe.MaximumActive);
        Assert.AreEqual(1, AudioProjectAnalyzer.DetermineMaximumWorkers(
            project,
            DialogueProcessingPreset.Balanced));
    }

    [TestMethod]
    public async Task AnalyzeAsyncHonorsCancellationBeforeOpeningMedia()
    {
        var missingWave = new PremiereAutoDialogueXml.Core.Media.WaveFileInfo(
            "missing.wav",
            PremiereAutoDialogueXml.Core.Media.WaveEncodingKind.Pcm,
            1,
            48_000,
            16,
            16,
            2,
            0,
            3_840,
            3_840,
            1_920,
            0,
            0);
        var clip = new PremiereAudioClip(
            "clip",
            "clip",
            true,
            0,
            1,
            0,
            1,
            0,
            0,
            0,
            1_920,
            "file",
            new("media", "media", "file:///missing.wav", "missing.wav", missingWave));
        var project = Project([new PremiereAudioTrack(1, 1, [clip])]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var analyzer = new AudioProjectAnalyzer(
            () => new ConstantDetector(),
            new PcmWaveSampleReader());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => analyzer.AnalyzeAsync(
                project,
                DialogueProcessingPreset.Balanced,
                cancellationToken: cancellation.Token));
    }

    [TestMethod]
    public async Task AnalyzeAsyncAddsShadowEvidenceWithoutChangingEnergyConflictStatus()
    {
        const int sourceChunkSamples = 1_536;
        const int frameCount = 20;
        var targetSamples = new float[frameCount * 1_920];
        for (var index = 0; index < sourceChunkSamples * 8; index++)
        {
            targetSamples[index] = 0.20f * MathF.Sin(2 * MathF.PI * 440 * index / 48_000);
        }

        for (var index = sourceChunkSamples * 12; index < targetSamples.Length; index++)
        {
            targetSamples[index] = 0.03f * MathF.Sin(2 * MathF.PI * 440 * index / 48_000);
        }

        var otherSamples = Enumerable.Range(0, targetSamples.Length)
            .Select(index => 0.40f * MathF.Sin(2 * MathF.PI * 440 * index / 48_000))
            .ToArray();
        using var targetFixture = TestAudioFixture.CreatePcm16(targetSamples);
        using var otherFixture = TestAudioFixture.CreatePcm16(otherSamples);
        var tracks = new[]
        {
            new PremiereAudioTrack(1, 1, [targetFixture.Clip("target", 0, frameCount)]),
            new PremiereAudioTrack(2, 2, [otherFixture.Clip("other", 0, frameCount)])
        };
        var project = Project(tracks, frameCount);
        var detectorIndex = 0;
        var analyzer = new AudioProjectAnalyzer(
            () => Interlocked.Increment(ref detectorIndex) == 1
                ? new SwitchingDetector(highProbabilityChunkCount: 8)
                : new ConstantDetector(0.99f),
            new PcmWaveSampleReader());

        var result = await analyzer.AnalyzeAsync(
            project,
            DialogueProcessingPreset.Balanced with { MaximumWorkers = 1 });

        var energyConflicts = result.Tracks
            .Single(track => track.TrackIndex == 1)
            .Segments
            .Where(segment => segment.Reason.StartsWith(
                "ambiguous-energy-vad-conflict",
                StringComparison.Ordinal))
            .ToArray();
        Assert.IsNotEmpty(energyConflicts);
        Assert.IsTrue(energyConflicts.All(segment => segment.Status == AudioSegmentStatus.Ambiguous));
        Assert.IsTrue(result.ShadowEvidence.Any(evidence =>
            evidence.TrackIndex == 1 &&
            evidence.Outcome == CrossTrackShadowOutcome.LikelyBleed));
    }

    [TestMethod]
    public async Task AnalyzeAsyncShadowNeverDisablesLegacySpeechWhenAntiAliasChangesVad()
    {
        const int frameCount = 20;
        var samples = Enumerable.Range(0, frameCount * 1_920)
            .Select(index => 0.35f * MathF.Sin(2 * MathF.PI * 12_000 * index / 48_000))
            .ToArray();
        using var fixture = TestAudioFixture.CreatePcm16(samples);
        var project = Project(
            [new PremiereAudioTrack(1, 1, [fixture.Clip("clip", 0, frameCount)])],
            frameCount);
        var analyzer = new AudioProjectAnalyzer(
            () => new RmsThresholdDetector(0.05f),
            new PcmWaveSampleReader(),
            enableVadFrontEndShadow: true);

        var result = await analyzer.AnalyzeAsync(
            project,
            DialogueProcessingPreset.Balanced with
            {
                MaximumWorkers = 1,
                PreserveVadNegativeHighEnergyConflicts = false
            });

        Assert.IsNotNull(result.VadFrontEndComparison);
        Assert.IsNotNull(result.NoiseBoundaryComparison);
        Assert.HasCount(2, result.NoiseBoundaryComparison.FrontEnds);
        Assert.AreEqual(0, result.NoiseBoundaryComparison.BaselineEnabledFinalDisabledCount);
        Assert.IsTrue(result.NoiseBoundaryComparison.FrontEnds.All(frontEnd =>
            frontEnd.Tracks.Single().BaselinePolicyVersion == "phase09-adaptive-p20-v1" &&
            frontEnd.Tracks.Single().CandidatePolicyVersion == "phase10-background-eligible-p20-v1"));
        Assert.IsGreaterThan(0, result.VadFrontEndComparison.ChangedObservationCount);
        Assert.IsGreaterThan(0, result.VadFrontEndComparison.LegacyEnabledCandidateDisabledCount);
        Assert.IsTrue(result.Tracks.Single().Segments.All(segment =>
            segment.Status is AudioSegmentStatus.Speech or AudioSegmentStatus.Ambiguous));
        Assert.IsTrue(result.Tracks.Single().Segments.Any(segment =>
            segment.Reason == "ambiguous-vad-front-end-disagreement"));
    }

    private static PremiereProject Project(
        IReadOnlyList<PremiereAudioTrack> tracks,
        long durationFrames = 1) => new(
        "fixture.xml",
        "SHA256",
        new("sequence", "uuid", "fixture", 25, durationFrames, 2, 48_000, tracks));

    private sealed class ConcurrencyProbe(int expectedParallelWorkers) : IDisposable
    {
        private readonly CountdownEvent _ready = new(expectedParallelWorkers);
        private readonly ManualResetEventSlim _release = new(false);
        private int _active;
        private int _maximumActive;
        private int _entered;

        public int MaximumActive => Volatile.Read(ref _maximumActive);

        public void Enter()
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            if (Interlocked.Increment(ref _entered) <= expectedParallelWorkers)
            {
                _ready.Signal();
            }
        }

        public void WaitForPeers()
        {
            if (_ready.Wait(TimeSpan.FromSeconds(5)))
            {
                _release.Set();
            }

            Assert.IsTrue(_release.Wait(TimeSpan.FromSeconds(5)), "Không khởi chạy đủ worker song song.");
        }

        public void Exit() => Interlocked.Decrement(ref _active);

        public void Dispose()
        {
            _ready.Dispose();
            _release.Dispose();
        }

        private void UpdateMaximum(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumActive);
                if (active <= current || Interlocked.CompareExchange(ref _maximumActive, active, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class ProbedDetector : IVoiceActivityDetector
    {
        private readonly ConcurrencyProbe _probe;
        private bool _waited;
        private bool _disposed;

        public ProbedDetector(ConcurrencyProbe probe)
        {
            _probe = probe;
            _probe.Enter();
        }

        public int SampleRate => 16_000;

        public int ChunkSampleCount => 512;

        public float ProcessChunk(ReadOnlySpan<float> samples)
        {
            if (!_waited)
            {
                _probe.WaitForPeers();
                _waited = true;
            }

            return 0.01f;
        }

        public void Reset()
        {
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _probe.Exit();
                _disposed = true;
            }
        }
    }

    private sealed class ConstantDetector(float probability = 0.01f) : IVoiceActivityDetector
    {
        public int SampleRate => 16_000;

        public int ChunkSampleCount => 512;

        public float ProcessChunk(ReadOnlySpan<float> samples) => probability;

        public void Reset()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class SwitchingDetector(int highProbabilityChunkCount) : IVoiceActivityDetector
    {
        private int _processedChunks;

        public int SampleRate => 16_000;

        public int ChunkSampleCount => 512;

        public float ProcessChunk(ReadOnlySpan<float> samples) =>
            _processedChunks++ < highProbabilityChunkCount ? 0.99f : 0.01f;

        public void Reset()
        {
            _processedChunks = 0;
        }

        public void Dispose()
        {
        }
    }

    private sealed class RmsThresholdDetector(float threshold) : IVoiceActivityDetector
    {
        public int SampleRate => 16_000;

        public int ChunkSampleCount => 512;

        public float ProcessChunk(ReadOnlySpan<float> samples)
        {
            double squareSum = 0;
            foreach (var sample in samples)
            {
                squareSum += sample * sample;
            }

            return Math.Sqrt(squareSum / samples.Length) >= threshold ? 0.99f : 0.01f;
        }

        public void Reset()
        {
        }

        public void Dispose()
        {
        }
    }
}
