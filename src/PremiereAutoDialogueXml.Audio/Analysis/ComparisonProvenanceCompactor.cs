using System.Security.Cryptography;
using System.Text;

namespace PremiereAutoDialogueXml.Audio.Analysis;

internal static class ComparisonProvenanceCompactor
{
    internal const int SampleLimit = 64;
    internal const string EmptyArraySha256 =
        "DF3F619804A92FDB4057192DC43DD748EA778ADC52BC498CE80524C014B81119";

    public static VadFrontEndTrackComparison Compact(VadFrontEndTrackComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        if (comparison.Differences.Count != comparison.SegmentDifferenceCount)
        {
            return comparison;
        }

        var full = comparison.Differences;
        return comparison with
        {
            Differences = SelectSamples(
                full,
                difference => IsEnabled(difference.LegacyStatus) && !IsEnabled(difference.CandidateStatus),
                difference => !IsEnabled(difference.LegacyStatus) && IsEnabled(difference.CandidateStatus)),
            DifferenceTraceSha256 = Hash(full)
        };
    }

    public static NoiseBoundaryTrackComparison Compact(NoiseBoundaryTrackComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        var compacted = comparison;
        if (comparison.PhraseDifferences.Count == comparison.PhraseDifferenceCount)
        {
            var full = comparison.PhraseDifferences;
            compacted = compacted with
            {
                PhraseDifferences = SelectSamples(
                    full,
                    difference => difference.ChangeKind == "candidate-only",
                    difference => difference.ChangeKind == "baseline-only",
                    difference => difference.ChangeKind == "split",
                    difference => difference.ChangeKind == "merge"),
                PhraseDifferenceTraceSha256 = Hash(full)
            };
        }

        if (comparison.DecisionDifferences.Count == comparison.SegmentDifferenceCount)
        {
            var full = comparison.DecisionDifferences;
            compacted = compacted with
            {
                DecisionDifferences = SelectSamples(
                    full,
                    difference => difference.BaselineEnabled && !difference.CandidateEnabled,
                    difference => !difference.BaselineEnabled && difference.CandidateEnabled),
                DecisionDifferenceTraceSha256 = Hash(full)
            };
        }

        if (comparison.FinalDifferences.Count == comparison.FinalDifferenceCount)
        {
            var full = comparison.FinalDifferences;
            compacted = compacted with
            {
                FinalDifferences = SelectSamples(
                    full,
                    difference => difference.BaselineEnabled && !difference.FinalEnabled,
                    difference => difference.BaselineEnabled && !difference.CandidateEnabled,
                    difference => !difference.BaselineEnabled && difference.CandidateEnabled),
                FinalDifferenceTraceSha256 = Hash(full)
            };
        }

        return compacted;
    }

    private static IReadOnlyList<T> SelectSamples<T>(
        IReadOnlyList<T> values,
        params Func<T, bool>[] categorySelectors)
    {
        if (values.Count <= SampleLimit)
        {
            return values.ToArray();
        }

        var indexes = new HashSet<int>();
        AddRange(indexes, 0, Math.Min(8, values.Count));
        AddRange(indexes, Math.Max(0, values.Count - 8), values.Count);
        foreach (var selector in categorySelectors)
        {
            var added = 0;
            for (var index = 0; index < values.Count && added < 12; index++)
            {
                if (selector(values[index]) && indexes.Add(index))
                {
                    added++;
                }
            }
        }

        if (indexes.Count < SampleLimit)
        {
            var stride = Math.Max(1d, (values.Count - 1d) / (SampleLimit - 1d));
            for (var sample = 0; sample < SampleLimit && indexes.Count < SampleLimit; sample++)
            {
                indexes.Add((int)Math.Round(sample * stride));
            }
        }

        for (var index = 0; index < values.Count && indexes.Count < SampleLimit; index++)
        {
            indexes.Add(index);
        }

        return indexes.Order().Select(index => values[index]).ToArray();
    }

    private static void AddRange(ISet<int> indexes, int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            indexes.Add(index);
        }
    }

    private static string Hash<T>(IReadOnlyList<T> values)
    {
        using var writer = new ProvenanceHashWriter();
        writer.Add(values.Count);
        if (typeof(T) == typeof(VadDecisionDifference))
        {
            foreach (var value in values)
            {
                writer.Add((VadDecisionDifference)(object)value!);
            }
        }
        else if (typeof(T) == typeof(NoiseBoundaryPhraseDifference))
        {
            foreach (var value in values)
            {
                writer.Add((NoiseBoundaryPhraseDifference)(object)value!);
            }
        }
        else if (typeof(T) == typeof(NoiseBoundaryDecisionDifference))
        {
            foreach (var value in values)
            {
                writer.Add((NoiseBoundaryDecisionDifference)(object)value!);
            }
        }
        else if (typeof(T) == typeof(NoiseBoundaryFinalDecisionDifference))
        {
            foreach (var value in values)
            {
                writer.Add((NoiseBoundaryFinalDecisionDifference)(object)value!);
            }
        }
        else
        {
            throw new NotSupportedException($"Không hỗ trợ hash provenance cho {typeof(T).Name}.");
        }

        return writer.Finish();
    }

    private static bool IsEnabled(AudioSegmentStatus status) =>
        status is AudioSegmentStatus.Speech or AudioSegmentStatus.Ambiguous;

    private sealed class ProvenanceHashWriter : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly Dictionary<string, byte[]> _stringCache = new(StringComparer.Ordinal);

        public void Add(VadDecisionDifference value)
        {
            Add(value.TrackIndex);
            Add(value.SourceClipId);
            Add(value.TimelineStartSample);
            Add(value.TimelineEndSample);
            Add((int)value.LegacyStatus);
            Add(value.LegacyReason);
            Add((int)value.CandidateStatus);
            Add(value.CandidateReason);
            Add((int)value.FinalStatus);
            Add(value.FinalReason);
        }

        public void Add(NoiseBoundaryPhraseDifference value)
        {
            Add(value.ChangeKind);
            Add(value.BaselinePhrases.Count);
            foreach (var phrase in value.BaselinePhrases)
            {
                Add(phrase);
            }

            Add(value.CandidatePhrases.Count);
            foreach (var phrase in value.CandidatePhrases)
            {
                Add(phrase);
            }
        }

        public void Add(NoiseBoundaryDecisionDifference value)
        {
            Add(value.TimelineStartSample);
            Add(value.TimelineEndSample);
            Add(value.BaselineSourceClipId);
            Add(value.BaselineStatus);
            Add(value.BaselineReason);
            Add(value.BaselineEnabled);
            Add(value.BaselinePhraseId);
            Add(value.BaselineGainDb);
            Add(value.CandidateSourceClipId);
            Add(value.CandidateStatus);
            Add(value.CandidateReason);
            Add(value.CandidateEnabled);
            Add(value.CandidatePhraseId);
            Add(value.CandidateGainDb);
        }

        public void Add(NoiseBoundaryFinalDecisionDifference value)
        {
            Add(value.TimelineStartSample);
            Add(value.TimelineEndSample);
            Add(value.SourceClipId);
            Add((int)value.BaselineStatus);
            Add(value.BaselineReason);
            Add(value.BaselineEnabled);
            Add(value.BaselinePhraseId);
            Add(value.BaselineGainDb);
            Add((int)value.CandidateStatus);
            Add(value.CandidateReason);
            Add(value.CandidateEnabled);
            Add(value.CandidatePhraseId);
            Add(value.CandidateGainDb);
            Add((int)value.FinalStatus);
            Add(value.FinalReason);
            Add(value.FinalEnabled);
            Add(value.FinalPhraseId);
            Add(value.FinalGainDb);
        }

        public void Add(int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BitConverter.TryWriteBytes(bytes, value);
            _hash.AppendData(bytes);
        }

        private void Add(long value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            BitConverter.TryWriteBytes(bytes, value);
            _hash.AppendData(bytes);
        }

        private void Add(bool value) => Add(value ? 1 : 0);

        private void Add(float? value)
        {
            Add(value.HasValue);
            if (value.HasValue)
            {
                Add(BitConverter.SingleToInt32Bits(value.Value));
            }
        }

        private void Add(AudioSegmentStatus? value)
        {
            Add(value.HasValue);
            if (value.HasValue)
            {
                Add((int)value.Value);
            }
        }

        private void Add(string? value)
        {
            if (value is null)
            {
                Add(-1);
                return;
            }

            if (!_stringCache.TryGetValue(value, out var bytes))
            {
                bytes = Encoding.UTF8.GetBytes(value);
                _stringCache.Add(value, bytes);
            }

            Add(bytes.Length);
            _hash.AppendData(bytes);
        }

        private void Add(NoiseBoundaryPhraseSnapshot phrase)
        {
            Add(phrase.Id);
            Add(phrase.CoreStartSample);
            Add(phrase.CoreEndSample);
            Add(phrase.PaddedStartSample);
            Add(phrase.PaddedEndSample);
            Add(BitConverter.SingleToInt32Bits(phrase.MeasuredPeakDbfs));
            Add(BitConverter.SingleToInt32Bits(phrase.RequiredGainDb));
            Add(BitConverter.SingleToInt32Bits(phrase.AppliedGainDb));
            Add(phrase.GainWasCapped);
        }

        public string Finish() => Convert.ToHexString(_hash.GetHashAndReset());

        public void Dispose() => _hash.Dispose();
    }
}
