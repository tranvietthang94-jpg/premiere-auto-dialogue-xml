using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.ProjectModel;

namespace PremiereAutoDialogueXml.Audio.Analysis;

public sealed class CrossTrackShadowEvidenceBuilder(TimelinePcmAccessor pcmAccessor)
{
    public IReadOnlyList<CrossTrackShadowEvidence> Build(
        PremiereSequence sequence,
        IReadOnlyList<TrackAudioAnalysis> analyses,
        DialogueProcessingPreset preset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(analyses);
        ArgumentNullException.ThrowIfNull(preset);

        var tracksByIndex = sequence.AudioTracks.ToDictionary(track => track.Index);
        var orderedAnalyses = analyses.OrderBy(analysis => analysis.TrackIndex).ToArray();
        var phraseIndexes = orderedAnalyses.ToDictionary(
            analysis => analysis.TrackIndex,
            analysis => new PhraseIntervalIndex(analysis.Phrases));
        var comparisonBufferLength = checked((int)AudioMath.MillisecondsToSamples(
            BleedResolver.MaximumComparisonMilliseconds));
        var targetBuffer = new float[comparisonBufferLength];
        var otherBuffer = new float[comparisonBufferLength];
        var result = new List<CrossTrackShadowEvidence>();

        foreach (var targetAnalysis in orderedAnalyses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetTrack = tracksByIndex[targetAnalysis.TrackIndex];
            foreach (var segment in targetAnalysis.Segments
                         .Where(IsEnergyVadConflict)
                         .OrderBy(segment => segment.TimelineStartSample)
                         .ThenBy(segment => segment.SourceClipId, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var comparison = FindBestComparison(
                    segment,
                    targetTrack,
                    targetAnalysis,
                    tracksByIndex,
                    orderedAnalyses,
                    phraseIndexes,
                    preset,
                    targetBuffer,
                    otherBuffer,
                    cancellationToken);

                result.Add(new(
                    segment.TrackIndex,
                    segment.SourceClipId,
                    segment.TimelineStartSample,
                    segment.TimelineEndSample,
                    segment.Reason,
                    comparison?.Outcome ?? CrossTrackShadowOutcome.NoComparableSpeech,
                    comparison?.StartSample,
                    comparison?.EndSample,
                    comparison?.Evidence));
            }
        }

        return result;
    }

    private ComparisonCandidate? FindBestComparison(
        AnalyzedAudioSegment targetSegment,
        PremiereAudioTrack targetTrack,
        TrackAudioAnalysis targetAnalysis,
        IReadOnlyDictionary<int, PremiereAudioTrack> tracks,
        IReadOnlyList<TrackAudioAnalysis> analyses,
        IReadOnlyDictionary<int, PhraseIntervalIndex> phraseIndexes,
        DialogueProcessingPreset preset,
        float[] targetBuffer,
        float[] otherBuffer,
        CancellationToken cancellationToken)
    {
        ComparisonCandidate? bestPassingThresholds = null;
        ComparisonCandidate? bestObserved = null;
        var minimumComparisonSamples = AudioMath.MillisecondsToSamples(preset.MinimumSpeechMilliseconds);
        var maximumComparisonSamples = AudioMath.MillisecondsToSamples(
            BleedResolver.MaximumComparisonMilliseconds);

        foreach (var otherAnalysis in analyses)
        {
            if (otherAnalysis.TrackIndex == targetAnalysis.TrackIndex)
            {
                continue;
            }

            var otherTrack = tracks[otherAnalysis.TrackIndex];
            foreach (var phrase in phraseIndexes[otherAnalysis.TrackIndex].Overlapping(
                         targetSegment.TimelineStartSample,
                         targetSegment.TimelineEndSample))
            {
                var overlapStart = Math.Max(targetSegment.TimelineStartSample, phrase.CoreStartSample);
                var overlapEnd = Math.Min(targetSegment.TimelineEndSample, phrase.CoreEndSample);
                if (overlapEnd - overlapStart < minimumComparisonSamples)
                {
                    continue;
                }

                if (overlapEnd - overlapStart > maximumComparisonSamples)
                {
                    var center = overlapStart + ((overlapEnd - overlapStart) / 2);
                    overlapStart = center - (maximumComparisonSamples / 2);
                    overlapEnd = overlapStart + maximumComparisonSamples;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var sampleCount = pcmAccessor.ReadTimelineRangeInto(
                    targetTrack,
                    overlapStart,
                    overlapEnd,
                    targetBuffer,
                    cancellationToken);
                pcmAccessor.ReadTimelineRangeInto(
                    otherTrack,
                    overlapStart,
                    overlapEnd,
                    otherBuffer,
                    cancellationToken);
                var targetWaveform = targetBuffer.AsSpan(0, sampleCount);
                var otherWaveform = otherBuffer.AsSpan(0, sampleCount);
                var targetRms = WaveformCorrelation.RmsDbfs(targetWaveform);
                var otherRms = WaveformCorrelation.RmsDbfs(otherWaveform);
                var advantage = otherRms - targetRms;
                var correlation = WaveformCorrelation.FindBest(
                    targetWaveform,
                    otherWaveform,
                    preset.BleedMaximumLagMilliseconds);
                var residualToTarget = WaveformCorrelation.ResidualToTargetDb(
                    targetWaveform,
                    otherWaveform,
                    correlation.LagSamples16Khz);
                var evidence = new BleedEvidence(
                    otherAnalysis.TrackIndex,
                    advantage,
                    correlation.Correlation,
                    correlation.LagMilliseconds,
                    targetRms,
                    targetAnalysis.LearnedDirectVoiceRmsDbfs,
                    residualToTarget);
                var passesThresholds =
                    targetAnalysis.LearnedDirectVoiceRmsDbfs > AudioMath.SilenceDbfs &&
                    advantage >= preset.BleedOtherMicAdvantageDb &&
                    targetRms < targetAnalysis.LearnedDirectVoiceRmsDbfs &&
                    correlation.Correlation >= preset.BleedCorrelationThreshold &&
                    Math.Abs(correlation.LagMilliseconds) <= preset.BleedMaximumLagMilliseconds;
                var candidate = new ComparisonCandidate(
                    overlapStart,
                    overlapEnd,
                    passesThresholds && residualToTarget > BleedResolver.ConflictingResidualThresholdDb
                        ? CrossTrackShadowOutcome.ConflictingEvidence
                        : passesThresholds
                            ? CrossTrackShadowOutcome.LikelyBleed
                            : CrossTrackShadowOutcome.BelowThreshold,
                    evidence);

                if (IsBetter(candidate, bestObserved))
                {
                    bestObserved = candidate;
                }

                if (passesThresholds && IsBetter(candidate, bestPassingThresholds))
                {
                    bestPassingThresholds = candidate;
                }
            }
        }

        return bestPassingThresholds ?? bestObserved;
    }

    private static bool IsEnergyVadConflict(AnalyzedAudioSegment segment) =>
        segment.Status == AudioSegmentStatus.Ambiguous &&
        segment.Reason.StartsWith("ambiguous-energy-vad-conflict", StringComparison.Ordinal);

    private static bool IsBetter(ComparisonCandidate candidate, ComparisonCandidate? current)
    {
        if (current is null)
        {
            return true;
        }

        var correlationOrder = candidate.Evidence.WaveformCorrelation.CompareTo(
            current.Evidence.WaveformCorrelation);
        if (correlationOrder != 0)
        {
            return correlationOrder > 0;
        }

        var advantageOrder = candidate.Evidence.OtherMicAdvantageDb.CompareTo(
            current.Evidence.OtherMicAdvantageDb);
        if (advantageOrder != 0)
        {
            return advantageOrder > 0;
        }

        var lagOrder = Math.Abs(candidate.Evidence.LagMilliseconds).CompareTo(
            Math.Abs(current.Evidence.LagMilliseconds));
        return lagOrder != 0
            ? lagOrder < 0
            : candidate.Evidence.OtherTrackIndex < current.Evidence.OtherTrackIndex;
    }

    private sealed record ComparisonCandidate(
        long StartSample,
        long EndSample,
        CrossTrackShadowOutcome Outcome,
        BleedEvidence Evidence);

}
