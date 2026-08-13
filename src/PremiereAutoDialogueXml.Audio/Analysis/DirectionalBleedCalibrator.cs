using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.ProjectModel;

namespace PremiereAutoDialogueXml.Audio.Analysis;

public sealed class DirectionalBleedCalibrator(TimelinePcmAccessor pcmAccessor)
{
    public DirectionalBleedCalibrationShadow Build(
        PremiereSequence sequence,
        IReadOnlyList<TrackAudioAnalysis> analyses,
        DialogueProcessingPreset preset,
        DirectionalBleedCalibrationExclusion? exclusion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(analyses);
        ArgumentNullException.ThrowIfNull(preset);
        ValidateExclusion(exclusion);
        var presetIssues = preset.Validate();
        if (presetIssues.Count > 0)
        {
            throw new ArgumentException(
                $"Preset calibration bleed không hợp lệ: {string.Join(", ", presetIssues.Select(issue => issue.Code))}",
                nameof(preset));
        }

        var policy = DirectionalBleedCalibrationPolicy.From(preset);
        var tracks = sequence.AudioTracks
            .OrderBy(track => track.Index)
            .ToArray();
        var tracksByIndex = tracks.ToDictionary(track => track.Index);
        var analysesByIndex = analyses.ToDictionary(analysis => analysis.TrackIndex);
        if (tracksByIndex.Count != analysesByIndex.Count ||
            tracksByIndex.Keys.Any(index => !analysesByIndex.ContainsKey(index)))
        {
            throw new InvalidDataException("Calibration bleed không cùng tập track giữa sequence và analysis.");
        }

        if (exclusion is not null && !tracksByIndex.ContainsKey(exclusion.TargetTrackIndex))
        {
            throw new InvalidDataException("Vùng leave-one-region-out trỏ tới track không tồn tại.");
        }

        var maximumWindowSamples = checked((int)AudioMath.MillisecondsToSamples(
            policy.MaximumAnchorWindowMilliseconds));
        var targetBuffer = new float[maximumWindowSamples];
        var sourceBuffer = new float[maximumWindowSamples];
        var fingerprints = new List<DirectionalBleedFingerprint>(
            checked(tracks.Length * Math.Max(0, tracks.Length - 1)));

        foreach (var sourceTrack in tracks)
        {
            foreach (var targetTrack in tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sourceTrack.Index == targetTrack.Index)
                {
                    continue;
                }

                fingerprints.Add(BuildPair(
                    sourceTrack,
                    targetTrack,
                    analysesByIndex[sourceTrack.Index],
                    analysesByIndex[targetTrack.Index],
                    preset,
                    policy,
                    exclusion,
                    sourceBuffer,
                    targetBuffer,
                    cancellationToken));
            }
        }

        return new(policy, fingerprints);
    }

    private DirectionalBleedFingerprint BuildPair(
        PremiereAudioTrack sourceTrack,
        PremiereAudioTrack targetTrack,
        TrackAudioAnalysis sourceAnalysis,
        TrackAudioAnalysis targetAnalysis,
        DialogueProcessingPreset preset,
        DirectionalBleedCalibrationPolicy policy,
        DirectionalBleedCalibrationExclusion? exclusion,
        float[] sourceBuffer,
        float[] targetBuffer,
        CancellationToken cancellationToken)
    {
        var phrases = sourceAnalysis.Phrases
            .OrderBy(phrase => phrase.CoreStartSample)
            .ThenBy(phrase => phrase.CoreEndSample)
            .ThenBy(phrase => phrase.Id, StringComparer.Ordinal)
            .ToArray();
        var targetSegments = targetAnalysis.Segments
            .Where(segment =>
                segment.Status == AudioSegmentStatus.Bleed &&
                segment.BleedEvidence?.OtherTrackIndex == sourceTrack.Index)
            .OrderBy(segment => segment.TimelineStartSample)
            .ThenBy(segment => segment.TimelineEndSample)
            .ThenBy(segment => segment.SourceClipId, StringComparer.Ordinal)
            .ToArray();
        var retainedAnchors = new List<RankedAnchor>(policy.MaximumRetainedAnchorCount);
        using var evidenceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendPairHeader(evidenceHash, sourceTrack.Index, targetTrack.Index, policy);
        var rejections = new MutableRejectionCounts();
        var acceptedAnchorCount = 0;

        foreach (var phrase in phrases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matchingSegment = FindBestTargetSegment(phrase, targetSegments);
            if (matchingSegment is null)
            {
                rejections.NoMatchingBaselineBleed++;
                AppendRejected(evidenceHash, phrase, AnchorDisposition.NoMatchingBaselineBleed);
                continue;
            }

            var overlapStart = Math.Max(phrase.CoreStartSample, matchingSegment.TimelineStartSample);
            var overlapEnd = Math.Min(phrase.CoreEndSample, matchingSegment.TimelineEndSample);
            var minimumSamples = AudioMath.MillisecondsToSamples(preset.MinimumSpeechMilliseconds);
            if (overlapEnd - overlapStart < minimumSamples)
            {
                rejections.TooShort++;
                AppendRejected(evidenceHash, phrase, AnchorDisposition.TooShort);
                continue;
            }

            var maximumSamples = AudioMath.MillisecondsToSamples(policy.MaximumAnchorWindowMilliseconds);
            if (overlapEnd - overlapStart > maximumSamples)
            {
                var center = overlapStart + ((overlapEnd - overlapStart) / 2);
                overlapStart = center - (maximumSamples / 2);
                overlapEnd = overlapStart + maximumSamples;
            }

            if (!HasConfirmedSourceSpeechCoverage(
                    sourceAnalysis,
                    phrase.Id,
                    overlapStart,
                    overlapEnd))
            {
                rejections.NoConfirmedSourceSpeech++;
                AppendRejected(
                    evidenceHash,
                    phrase,
                    AnchorDisposition.NoConfirmedSourceSpeech,
                    overlapStart,
                    overlapEnd);
                continue;
            }

            if (exclusion is not null &&
                exclusion.TargetTrackIndex == targetTrack.Index &&
                overlapStart < exclusion.TimelineEndSample &&
                overlapEnd > exclusion.TimelineStartSample)
            {
                rejections.ExcludedCandidateRegion++;
                AppendRejected(
                    evidenceHash,
                    phrase,
                    AnchorDisposition.ExcludedCandidateRegion,
                    overlapStart,
                    overlapEnd);
                continue;
            }

            if (!HasContinuousMedia(sourceTrack, overlapStart, overlapEnd) ||
                !HasContinuousMedia(targetTrack, overlapStart, overlapEnd))
            {
                rejections.IncompleteMedia++;
                AppendRejected(
                    evidenceHash,
                    phrase,
                    AnchorDisposition.IncompleteMedia,
                    overlapStart,
                    overlapEnd);
                continue;
            }

            var sampleCount = pcmAccessor.ReadTimelineRangeInto(
                sourceTrack,
                overlapStart,
                overlapEnd,
                sourceBuffer,
                cancellationToken);
            pcmAccessor.ReadTimelineRangeInto(
                targetTrack,
                overlapStart,
                overlapEnd,
                targetBuffer,
                cancellationToken);
            var sourceWaveform = sourceBuffer.AsSpan(0, sampleCount);
            var targetWaveform = targetBuffer.AsSpan(0, sampleCount);
            if (SamplePeakDbfs(sourceWaveform) >= policy.ClippingPeakDbfs ||
                SamplePeakDbfs(targetWaveform) >= policy.ClippingPeakDbfs)
            {
                rejections.Clipped++;
                AppendRejected(
                    evidenceHash,
                    phrase,
                    AnchorDisposition.Clipped,
                    overlapStart,
                    overlapEnd);
                continue;
            }

            var sourceRms = WaveformCorrelation.RmsDbfs(sourceWaveform);
            var targetRms = WaveformCorrelation.RmsDbfs(targetWaveform);
            var advantage = sourceRms - targetRms;
            var correlation = WaveformCorrelation.FindBest(
                targetWaveform,
                sourceWaveform,
                preset.BleedMaximumLagMilliseconds);
            var residual = WaveformCorrelation.ResidualToTargetDb(
                targetWaveform,
                sourceWaveform,
                correlation.LagSamples16Khz);
            var transferScale = WaveformCorrelation.TransferScaleToTarget(
                targetWaveform,
                sourceWaveform,
                correlation.LagSamples16Khz);
            var disposition = Classify(
                advantage,
                correlation.Correlation,
                correlation.LagMilliseconds,
                transferScale,
                residual,
                policy);
            var anchor = new DirectionalBleedAnchorEvidence(
                phrase.Id,
                matchingSegment.SourceClipId,
                overlapStart,
                overlapEnd,
                advantage,
                correlation.Correlation,
                correlation.LagMilliseconds,
                transferScale,
                residual);
            AppendMeasured(evidenceHash, anchor, disposition);
            if (disposition != AnchorDisposition.Accepted)
            {
                rejections.Add(disposition);
                continue;
            }

            acceptedAnchorCount++;
            RetainBounded(
                retainedAnchors,
                new(StablePriority(anchor), anchor),
                policy.MaximumRetainedAnchorCount);
        }

        var immutableRejections = rejections.ToImmutable();
        if (acceptedAnchorCount + immutableRejections.Total != phrases.Length)
        {
            throw new InvalidDataException("Calibration bleed không khép kín provenance anchor.");
        }

        var hash = Convert.ToHexString(evidenceHash.GetHashAndReset());
        return Summarize(
            sourceTrack.Index,
            targetTrack.Index,
            phrases.Length,
            acceptedAnchorCount,
            retainedAnchors,
            hash,
            immutableRejections,
            policy);
    }

    private static DirectionalBleedFingerprint Summarize(
        int sourceTrackIndex,
        int targetTrackIndex,
        int sourcePhraseCount,
        int acceptedAnchorCount,
        IReadOnlyList<RankedAnchor> retainedAnchors,
        string evidenceSha256,
        DirectionalBleedCalibrationRejectionCounts rejections,
        DirectionalBleedCalibrationPolicy policy)
    {
        var retained = retainedAnchors
            .Select(item => item.Anchor)
            .OrderBy(anchor => anchor.TimelineStartSample)
            .ThenBy(anchor => anchor.TimelineEndSample)
            .ThenBy(anchor => anchor.SourcePhraseId, StringComparer.Ordinal)
            .ToArray();
        var samples = retained.Take(policy.MaximumAnchorSampleCount).ToArray();
        if (retained.Length < policy.MinimumAnchorCount)
        {
            return Fingerprint(
                sourceTrackIndex,
                targetTrackIndex,
                DirectionalBleedCalibrationStatus.InsufficientSupport,
                "insufficient-eligible-anchors",
                sourcePhraseCount,
                acceptedAnchorCount,
                retained,
                retained,
                evidenceSha256,
                rejections,
                samples);
        }

        var medianLag = Median(retained.Select(anchor => (float)anchor.LagMilliseconds));
        var medianAttenuation = Median(retained.Select(anchor => anchor.OtherMicAdvantageDb));
        var consistent = retained
            .Where(anchor =>
                Math.Abs(anchor.LagMilliseconds - medianLag) <=
                    policy.MaximumLagDeviationMilliseconds &&
                Math.Abs(anchor.OtherMicAdvantageDb - medianAttenuation) <=
                    policy.MaximumAttenuationDeviationDb)
            .ToArray();
        if (consistent.Length < policy.MinimumAnchorCount)
        {
            return Fingerprint(
                sourceTrackIndex,
                targetTrackIndex,
                DirectionalBleedCalibrationStatus.UnstableFingerprint,
                "insufficient-consistent-anchors",
                sourcePhraseCount,
                acceptedAnchorCount,
                retained,
                consistent,
                evidenceSha256,
                rejections,
                samples);
        }

        if (consistent.Length / (double)retained.Length < policy.MinimumConsistentSupportRatio)
        {
            return Fingerprint(
                sourceTrackIndex,
                targetTrackIndex,
                DirectionalBleedCalibrationStatus.UnstableFingerprint,
                "consistent-support-ratio-below-threshold",
                sourcePhraseCount,
                acceptedAnchorCount,
                retained,
                consistent,
                evidenceSha256,
                rejections,
                samples);
        }

        return Fingerprint(
            sourceTrackIndex,
            targetTrackIndex,
            DirectionalBleedCalibrationStatus.Stable,
            "stable-directional-fingerprint",
            sourcePhraseCount,
            acceptedAnchorCount,
            retained,
            consistent,
            evidenceSha256,
            rejections,
            samples);
    }

    private static DirectionalBleedFingerprint Fingerprint(
        int sourceTrackIndex,
        int targetTrackIndex,
        DirectionalBleedCalibrationStatus status,
        string reason,
        int sourcePhraseCount,
        int acceptedAnchorCount,
        IReadOnlyList<DirectionalBleedAnchorEvidence> retained,
        IReadOnlyList<DirectionalBleedAnchorEvidence> consistent,
        string evidenceSha256,
        DirectionalBleedCalibrationRejectionCounts rejections,
        IReadOnlyList<DirectionalBleedAnchorEvidence> samples)
    {
        var statistics = consistent.Count > 0 ? consistent : retained;
        return new(
            sourceTrackIndex,
            targetTrackIndex,
            status,
            reason,
            sourcePhraseCount,
            acceptedAnchorCount,
            retained.Count,
            consistent.Count,
            retained.Count - consistent.Count,
            statistics.Count == 0
                ? null
                : Median(statistics.Select(anchor => (float)anchor.LagMilliseconds)),
            statistics.Count == 0
                ? null
                : Spread(statistics.Select(anchor => (float)anchor.LagMilliseconds)),
            statistics.Count == 0
                ? null
                : Median(statistics.Select(anchor => anchor.OtherMicAdvantageDb)),
            statistics.Count == 0
                ? null
                : Spread(statistics.Select(anchor => anchor.OtherMicAdvantageDb)),
            statistics.Count == 0
                ? null
                : Median(statistics.Select(anchor => anchor.WaveformCorrelation)),
            statistics.Count == 0
                ? null
                : statistics.Max(anchor => anchor.ResidualToTargetDb),
            evidenceSha256,
            rejections,
            samples);
    }

    private static AnalyzedAudioSegment? FindBestTargetSegment(
        DialoguePhrase phrase,
        IReadOnlyList<AnalyzedAudioSegment> targetSegments)
    {
        AnalyzedAudioSegment? best = null;
        var bestOverlap = 0L;
        foreach (var segment in targetSegments)
        {
            if (segment.TimelineEndSample <= phrase.CoreStartSample)
            {
                continue;
            }

            if (segment.TimelineStartSample >= phrase.CoreEndSample)
            {
                break;
            }

            var overlap = Math.Min(segment.TimelineEndSample, phrase.CoreEndSample) -
                          Math.Max(segment.TimelineStartSample, phrase.CoreStartSample);
            if (overlap > bestOverlap)
            {
                best = segment;
                bestOverlap = overlap;
            }
        }

        return best;
    }

    private static bool HasConfirmedSourceSpeechCoverage(
        TrackAudioAnalysis sourceAnalysis,
        string phraseId,
        long startSample,
        long endSample)
    {
        var cursor = startSample;
        foreach (var segment in sourceAnalysis.Segments
                     .Where(segment =>
                         segment.Status == AudioSegmentStatus.Speech &&
                         string.Equals(segment.PhraseId, phraseId, StringComparison.Ordinal))
                     .OrderBy(segment => segment.TimelineStartSample)
                     .ThenBy(segment => segment.TimelineEndSample))
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

    private static AnchorDisposition Classify(
        float advantage,
        float correlation,
        int lagMilliseconds,
        float transferScale,
        float residual,
        DirectionalBleedCalibrationPolicy policy)
    {
        if (advantage < policy.MinimumOtherMicAdvantageDb)
        {
            return AnchorDisposition.BelowAdvantage;
        }

        if (correlation < policy.MinimumCorrelation)
        {
            return AnchorDisposition.BelowCorrelation;
        }

        if (Math.Abs(lagMilliseconds) > policy.MaximumLagMilliseconds)
        {
            return AnchorDisposition.LagOutOfRange;
        }

        if (transferScale <= 0)
        {
            return AnchorDisposition.PolarityInverted;
        }

        return residual > policy.MaximumResidualToTargetDb
            ? AnchorDisposition.ConflictingResidual
            : AnchorDisposition.Accepted;
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

    private static float Median(IEnumerable<float> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0)
        {
            throw new ArgumentException("Không thể tính median trên tập rỗng.", nameof(values));
        }

        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2f
            : ordered[middle];
    }

    private static float Spread(IEnumerable<float> values)
    {
        var array = values.ToArray();
        return array.Length == 0 ? 0 : array.Max() - array.Min();
    }

    private static void RetainBounded(
        List<RankedAnchor> retained,
        RankedAnchor candidate,
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

    private static ulong StablePriority(DirectionalBleedAnchorEvidence anchor)
    {
        const ulong offset = 14_695_981_039_346_656_037UL;
        const ulong prime = 1_099_511_628_211UL;
        var hash = offset;
        foreach (var value in Encoding.UTF8.GetBytes(
                     $"{anchor.SourcePhraseId}\n{anchor.TargetSourceClipId}\n" +
                     $"{anchor.TimelineStartSample}\n{anchor.TimelineEndSample}"))
        {
            hash ^= value;
            hash *= prime;
        }

        return hash;
    }

    private static void AppendPairHeader(
        IncrementalHash hash,
        int sourceTrackIndex,
        int targetTrackIndex,
        DirectionalBleedCalibrationPolicy policy)
    {
        AppendInt32(hash, sourceTrackIndex);
        AppendInt32(hash, targetTrackIndex);
        AppendString(hash, policy.Version);
        AppendInt32(hash, policy.MaximumAnchorWindowMilliseconds);
        AppendInt32(hash, policy.MinimumAnchorCount);
        AppendInt32(hash, policy.MaximumRetainedAnchorCount);
        AppendInt32(hash, policy.MaximumAnchorSampleCount);
        AppendDouble(hash, policy.MinimumConsistentSupportRatio);
        AppendDouble(hash, policy.MaximumLagDeviationMilliseconds);
        AppendDouble(hash, policy.MaximumAttenuationDeviationDb);
        AppendDouble(hash, policy.ClippingPeakDbfs);
        AppendDouble(hash, policy.MinimumOtherMicAdvantageDb);
        AppendDouble(hash, policy.MinimumCorrelation);
        AppendInt32(hash, policy.MaximumLagMilliseconds);
        AppendDouble(hash, policy.MaximumResidualToTargetDb);
    }

    private static void AppendRejected(
        IncrementalHash hash,
        DialoguePhrase phrase,
        AnchorDisposition disposition,
        long startSample = 0,
        long endSample = 0)
    {
        AppendString(hash, phrase.Id);
        AppendInt64(hash, phrase.CoreStartSample);
        AppendInt64(hash, phrase.CoreEndSample);
        AppendInt64(hash, startSample);
        AppendInt64(hash, endSample);
        AppendInt32(hash, (int)disposition);
    }

    private static void AppendMeasured(
        IncrementalHash hash,
        DirectionalBleedAnchorEvidence anchor,
        AnchorDisposition disposition)
    {
        AppendString(hash, anchor.SourcePhraseId);
        AppendString(hash, anchor.TargetSourceClipId);
        AppendInt64(hash, anchor.TimelineStartSample);
        AppendInt64(hash, anchor.TimelineEndSample);
        AppendSingle(hash, anchor.OtherMicAdvantageDb);
        AppendSingle(hash, anchor.WaveformCorrelation);
        AppendInt32(hash, anchor.LagMilliseconds);
        AppendSingle(hash, anchor.TransferScaleToTarget);
        AppendSingle(hash, anchor.ResidualToTargetDb);
        AppendInt32(hash, (int)disposition);
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

    private static void ValidateExclusion(DirectionalBleedCalibrationExclusion? exclusion)
    {
        if (exclusion is not null &&
            (exclusion.TargetTrackIndex <= 0 ||
             exclusion.TimelineStartSample < 0 ||
             exclusion.TimelineEndSample <= exclusion.TimelineStartSample))
        {
            throw new ArgumentOutOfRangeException(
                nameof(exclusion),
                "Vùng leave-one-region-out không hợp lệ.");
        }
    }

    private enum AnchorDisposition
    {
        Accepted,
        NoConfirmedSourceSpeech,
        NoMatchingBaselineBleed,
        TooShort,
        ExcludedCandidateRegion,
        IncompleteMedia,
        Clipped,
        PolarityInverted,
        BelowAdvantage,
        BelowCorrelation,
        LagOutOfRange,
        ConflictingResidual
    }

    private sealed class MutableRejectionCounts
    {
        public int NoConfirmedSourceSpeech { get; set; }

        public int NoMatchingBaselineBleed { get; set; }

        public int TooShort { get; set; }

        public int ExcludedCandidateRegion { get; set; }

        public int IncompleteMedia { get; set; }

        public int Clipped { get; set; }

        public int PolarityInverted { get; set; }

        public int BelowAdvantage { get; set; }

        public int BelowCorrelation { get; set; }

        public int LagOutOfRange { get; set; }

        public int ConflictingResidual { get; set; }

        public void Add(AnchorDisposition disposition)
        {
            switch (disposition)
            {
                case AnchorDisposition.BelowAdvantage:
                    BelowAdvantage++;
                    break;
                case AnchorDisposition.BelowCorrelation:
                    BelowCorrelation++;
                    break;
                case AnchorDisposition.LagOutOfRange:
                    LagOutOfRange++;
                    break;
                case AnchorDisposition.ConflictingResidual:
                    ConflictingResidual++;
                    break;
                case AnchorDisposition.PolarityInverted:
                    PolarityInverted++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(disposition));
            }
        }

        public DirectionalBleedCalibrationRejectionCounts ToImmutable() => new(
            NoConfirmedSourceSpeech,
            NoMatchingBaselineBleed,
            TooShort,
            ExcludedCandidateRegion,
            IncompleteMedia,
            Clipped,
            PolarityInverted,
            BelowAdvantage,
            BelowCorrelation,
            LagOutOfRange,
            ConflictingResidual);
    }

    private readonly record struct RankedAnchor(
        ulong Priority,
        DirectionalBleedAnchorEvidence Anchor);
}
