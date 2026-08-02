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

    private static PremiereProject Project(IReadOnlyList<PremiereAudioTrack> tracks) => new(
        "fixture.xml",
        "SHA256",
        new("sequence", "uuid", "fixture", 25, 1, 2, 48_000, tracks));

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

    private sealed class ConstantDetector : IVoiceActivityDetector
    {
        public int SampleRate => 16_000;

        public int ChunkSampleCount => 512;

        public float ProcessChunk(ReadOnlySpan<float> samples) => 0.01f;

        public void Reset()
        {
        }

        public void Dispose()
        {
        }
    }
}
