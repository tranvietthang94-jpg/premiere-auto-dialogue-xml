using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.ProjectModel;

namespace PremiereAutoDialogueXml.Audio.Analysis;

public sealed class CalibratedBleedShadowScorer(TimelinePcmAccessor pcmAccessor)
{
    public CalibratedBleedProjectShadow Build(
        PremiereSequence sequence,
        IReadOnlyList<TrackAudioAnalysis> analyses,
        DialogueProcessingPreset preset,
        DirectionalBleedCalibrationShadow calibration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(analyses);
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(calibration);
        var presetIssues = preset.Validate();
        if (presetIssues.Count > 0)
        {
            throw new ArgumentException(
                $"Preset scorer bleed không hợp lệ: {string.Join(", ", presetIssues.Select(issue => issue.Code))}",
                nameof(preset));
        }

        var expectedCalibrationPolicy = DirectionalBleedCalibrationPolicy.From(preset);
        if (calibration.Policy != expectedCalibrationPolicy)
        {
            throw new InvalidDataException("Scorer bleed nhận calibration sai policy provenance.");
        }

        var scoringPolicy = CalibratedBleedScoringPolicy.From(preset, calibration.Policy);
        var tracksByIndex = sequence.AudioTracks.ToDictionary(track => track.Index);
        var analysesByIndex = analyses.ToDictionary(analysis => analysis.TrackIndex);
        if (tracksByIndex.Count != analysesByIndex.Count ||
            tracksByIndex.Keys.Any(index => !analysesByIndex.ContainsKey(index)))
        {
            throw new InvalidDataException("Scorer bleed không cùng tập track giữa sequence và analysis.");
        }

        var stableFingerprintsByTarget = calibration.Fingerprints
            .Where(fingerprint => fingerprint.Status == DirectionalBleedCalibrationStatus.Stable)
            .GroupBy(fingerprint => fingerprint.TargetTrackIndex)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(fingerprint => fingerprint.SourceTrackIndex).ToArray());
        var stableSourceTrackIndexes = stableFingerprintsByTarget.Values
            .SelectMany(fingerprints => fingerprints)
            .Select(fingerprint => fingerprint.SourceTrackIndex)
            .Distinct()
            .ToHashSet();
        var sourceIndexes = analysesByIndex
            .Where(item => stableSourceTrackIndexes.Contains(item.Key))
            .ToDictionary(
                item => item.Key,
                item => SourceScoringIndex.Create(item.Value));
        var maximumWindowSamples = checked((int)AudioMath.MillisecondsToSamples(
            scoringPolicy.MaximumWindowMilliseconds));
        var targetBuffer = new float[maximumWindowSamples];
        var sourceBuffer = new float[maximumWindowSamples];
        var candidates = new List<CalibratedBleedCandidateEvidence>();

        foreach (var targetAnalysis in analyses.OrderBy(analysis => analysis.TrackIndex))
        {
            var targetTrack = tracksByIndex[targetAnalysis.TrackIndex];
            stableFingerprintsByTarget.TryGetValue(targetAnalysis.TrackIndex, out var fingerprints);
            foreach (var segment in targetAnalysis.Segments
                         .Where(IsCandidate)
                         .OrderBy(segment => segment.TimelineStartSample)
                         .ThenBy(segment => segment.TimelineEndSample)
                         .ThenBy(segment => segment.SourceClipId, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                candidates.Add(ScoreCandidate(
                    segment,
                    targetTrack,
                    fingerprints ?? [],
                    tracksByIndex,
                    sourceIndexes,
                    scoringPolicy,
                    sourceBuffer,
                    targetBuffer,
                    cancellationToken));
            }
        }

        return new(calibration, scoringPolicy, candidates);
    }

    private CalibratedBleedCandidateEvidence ScoreCandidate(
        AnalyzedAudioSegment segment,
        PremiereAudioTrack targetTrack,
        IReadOnlyList<DirectionalBleedFingerprint> fingerprints,
        IReadOnlyDictionary<int, PremiereAudioTrack> tracks,
        IReadOnlyDictionary<int, SourceScoringIndex> sourceIndexes,
        CalibratedBleedScoringPolicy policy,
        float[] sourceBuffer,
        float[] targetBuffer,
        CancellationToken cancellationToken)
    {
        var pairScores = new List<PairScore>(fingerprints.Count);
        foreach (var fingerprint in fingerprints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var score = ScorePair(
                segment,
                targetTrack,
                tracks[fingerprint.SourceTrackIndex],
                sourceIndexes[fingerprint.SourceTrackIndex],
                fingerprint,
                policy,
                sourceBuffer,
                targetBuffer,
                cancellationToken);
            if (score is not null)
            {
                pairScores.Add(score);
            }
        }

        if (pairScores.Count == 0)
        {
            return NoCalibration(segment, policy, "no-stable-overlapping-calibration");
        }

        var selected = pairScores
            .OrderByDescending(score => OutcomePriority(score.Outcome))
            .ThenByDescending(score => score.ConflictingWindowCount)
            .ThenByDescending(score => score.PassingWindowCount)
            .ThenByDescending(score => score.EvaluatedWindowCount == 0
                ? 0
                : score.PassingWindowCount / (double)score.EvaluatedWindowCount)
            .ThenBy(score => score.SourceTrackIndex)
            .First();
        return new(
            segment.TrackIndex,
            segment.SourceClipId,
            segment.TimelineStartSample,
            segment.TimelineEndSample,
            segment.Status,
            segment.Reason,
            segment.Status,
            segment.Reason,
            IsEnabled(segment.Status),
            selected.Outcome,
            selected.OutcomeReason,
            selected.SourceTrackIndex,
            selected.FingerprintEvidenceSha256,
            selected.AvailableWindowCount,
            selected.EvaluatedWindowCount,
            selected.PassingWindowCount,
            selected.ConflictingWindowCount,
            selected.WindowEvidenceSha256,
            selected.WindowSamples);
    }

    private PairScore? ScorePair(
        AnalyzedAudioSegment segment,
        PremiereAudioTrack targetTrack,
        PremiereAudioTrack sourceTrack,
        SourceScoringIndex sourceIndex,
        DirectionalBleedFingerprint fingerprint,
        CalibratedBleedScoringPolicy policy,
        float[] sourceBuffer,
        float[] targetBuffer,
        CancellationToken cancellationToken)
    {
        var selectedWindows = new List<RankedWindow>(policy.MaximumRetainedWindowCount);
        var availableWindowCount = 0;
        foreach (var phrase in sourceIndex.FindOverlapping(
                     segment.TimelineStartSample,
                     segment.TimelineEndSample))
        {
            var overlapStart = Math.Max(phrase.CoreStartSample, segment.TimelineStartSample);
            var overlapEnd = Math.Min(phrase.CoreEndSample, segment.TimelineEndSample);
            var length = overlapEnd - overlapStart;
            var minimumSamples = AudioMath.MillisecondsToSamples(policy.MinimumWindowMilliseconds);
            if (length < minimumSamples)
            {
                continue;
            }

            var maximumSamples = AudioMath.MillisecondsToSamples(policy.MaximumWindowMilliseconds);
            var windowCount = checked((int)((length + maximumSamples - 1) / maximumSamples));
            for (var position = 0; position < windowCount; position++)
            {
                var start = overlapStart + ((length * position) / windowCount);
                var end = overlapStart + ((length * (position + 1)) / windowCount);
                if (end - start < minimumSamples ||
                    !sourceIndex.HasConfirmedSpeechCoverage(phrase.Id, start, end))
                {
                    continue;
                }

                availableWindowCount++;
                var range = new ScoringWindowRange(phrase.Id, start, end);
                RetainBounded(
                    selectedWindows,
                    new(StablePriority(range), range),
                    policy.MaximumRetainedWindowCount);
            }
        }

        if (availableWindowCount == 0)
        {
            return null;
        }

        var windows = selectedWindows
            .Select(item => item.Range)
            .OrderBy(range => range.StartSample)
            .ThenBy(range => range.EndSample)
            .ThenBy(range => range.SourcePhraseId, StringComparer.Ordinal)
            .ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHeader(hash, segment, sourceTrack.Index, fingerprint, policy, availableWindowCount);
        var evidence = new List<CalibratedBleedWindowEvidence>(windows.Length);
        foreach (var window in windows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            evidence.Add(MeasureWindow(
                window,
                targetTrack,
                sourceTrack,
                fingerprint,
                policy,
                sourceBuffer,
                targetBuffer,
                hash,
                cancellationToken));
        }

        var passing = evidence.Count(item => item.Disposition == CalibratedBleedWindowDisposition.Pass);
        var conflicting = evidence.Count(item => item.Disposition is
            CalibratedBleedWindowDisposition.ConflictingResidual or
            CalibratedBleedWindowDisposition.PolarityInverted);
        CalibratedBleedShadowOutcome outcome;
        string reason;
        if (conflicting > 0 || (segment.Status == AudioSegmentStatus.Speech && passing > 0))
        {
            outcome = CalibratedBleedShadowOutcome.ConflictingEvidence;
            reason = segment.Status == AudioSegmentStatus.Speech && passing > 0
                ? "baseline-direct-speech-conflicts-with-calibrated-bleed"
                : "multi-window-direct-or-polarity-conflict";
        }
        else if (passing >= policy.MinimumPassingWindowCount &&
                 passing / (double)evidence.Count >= policy.MinimumPassingWindowRatio)
        {
            outcome = CalibratedBleedShadowOutcome.CalibratedLikelyBleed;
            reason = "multi-window-matches-directional-fingerprint";
        }
        else
        {
            outcome = CalibratedBleedShadowOutcome.BelowCalibratedThreshold;
            reason = "multi-window-support-below-threshold";
        }

        return new(
            sourceTrack.Index,
            fingerprint.EvidenceSha256,
            availableWindowCount,
            evidence.Count,
            passing,
            conflicting,
            outcome,
            reason,
            Convert.ToHexString(hash.GetHashAndReset()),
            evidence);
    }

    private CalibratedBleedWindowEvidence MeasureWindow(
        ScoringWindowRange window,
        PremiereAudioTrack targetTrack,
        PremiereAudioTrack sourceTrack,
        DirectionalBleedFingerprint fingerprint,
        CalibratedBleedScoringPolicy policy,
        float[] sourceBuffer,
        float[] targetBuffer,
        IncrementalHash hash,
        CancellationToken cancellationToken)
    {
        if (!HasContinuousMedia(sourceTrack, window.StartSample, window.EndSample) ||
            !HasContinuousMedia(targetTrack, window.StartSample, window.EndSample))
        {
            return AppendWindow(hash, window, CalibratedBleedWindowDisposition.IncompleteMedia);
        }

        var sampleCount = pcmAccessor.ReadTimelineRangeInto(
            sourceTrack,
            window.StartSample,
            window.EndSample,
            sourceBuffer,
            cancellationToken);
        pcmAccessor.ReadTimelineRangeInto(
            targetTrack,
            window.StartSample,
            window.EndSample,
            targetBuffer,
            cancellationToken);
        var source = sourceBuffer.AsSpan(0, sampleCount);
        var target = targetBuffer.AsSpan(0, sampleCount);
        if (SamplePeakDbfs(source) >= policy.ClippingPeakDbfs ||
            SamplePeakDbfs(target) >= policy.ClippingPeakDbfs)
        {
            return AppendWindow(hash, window, CalibratedBleedWindowDisposition.Clipped);
        }

        var sourceRms = WaveformCorrelation.RmsDbfs(source);
        var targetRms = WaveformCorrelation.RmsDbfs(target);
        var advantage = sourceRms - targetRms;
        var correlation = WaveformCorrelation.FindBest(
            target,
            source,
            fingerprint.MedianLagMilliseconds is null
                ? 0
                : Math.Max(
                    0,
                    checked((int)Math.Ceiling(
                        Math.Abs(fingerprint.MedianLagMilliseconds.Value) +
                        policy.MaximumLagDeviationMilliseconds))));
        var transferScale = WaveformCorrelation.TransferScaleToTarget(
            target,
            source,
            correlation.LagSamples16Khz);
        var residual = WaveformCorrelation.ResidualToTargetDb(
            target,
            source,
            correlation.LagSamples16Khz);
        var disposition = Classify(
            advantage,
            correlation.Correlation,
            correlation.LagMilliseconds,
            transferScale,
            residual,
            fingerprint,
            policy);
        return AppendWindow(
            hash,
            window,
            disposition,
            advantage,
            correlation.Correlation,
            correlation.LagMilliseconds,
            transferScale,
            residual);
    }

    private static CalibratedBleedWindowDisposition Classify(
        float advantage,
        float correlation,
        int lagMilliseconds,
        float transferScale,
        float residual,
        DirectionalBleedFingerprint fingerprint,
        CalibratedBleedScoringPolicy policy)
    {
        if (correlation < policy.MinimumCorrelation ||
            fingerprint.MedianLagMilliseconds is not { } expectedLag ||
            fingerprint.MedianAttenuationDb is not { } expectedAttenuation ||
            Math.Abs(lagMilliseconds - expectedLag) > policy.MaximumLagDeviationMilliseconds ||
            Math.Abs(advantage - expectedAttenuation) > policy.MaximumAttenuationDeviationDb)
        {
            return CalibratedBleedWindowDisposition.BelowCalibration;
        }

        if (transferScale <= 0)
        {
            return CalibratedBleedWindowDisposition.PolarityInverted;
        }

        return residual > policy.MaximumResidualToTargetDb
            ? CalibratedBleedWindowDisposition.ConflictingResidual
            : CalibratedBleedWindowDisposition.Pass;
    }

    private static CalibratedBleedCandidateEvidence NoCalibration(
        AnalyzedAudioSegment segment,
        CalibratedBleedScoringPolicy policy,
        string reason)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendPolicy(hash, policy);
        AppendInt32(hash, segment.TrackIndex);
        AppendString(hash, segment.SourceClipId);
        AppendInt64(hash, segment.TimelineStartSample);
        AppendInt64(hash, segment.TimelineEndSample);
        return new(
            segment.TrackIndex,
            segment.SourceClipId,
            segment.TimelineStartSample,
            segment.TimelineEndSample,
            segment.Status,
            segment.Reason,
            segment.Status,
            segment.Reason,
            IsEnabled(segment.Status),
            CalibratedBleedShadowOutcome.NoCalibration,
            reason,
            null,
            null,
            0,
            0,
            0,
            0,
            Convert.ToHexString(hash.GetHashAndReset()),
            []);
    }

    private static CalibratedBleedWindowEvidence AppendWindow(
        IncrementalHash hash,
        ScoringWindowRange window,
        CalibratedBleedWindowDisposition disposition,
        float advantage = 0,
        float correlation = 0,
        int lagMilliseconds = 0,
        float transferScale = 0,
        float residual = 0)
    {
        var evidence = new CalibratedBleedWindowEvidence(
            window.SourcePhraseId,
            window.StartSample,
            window.EndSample,
            disposition,
            advantage,
            correlation,
            lagMilliseconds,
            transferScale,
            residual);
        AppendString(hash, evidence.SourcePhraseId);
        AppendInt64(hash, evidence.TimelineStartSample);
        AppendInt64(hash, evidence.TimelineEndSample);
        AppendInt32(hash, (int)evidence.Disposition);
        AppendSingle(hash, evidence.OtherMicAdvantageDb);
        AppendSingle(hash, evidence.WaveformCorrelation);
        AppendInt32(hash, evidence.LagMilliseconds);
        AppendSingle(hash, evidence.TransferScaleToTarget);
        AppendSingle(hash, evidence.ResidualToTargetDb);
        return evidence;
    }

    private static void AppendHeader(
        IncrementalHash hash,
        AnalyzedAudioSegment segment,
        int sourceTrackIndex,
        DirectionalBleedFingerprint fingerprint,
        CalibratedBleedScoringPolicy policy,
        int availableWindowCount)
    {
        AppendPolicy(hash, policy);
        AppendInt32(hash, segment.TrackIndex);
        AppendInt32(hash, sourceTrackIndex);
        AppendString(hash, segment.SourceClipId);
        AppendInt64(hash, segment.TimelineStartSample);
        AppendInt64(hash, segment.TimelineEndSample);
        AppendString(hash, fingerprint.EvidenceSha256);
        AppendInt32(hash, availableWindowCount);
    }

    private static void AppendPolicy(
        IncrementalHash hash,
        CalibratedBleedScoringPolicy policy)
    {
        AppendString(hash, policy.Version);
        AppendInt32(hash, policy.MaximumWindowMilliseconds);
        AppendInt32(hash, policy.MinimumWindowMilliseconds);
        AppendInt32(hash, policy.MinimumPassingWindowCount);
        AppendInt32(hash, policy.MaximumRetainedWindowCount);
        AppendDouble(hash, policy.MinimumPassingWindowRatio);
        AppendDouble(hash, policy.MaximumLagDeviationMilliseconds);
        AppendDouble(hash, policy.MaximumAttenuationDeviationDb);
        AppendDouble(hash, policy.ClippingPeakDbfs);
        AppendDouble(hash, policy.MinimumCorrelation);
        AppendDouble(hash, policy.MaximumResidualToTargetDb);
    }

    private static bool IsCandidate(AnalyzedAudioSegment segment) =>
        segment.Status is AudioSegmentStatus.Speech or AudioSegmentStatus.Ambiguous;

    private static bool IsEnabled(AudioSegmentStatus status) =>
        status is AudioSegmentStatus.Speech or AudioSegmentStatus.Ambiguous;

    private static int OutcomePriority(CalibratedBleedShadowOutcome outcome) => outcome switch
    {
        CalibratedBleedShadowOutcome.ConflictingEvidence => 3,
        CalibratedBleedShadowOutcome.CalibratedLikelyBleed => 2,
        CalibratedBleedShadowOutcome.BelowCalibratedThreshold => 1,
        _ => 0
    };

    private static bool HasContinuousMedia(
        PremiereAudioTrack track,
        long startSample,
        long endSample)
    {
        var cursor = startSample;
        foreach (var clip in track.Clips
                     .OrderBy(clip => clip.TimelineStartFrame)
                     .ThenBy(clip => clip.TimelineEndFrame)
                     .ThenBy(clip => clip.Id, StringComparer.Ordinal))
        {
            var clipStart = AudioMath.FramesToSamples(clip.TimelineStartFrame);
            var clipEnd = AudioMath.FramesToSamples(clip.TimelineEndFrame);
            if (clipEnd <= cursor)
            {
                continue;
            }

            if (clipStart > cursor)
            {
                return false;
            }

            var availableEnd = Math.Min(
                clipEnd,
                clipStart + (clip.SourceEndSample - clip.SourceStartSample));
            cursor = Math.Max(cursor, availableEnd);
            if (cursor >= endSample)
            {
                return true;
            }
        }

        return false;
    }

    private static float SamplePeakDbfs(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        return AudioMath.LinearToDbfs(peak);
    }

    private static void RetainBounded(
        List<RankedWindow> retained,
        RankedWindow candidate,
        int limit)
    {
        if (retained.Count < limit)
        {
            retained.Add(candidate);
            return;
        }

        var highestIndex = 0;
        for (var index = 1; index < retained.Count; index++)
        {
            if (retained[index].Priority > retained[highestIndex].Priority)
            {
                highestIndex = index;
            }
        }

        if (candidate.Priority < retained[highestIndex].Priority)
        {
            retained[highestIndex] = candidate;
        }
    }

    private static ulong StablePriority(ScoringWindowRange range)
    {
        const ulong offset = 14_695_981_039_346_656_037UL;
        const ulong prime = 1_099_511_628_211UL;
        var hash = offset;
        foreach (var value in Encoding.UTF8.GetBytes(
                     $"{range.SourcePhraseId}\n{range.StartSample}\n{range.EndSample}"))
        {
            hash ^= value;
            hash *= prime;
        }

        return hash;
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        hash.AppendData(buffer);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        hash.AppendData(buffer);
    }

    private static void AppendSingle(IncrementalHash hash, float value) =>
        AppendInt32(hash, BitConverter.SingleToInt32Bits(value));

    private static void AppendDouble(IncrementalHash hash, double value) =>
        AppendInt64(hash, BitConverter.DoubleToInt64Bits(value));

    private readonly record struct ScoringWindowRange(
        string SourcePhraseId,
        long StartSample,
        long EndSample);

    private readonly record struct RankedWindow(ulong Priority, ScoringWindowRange Range);

    private sealed record PairScore(
        int SourceTrackIndex,
        string FingerprintEvidenceSha256,
        int AvailableWindowCount,
        int EvaluatedWindowCount,
        int PassingWindowCount,
        int ConflictingWindowCount,
        CalibratedBleedShadowOutcome Outcome,
        string OutcomeReason,
        string WindowEvidenceSha256,
        IReadOnlyList<CalibratedBleedWindowEvidence> WindowSamples);

    private sealed class SourceScoringIndex(
        DialoguePhrase[] phrases,
        long[] prefixMaximumEnds,
        IReadOnlyDictionary<string, AnalyzedAudioSegment[]> speechSegmentsByPhrase)
    {
        public static SourceScoringIndex Create(TrackAudioAnalysis analysis)
        {
            var phrases = analysis.Phrases
                .OrderBy(phrase => phrase.CoreStartSample)
                .ThenBy(phrase => phrase.CoreEndSample)
                .ThenBy(phrase => phrase.Id, StringComparer.Ordinal)
                .ToArray();
            var prefixMaximumEnds = new long[phrases.Length];
            var maximumEnd = long.MinValue;
            for (var index = 0; index < phrases.Length; index++)
            {
                maximumEnd = Math.Max(maximumEnd, phrases[index].CoreEndSample);
                prefixMaximumEnds[index] = maximumEnd;
            }

            var speechSegmentsByPhrase = analysis.Segments
                .Where(segment =>
                    segment.Status == AudioSegmentStatus.Speech &&
                    segment.PhraseId is not null)
                .GroupBy(segment => segment.PhraseId!, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderBy(segment => segment.TimelineStartSample)
                        .ThenBy(segment => segment.TimelineEndSample)
                        .ToArray(),
                    StringComparer.Ordinal);
            return new(phrases, prefixMaximumEnds, speechSegmentsByPhrase);
        }

        public IEnumerable<DialoguePhrase> FindOverlapping(long startSample, long endSample)
        {
            var lower = 0;
            var upper = prefixMaximumEnds.Length;
            while (lower < upper)
            {
                var middle = lower + ((upper - lower) / 2);
                if (prefixMaximumEnds[middle] <= startSample)
                {
                    lower = middle + 1;
                }
                else
                {
                    upper = middle;
                }
            }

            for (var index = lower; index < phrases.Length; index++)
            {
                var phrase = phrases[index];
                if (phrase.CoreStartSample >= endSample)
                {
                    yield break;
                }

                if (phrase.CoreEndSample > startSample)
                {
                    yield return phrase;
                }
            }
        }

        public bool HasConfirmedSpeechCoverage(
            string phraseId,
            long startSample,
            long endSample)
        {
            if (!speechSegmentsByPhrase.TryGetValue(phraseId, out var segments))
            {
                return false;
            }

            var cursor = startSample;
            foreach (var segment in segments)
            {
                if (segment.TimelineEndSample <= cursor)
                {
                    continue;
                }

                if (segment.TimelineStartSample > cursor)
                {
                    return false;
                }

                cursor = Math.Max(cursor, segment.TimelineEndSample);
                if (cursor >= endSample)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
