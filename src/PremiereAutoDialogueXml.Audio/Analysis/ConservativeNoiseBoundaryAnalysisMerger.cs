namespace PremiereAutoDialogueXml.Audio.Analysis;

internal static class ConservativeNoiseBoundaryAnalysisMerger
{
    internal const string SafetyReason = "ambiguous-noise-boundary-disagreement";
    internal const string CandidateOnlyFallbackReason =
        "ambiguous-noise-boundary-candidate-only-in-baseline-fallback";

    public static (TrackAudioAnalysis Analysis, NoiseBoundaryTrackComparison Comparison) Merge(
        TrackAudioAnalysis baseline,
        TrackAudioAnalysis candidate,
        NoiseBoundaryTrackComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(comparison);
        if (comparison.TrackIndex != baseline.TrackIndex ||
            candidate.TrackIndex != baseline.TrackIndex)
        {
            throw new InvalidDataException("Noise-boundary comparison không cùng track cần merge.");
        }

        var (analysis, _) = ConservativeVadAnalysisMerger.Merge(
            baseline,
            candidate,
            comparison.ObservationCount,
            comparison.ChangedFrameCount,
            comparison.MaximumNoiseFloorDeltaDb,
            SafetyReason,
            CandidateOnlyFallbackReason);
        var finalDifferences = CompareFinalDecisions(baseline, candidate, analysis);
        var baselineEnabledFinalDisabledCount = finalDifferences.Count(difference =>
            difference.BaselineEnabled && !difference.FinalEnabled);
        if (baselineEnabledFinalDisabledCount != 0)
        {
            throw new InvalidDataException("Noise-boundary merge đã làm mất vùng Enabled của baseline.");
        }

        return (analysis, comparison with
        {
            BaselineEnabledFinalDisabledCount = baselineEnabledFinalDisabledCount,
            FinalDifferences = finalDifferences
        });
    }

    private static IReadOnlyList<NoiseBoundaryFinalDecisionDifference> CompareFinalDecisions(
        TrackAudioAnalysis baseline,
        TrackAudioAnalysis candidate,
        TrackAudioAnalysis final)
    {
        var differences = new List<NoiseBoundaryFinalDecisionDifference>();
        var clipIds = baseline.Segments.Select(segment => segment.SourceClipId)
            .Concat(candidate.Segments.Select(segment => segment.SourceClipId))
            .Concat(final.Segments.Select(segment => segment.SourceClipId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var clipId in clipIds)
        {
            var baselineSegments = ForClip(baseline, clipId);
            var candidateSegments = ForClip(candidate, clipId);
            var finalSegments = ForClip(final, clipId);
            if (baselineSegments.Length == 0 || candidateSegments.Length == 0 || finalSegments.Length == 0)
            {
                throw new InvalidDataException($"Noise-boundary merge thiếu coverage clip '{clipId}'.");
            }

            var boundaries = baselineSegments.SelectMany(Boundaries)
                .Concat(candidateSegments.SelectMany(Boundaries))
                .Concat(finalSegments.SelectMany(Boundaries))
                .Distinct()
                .Order()
                .ToArray();
            var baselineIndex = 0;
            var candidateIndex = 0;
            var finalIndex = 0;
            for (var boundaryIndex = 0; boundaryIndex + 1 < boundaries.Length; boundaryIndex++)
            {
                var start = boundaries[boundaryIndex];
                var end = boundaries[boundaryIndex + 1];
                if (end <= start)
                {
                    continue;
                }

                var baselineSegment = CoveringSegment(baselineSegments, ref baselineIndex, start, end, "baseline");
                var candidateSegment = CoveringSegment(candidateSegments, ref candidateIndex, start, end, "candidate");
                var finalSegment = CoveringSegment(finalSegments, ref finalIndex, start, end, "final");
                if (SemanticallyEqual(baselineSegment, candidateSegment) &&
                    SemanticallyEqual(candidateSegment, finalSegment))
                {
                    continue;
                }

                AddOrMerge(differences, new(
                    start,
                    end,
                    clipId,
                    baselineSegment.Status,
                    baselineSegment.Reason,
                    IsEnabled(baselineSegment),
                    baselineSegment.PhraseId,
                    baselineSegment.GainDb,
                    candidateSegment.Status,
                    candidateSegment.Reason,
                    IsEnabled(candidateSegment),
                    candidateSegment.PhraseId,
                    candidateSegment.GainDb,
                    finalSegment.Status,
                    finalSegment.Reason,
                    IsEnabled(finalSegment),
                    finalSegment.PhraseId,
                    finalSegment.GainDb));
            }
        }

        return differences;
    }

    private static AnalyzedAudioSegment[] ForClip(TrackAudioAnalysis analysis, string clipId) =>
        analysis.Segments
            .Where(segment => segment.SourceClipId == clipId)
            .OrderBy(segment => segment.TimelineStartSample)
            .ToArray();

    private static long[] Boundaries(AnalyzedAudioSegment segment) =>
        [segment.TimelineStartSample, segment.TimelineEndSample];

    private static AnalyzedAudioSegment CoveringSegment(
        IReadOnlyList<AnalyzedAudioSegment> segments,
        ref int index,
        long start,
        long end,
        string label)
    {
        while (index + 1 < segments.Count && segments[index].TimelineEndSample <= start)
        {
            index++;
        }

        var segment = segments[index];
        if (segment.TimelineStartSample > start || segment.TimelineEndSample < end)
        {
            throw new InvalidDataException($"Coverage {label} noise-boundary không phủ [{start}, {end}).");
        }

        return segment;
    }

    private static bool SemanticallyEqual(AnalyzedAudioSegment left, AnalyzedAudioSegment right) =>
        left.Status == right.Status &&
        left.Reason == right.Reason &&
        left.PhraseId == right.PhraseId &&
        GainsEqual(left.GainDb, right.GainDb);

    private static bool IsEnabled(AnalyzedAudioSegment segment) =>
        segment.Status is AudioSegmentStatus.Speech or AudioSegmentStatus.Ambiguous;

    private static bool GainsEqual(float? left, float? right) =>
        left is null || right is null
            ? left is null && right is null
            : Math.Abs(left.Value - right.Value) <= 0.001f;

    private static void AddOrMerge(
        List<NoiseBoundaryFinalDecisionDifference> differences,
        NoiseBoundaryFinalDecisionDifference difference)
    {
        if (differences.Count > 0)
        {
            var previous = differences[^1];
            if (previous.SourceClipId == difference.SourceClipId &&
                previous.TimelineEndSample == difference.TimelineStartSample &&
                previous.BaselineStatus == difference.BaselineStatus &&
                previous.BaselineReason == difference.BaselineReason &&
                previous.BaselinePhraseId == difference.BaselinePhraseId &&
                GainsEqual(previous.BaselineGainDb, difference.BaselineGainDb) &&
                previous.CandidateStatus == difference.CandidateStatus &&
                previous.CandidateReason == difference.CandidateReason &&
                previous.CandidatePhraseId == difference.CandidatePhraseId &&
                GainsEqual(previous.CandidateGainDb, difference.CandidateGainDb) &&
                previous.FinalStatus == difference.FinalStatus &&
                previous.FinalReason == difference.FinalReason &&
                previous.FinalPhraseId == difference.FinalPhraseId &&
                GainsEqual(previous.FinalGainDb, difference.FinalGainDb))
            {
                differences[^1] = previous with { TimelineEndSample = difference.TimelineEndSample };
                return;
            }
        }

        differences.Add(difference);
    }
}
