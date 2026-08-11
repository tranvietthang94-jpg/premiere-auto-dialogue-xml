namespace PremiereAutoDialogueXml.Audio.Analysis;

public static class NoiseBoundaryShadowComparer
{
    private const float FloatTolerance = 0.0001f;

    public static NoiseBoundaryTrackComparison Compare(
        TrackAudioAnalysis baseline,
        TrackAudioAnalysis candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        if (baseline.TrackIndex != candidate.TrackIndex)
        {
            throw new InvalidDataException("Baseline/candidate noise-boundary không cùng track.");
        }

        var baselineTrace = baseline.NoiseBoundaryTrace
            ?? throw new InvalidDataException("Baseline noise-boundary thiếu frame trace.");
        var candidateTrace = candidate.NoiseBoundaryTrace
            ?? throw new InvalidDataException("Candidate noise-boundary thiếu frame trace.");
        if (baselineTrace.Mode != NoiseBoundaryAnalysisMode.Phase09Baseline ||
            candidateTrace.Mode != NoiseBoundaryAnalysisMode.NoiseBoundaryCandidate)
        {
            throw new InvalidDataException("Noise-boundary trace không đúng baseline/candidate mode.");
        }

        if (baselineTrace.Policy != NoiseFloorPolicyCatalog.Phase09Baseline ||
            candidateTrace.Policy != NoiseFloorPolicyCatalog.NoiseBoundaryCandidate)
        {
            throw new InvalidDataException("Noise-boundary trace không đúng policy provenance.");
        }

        if (baselineTrace.TrackIndex != baseline.TrackIndex ||
            candidateTrace.TrackIndex != candidate.TrackIndex ||
            baselineTrace.Frames.Count != candidateTrace.Frames.Count)
        {
            throw new InvalidDataException("Baseline/candidate noise-boundary không cùng observation contract.");
        }

        var frameDifferences = new List<NoiseBoundaryFrameDifference>();
        for (var index = 0; index < baselineTrace.Frames.Count; index++)
        {
            var baselineFrame = baselineTrace.Frames[index];
            var candidateFrame = candidateTrace.Frames[index];
            if (baselineFrame.TimelineStartSample != candidateFrame.TimelineStartSample ||
                baselineFrame.TimelineEndSample != candidateFrame.TimelineEndSample)
            {
                throw new InvalidDataException("Baseline/candidate noise-boundary lệch timeline frame.");
            }

            if (!FramesMatch(baselineFrame, candidateFrame))
            {
                frameDifferences.Add(new(baselineFrame, candidateFrame));
            }
        }

        var decisionDifferences = CompareDecisions(baseline.Segments, candidate.Segments);
        return new(
            baseline.TrackIndex,
            baselineTrace.Mode,
            candidateTrace.Mode,
            baselineTrace.Policy.Version,
            candidateTrace.Policy.Version,
            baselineTrace.Frames.Count,
            frameDifferences.Count,
            CountPhraseDifferences(baseline.Phrases, candidate.Phrases),
            decisionDifferences.Count,
            decisionDifferences.Count(difference =>
                difference.BaselineEnabled && !difference.CandidateEnabled),
            decisionDifferences.Count(difference =>
                !difference.BaselineEnabled && difference.CandidateEnabled),
            frameDifferences,
            decisionDifferences);
    }

    private static bool FramesMatch(
        NoiseBoundaryFrameTrace baseline,
        NoiseBoundaryFrameTrace candidate) =>
        MathF.Abs(baseline.VadProbability - candidate.VadProbability) <= FloatTolerance &&
        MathF.Abs(baseline.RmsDbfs - candidate.RmsDbfs) <= FloatTolerance &&
        MathF.Abs(baseline.NoiseFloorBeforeDbfs - candidate.NoiseFloorBeforeDbfs) <= FloatTolerance &&
        MathF.Abs(baseline.NoiseFloorAfterDbfs - candidate.NoiseFloorAfterDbfs) <= FloatTolerance &&
        baseline.NoiseFloorReadyBefore == candidate.NoiseFloorReadyBefore &&
        baseline.NoiseFloorReadyAfter == candidate.NoiseFloorReadyAfter &&
        baseline.NoiseFloorTrainingDecision == candidate.NoiseFloorTrainingDecision &&
        baseline.IsVadSpeech == candidate.IsVadSpeech &&
        baseline.IsDirectEvidence == candidate.IsDirectEvidence &&
        baseline.IsAboveDirectEnergyThreshold == candidate.IsAboveDirectEnergyThreshold &&
        baseline.IsWarmupUncertain == candidate.IsWarmupUncertain &&
        baseline.BoundaryState == candidate.BoundaryState;

    private static int CountPhraseDifferences(
        IReadOnlyList<DialoguePhrase> baseline,
        IReadOnlyList<DialoguePhrase> candidate)
    {
        var baselineCounts = baseline
            .GroupBy(PhraseSignature)
            .ToDictionary(group => group.Key, group => group.Count());
        var candidateCounts = candidate
            .GroupBy(PhraseSignature)
            .ToDictionary(group => group.Key, group => group.Count());
        return baselineCounts.Keys
            .Concat(candidateCounts.Keys)
            .Distinct()
            .Sum(signature =>
                Math.Abs(
                    baselineCounts.GetValueOrDefault(signature) -
                    candidateCounts.GetValueOrDefault(signature)));
    }

    private static PhraseComparisonSignature PhraseSignature(DialoguePhrase phrase) => new(
        phrase.CoreStartSample,
        phrase.CoreEndSample,
        phrase.PaddedStartSample,
        phrase.PaddedEndSample,
        phrase.MeasuredPeakDbfs,
        phrase.RequiredGainDb,
        phrase.AppliedGainDb,
        phrase.GainWasCapped);

    private static IReadOnlyList<NoiseBoundaryDecisionDifference> CompareDecisions(
        IReadOnlyList<AnalyzedAudioSegment> baseline,
        IReadOnlyList<AnalyzedAudioSegment> candidate)
    {
        var baselineOrdered = baseline.OrderBy(segment => segment.TimelineStartSample).ToArray();
        var candidateOrdered = candidate.OrderBy(segment => segment.TimelineStartSample).ToArray();
        var boundaries = baselineOrdered
            .SelectMany(segment => new[] { segment.TimelineStartSample, segment.TimelineEndSample })
            .Concat(candidateOrdered.SelectMany(segment =>
                new[] { segment.TimelineStartSample, segment.TimelineEndSample }))
            .Distinct()
            .Order()
            .ToArray();
        var differences = new List<NoiseBoundaryDecisionDifference>();
        var baselineIndex = 0;
        var candidateIndex = 0;

        for (var index = 0; index + 1 < boundaries.Length; index++)
        {
            var start = boundaries[index];
            var end = boundaries[index + 1];
            if (end <= start)
            {
                continue;
            }

            var midpoint = start + ((end - start) / 2);
            var baselineSegment = FindSegmentAt(baselineOrdered, ref baselineIndex, midpoint);
            var candidateSegment = FindSegmentAt(candidateOrdered, ref candidateIndex, midpoint);
            if (DecisionsMatch(baselineSegment, candidateSegment))
            {
                continue;
            }

            differences.Add(new(
                start,
                end,
                baselineSegment?.SourceClipId,
                baselineSegment?.Status,
                baselineSegment?.Reason,
                IsEnabled(baselineSegment),
                candidateSegment?.SourceClipId,
                candidateSegment?.Status,
                candidateSegment?.Reason,
                IsEnabled(candidateSegment)));
        }

        return differences;
    }

    private static AnalyzedAudioSegment? FindSegmentAt(
        IReadOnlyList<AnalyzedAudioSegment> segments,
        ref int index,
        long sample)
    {
        while (index < segments.Count && segments[index].TimelineEndSample <= sample)
        {
            index++;
        }

        if (index < segments.Count &&
            segments[index].TimelineStartSample <= sample &&
            segments[index].TimelineEndSample > sample)
        {
            return segments[index];
        }

        return null;
    }

    private static bool DecisionsMatch(
        AnalyzedAudioSegment? baseline,
        AnalyzedAudioSegment? candidate)
    {
        if (baseline is null || candidate is null)
        {
            return baseline is null && candidate is null;
        }

        return baseline.SourceClipId == candidate.SourceClipId &&
               baseline.Status == candidate.Status &&
               baseline.Reason == candidate.Reason &&
               baseline.PhraseId == candidate.PhraseId &&
               NullableFloatMatches(baseline.GainDb, candidate.GainDb);
    }

    private static bool NullableFloatMatches(float? baseline, float? candidate) =>
        baseline.HasValue == candidate.HasValue &&
        (!baseline.HasValue || MathF.Abs(baseline.Value - candidate!.Value) <= FloatTolerance);

    private static bool IsEnabled(AnalyzedAudioSegment? segment) =>
        segment?.Status is AudioSegmentStatus.Speech or AudioSegmentStatus.Ambiguous;

    private readonly record struct PhraseComparisonSignature(
        long CoreStartSample,
        long CoreEndSample,
        long PaddedStartSample,
        long PaddedEndSample,
        float MeasuredPeakDbfs,
        float RequiredGainDb,
        float AppliedGainDb,
        bool GainWasCapped);
}
