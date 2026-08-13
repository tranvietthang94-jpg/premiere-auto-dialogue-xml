using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PremiereAutoDialogueXml.Audio.Analysis;

public static class NoiseBoundaryShadowComparer
{
    private const float FloatTolerance = 0.0001f;
    private const int SampleGroupLimit = 16;

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

        if (!BoundaryPoliciesMatch(baselineTrace.BoundaryPolicy, candidateTrace.BoundaryPolicy))
        {
            throw new InvalidDataException("Noise-boundary trace không đúng VAD boundary policy provenance.");
        }

        if (baselineTrace.TrackIndex != baseline.TrackIndex ||
            candidateTrace.TrackIndex != candidate.TrackIndex ||
            baselineTrace.Frames.Count != candidateTrace.Frames.Count)
        {
            throw new InvalidDataException("Baseline/candidate noise-boundary không cùng observation contract.");
        }

        var firstSamples = new List<NoiseBoundaryFrameDifference>(SampleGroupLimit);
        var lastSamples = new Queue<NoiseBoundaryFrameDifference>(SampleGroupLimit);
        var eligibilitySamples = new List<NoiseBoundaryFrameDifference>(SampleGroupLimit);
        var highestFloorDeltaSamples = new List<RankedFrameDifference>(SampleGroupLimit);
        using var traceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var changedFrameCount = 0;
        var maximumNoiseFloorDeltaDb = 0f;
        var trainingEligibilityDifferenceCount = 0;
        var noiseFloorBeforeDifferenceCount = 0;
        var noiseFloorAfterDifferenceCount = 0;
        var noiseFloorReadyBeforeDifferenceCount = 0;
        var noiseFloorReadyAfterDifferenceCount = 0;
        var trainingDecisionDifferenceCount = 0;
        var vadSpeechDifferenceCount = 0;
        var directEvidenceDifferenceCount = 0;
        var directEnergyThresholdDifferenceCount = 0;
        var warmupUncertainDifferenceCount = 0;
        var boundaryStateDifferenceCount = 0;
        for (var index = 0; index < baselineTrace.Frames.Count; index++)
        {
            var baselineFrame = baselineTrace.Frames[index];
            var candidateFrame = candidateTrace.Frames[index];
            if (baselineFrame.TimelineStartSample != candidateFrame.TimelineStartSample ||
                baselineFrame.TimelineEndSample != candidateFrame.TimelineEndSample ||
                MathF.Abs(baselineFrame.VadProbability - candidateFrame.VadProbability) > FloatTolerance ||
                MathF.Abs(baselineFrame.RmsDbfs - candidateFrame.RmsDbfs) > FloatTolerance)
            {
                throw new InvalidDataException("Baseline/candidate noise-boundary lệch timeline frame.");
            }

            AppendFramePair(traceHash, baselineFrame, candidateFrame);
            var floorBeforeDiffers =
                MathF.Abs(baselineFrame.NoiseFloorBeforeDbfs - candidateFrame.NoiseFloorBeforeDbfs) >
                FloatTolerance;
            var floorAfterDiffers =
                MathF.Abs(baselineFrame.NoiseFloorAfterDbfs - candidateFrame.NoiseFloorAfterDbfs) >
                FloatTolerance;
            var eligibilityDiffers =
                IsTrainingEligible(baselineFrame.NoiseFloorTrainingDecision) !=
                IsTrainingEligible(candidateFrame.NoiseFloorTrainingDecision);
            if (!FramesMatch(baselineFrame, candidateFrame))
            {
                changedFrameCount++;
                var difference = new NoiseBoundaryFrameDifference(baselineFrame, candidateFrame);
                AddSamples(
                    difference,
                    Math.Max(
                        MathF.Abs(baselineFrame.NoiseFloorBeforeDbfs - candidateFrame.NoiseFloorBeforeDbfs),
                        MathF.Abs(baselineFrame.NoiseFloorAfterDbfs - candidateFrame.NoiseFloorAfterDbfs)),
                    eligibilityDiffers,
                    firstSamples,
                    lastSamples,
                    eligibilitySamples,
                    highestFloorDeltaSamples);
            }

            noiseFloorBeforeDifferenceCount += floorBeforeDiffers ? 1 : 0;
            noiseFloorAfterDifferenceCount += floorAfterDiffers ? 1 : 0;
            noiseFloorReadyBeforeDifferenceCount +=
                baselineFrame.NoiseFloorReadyBefore != candidateFrame.NoiseFloorReadyBefore ? 1 : 0;
            noiseFloorReadyAfterDifferenceCount +=
                baselineFrame.NoiseFloorReadyAfter != candidateFrame.NoiseFloorReadyAfter ? 1 : 0;
            trainingDecisionDifferenceCount +=
                baselineFrame.NoiseFloorTrainingDecision != candidateFrame.NoiseFloorTrainingDecision ? 1 : 0;
            vadSpeechDifferenceCount += baselineFrame.IsVadSpeech != candidateFrame.IsVadSpeech ? 1 : 0;
            directEvidenceDifferenceCount +=
                baselineFrame.IsDirectEvidence != candidateFrame.IsDirectEvidence ? 1 : 0;
            directEnergyThresholdDifferenceCount +=
                baselineFrame.IsAboveDirectEnergyThreshold != candidateFrame.IsAboveDirectEnergyThreshold ? 1 : 0;
            warmupUncertainDifferenceCount +=
                baselineFrame.IsWarmupUncertain != candidateFrame.IsWarmupUncertain ? 1 : 0;
            boundaryStateDifferenceCount +=
                baselineFrame.BoundaryState != candidateFrame.BoundaryState ? 1 : 0;

            maximumNoiseFloorDeltaDb = Math.Max(
                maximumNoiseFloorDeltaDb,
                Math.Max(
                    MathF.Abs(baselineFrame.NoiseFloorBeforeDbfs - candidateFrame.NoiseFloorBeforeDbfs),
                    MathF.Abs(baselineFrame.NoiseFloorAfterDbfs - candidateFrame.NoiseFloorAfterDbfs)));
            if (eligibilityDiffers)
            {
                trainingEligibilityDifferenceCount++;
            }
        }

        var frameDifferenceSamples = firstSamples
            .Concat(lastSamples)
            .Concat(eligibilitySamples)
            .Concat(highestFloorDeltaSamples.Select(sample => sample.Difference))
            .DistinctBy(difference => new
            {
                difference.Baseline.TimelineStartSample,
                difference.Baseline.TimelineEndSample
            })
            .OrderBy(difference => difference.Baseline.TimelineStartSample)
            .ToArray();
        var phraseDifferences = ComparePhrases(baseline.Phrases, candidate.Phrases);
        var decisionDifferences = CompareDecisions(baseline.Segments, candidate.Segments);
        return new(
            baseline.TrackIndex,
            baselineTrace.Mode,
            candidateTrace.Mode,
            baselineTrace.Policy.Version,
            candidateTrace.Policy.Version,
            baselineTrace.BoundaryPolicy.Version,
            candidateTrace.BoundaryPolicy.Version,
            baselineTrace.Frames.Count,
            changedFrameCount,
            phraseDifferences.Count,
            decisionDifferences.Count,
            decisionDifferences.Count(difference =>
                difference.BaselineEnabled && !difference.CandidateEnabled),
            decisionDifferences.Count(difference =>
                !difference.BaselineEnabled && difference.CandidateEnabled),
            frameDifferenceSamples,
            decisionDifferences)
        {
            BaselinePolicy = baselineTrace.Policy,
            CandidatePolicy = candidateTrace.Policy,
            BaselineBoundaryPolicy = baselineTrace.BoundaryPolicy,
            CandidateBoundaryPolicy = candidateTrace.BoundaryPolicy,
            MaximumNoiseFloorDeltaDb = maximumNoiseFloorDeltaDb,
            TrainingEligibilityDifferenceCount = trainingEligibilityDifferenceCount,
            FrameTraceSha256 = Convert.ToHexString(traceHash.GetHashAndReset()),
            FrameDifferenceSummary = new(
                noiseFloorBeforeDifferenceCount,
                noiseFloorAfterDifferenceCount,
                noiseFloorReadyBeforeDifferenceCount,
                noiseFloorReadyAfterDifferenceCount,
                trainingDecisionDifferenceCount,
                vadSpeechDifferenceCount,
                directEvidenceDifferenceCount,
                directEnergyThresholdDifferenceCount,
                warmupUncertainDifferenceCount,
                boundaryStateDifferenceCount),
            PhraseDifferences = phraseDifferences
        };
    }

    internal static NoiseBoundaryTrackComparison RefreshDecisions(
        NoiseBoundaryTrackComparison comparison,
        TrackAudioAnalysis baseline,
        TrackAudioAnalysis candidate)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        if (comparison.TrackIndex != baseline.TrackIndex || baseline.TrackIndex != candidate.TrackIndex)
        {
            throw new InvalidDataException("Noise-boundary decision refresh không cùng track.");
        }

        var phraseDifferences = ComparePhrases(baseline.Phrases, candidate.Phrases);
        var decisionDifferences = CompareDecisions(baseline.Segments, candidate.Segments);
        return comparison with
        {
            PhraseDifferenceCount = phraseDifferences.Count,
            SegmentDifferenceCount = decisionDifferences.Count,
            BaselineEnabledCandidateDisabledCount = decisionDifferences.Count(difference =>
                difference.BaselineEnabled && !difference.CandidateEnabled),
            BaselineDisabledCandidateEnabledCount = decisionDifferences.Count(difference =>
                !difference.BaselineEnabled && difference.CandidateEnabled),
            PhraseDifferences = phraseDifferences,
            DecisionDifferences = decisionDifferences
        };
    }

    private static bool BoundaryPoliciesMatch(
        VadBoundaryPolicyDescriptor baseline,
        VadBoundaryPolicyDescriptor candidate) =>
        baseline.Version == "phase09-vad-single-threshold-v1" &&
        candidate.Version == "phase10-vad-start050-continue040-v1" &&
        Math.Abs(baseline.StartThreshold - candidate.StartThreshold) <= FloatTolerance &&
        Math.Abs(candidate.StartThreshold - 0.50) <= FloatTolerance &&
        Math.Abs(baseline.StartThreshold - baseline.ContinueThreshold) <= FloatTolerance &&
        Math.Abs(candidate.ContinueThreshold - 0.40) <= FloatTolerance &&
        candidate.ContinueThreshold < candidate.StartThreshold &&
        baseline.PhraseBreakMilliseconds == candidate.PhraseBreakMilliseconds &&
        baseline.MinimumStartEvidenceMilliseconds == candidate.MinimumStartEvidenceMilliseconds &&
        baseline.MinimumDirectEvidenceMilliseconds == candidate.MinimumDirectEvidenceMilliseconds;

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

    private static IReadOnlyList<NoiseBoundaryPhraseDifference> ComparePhrases(
        IReadOnlyList<DialoguePhrase> baseline,
        IReadOnlyList<DialoguePhrase> candidate)
    {
        var nodes = baseline.Select(phrase => new PhraseNode(phrase, IsBaseline: true))
            .Concat(candidate.Select(phrase => new PhraseNode(phrase, IsBaseline: false)))
            .OrderBy(node => node.Phrase.PaddedStartSample)
            .ThenBy(node => node.Phrase.PaddedEndSample)
            .ToArray();
        if (nodes.Length == 0)
        {
            return [];
        }

        var differences = new List<NoiseBoundaryPhraseDifference>();
        var component = new List<PhraseNode> { nodes[0] };
        var componentEnd = nodes[0].Phrase.PaddedEndSample;
        for (var index = 1; index < nodes.Length; index++)
        {
            var node = nodes[index];
            if (node.Phrase.PaddedStartSample < componentEnd)
            {
                component.Add(node);
                componentEnd = Math.Max(componentEnd, node.Phrase.PaddedEndSample);
                continue;
            }

            AddPhraseDifference(differences, component);
            component = [node];
            componentEnd = node.Phrase.PaddedEndSample;
        }

        AddPhraseDifference(differences, component);
        return differences;
    }

    private static void AddPhraseDifference(
        List<NoiseBoundaryPhraseDifference> differences,
        IReadOnlyList<PhraseNode> component)
    {
        var baseline = component.Where(node => node.IsBaseline).Select(node => node.Phrase).ToArray();
        var candidate = component.Where(node => !node.IsBaseline).Select(node => node.Phrase).ToArray();
        if (PhraseMultisetMatches(baseline, candidate))
        {
            return;
        }

        var changeKind = (baseline.Length, candidate.Length) switch
        {
            (0, _) => "candidate-only",
            (_, 0) => "baseline-only",
            (1, 1) => "boundary-or-gain-changed",
            (1, > 1) => "split",
            ( > 1, 1) => "merge",
            _ => "resegmented"
        };
        differences.Add(new(
            changeKind,
            baseline.Select(PhraseSnapshot).ToArray(),
            candidate.Select(PhraseSnapshot).ToArray()));
    }

    private static bool PhraseMultisetMatches(
        IReadOnlyList<DialoguePhrase> baseline,
        IReadOnlyList<DialoguePhrase> candidate)
    {
        var baselineCounts = baseline.GroupBy(PhraseSignature)
            .ToDictionary(group => group.Key, group => group.Count());
        var candidateCounts = candidate.GroupBy(PhraseSignature)
            .ToDictionary(group => group.Key, group => group.Count());
        return baselineCounts.Count == candidateCounts.Count &&
               baselineCounts.All(pair => candidateCounts.GetValueOrDefault(pair.Key) == pair.Value);
    }

    private static NoiseBoundaryPhraseSnapshot PhraseSnapshot(DialoguePhrase phrase) => new(
        phrase.Id,
        phrase.CoreStartSample,
        phrase.CoreEndSample,
        phrase.PaddedStartSample,
        phrase.PaddedEndSample,
        phrase.MeasuredPeakDbfs,
        phrase.RequiredGainDb,
        phrase.AppliedGainDb,
        phrase.GainWasCapped);

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
                IsEnabled(candidateSegment))
            {
                BaselinePhraseId = baselineSegment?.PhraseId,
                BaselineGainDb = baselineSegment?.GainDb,
                CandidatePhraseId = candidateSegment?.PhraseId,
                CandidateGainDb = candidateSegment?.GainDb
            });
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

    private static bool IsTrainingEligible(NoiseFloorTrainingDecision decision) => decision is
        NoiseFloorTrainingDecision.WarmupCandidate or
        NoiseFloorTrainingDecision.EligibleVadNegative or
        NoiseFloorTrainingDecision.EligibleBackground or
        NoiseFloorTrainingDecision.EligibleStableFloorStep;

    private static void AddSamples(
        NoiseBoundaryFrameDifference difference,
        float floorDeltaDb,
        bool eligibilityDiffers,
        List<NoiseBoundaryFrameDifference> firstSamples,
        Queue<NoiseBoundaryFrameDifference> lastSamples,
        List<NoiseBoundaryFrameDifference> eligibilitySamples,
        List<RankedFrameDifference> highestFloorDeltaSamples)
    {
        if (firstSamples.Count < SampleGroupLimit)
        {
            firstSamples.Add(difference);
        }

        if (lastSamples.Count == SampleGroupLimit)
        {
            lastSamples.Dequeue();
        }

        lastSamples.Enqueue(difference);
        if (eligibilityDiffers && eligibilitySamples.Count < SampleGroupLimit)
        {
            eligibilitySamples.Add(difference);
        }

        if (highestFloorDeltaSamples.Count < SampleGroupLimit)
        {
            highestFloorDeltaSamples.Add(new(floorDeltaDb, difference));
            return;
        }

        var smallestIndex = 0;
        for (var index = 1; index < highestFloorDeltaSamples.Count; index++)
        {
            if (highestFloorDeltaSamples[index].FloorDeltaDb <
                highestFloorDeltaSamples[smallestIndex].FloorDeltaDb)
            {
                smallestIndex = index;
            }
        }

        if (floorDeltaDb > highestFloorDeltaSamples[smallestIndex].FloorDeltaDb)
        {
            highestFloorDeltaSamples[smallestIndex] = new(floorDeltaDb, difference);
        }
    }

    private static void AppendFramePair(
        IncrementalHash hash,
        NoiseBoundaryFrameTrace baseline,
        NoiseBoundaryFrameTrace candidate)
    {
        Span<byte> buffer = stackalloc byte[96];
        var offset = 0;
        WriteFrame(buffer, ref offset, baseline);
        WriteFrame(buffer, ref offset, candidate);
        hash.AppendData(buffer[..offset]);
    }

    private static void WriteFrame(
        Span<byte> buffer,
        ref int offset,
        NoiseBoundaryFrameTrace frame)
    {
        WriteInt64(buffer, ref offset, frame.TimelineStartSample);
        WriteInt64(buffer, ref offset, frame.TimelineEndSample);
        WriteSingle(buffer, ref offset, frame.VadProbability);
        WriteSingle(buffer, ref offset, frame.RmsDbfs);
        WriteSingle(buffer, ref offset, frame.NoiseFloorBeforeDbfs);
        WriteSingle(buffer, ref offset, frame.NoiseFloorAfterDbfs);
        buffer[offset++] = frame.NoiseFloorReadyBefore ? (byte)1 : (byte)0;
        buffer[offset++] = frame.NoiseFloorReadyAfter ? (byte)1 : (byte)0;
        WriteInt32(buffer, ref offset, (int)frame.NoiseFloorTrainingDecision);
        buffer[offset++] = frame.IsVadSpeech ? (byte)1 : (byte)0;
        buffer[offset++] = frame.IsDirectEvidence ? (byte)1 : (byte)0;
        buffer[offset++] = frame.IsAboveDirectEnergyThreshold ? (byte)1 : (byte)0;
        buffer[offset++] = frame.IsWarmupUncertain ? (byte)1 : (byte)0;
        WriteInt32(buffer, ref offset, (int)frame.BoundaryState);
    }

    private static void WriteInt32(Span<byte> buffer, ref int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer[offset..], value);
        offset += sizeof(int);
    }

    private static void WriteInt64(Span<byte> buffer, ref int offset, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(buffer[offset..], value);
        offset += sizeof(long);
    }

    private static void WriteSingle(Span<byte> buffer, ref int offset, float value) =>
        WriteInt32(buffer, ref offset, BitConverter.SingleToInt32Bits(value));

    private readonly record struct PhraseComparisonSignature(
        long CoreStartSample,
        long CoreEndSample,
        long PaddedStartSample,
        long PaddedEndSample,
        float MeasuredPeakDbfs,
        float RequiredGainDb,
        float AppliedGainDb,
        bool GainWasCapped);

    private readonly record struct PhraseNode(DialoguePhrase Phrase, bool IsBaseline);

    private readonly record struct RankedFrameDifference(
        float FloorDeltaDb,
        NoiseBoundaryFrameDifference Difference);
}
