using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Core.ProjectModel;

namespace PremiereAutoDialogueXml.Core.Tests;

internal static class Phase11BleedSyntheticCorpus
{
    private const int SampleRate = 48_000;
    private const int SamplesPerFrame25Fps = 1_920;

    public static IEnumerable<Phase11BleedSyntheticScenario> CreateAll()
    {
        yield return StableDelayedAttenuatedCopy();
        yield return DirectionalPairSelection();
        yield return CenterWindowWithDriftingOuterFingerprint();
        yield return IndependentSpeech();
        yield return SimultaneousDirectAndBleedSpeech();
        yield return SharedCorrelatedRoomTone();
        yield return PolarityInvertedCopy();
        yield return ClippedCorrelatedCopy();
        yield return ShortOverlap();
        yield return SharedMediaGap();
        yield return ContiguousClipBoundary();
    }

    public static Phase11BleedSyntheticScenario CenterWindowWithDriftingOuterFingerprint()
    {
        const int durationFrames = 100;
        var source = BroadbandVoice(durationFrames * SamplesPerFrame25Fps, seed: 0x11A00003u);
        var target = new float[source.Length];
        var centeredWindowStart = (source.Length - SampleRate) / 2;
        var centeredWindowEnd = centeredWindowStart + SampleRate;

        CopyRegion(source, target, 0, centeredWindowStart, delayMilliseconds: -8, scale: 0.06f);
        CopyRegion(
            source,
            target,
            centeredWindowStart,
            centeredWindowEnd,
            delayMilliseconds: 4,
            scale: 0.08f);
        CopyRegion(
            source,
            target,
            centeredWindowEnd,
            source.Length,
            delayMilliseconds: 11,
            scale: 0.12f);

        return TwoTrack(
            "center-window-drift",
            target,
            source,
            durationFrames,
            TargetKind.Ambiguous,
            Expected(
                AudioSegmentStatus.Bleed,
                "confirmed-bleed-from-track-2",
                hasEvidence: true,
                otherTrackIndex: 2,
                absoluteLagMilliseconds: 4));
    }

    private static Phase11BleedSyntheticScenario StableDelayedAttenuatedCopy()
    {
        const int durationFrames = 20;
        var source = BroadbandVoice(durationFrames * SamplesPerFrame25Fps, seed: 0x11A00001u);
        var target = DelayedScaled(source, delayMilliseconds: 6, scale: 0.08f);

        return TwoTrack(
            "stable-delayed-attenuated-copy",
            target,
            source,
            durationFrames,
            TargetKind.Ambiguous,
            Expected(
                AudioSegmentStatus.Bleed,
                "confirmed-bleed-from-track-2",
                hasEvidence: true,
                otherTrackIndex: 2,
                absoluteLagMilliseconds: 6));
    }

    private static Phase11BleedSyntheticScenario DirectionalPairSelection()
    {
        const int durationFrames = 20;
        var sourceTrack2 = BroadbandVoice(
            durationFrames * SamplesPerFrame25Fps,
            seed: 0x11A00002u);
        var independentTrack3 = BroadbandVoice(
            durationFrames * SamplesPerFrame25Fps,
            seed: 0x11A00023u);
        var target = DelayedScaled(sourceTrack2, delayMilliseconds: 3, scale: 0.08f);
        var fixtures = new List<TestAudioFixture>
        {
            TestAudioFixture.CreatePcm16(target),
            TestAudioFixture.CreatePcm16(sourceTrack2),
            TestAudioFixture.CreatePcm16(independentTrack3)
        };
        var targetTrack = Track(fixtures[0], 1, durationFrames, "pair-target");
        var sourceTrack = Track(fixtures[1], 2, durationFrames, "pair-source");
        var independentTrack = Track(fixtures[2], 3, durationFrames, "pair-independent");
        var sequence = Sequence(durationFrames, [targetTrack, sourceTrack, independentTrack]);

        return new(
            "directional-pair-selection",
            fixtures,
            sequence,
            [
                TargetAnalysis(targetTrack, durationFrames, TargetKind.Ambiguous),
                OtherAnalysis(sourceTrack, durationFrames),
                OtherAnalysis(independentTrack, durationFrames)
            ],
            targetTrack.Index,
            Expected(
                AudioSegmentStatus.Bleed,
                "confirmed-bleed-from-track-2",
                hasEvidence: true,
                otherTrackIndex: 2,
                absoluteLagMilliseconds: 3));
    }

    private static Phase11BleedSyntheticScenario IndependentSpeech()
    {
        const int durationFrames = 20;
        var target = DelayedScaled(
            BroadbandVoice(durationFrames * SamplesPerFrame25Fps, seed: 0x11A00004u),
            delayMilliseconds: 0,
            scale: 0.08f);
        var other = BroadbandVoice(durationFrames * SamplesPerFrame25Fps, seed: 0x11A00005u);

        return TwoTrack(
            "independent-speech",
            target,
            other,
            durationFrames,
            TargetKind.Ambiguous,
            Expected(AudioSegmentStatus.Ambiguous, "ambiguous-independent", hasEvidence: false));
    }

    private static Phase11BleedSyntheticScenario SimultaneousDirectAndBleedSpeech()
    {
        const int durationFrames = 20;
        var source = BroadbandVoice(durationFrames * SamplesPerFrame25Fps, seed: 0x11A00006u);
        var independent = BroadbandVoice(
            durationFrames * SamplesPerFrame25Fps,
            seed: 0x11A00007u);
        var target = new float[source.Length];
        for (var index = 0; index < target.Length; index++)
        {
            target[index] = (source[index] * 0.08f) + (independent[index] * 0.04f);
        }

        return TwoTrack(
            "simultaneous-direct-and-bleed",
            target,
            source,
            durationFrames,
            TargetKind.Speech,
            Expected(
                AudioSegmentStatus.Ambiguous,
                "conflicting-direct-and-bleed-evidence",
                hasEvidence: true,
                otherTrackIndex: 2,
                absoluteLagMilliseconds: 0));
    }

    private static Phase11BleedSyntheticScenario SharedCorrelatedRoomTone()
    {
        const int durationFrames = 20;
        var roomTone = CorrelatedRoomTone(
            durationFrames * SamplesPerFrame25Fps,
            seed: 0x11A00008u);
        var target = roomTone.Select(sample => sample * 0.08f).ToArray();

        return TwoTrack(
            "shared-correlated-room-tone",
            target,
            roomTone,
            durationFrames,
            TargetKind.Ambiguous,
            Expected(
                AudioSegmentStatus.Bleed,
                "confirmed-bleed-from-track-2",
                hasEvidence: true,
                otherTrackIndex: 2,
                absoluteLagMilliseconds: 0));
    }

    private static Phase11BleedSyntheticScenario PolarityInvertedCopy()
    {
        const int durationFrames = 20;
        var source = BroadbandVoice(durationFrames * SamplesPerFrame25Fps, seed: 0x11A00009u);
        var target = source.Select(sample => sample * -0.08f).ToArray();

        return TwoTrack(
            "polarity-inverted-copy",
            target,
            source,
            durationFrames,
            TargetKind.Ambiguous,
            Expected(
                AudioSegmentStatus.Bleed,
                "confirmed-bleed-from-track-2",
                hasEvidence: true,
                otherTrackIndex: 2,
                absoluteLagMilliseconds: 0));
    }

    private static Phase11BleedSyntheticScenario ClippedCorrelatedCopy()
    {
        const int durationFrames = 20;
        var unclipped = BroadbandVoice(
            durationFrames * SamplesPerFrame25Fps,
            seed: 0x11A00010u);
        var clipped = unclipped
            .Select(sample => Math.Clamp(sample * 5f, -0.72f, 0.72f))
            .ToArray();
        var target = clipped.Select(sample => sample * 0.08f).ToArray();

        return TwoTrack(
            "clipped-correlated-copy",
            target,
            clipped,
            durationFrames,
            TargetKind.Ambiguous,
            Expected(
                AudioSegmentStatus.Bleed,
                "confirmed-bleed-from-track-2",
                hasEvidence: true,
                otherTrackIndex: 2,
                absoluteLagMilliseconds: 0));
    }

    private static Phase11BleedSyntheticScenario ShortOverlap()
    {
        const int durationFrames = 10;
        const int overlapSamples = 4_800;
        var source = BroadbandVoice(durationFrames * SamplesPerFrame25Fps, seed: 0x11A00011u);
        var target = source.Select(sample => sample * 0.08f).ToArray();

        return TwoTrack(
            "short-overlap-below-120ms",
            target,
            source,
            durationFrames,
            TargetKind.Ambiguous,
            Expected(AudioSegmentStatus.Ambiguous, "ambiguous-independent", hasEvidence: false),
            otherPhraseEndSample: overlapSamples);
    }

    private static Phase11BleedSyntheticScenario SharedMediaGap()
    {
        const int durationFrames = 25;
        const int firstClipEndFrame = 10;
        const int secondClipStartFrame = 15;
        var source = BroadbandVoice(durationFrames * SamplesPerFrame25Fps, seed: 0x11A00012u);
        var firstSource = source[..(firstClipEndFrame * SamplesPerFrame25Fps)];
        var secondSource = source[(secondClipStartFrame * SamplesPerFrame25Fps)..];
        var fixtures = new List<TestAudioFixture>
        {
            TestAudioFixture.CreatePcm16(firstSource.Select(sample => sample * 0.08f).ToArray()),
            TestAudioFixture.CreatePcm16(secondSource.Select(sample => sample * 0.08f).ToArray()),
            TestAudioFixture.CreatePcm16(firstSource),
            TestAudioFixture.CreatePcm16(secondSource)
        };
        var targetTrack = new PremiereAudioTrack(
            1,
            1,
            [
                fixtures[0].Clip("gap-target-a", 0, firstClipEndFrame),
                fixtures[1].Clip("gap-target-b", secondClipStartFrame, durationFrames)
            ]);
        var otherTrack = new PremiereAudioTrack(
            2,
            2,
            [
                fixtures[2].Clip("gap-source-a", 0, firstClipEndFrame),
                fixtures[3].Clip("gap-source-b", secondClipStartFrame, durationFrames)
            ]);

        return new(
            "shared-media-gap",
            fixtures,
            Sequence(durationFrames, [targetTrack, otherTrack]),
            [
                TargetAnalysis(targetTrack, durationFrames, TargetKind.Ambiguous),
                OtherAnalysis(otherTrack, durationFrames)
            ],
            targetTrack.Index,
            Expected(
                AudioSegmentStatus.Bleed,
                "confirmed-bleed-from-track-2",
                hasEvidence: true,
                otherTrackIndex: 2,
                absoluteLagMilliseconds: 0));
    }

    private static Phase11BleedSyntheticScenario ContiguousClipBoundary()
    {
        const int durationFrames = 20;
        const int boundaryFrame = 10;
        var source = BroadbandVoice(durationFrames * SamplesPerFrame25Fps, seed: 0x11A00013u);
        var firstSource = source[..(boundaryFrame * SamplesPerFrame25Fps)];
        var secondSource = source[(boundaryFrame * SamplesPerFrame25Fps)..];
        var fixtures = new List<TestAudioFixture>
        {
            TestAudioFixture.CreatePcm16(firstSource.Select(sample => sample * 0.08f).ToArray()),
            TestAudioFixture.CreatePcm16(secondSource.Select(sample => sample * 0.08f).ToArray()),
            TestAudioFixture.CreatePcm16(firstSource),
            TestAudioFixture.CreatePcm16(secondSource)
        };
        var targetTrack = new PremiereAudioTrack(
            1,
            1,
            [
                fixtures[0].Clip("boundary-target-a", 0, boundaryFrame),
                fixtures[1].Clip("boundary-target-b", boundaryFrame, durationFrames)
            ]);
        var otherTrack = new PremiereAudioTrack(
            2,
            2,
            [
                fixtures[2].Clip("boundary-source-a", 0, boundaryFrame),
                fixtures[3].Clip("boundary-source-b", boundaryFrame, durationFrames)
            ]);

        return new(
            "contiguous-clip-boundary",
            fixtures,
            Sequence(durationFrames, [targetTrack, otherTrack]),
            [
                TargetAnalysis(targetTrack, durationFrames, TargetKind.Ambiguous),
                OtherAnalysis(otherTrack, durationFrames)
            ],
            targetTrack.Index,
            Expected(
                AudioSegmentStatus.Bleed,
                "confirmed-bleed-from-track-2",
                hasEvidence: true,
                otherTrackIndex: 2,
                absoluteLagMilliseconds: 0));
    }

    private static Phase11BleedSyntheticScenario TwoTrack(
        string name,
        IReadOnlyList<float> targetSamples,
        IReadOnlyList<float> otherSamples,
        int durationFrames,
        TargetKind targetKind,
        Phase10BleedExpectedSnapshot expected,
        long? otherPhraseEndSample = null)
    {
        var fixtures = new List<TestAudioFixture>
        {
            TestAudioFixture.CreatePcm16(targetSamples),
            TestAudioFixture.CreatePcm16(otherSamples)
        };
        var targetTrack = Track(fixtures[0], 1, durationFrames, $"{name}-target");
        var otherTrack = Track(fixtures[1], 2, durationFrames, $"{name}-source");

        return new(
            name,
            fixtures,
            Sequence(durationFrames, [targetTrack, otherTrack]),
            [
                TargetAnalysis(targetTrack, durationFrames, targetKind),
                OtherAnalysis(otherTrack, durationFrames, otherPhraseEndSample)
            ],
            targetTrack.Index,
            expected);
    }

    private static PremiereAudioTrack Track(
        TestAudioFixture fixture,
        int trackIndex,
        int durationFrames,
        string clipId) =>
        new(trackIndex, trackIndex, [fixture.Clip(clipId, 0, durationFrames)]);

    private static PremiereSequence Sequence(
        int durationFrames,
        IReadOnlyList<PremiereAudioTrack> tracks) =>
        new("phase11-corpus", "phase11-corpus-uuid", "phase11-corpus", 25, durationFrames, 2, 48_000, tracks);

    private static TrackAudioAnalysis TargetAnalysis(
        PremiereAudioTrack track,
        int durationFrames,
        TargetKind targetKind)
    {
        var endSample = checked(durationFrames * (long)SamplesPerFrame25Fps);
        var isSpeech = targetKind == TargetKind.Speech;
        var segment = new AnalyzedAudioSegment(
            track.Index,
            track.Clips[0].Id,
            0,
            endSample,
            0,
            endSample,
            isSpeech ? AudioSegmentStatus.Speech : AudioSegmentStatus.Ambiguous,
            isSpeech ? "T01-P000001" : null,
            isSpeech ? 6f : 0f,
            isSpeech ? "confirmed-direct-speech" : "ambiguous-independent");
        return new(track.Index, 0, [], [segment], LearnedDirectVoiceRmsDbfs: -15f);
    }

    private static TrackAudioAnalysis OtherAnalysis(
        PremiereAudioTrack track,
        int durationFrames,
        long? phraseEndSample = null)
    {
        var endSample = phraseEndSample ?? checked(durationFrames * (long)SamplesPerFrame25Fps);
        var phrase = new DialoguePhrase(
            $"T{track.Index:00}-P000001",
            track.Index,
            0,
            endSample,
            0,
            endSample,
            -8f,
            2f,
            2f,
            false);
        return new(track.Index, 0, [phrase], [], LearnedDirectVoiceRmsDbfs: -8f);
    }

    private static Phase10BleedExpectedSnapshot Expected(
        AudioSegmentStatus status,
        string reason,
        bool hasEvidence,
        int? otherTrackIndex = null,
        int? absoluteLagMilliseconds = null) =>
        new(status, reason, hasEvidence, otherTrackIndex, absoluteLagMilliseconds);

    private static float[] BroadbandVoice(int sampleCount, uint seed)
    {
        var result = new float[sampleCount];
        var state = seed;
        for (var index = 0; index < result.Length; index++)
        {
            state = unchecked((state * 1_664_525u) + 1_013_904_223u);
            var noise = ((state >> 8) / 8_388_607.5f) - 1f;
            var envelope = 0.70f + (0.30f * MathF.Sin(
                2f * MathF.PI * 3.1f * index / SampleRate));
            result[index] = noise * envelope * 0.42f;
        }

        return result;
    }

    private static float[] CorrelatedRoomTone(int sampleCount, uint seed)
    {
        var broadband = BroadbandVoice(sampleCount, seed);
        var result = new float[sampleCount];
        var state = 0f;
        for (var index = 0; index < result.Length; index++)
        {
            state = (state * 0.94f) + (broadband[index] * 0.06f);
            result[index] = state * 0.55f;
        }

        return result;
    }

    private static float[] DelayedScaled(
        IReadOnlyList<float> source,
        int delayMilliseconds,
        float scale)
    {
        var result = new float[source.Count];
        CopyRegion(source, result, 0, result.Length, delayMilliseconds, scale);
        return result;
    }

    private static void CopyRegion(
        IReadOnlyList<float> source,
        float[] destination,
        int start,
        int end,
        int delayMilliseconds,
        float scale)
    {
        var delaySamples = checked(delayMilliseconds * (SampleRate / 1_000));
        for (var index = start; index < end; index++)
        {
            var sourceIndex = index - delaySamples;
            destination[index] = sourceIndex >= 0 && sourceIndex < source.Count
                ? source[sourceIndex] * scale
                : 0f;
        }
    }

    internal enum TargetKind
    {
        Ambiguous,
        Speech
    }
}

internal sealed class Phase11BleedSyntheticScenario(
    string name,
    IReadOnlyList<TestAudioFixture> fixtures,
    PremiereSequence sequence,
    IReadOnlyList<TrackAudioAnalysis> analyses,
    int targetTrackIndex,
    Phase10BleedExpectedSnapshot expected) : IDisposable
{
    public string Name { get; } = name;

    public PremiereSequence Sequence { get; } = sequence;

    public IReadOnlyList<TrackAudioAnalysis> Analyses { get; } = analyses;

    public int TargetTrackIndex { get; } = targetTrackIndex;

    public Phase10BleedExpectedSnapshot Expected { get; } = expected;

    public void Dispose()
    {
        foreach (var fixture in fixtures.Reverse())
        {
            fixture.Dispose();
        }
    }
}

internal sealed record Phase10BleedExpectedSnapshot(
    AudioSegmentStatus Status,
    string Reason,
    bool HasEvidence,
    int? OtherTrackIndex,
    int? AbsoluteLagMilliseconds);
