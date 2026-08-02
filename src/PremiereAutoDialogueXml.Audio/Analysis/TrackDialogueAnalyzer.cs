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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(preset);

        var evidence = BuildEvidence(observations, preset);
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

        var segments = BuildClipSegments(track, phrases, ambiguous);
        return new(track.Index, observations.Count, phrases, segments, learnedVoiceRms);
    }

    private static IReadOnlyList<FrameEvidence> BuildEvidence(
        IReadOnlyList<AudioFrameObservation> observations,
        DialogueProcessingPreset preset)
    {
        var noiseFloor = new AdaptiveNoiseFloor();
        var result = new List<FrameEvidence>(observations.Count);

        foreach (var observation in observations)
        {
            var isVadSpeech = observation.ContainsMedia && observation.VadProbability >= preset.VadThreshold;
            if (observation.ContainsMedia && !isVadSpeech)
            {
                noiseFloor.Observe(observation.RmsDbfs);
            }

            var aboveDirectThreshold =
                observation.RmsDbfs >= noiseFloor.CurrentDbfs + preset.DirectVoiceAboveNoiseDb;
            result.Add(new(
                observation,
                noiseFloor.CurrentDbfs,
                isVadSpeech,
                isVadSpeech && aboveDirectThreshold,
                aboveDirectThreshold));
        }

        return result;
    }

    private static IReadOnlyList<CandidateGroup> BuildVadGroups(
        IReadOnlyList<FrameEvidence> evidence,
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
            var requiredGain = (float)(preset.TargetSamplePeakDbfs - peakDbfs);
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
        IReadOnlyList<TimelineInterval> ambiguous)
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

            var ordered = boundaries.ToArray();
            for (var index = 0; index + 1 < ordered.Length; index++)
            {
                var start = ordered[index];
                var end = ordered[index + 1];
                var midpoint = start + ((end - start) / 2);
                var phrase = phrases.FirstOrDefault(
                    candidate => midpoint >= candidate.PaddedStartSample && midpoint < candidate.PaddedEndSample);
                var isAmbiguous = ambiguous.Any(interval => interval.Contains(midpoint));
                var status = isAmbiguous
                    ? AudioSegmentStatus.Ambiguous
                    : phrase is null ? AudioSegmentStatus.Noise : AudioSegmentStatus.Speech;
                var isCoreSpeech = phrase is not null &&
                    midpoint >= phrase.CoreStartSample && midpoint < phrase.CoreEndSample;
                var reason = (status, phrase is not null, isCoreSpeech) switch
                {
                    (AudioSegmentStatus.Speech, _, true) => "confirmed-direct-speech",
                    (AudioSegmentStatus.Speech, _, false) => "speech-padding",
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

    private sealed record FrameEvidence(
        AudioFrameObservation Observation,
        float NoiseFloorDbfs,
        bool IsVadSpeech,
        bool IsDirectEvidence,
        bool IsAboveDirectEnergyThreshold);

    private sealed record CandidateGroup(
        long StartSample,
        long EndSample,
        long VadEvidenceSamples,
        long DirectEvidenceSamples,
        IReadOnlyList<FrameEvidence> Frames);

    private sealed class CandidateGroupBuilder(FrameEvidence first)
    {
        private readonly List<FrameEvidence> _frames = [first];

        public long EndSample { get; private set; } = first.Observation.TimelineEndSample;

        public void Add(FrameEvidence frame)
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
