using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.ProjectModel;

namespace PremiereAutoDialogueXml.Audio.Analysis;

public sealed class TrackDialogueAnalyzer(TimelinePcmAccessor pcmAccessor)
{
    private const float VadAmbiguityMargin = 0.15f;

    public TrackAudioAnalysis Analyze(
        PremiereAudioTrack track,
        IReadOnlyList<AudioFrameObservation> observations,
        DialogueProcessingPreset preset,
        CancellationToken cancellationToken = default) =>
        AnalyzeCore(
            track,
            observations,
            preset,
            NoiseBoundaryAnalysisMode.Phase09Baseline,
            captureNoiseBoundaryTrace: false,
            cancellationToken);

    public NoiseBoundaryShadowAnalysis AnalyzeShadow(
        PremiereAudioTrack track,
        IReadOnlyList<AudioFrameObservation> observations,
        DialogueProcessingPreset preset,
        CancellationToken cancellationToken = default)
    {
        var baseline = AnalyzeCore(
            track,
            observations,
            preset,
            NoiseBoundaryAnalysisMode.Phase09Baseline,
            captureNoiseBoundaryTrace: true,
            cancellationToken);
        var candidate = AnalyzeCore(
            track,
            observations,
            preset,
            NoiseBoundaryAnalysisMode.NoiseBoundaryCandidate,
            captureNoiseBoundaryTrace: true,
            cancellationToken);
        return new(baseline, candidate, NoiseBoundaryShadowComparer.Compare(baseline, candidate));
    }

    private TrackAudioAnalysis AnalyzeCore(
        PremiereAudioTrack track,
        IReadOnlyList<AudioFrameObservation> observations,
        DialogueProcessingPreset preset,
        NoiseBoundaryAnalysisMode mode,
        bool captureNoiseBoundaryTrace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(preset);

        var evidenceBuilder = new AudioFrameEvidenceBuilder();
        AudioFrameEvidenceBuildResult? evidenceWithTrace = null;
        var evidence = captureNoiseBoundaryTrace
            ? (evidenceWithTrace = evidenceBuilder.BuildWithTrace(observations, preset, mode)).Evidence
            : evidenceBuilder.Build(observations, preset);
        var minimumSpeechSamples = AudioMath.MillisecondsToSamples(preset.MinimumSpeechMilliseconds);
        var phraseBreakSamples = AudioMath.MillisecondsToSamples(preset.PhraseBreakMilliseconds);
        var candidateGroups = BuildVadGroups(evidence, phraseBreakSamples);
        var confirmedGroups = new List<CandidateGroup>();
        var ambiguous = new List<TimelineInterval>();

        foreach (var group in candidateGroups)
        {
            if (group.VadEvidenceSamples >= minimumSpeechSamples &&
                group.DirectEvidenceSamples >= minimumSpeechSamples)
            {
                confirmedGroups.Add(group);
            }
            else
            {
                ambiguous.Add(new(group.StartSample, group.EndSample));
            }
        }

        foreach (var frame in evidence)
        {
            if (frame.Observation.ContainsMedia &&
                frame.Observation.VadProbability >= preset.VadThreshold - VadAmbiguityMargin &&
                frame.Observation.VadProbability < preset.VadThreshold &&
                frame.IsAboveDirectEnergyThreshold)
            {
                ambiguous.Add(new(frame.Observation.TimelineStartSample, frame.Observation.TimelineEndSample));
            }
        }

        ambiguous = MergeIntervals(ambiguous, maximumGapSamples: 0);
        var warmupUncertain = MergeIntervals(
            evidence
                .Where(frame => frame.IsWarmupUncertain)
                .Select(frame => new TimelineInterval(
                    frame.Observation.TimelineStartSample,
                    frame.Observation.TimelineEndSample)),
            maximumGapSamples: 0);
        var energyVadConflicts = preset.PreserveVadNegativeHighEnergyConflicts
            ? BuildEnergyVadConflictIntervals(evidence, minimumSpeechSamples)
            : [];
        var phrases = BuildPhrases(track, confirmedGroups, preset, cancellationToken);
        phrases = ApplyNonOverlappingPadding(phrases, track, preset);

        var confirmedFrameRms = confirmedGroups
            .SelectMany(group => group.Frames)
            .Where(frame => frame.IsDirectEvidence)
            .Select(frame => frame.Observation.RmsDbfs)
            .Order()
            .ToArray();
        var learnedVoiceRms = confirmedFrameRms.Length == 0
            ? AudioMath.SilenceDbfs
            : confirmedFrameRms[confirmedFrameRms.Length / 2];

        var segments = BuildClipSegments(
            track,
            phrases,
            ambiguous,
            energyVadConflicts,
            warmupUncertain);
        (phrases, segments) = ReconcileGainWithFrameAlignedOutput(
            track,
            phrases,
            segments,
            preset,
            cancellationToken);
        var result = new TrackAudioAnalysis(
            track.Index,
            observations.Count,
            phrases,
            segments,
            learnedVoiceRms);
        return evidenceWithTrace is null
            ? result
            : result with
            {
                NoiseBoundaryTrace = BuildNoiseBoundaryTrace(
                    track.Index,
                    mode,
                    preset.VadThreshold,
                    evidenceWithTrace.Policy,
                    evidence,
                    evidenceWithTrace.NoiseFloorTrace,
                    confirmedGroups,
                    energyVadConflicts,
                    warmupUncertain)
            };
    }

    private static NoiseBoundaryTrackTrace BuildNoiseBoundaryTrace(
        int trackIndex,
        NoiseBoundaryAnalysisMode mode,
        double vadThreshold,
        NoiseFloorPolicyDescriptor policy,
        IReadOnlyList<AudioFrameEvidence> evidence,
        IReadOnlyList<NoiseFloorFrameSnapshot> noiseFloorTrace,
        IReadOnlyList<CandidateGroup> confirmedGroups,
        IReadOnlyList<TimelineInterval> energyVadConflicts,
        IReadOnlyList<TimelineInterval> warmupUncertain)
    {
        if (evidence.Count != noiseFloorTrace.Count)
        {
            throw new InvalidDataException("Noise-boundary evidence/trace không cùng số frame.");
        }

        var confirmedFrames = confirmedGroups
            .SelectMany(group => group.Frames)
            .Select(frame => (
                frame.Observation.TimelineStartSample,
                frame.Observation.TimelineEndSample))
            .ToHashSet();
        var frames = new NoiseBoundaryFrameTrace[evidence.Count];
        for (var index = 0; index < evidence.Count; index++)
        {
            var frame = evidence[index];
            var floor = noiseFloorTrace[index];
            var observation = frame.Observation;
            var midpoint = observation.TimelineStartSample +
                           ((observation.TimelineEndSample - observation.TimelineStartSample) / 2);
            var state = !observation.ContainsMedia
                ? NoiseBoundaryFrameState.NoMedia
                : warmupUncertain.Any(interval => interval.Contains(midpoint))
                    ? NoiseBoundaryFrameState.WarmupAmbiguous
                    : energyVadConflicts.Any(interval => interval.Contains(midpoint))
                    ? NoiseBoundaryFrameState.EnergyConflictAmbiguous
                    : frame.IsVadSpeech
                        ? confirmedFrames.Contains((
                            observation.TimelineStartSample,
                            observation.TimelineEndSample))
                            ? NoiseBoundaryFrameState.ConfirmedStartEvidence
                            : NoiseBoundaryFrameState.UnconfirmedStartEvidence
                        : observation.VadProbability >= vadThreshold - VadAmbiguityMargin &&
                          frame.IsAboveDirectEnergyThreshold
                            ? NoiseBoundaryFrameState.BorderlineAmbiguous
                            : NoiseBoundaryFrameState.VadNegative;
            frames[index] = new(
                observation.TimelineStartSample,
                observation.TimelineEndSample,
                observation.VadProbability,
                observation.RmsDbfs,
                floor.NoiseFloorBeforeDbfs,
                floor.NoiseFloorAfterDbfs,
                floor.NoiseFloorReadyBefore,
                floor.NoiseFloorReadyAfter,
                floor.TrainingDecision,
                frame.IsVadSpeech,
                frame.IsDirectEvidence,
                frame.IsAboveDirectEnergyThreshold,
                frame.IsWarmupUncertain,
                state);
        }

        return new(trackIndex, mode, policy, frames);
    }

    private (IReadOnlyList<DialoguePhrase> Phrases, IReadOnlyList<AnalyzedAudioSegment> Segments)
        ReconcileGainWithFrameAlignedOutput(
            PremiereAudioTrack track,
            IReadOnlyList<DialoguePhrase> phrases,
            IReadOnlyList<AnalyzedAudioSegment> segments,
            DialogueProcessingPreset preset,
            CancellationToken cancellationToken)
    {
        var intervalsByPhrase = BuildFrameAlignedPhraseIntervals(track, segments, cancellationToken);
        var updatedPhrases = new DialoguePhrase[phrases.Count];

        for (var index = 0; index < phrases.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var phrase = phrases[index];
            var frameAlignedPeakDbfs = intervalsByPhrase.TryGetValue(phrase.Id, out var intervals)
                ? intervals
                    .Select(interval => pcmAccessor.MeasureSamplePeakDbfs(
                        track,
                        interval.StartSample,
                        interval.EndSample,
                        cancellationToken))
                    .DefaultIfEmpty(AudioMath.SilenceDbfs)
                    .Max()
                : AudioMath.SilenceDbfs;
            var gainReferencePeakDbfs = MathF.Max(phrase.MeasuredPeakDbfs, frameAlignedPeakDbfs);
            var requiredGainDb = (float)(
                preset.TargetSamplePeakDbfs -
                gainReferencePeakDbfs +
                preset.PremiereCenterPanCompensationDb);
            var appliedGainDb = MathF.Min(requiredGainDb, (float)preset.MaximumBoostDb);

            updatedPhrases[index] = phrase with
            {
                MeasuredPeakDbfs = gainReferencePeakDbfs,
                RequiredGainDb = requiredGainDb,
                AppliedGainDb = appliedGainDb,
                GainWasCapped = requiredGainDb > preset.MaximumBoostDb
            };
        }

        var gainsByPhrase = updatedPhrases.ToDictionary(phrase => phrase.Id, StringComparer.Ordinal);
        var updatedSegments = segments
            .Select(segment => segment.PhraseId is not null && gainsByPhrase.TryGetValue(segment.PhraseId, out var phrase)
                ? segment with { GainDb = phrase.AppliedGainDb }
                : segment)
            .ToArray();
        return (updatedPhrases, updatedSegments);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<TimelineInterval>> BuildFrameAlignedPhraseIntervals(
        PremiereAudioTrack track,
        IReadOnlyList<AnalyzedAudioSegment> segments,
        CancellationToken cancellationToken)
    {
        var intervalsByPhrase = new Dictionary<string, List<TimelineInterval>>(StringComparer.Ordinal);

        foreach (var clip in track.Clips)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frameCountLong = clip.TimelineEndFrame - clip.TimelineStartFrame;
            if (frameCountLong <= 0 || frameCountLong > int.MaxValue)
            {
                throw new InvalidDataException($"Clip '{clip.Id}' có duration frame không hỗ trợ.");
            }

            var frameCount = checked((int)frameCountLong);
            var choices = new FrameAlignmentChoice[frameCount];
            var clipStartSample = AudioMath.FramesToSamples(clip.TimelineStartFrame);
            foreach (var segment in segments
                         .Where(segment => segment.SourceClipId == clip.Id)
                         .OrderBy(segment => segment.TimelineStartSample))
            {
                var startOffset = Math.Max(0, segment.TimelineStartSample - clipStartSample);
                var endOffset = Math.Min(
                    checked(frameCountLong * AudioMath.SamplesPerSequenceFrame),
                    segment.TimelineEndSample - clipStartSample);
                var firstFrame = checked((int)(startOffset / AudioMath.SamplesPerSequenceFrame));
                var lastFrameExclusive = checked((int)Math.Min(
                    frameCountLong,
                    (endOffset + AudioMath.SamplesPerSequenceFrame - 1) /
                    AudioMath.SamplesPerSequenceFrame));
                var priority = FramePriority(segment.Status);

                for (var frame = firstFrame; frame < lastFrameExclusive; frame++)
                {
                    var frameStart = checked((long)frame * AudioMath.SamplesPerSequenceFrame);
                    var frameEnd = frameStart + AudioMath.SamplesPerSequenceFrame;
                    var overlap = Math.Max(
                        0,
                        Math.Min(endOffset, frameEnd) - Math.Max(startOffset, frameStart));
                    var existing = choices[frame];
                    if (!existing.IsAssigned ||
                        priority > existing.Priority ||
                        (priority == existing.Priority && overlap > existing.OverlapSamples))
                    {
                        choices[frame] = new(segment, priority, overlap, IsAssigned: true);
                    }
                }
            }

            if (choices.Any(choice => !choice.IsAssigned))
            {
                throw new InvalidDataException($"Phân tích không phủ kín toàn bộ frame của clip '{clip.Id}'.");
            }

            for (var frame = 0; frame < choices.Length; frame++)
            {
                var segment = choices[frame].Segment!;
                if (segment.PhraseId is null ||
                    segment.Status is not (AudioSegmentStatus.Speech or AudioSegmentStatus.Ambiguous))
                {
                    continue;
                }

                var frameStartSample = AudioMath.FramesToSamples(clip.TimelineStartFrame + frame);
                var interval = new TimelineInterval(
                    frameStartSample,
                    frameStartSample + AudioMath.SamplesPerSequenceFrame);
                if (!intervalsByPhrase.TryGetValue(segment.PhraseId, out var phraseIntervals))
                {
                    phraseIntervals = [];
                    intervalsByPhrase.Add(segment.PhraseId, phraseIntervals);
                }

                if (phraseIntervals.Count > 0 && phraseIntervals[^1].EndSample == interval.StartSample)
                {
                    phraseIntervals[^1] = new(phraseIntervals[^1].StartSample, interval.EndSample);
                }
                else
                {
                    phraseIntervals.Add(interval);
                }
            }
        }

        return intervalsByPhrase.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<TimelineInterval>)pair.Value,
            StringComparer.Ordinal);
    }

    private static int FramePriority(AudioSegmentStatus status) => status switch
    {
        AudioSegmentStatus.Ambiguous => 4,
        AudioSegmentStatus.Speech => 3,
        AudioSegmentStatus.Bleed => 2,
        _ => 1
    };

    private static IReadOnlyList<CandidateGroup> BuildVadGroups(
        IReadOnlyList<AudioFrameEvidence> evidence,
        long phraseBreakSamples)
    {
        var groups = new List<CandidateGroup>();
        CandidateGroupBuilder? current = null;

        foreach (var frame in evidence.Where(frame => frame.IsVadSpeech))
        {
            if (current is null || frame.Observation.TimelineStartSample - current.EndSample >= phraseBreakSamples)
            {
                if (current is not null)
                {
                    groups.Add(current.Build());
                }

                current = new(frame);
            }
            else
            {
                current.Add(frame);
            }
        }

        if (current is not null)
        {
            groups.Add(current.Build());
        }

        return groups;
    }

    private static IReadOnlyList<TimelineInterval> BuildEnergyVadConflictIntervals(
        IReadOnlyList<AudioFrameEvidence> evidence,
        long minimumSpeechSamples) =>
        MergeIntervals(
                evidence
                    .Where(frame =>
                        frame.Observation.ContainsMedia &&
                        !frame.IsVadSpeech &&
                        !frame.IsStableFloorStep &&
                        frame.IsAboveDirectEnergyThreshold)
                    .Select(frame => new TimelineInterval(
                        frame.Observation.TimelineStartSample,
                        frame.Observation.TimelineEndSample)),
                maximumGapSamples: 0)
            .Where(interval => interval.Length >= minimumSpeechSamples)
            .ToArray();

    private IReadOnlyList<DialoguePhrase> BuildPhrases(
        PremiereAudioTrack track,
        IReadOnlyList<CandidateGroup> groups,
        DialogueProcessingPreset preset,
        CancellationToken cancellationToken)
    {
        var phrases = new List<DialoguePhrase>(groups.Count);
        for (var index = 0; index < groups.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = groups[index];
            var directIntervals = MergeIntervals(
                group.Frames
                    .Where(frame => frame.IsDirectEvidence)
                    .Select(frame => new TimelineInterval(
                        frame.Observation.TimelineStartSample,
                        frame.Observation.TimelineEndSample)),
                maximumGapSamples: 0);
            var peakDbfs = directIntervals
                .Select(interval => pcmAccessor.MeasureSamplePeakDbfs(
                    track,
                    interval.StartSample,
                    interval.EndSample,
                    cancellationToken))
                .DefaultIfEmpty(AudioMath.SilenceDbfs)
                .Max();
            var requiredGain = (float)(
                preset.TargetSamplePeakDbfs -
                peakDbfs +
                preset.PremiereCenterPanCompensationDb);
            var appliedGain = MathF.Min(requiredGain, (float)preset.MaximumBoostDb);

            phrases.Add(new(
                Id: $"T{track.Index:D2}-P{index + 1:D6}",
                TrackIndex: track.Index,
                CoreStartSample: group.StartSample,
                CoreEndSample: group.EndSample,
                PaddedStartSample: group.StartSample,
                PaddedEndSample: group.EndSample,
                MeasuredPeakDbfs: peakDbfs,
                RequiredGainDb: requiredGain,
                AppliedGainDb: appliedGain,
                GainWasCapped: requiredGain > preset.MaximumBoostDb));
        }

        return phrases;
    }

    private static IReadOnlyList<DialoguePhrase> ApplyNonOverlappingPadding(
        IReadOnlyList<DialoguePhrase> phrases,
        PremiereAudioTrack track,
        DialogueProcessingPreset preset)
    {
        if (phrases.Count == 0)
        {
            return phrases;
        }

        var mediaStart = track.Clips.Min(clip => AudioMath.FramesToSamples(clip.TimelineStartFrame));
        var mediaEnd = track.Clips.Max(clip => AudioMath.FramesToSamples(clip.TimelineEndFrame));
        var before = AudioMath.MillisecondsToSamples(preset.PaddingBeforeMilliseconds);
        var after = AudioMath.MillisecondsToSamples(preset.PaddingAfterMilliseconds);
        var result = new DialoguePhrase[phrases.Count];

        for (var index = 0; index < phrases.Count; index++)
        {
            var phrase = phrases[index];
            var start = Math.Max(mediaStart, phrase.CoreStartSample - before);
            var end = Math.Min(mediaEnd, phrase.CoreEndSample + after);

            if (index > 0)
            {
                var previous = phrases[index - 1];
                var boundary = previous.CoreEndSample + ((phrase.CoreStartSample - previous.CoreEndSample) / 2);
                start = Math.Max(start, boundary);
            }

            if (index + 1 < phrases.Count)
            {
                var next = phrases[index + 1];
                var boundary = phrase.CoreEndSample + ((next.CoreStartSample - phrase.CoreEndSample) / 2);
                end = Math.Min(end, boundary);
            }

            result[index] = phrase with { PaddedStartSample = start, PaddedEndSample = end };
        }

        return result;
    }

    private static IReadOnlyList<AnalyzedAudioSegment> BuildClipSegments(
        PremiereAudioTrack track,
        IReadOnlyList<DialoguePhrase> phrases,
        IReadOnlyList<TimelineInterval> ambiguous,
        IReadOnlyList<TimelineInterval> energyVadConflicts,
        IReadOnlyList<TimelineInterval> warmupUncertain)
    {
        var segments = new List<AnalyzedAudioSegment>();

        foreach (var clip in track.Clips)
        {
            var clipStart = AudioMath.FramesToSamples(clip.TimelineStartFrame);
            var clipEnd = AudioMath.FramesToSamples(clip.TimelineEndFrame);
            var boundaries = new SortedSet<long> { clipStart, clipEnd };

            foreach (var phrase in phrases)
            {
                AddBoundaryIfInside(boundaries, phrase.PaddedStartSample, clipStart, clipEnd);
                AddBoundaryIfInside(boundaries, phrase.CoreStartSample, clipStart, clipEnd);
                AddBoundaryIfInside(boundaries, phrase.CoreEndSample, clipStart, clipEnd);
                AddBoundaryIfInside(boundaries, phrase.PaddedEndSample, clipStart, clipEnd);
            }

            foreach (var interval in ambiguous)
            {
                AddBoundaryIfInside(boundaries, interval.StartSample, clipStart, clipEnd);
                AddBoundaryIfInside(boundaries, interval.EndSample, clipStart, clipEnd);
            }

            foreach (var interval in energyVadConflicts)
            {
                AddBoundaryIfInside(boundaries, interval.StartSample, clipStart, clipEnd);
                AddBoundaryIfInside(boundaries, interval.EndSample, clipStart, clipEnd);
            }

            foreach (var interval in warmupUncertain)
            {
                AddBoundaryIfInside(boundaries, interval.StartSample, clipStart, clipEnd);
                AddBoundaryIfInside(boundaries, interval.EndSample, clipStart, clipEnd);
            }

            var ordered = boundaries.ToArray();
            for (var index = 0; index + 1 < ordered.Length; index++)
            {
                var start = ordered[index];
                var end = ordered[index + 1];
                var midpoint = start + ((end - start) / 2);
                var phrase = phrases.FirstOrDefault(
                    candidate => midpoint >= candidate.PaddedStartSample && midpoint < candidate.PaddedEndSample);
                var isWarmupUncertain = warmupUncertain.Any(interval => interval.Contains(midpoint));
                var isEnergyVadConflict = energyVadConflicts.Any(interval => interval.Contains(midpoint));
                var isAmbiguous = isWarmupUncertain ||
                                  isEnergyVadConflict ||
                                  ambiguous.Any(interval => interval.Contains(midpoint));
                var status = isAmbiguous
                    ? AudioSegmentStatus.Ambiguous
                    : phrase is null ? AudioSegmentStatus.Noise : AudioSegmentStatus.Speech;
                var isCoreSpeech = phrase is not null &&
                    midpoint >= phrase.CoreStartSample && midpoint < phrase.CoreEndSample;
                var reason = (status, phrase is not null, isCoreSpeech) switch
                {
                    (AudioSegmentStatus.Speech, _, true) => "confirmed-direct-speech",
                    (AudioSegmentStatus.Speech, _, false) => "speech-padding",
                    (AudioSegmentStatus.Ambiguous, true, _) when isWarmupUncertain =>
                        "ambiguous-noise-floor-warmup-near-speech",
                    (AudioSegmentStatus.Ambiguous, false, _) when isWarmupUncertain =>
                        "ambiguous-noise-floor-warmup",
                    (AudioSegmentStatus.Ambiguous, true, _) when isEnergyVadConflict =>
                        "ambiguous-energy-vad-conflict-near-speech",
                    (AudioSegmentStatus.Ambiguous, false, _) when isEnergyVadConflict =>
                        "ambiguous-energy-vad-conflict",
                    (AudioSegmentStatus.Ambiguous, true, _) => "ambiguous-near-speech",
                    (AudioSegmentStatus.Ambiguous, false, _) => "ambiguous-independent",
                    _ => "vad-negative-noise"
                };
                var sourceStart = Math.Min(
                    clip.SourceEndSample,
                    checked(clip.SourceStartSample + (start - clipStart)));
                var sourceEnd = Math.Min(
                    clip.SourceEndSample,
                    checked(clip.SourceStartSample + (end - clipStart)));

                AddOrMerge(segments, new(
                    track.Index,
                    clip.Id,
                    start,
                    end,
                    sourceStart,
                    sourceEnd,
                    status,
                    phrase?.Id,
                    phrase?.AppliedGainDb ?? (isAmbiguous ? 0f : null),
                    reason));
            }
        }

        return segments;
    }

    private static List<TimelineInterval> MergeIntervals(
        IEnumerable<TimelineInterval> intervals,
        long maximumGapSamples)
    {
        var ordered = intervals.OrderBy(interval => interval.StartSample).ToArray();
        var result = new List<TimelineInterval>();
        foreach (var interval in ordered)
        {
            if (result.Count == 0 || interval.StartSample - result[^1].EndSample > maximumGapSamples)
            {
                result.Add(interval);
            }
            else
            {
                result[^1] = new(result[^1].StartSample, Math.Max(result[^1].EndSample, interval.EndSample));
            }
        }

        return result;
    }

    private static void AddBoundaryIfInside(SortedSet<long> boundaries, long value, long start, long end)
    {
        if (value > start && value < end)
        {
            boundaries.Add(value);
        }
    }

    private static void AddOrMerge(List<AnalyzedAudioSegment> segments, AnalyzedAudioSegment segment)
    {
        if (segments.Count > 0)
        {
            var previous = segments[^1];
            if (previous.SourceClipId == segment.SourceClipId &&
                previous.TimelineEndSample == segment.TimelineStartSample &&
                previous.SourceEndSample == segment.SourceStartSample &&
                previous.Status == segment.Status &&
                previous.PhraseId == segment.PhraseId &&
                previous.GainDb == segment.GainDb &&
                previous.Reason == segment.Reason)
            {
                segments[^1] = previous with
                {
                    TimelineEndSample = segment.TimelineEndSample,
                    SourceEndSample = segment.SourceEndSample
                };
                return;
            }
        }

        segments.Add(segment);
    }

    private readonly record struct FrameAlignmentChoice(
        AnalyzedAudioSegment? Segment,
        int Priority,
        long OverlapSamples,
        bool IsAssigned);

    private sealed record CandidateGroup(
        long StartSample,
        long EndSample,
        long VadEvidenceSamples,
        long DirectEvidenceSamples,
        IReadOnlyList<AudioFrameEvidence> Frames);

    private sealed class CandidateGroupBuilder(AudioFrameEvidence first)
    {
        private readonly List<AudioFrameEvidence> _frames = [first];

        public long EndSample { get; private set; } = first.Observation.TimelineEndSample;

        public void Add(AudioFrameEvidence frame)
        {
            _frames.Add(frame);
            EndSample = frame.Observation.TimelineEndSample;
        }

        public CandidateGroup Build() => new(
            _frames[0].Observation.TimelineStartSample,
            EndSample,
            _frames.Sum(frame => frame.Observation.TimelineEndSample - frame.Observation.TimelineStartSample),
            _frames.Where(frame => frame.IsDirectEvidence)
                .Sum(frame => frame.Observation.TimelineEndSample - frame.Observation.TimelineStartSample),
            _frames);
    }
}
