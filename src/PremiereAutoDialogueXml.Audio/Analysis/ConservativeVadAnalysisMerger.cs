namespace PremiereAutoDialogueXml.Audio.Analysis;

internal static class ConservativeVadAnalysisMerger
{
    private const string SafetyReason = "ambiguous-vad-front-end-disagreement";

    public static (TrackAudioAnalysis Analysis, VadFrontEndTrackComparison Comparison) Merge(
        TrackAudioAnalysis legacy,
        TrackAudioAnalysis candidate,
        int observationCount,
        int changedObservationCount,
        float maximumProbabilityDelta)
    {
        if (legacy.TrackIndex != candidate.TrackIndex)
        {
            throw new InvalidDataException("Legacy/candidate VAD không cùng track.");
        }

        var candidatePhrases = candidate.Phrases.ToDictionary(phrase => phrase.Id, StringComparer.Ordinal);
        var legacyPhrases = legacy.Phrases.ToDictionary(phrase => phrase.Id, StringComparer.Ordinal);
        var finalPhrases = new Dictionary<string, DialoguePhrase>(StringComparer.Ordinal);
        var remappedLegacyPhrases = new Dictionary<string, DialoguePhrase>(StringComparer.Ordinal);
        var finalSegments = new List<AnalyzedAudioSegment>();
        var differences = new List<VadDecisionDifference>();
        var legacyEnabledCandidateDisabledCount = 0;
        var legacyDisabledCandidateEnabledCount = 0;

        var clipIds = legacy.Segments.Select(segment => segment.SourceClipId)
            .Concat(candidate.Segments.Select(segment => segment.SourceClipId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(clipId => Math.Min(
                legacy.Segments.Where(segment => segment.SourceClipId == clipId)
                    .Select(segment => segment.TimelineStartSample)
                    .DefaultIfEmpty(long.MaxValue)
                    .Min(),
                candidate.Segments.Where(segment => segment.SourceClipId == clipId)
                    .Select(segment => segment.TimelineStartSample)
                    .DefaultIfEmpty(long.MaxValue)
                    .Min()))
            .ToArray();

        foreach (var clipId in clipIds)
        {
            var legacySegments = legacy.Segments
                .Where(segment => segment.SourceClipId == clipId)
                .OrderBy(segment => segment.TimelineStartSample)
                .ToArray();
            var candidateSegments = candidate.Segments
                .Where(segment => segment.SourceClipId == clipId)
                .OrderBy(segment => segment.TimelineStartSample)
                .ToArray();
            if (legacySegments.Length == 0 || candidateSegments.Length == 0)
            {
                throw new InvalidDataException($"Legacy/candidate VAD không cùng coverage clip '{clipId}'.");
            }

            var boundaries = legacySegments.SelectMany(segment => new[]
                {
                    segment.TimelineStartSample,
                    segment.TimelineEndSample
                })
                .Concat(candidateSegments.SelectMany(segment => new[]
                {
                    segment.TimelineStartSample,
                    segment.TimelineEndSample
                }))
                .Distinct()
                .Order()
                .ToArray();
            var legacyIndex = 0;
            var candidateIndex = 0;
            for (var boundaryIndex = 0; boundaryIndex + 1 < boundaries.Length; boundaryIndex++)
            {
                var start = boundaries[boundaryIndex];
                var end = boundaries[boundaryIndex + 1];
                if (end <= start)
                {
                    continue;
                }

                var legacySegment = CoveringSegment(legacySegments, ref legacyIndex, start, end, "legacy");
                var candidateSegment = CoveringSegment(candidateSegments, ref candidateIndex, start, end, "candidate");
                var legacyEnabled = IsEnabled(legacySegment.Status);
                var candidateEnabled = IsEnabled(candidateSegment.Status);
                AnalyzedAudioSegment selected;
                if (legacyEnabled && !candidateEnabled)
                {
                    legacyEnabledCandidateDisabledCount++;
                    selected = Slice(legacySegment, start, end) with
                    {
                        Status = AudioSegmentStatus.Ambiguous,
                        Reason = SafetyReason,
                        PhraseId = RemapLegacyPhrase(
                            legacySegment.PhraseId,
                            legacyPhrases,
                            candidatePhrases,
                            remappedLegacyPhrases),
                        BleedEvidence = null
                    };
                }
                else
                {
                    if (!legacyEnabled && candidateEnabled)
                    {
                        legacyDisabledCandidateEnabledCount++;
                    }

                    selected = Slice(candidateSegment, start, end);
                }

                if (selected.PhraseId is { } selectedPhraseId)
                {
                    if (candidatePhrases.TryGetValue(selectedPhraseId, out var candidatePhrase))
                    {
                        finalPhrases.TryAdd(candidatePhrase.Id, candidatePhrase);
                    }
                    else if (remappedLegacyPhrases.TryGetValue(selectedPhraseId, out var legacyPhrase))
                    {
                        finalPhrases.TryAdd(legacyPhrase.Id, legacyPhrase);
                    }
                    else
                    {
                        throw new InvalidDataException($"Không tìm thấy phrase '{selectedPhraseId}' sau merge VAD.");
                    }
                }

                AddOrMerge(finalSegments, selected);
                if (!SemanticallyEqual(legacySegment, candidateSegment))
                {
                    AddOrMergeDifference(differences, new(
                        legacy.TrackIndex,
                        clipId,
                        start,
                        end,
                        legacySegment.Status,
                        legacySegment.Reason,
                        candidateSegment.Status,
                        candidateSegment.Reason,
                        selected.Status,
                        selected.Reason));
                }
            }
        }

        var finalAnalysis = candidate with
        {
            Phrases = finalPhrases.Values
                .OrderBy(phrase => phrase.CoreStartSample)
                .ThenBy(phrase => phrase.Id, StringComparer.Ordinal)
                .ToArray(),
            Segments = finalSegments
        };
        var comparison = new VadFrontEndTrackComparison(
            legacy.TrackIndex,
            observationCount,
            changedObservationCount,
            maximumProbabilityDelta,
            differences.Count,
            legacyEnabledCandidateDisabledCount,
            legacyDisabledCandidateEnabledCount,
            differences);
        return (finalAnalysis, comparison);
    }

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
            throw new InvalidDataException($"Coverage {label} VAD không phủ [{start}, {end}).");
        }

        return segment;
    }

    private static string? RemapLegacyPhrase(
        string? phraseId,
        IReadOnlyDictionary<string, DialoguePhrase> legacyPhrases,
        IReadOnlyDictionary<string, DialoguePhrase> candidatePhrases,
        IDictionary<string, DialoguePhrase> remapped)
    {
        if (phraseId is null)
        {
            return null;
        }

        var mappedId = $"{phraseId}-legacy-safety";
        if (remapped.ContainsKey(mappedId))
        {
            return mappedId;
        }

        if (!legacyPhrases.TryGetValue(phraseId, out var phrase))
        {
            throw new InvalidDataException($"Legacy segment trỏ phrase không tồn tại: '{phraseId}'.");
        }

        while (candidatePhrases.ContainsKey(mappedId) || remapped.ContainsKey(mappedId))
        {
            mappedId += "-x";
        }

        remapped.Add(mappedId, phrase with { Id = mappedId });
        return mappedId;
    }

    private static AnalyzedAudioSegment Slice(AnalyzedAudioSegment segment, long start, long end)
    {
        var sourceStart = checked(segment.SourceStartSample + (start - segment.TimelineStartSample));
        var sourceEnd = checked(sourceStart + (end - start));
        return segment with
        {
            TimelineStartSample = start,
            TimelineEndSample = end,
            SourceStartSample = sourceStart,
            SourceEndSample = sourceEnd
        };
    }

    private static bool IsEnabled(AudioSegmentStatus status) =>
        status is AudioSegmentStatus.Speech or AudioSegmentStatus.Ambiguous;

    private static bool SemanticallyEqual(AnalyzedAudioSegment legacy, AnalyzedAudioSegment candidate) =>
        legacy.Status == candidate.Status &&
        legacy.Reason == candidate.Reason &&
        GainsEqual(legacy.GainDb, candidate.GainDb) &&
        legacy.BleedEvidence == candidate.BleedEvidence;

    private static bool GainsEqual(float? left, float? right) =>
        left is null || right is null
            ? left is null && right is null
            : Math.Abs(left.Value - right.Value) <= 0.001f;

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
                GainsEqual(previous.GainDb, segment.GainDb) &&
                previous.Reason == segment.Reason &&
                previous.BleedEvidence == segment.BleedEvidence)
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

    private static void AddOrMergeDifference(
        List<VadDecisionDifference> differences,
        VadDecisionDifference difference)
    {
        if (differences.Count > 0)
        {
            var previous = differences[^1];
            if (previous.TrackIndex == difference.TrackIndex &&
                previous.SourceClipId == difference.SourceClipId &&
                previous.TimelineEndSample == difference.TimelineStartSample &&
                previous.LegacyStatus == difference.LegacyStatus &&
                previous.LegacyReason == difference.LegacyReason &&
                previous.CandidateStatus == difference.CandidateStatus &&
                previous.CandidateReason == difference.CandidateReason &&
                previous.FinalStatus == difference.FinalStatus &&
                previous.FinalReason == difference.FinalReason)
            {
                differences[^1] = previous with { TimelineEndSample = difference.TimelineEndSample };
                return;
            }
        }

        differences.Add(difference);
    }
}
