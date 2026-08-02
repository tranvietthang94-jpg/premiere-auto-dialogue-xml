using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.ProjectModel;

namespace PremiereAutoDialogueXml.Audio.Analysis;

public sealed class BleedResolver(TimelinePcmAccessor pcmAccessor)
{
    private const int MaximumComparisonMilliseconds = 1_000;
    private const float ConflictingResidualThresholdDb = -10f;

    public IReadOnlyList<TrackAudioAnalysis> Resolve(
        PremiereSequence sequence,
        IReadOnlyList<TrackAudioAnalysis> analyses,
        DialogueProcessingPreset preset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(analyses);
        ArgumentNullException.ThrowIfNull(preset);

        var tracksByIndex = sequence.AudioTracks.ToDictionary(track => track.Index);
        var analysesByIndex = analyses.ToDictionary(analysis => analysis.TrackIndex);
        var resolved = new List<TrackAudioAnalysis>(analyses.Count);
        var comparisonBufferLength = checked((int)AudioMath.MillisecondsToSamples(MaximumComparisonMilliseconds));
        var targetBuffer = new float[comparisonBufferLength];
        var otherBuffer = new float[comparisonBufferLength];

        foreach (var targetAnalysis in analyses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetTrack = tracksByIndex[targetAnalysis.TrackIndex];
            var segments = new List<AnalyzedAudioSegment>(targetAnalysis.Segments.Count);

            foreach (var segment in targetAnalysis.Segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsBleedCandidate(segment) ||
                    targetAnalysis.LearnedDirectVoiceRmsDbfs <= AudioMath.SilenceDbfs)
                {
                    segments.Add(segment);
                    continue;
                }

                var candidate = FindBestEvidence(
                    segment,
                    targetTrack,
                    targetAnalysis,
                    tracksByIndex,
                    analysesByIndex,
                    preset,
                    targetBuffer,
                    otherBuffer,
                    cancellationToken);

                if (candidate is null)
                {
                    segments.Add(segment);
                }
                else if (candidate.HasConflictingResidual)
                {
                    segments.Add(segment with
                    {
                        Status = AudioSegmentStatus.Ambiguous,
                        Reason = "conflicting-direct-and-bleed-evidence",
                        BleedEvidence = candidate.Evidence
                    });
                }
                else
                {
                    segments.Add(segment with
                    {
                        Status = AudioSegmentStatus.Bleed,
                        PhraseId = null,
                        GainDb = null,
                        Reason = $"confirmed-bleed-from-track-{candidate.Evidence.OtherTrackIndex}",
                        BleedEvidence = candidate.Evidence
                    });
                }
            }

            resolved.Add(targetAnalysis with { Segments = segments });
        }

        return resolved;
    }

    private BleedCandidate? FindBestEvidence(
        AnalyzedAudioSegment targetSegment,
        PremiereAudioTrack targetTrack,
        TrackAudioAnalysis targetAnalysis,
        IReadOnlyDictionary<int, PremiereAudioTrack> tracks,
        IReadOnlyDictionary<int, TrackAudioAnalysis> analyses,
        DialogueProcessingPreset preset,
        float[] targetBuffer,
        float[] otherBuffer,
        CancellationToken cancellationToken)
    {
        BleedCandidate? best = null;
        foreach (var otherAnalysis in analyses.Values)
        {
            if (otherAnalysis.TrackIndex == targetAnalysis.TrackIndex)
            {
                continue;
            }

            foreach (var otherPhrase in otherAnalysis.Phrases)
            {
                var overlapStart = Math.Max(targetSegment.TimelineStartSample, otherPhrase.CoreStartSample);
                var overlapEnd = Math.Min(targetSegment.TimelineEndSample, otherPhrase.CoreEndSample);
                if (overlapEnd - overlapStart < AudioMath.MillisecondsToSamples(preset.MinimumSpeechMilliseconds))
                {
                    continue;
                }

                var maximumSamples = AudioMath.MillisecondsToSamples(MaximumComparisonMilliseconds);
                if (overlapEnd - overlapStart > maximumSamples)
                {
                    var center = overlapStart + ((overlapEnd - overlapStart) / 2);
                    overlapStart = center - (maximumSamples / 2);
                    overlapEnd = overlapStart + maximumSamples;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var sampleCount = pcmAccessor.ReadTimelineRangeInto(
                    targetTrack,
                    overlapStart,
                    overlapEnd,
                    targetBuffer,
                    cancellationToken);
                pcmAccessor.ReadTimelineRangeInto(
                    tracks[otherAnalysis.TrackIndex],
                    overlapStart,
                    overlapEnd,
                    otherBuffer,
                    cancellationToken);
                var targetWaveform = targetBuffer.AsSpan(0, sampleCount);
                var otherWaveform = otherBuffer.AsSpan(0, sampleCount);
                var targetRms = WaveformCorrelation.RmsDbfs(targetWaveform);
                var otherRms = WaveformCorrelation.RmsDbfs(otherWaveform);
                var advantage = otherRms - targetRms;
                if (advantage < preset.BleedOtherMicAdvantageDb ||
                    targetRms >= targetAnalysis.LearnedDirectVoiceRmsDbfs)
                {
                    continue;
                }

                var correlation = WaveformCorrelation.FindBest(
                    targetWaveform,
                    otherWaveform,
                    preset.BleedMaximumLagMilliseconds);
                if (correlation.Correlation < preset.BleedCorrelationThreshold ||
                    Math.Abs(correlation.LagMilliseconds) > preset.BleedMaximumLagMilliseconds)
                {
                    continue;
                }

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
                var candidate = new BleedCandidate(
                    evidence,
                    residualToTarget > ConflictingResidualThresholdDb);
                if (best is null ||
                    candidate.Evidence.WaveformCorrelation > best.Evidence.WaveformCorrelation)
                {
                    best = candidate;
                }
            }
        }

        return best;
    }

    private static bool IsBleedCandidate(AnalyzedAudioSegment segment) =>
        segment.Reason is "confirmed-direct-speech" or "ambiguous-independent";

    private sealed record BleedCandidate(BleedEvidence Evidence, bool HasConflictingResidual);
}
