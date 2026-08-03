using System.Buffers.Binary;
using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Core.Media;
using PremiereAutoDialogueXml.Output.Audit;
using PremiereAutoDialogueXml.Validation;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class PcmTrackPeakValidatorTests
{
    [TestMethod]
    public void ValidateAcceptsTargetAndGainCappedPredictedPeaks()
    {
        using var wave = TemporaryWave.Create(-6, -12);
        var audit = Audit(
            Fragment("T01-P000001", startFrame: 0, endFrame: 1, measuredPeakDbfs: -12, appliedGainDb: 6),
            Fragment("T01-P000002", startFrame: 1, endFrame: 2, measuredPeakDbfs: -30, appliedGainDb: 18, gainWasCapped: true));

        var report = new PcmTrackPeakValidator().Validate(audit, wave.Info, trackIndex: 1);

        Assert.IsTrue(report.AllWithinTolerance);
        Assert.AreEqual(2, report.PhraseCount);
        Assert.AreEqual(2, report.PassedPhraseCount);
        Assert.AreEqual(-6, report.Phrases[0].ExpectedRenderedPeakDbfs, 0.0001);
        Assert.AreEqual(-12, report.Phrases[1].ExpectedRenderedPeakDbfs, 0.0001);
        Assert.IsTrue(report.Phrases[1].GainWasCapped);
        Assert.AreEqual(0, report.MedianObservedOffsetDb!.Value, 0.01);
        Assert.AreEqual(2, report.PhrasesMatchingMedianOffset);
        Assert.AreEqual(0, report.PhrasesOutsideMedianOffset);
    }

    [TestMethod]
    public void ValidateReportsPeakOutsideTolerance()
    {
        using var wave = TemporaryWave.Create(-4);
        var audit = Audit(
            Fragment("T01-P000001", startFrame: 0, endFrame: 1, measuredPeakDbfs: -12, appliedGainDb: 6));

        var report = new PcmTrackPeakValidator().Validate(audit, wave.Info, trackIndex: 1);

        Assert.IsFalse(report.AllWithinTolerance);
        Assert.AreEqual(0, report.PassedPhraseCount);
        Assert.AreEqual(1, report.FailedPhraseCount);
        Assert.AreEqual("peak-out-of-tolerance", report.Phrases[0].ResultReason);
        Assert.IsGreaterThan(1.9, report.Phrases[0].DeltaDb!.Value);
        Assert.IsGreaterThan(1.9, report.MedianObservedOffsetDb!.Value);
        Assert.AreEqual(1, report.PhrasesMatchingMedianOffset);
    }

    [TestMethod]
    public void ValidateRejectsExportThatEndsBeforeSpeech()
    {
        using var wave = TemporaryWave.Create(-6);
        var audit = Audit(
            Fragment("T01-P000001", startFrame: 1, endFrame: 2, measuredPeakDbfs: -12, appliedGainDb: 6));

        var exception = Assert.ThrowsExactly<InvalidDataException>(
            () => new PcmTrackPeakValidator().Validate(audit, wave.Info, trackIndex: 1));

        StringAssert.Contains(exception.Message, "export từ đầu sequence");
    }

    [TestMethod]
    public void ValidateIgnoresNoiseAndOtherTracks()
    {
        using var wave = TemporaryWave.Create(-6);
        var audit = Audit(
            Fragment("T01-P000001", startFrame: 0, endFrame: 1, measuredPeakDbfs: -12, appliedGainDb: 6),
            Fragment(null, startFrame: 0, endFrame: 1, status: AudioSegmentStatus.Noise),
            Fragment("T02-P000001", startFrame: 0, endFrame: 1, measuredPeakDbfs: -3, appliedGainDb: -3, trackIndex: 2));

        var report = new PcmTrackPeakValidator().Validate(audit, wave.Info, trackIndex: 1);

        Assert.AreEqual(1, report.PhraseCount);
        Assert.AreEqual(1, report.SpeechFragmentCount);
        Assert.AreEqual("T01-P000001", report.Phrases[0].PhraseId);
    }

    [TestMethod]
    public void ValidateSourceLinkedComparisonSeparatesRoutingOffsetFromFramePeak()
    {
        using var source = TemporaryWave.Create(-12, -30);
        using var rendered = TemporaryWave.Create(-9.0103, -15.0103);
        var audit = Audit(
            Fragment("T01-P000001", startFrame: 0, endFrame: 1, measuredPeakDbfs: -12, appliedGainDb: 6),
            Fragment("T01-P000002", startFrame: 1, endFrame: 2, measuredPeakDbfs: -30, appliedGainDb: 18, gainWasCapped: true));

        var report = new PcmTrackPeakValidator().Validate(
            audit,
            rendered.Info,
            trackIndex: 1,
            sourceMediaByFileId: new Dictionary<string, WaveFileInfo>(StringComparer.Ordinal)
            {
                ["file-1"] = source.Info
            });

        Assert.IsTrue(report.UsedSourceMedia);
        Assert.AreEqual(-3.0103, report.SourceDerivedMedianOffsetDb!.Value, 0.01);
        Assert.AreEqual(2, report.SourceDerivedPhrasesMatchingMedianOffset);
        Assert.AreEqual(0, report.SourceDerivedPhrasesOutsideMedianOffset);
        Assert.AreEqual(-6, report.Phrases[0].SourceDerivedExpectedRenderedPeakDbfs!.Value, 0.01);
        Assert.AreEqual(-3.0103, report.Phrases[0].SourceDerivedDeltaDb!.Value, 0.01);
    }

    private static OutputAudit Audit(params FragmentAudit[] fragments) => new(
        SchemaVersion: "1.0",
        RunId: "pilot-run",
        CreatedAtUtc: DateTimeOffset.UnixEpoch,
        SourceXmlFileName: "source.xml",
        SourceXmlSha256: new string('A', 64),
        OutputXmlFileName: "output.xml",
        OutputXmlSha256: new string('B', 64),
        SourceSequenceId: "sequence-source",
        OutputSequenceId: "sequence-output",
        OutputSequenceUuid: Guid.Empty.ToString(),
        OutputSequenceName: "Sequence - AUTO AUDIO",
        Model: new("test", new string('C', 64)),
        Preset: new(
            Name: "Cân bằng",
            VadThreshold: 0.5,
            MinimumSpeechMilliseconds: 120,
            PhraseBreakMilliseconds: 350,
            PaddingBeforeMilliseconds: 200,
            PaddingAfterMilliseconds: 300,
            DirectVoiceAboveNoiseDb: 10,
            BleedOtherMicAdvantageDb: 12,
            BleedCorrelationThreshold: 0.8,
            BleedMaximumLagMilliseconds: 12,
            TargetSamplePeakDbfs: -6,
            MaximumBoostDb: 18,
            MaximumWorkers: 4),
        Fragments: fragments,
        Markers: []);

    private static FragmentAudit Fragment(
        string? phraseId,
        long startFrame,
        long endFrame,
        double? measuredPeakDbfs = null,
        double? appliedGainDb = null,
        bool gainWasCapped = false,
        int trackIndex = 1,
        AudioSegmentStatus status = AudioSegmentStatus.Speech) => new(
        ClipItemId: $"clip-{trackIndex}-{startFrame}",
        TrackIndex: trackIndex,
        SourceClipId: $"source-clip-{trackIndex}",
        SourceFileId: $"file-{trackIndex}",
        SourceFileName: $"track-{trackIndex}.wav",
        TimelineStartFrame: startFrame,
        TimelineEndFrame: endFrame,
        SourceInFrame: startFrame,
        SourceOutFrame: endFrame,
        PproTicksIn: 0,
        PproTicksOut: 0,
        SourceStartSample: startFrame * 1_920,
        SourceEndSample: endFrame * 1_920,
        Status: status,
        Enabled: status != AudioSegmentStatus.Noise,
        PhraseId: phraseId,
        MeasuredPeakDbfs: measuredPeakDbfs,
        RequiredGainDb: measuredPeakDbfs is null ? null : -6 - measuredPeakDbfs,
        AppliedGainDb: appliedGainDb,
        GainWasCapped: gainWasCapped,
        Reason: status.ToString().ToLowerInvariant(),
        BleedEvidence: null);

    private sealed class TemporaryWave : IDisposable
    {
        private TemporaryWave(string path, WaveFileInfo info)
        {
            Path = path;
            Info = info;
        }

        public string Path { get; }

        public WaveFileInfo Info { get; }

        public static TemporaryWave Create(params double[] peakDbfsByVideoFrame)
        {
            const int samplesPerVideoFrame = 1_920;
            var samples = new short[checked(peakDbfsByVideoFrame.Length * samplesPerVideoFrame)];
            for (var frame = 0; frame < peakDbfsByVideoFrame.Length; frame++)
            {
                var linear = Math.Pow(10, peakDbfsByVideoFrame[frame] / 20d);
                samples[(frame * samplesPerVideoFrame) + (samplesPerVideoFrame / 2)] =
                    checked((short)Math.Round(linear * short.MaxValue));
            }

            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"padx-pcm-validation-{Guid.NewGuid():N}.wav");
            WritePcm16(path, samples);
            var inspection = new WaveFileInspector().Inspect(path);
            Assert.IsNotNull(inspection.File);
            return new(path, inspection.File);
        }

        public void Dispose()
        {
            File.Delete(Path);
        }

        private static void WritePcm16(string path, IReadOnlyList<short> samples)
        {
            const int sampleRate = 48_000;
            const short channelCount = 1;
            const short bitsPerSample = 16;
            const short blockAlign = channelCount * bitsPerSample / 8;
            var dataSize = checked(samples.Count * blockAlign);

            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new BinaryWriter(stream);
            writer.Write("RIFF"u8);
            writer.Write(checked(36 + dataSize));
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channelCount);
            writer.Write(sampleRate);
            writer.Write(sampleRate * blockAlign);
            writer.Write(blockAlign);
            writer.Write(bitsPerSample);
            writer.Write("data"u8);
            writer.Write(dataSize);
            Span<byte> bytes = stackalloc byte[sizeof(short)];
            foreach (var sample in samples)
            {
                BinaryPrimitives.WriteInt16LittleEndian(bytes, sample);
                writer.Write(bytes);
            }
        }
    }
}
