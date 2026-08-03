using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Audio.Pcm;
using PremiereAutoDialogueXml.Core.Media;
using PremiereAutoDialogueXml.Output.Audit;

namespace PremiereAutoDialogueXml.Validation;

public sealed record PhrasePeakValidation(
    string PhraseId,
    int TrackIndex,
    long TimelineStartFrame,
    long TimelineEndFrame,
    int SpeechFragmentCount,
    bool GainWasCapped,
    double MeasuredSourcePeakDbfs,
    double AppliedGainDb,
    double ExpectedRenderedPeakDbfs,
    double? ObservedRenderedPeakDbfs,
    double? DeltaDb,
    double? FrameRoundedSourcePeakDbfs,
    double? SourceDerivedExpectedRenderedPeakDbfs,
    double? SourceDerivedDeltaDb,
    double? TargetDeltaDb,
    bool GainWithinTolerance,
    bool TargetWithinTolerance,
    bool WithinTolerance,
    string ResultReason);

public sealed record PcmTrackPeakValidationReport(
    int TrackIndex,
    int SampleRate,
    int ValidBitsPerSample,
    long SampleFrameCount,
    double ToleranceDb,
    int SpeechFragmentCount,
    int PhraseCount,
    int PassedPhraseCount,
    int FailedPhraseCount,
    int UncappedPhraseCount,
    int TargetPassedPhraseCount,
    int TargetFailedPhraseCount,
    double? MedianObservedOffsetDb,
    int PhrasesMatchingMedianOffset,
    int PhrasesOutsideMedianOffset,
    bool UsedSourceMedia,
    double? SourceDerivedMedianOffsetDb,
    int SourceDerivedPhrasesMatchingMedianOffset,
    int SourceDerivedPhrasesOutsideMedianOffset,
    bool AllWithinTolerance,
    IReadOnlyList<PhrasePeakValidation> Phrases);

public sealed class PcmTrackPeakValidator
{
    private const int SupportedFrameRate = 25;
    private const int SupportedSampleRate = 48_000;
    private const int SamplesPerVideoFrame = SupportedSampleRate / SupportedFrameRate;

    private readonly PcmWaveSampleReader _sampleReader;

    public PcmTrackPeakValidator(PcmWaveSampleReader? sampleReader = null)
    {
        _sampleReader = sampleReader ?? new PcmWaveSampleReader();
    }

    public PcmTrackPeakValidationReport Validate(
        OutputAudit audit,
        WaveFileInfo renderedWave,
        int trackIndex,
        IReadOnlyDictionary<string, WaveFileInfo>? sourceMediaByFileId = null,
        double toleranceDb = 0.1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(renderedWave);

        if (trackIndex <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackIndex), "Số track phải bắt đầu từ 1.");
        }

        if (!double.IsFinite(toleranceDb) || toleranceDb <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(toleranceDb), "Sai số peak phải lớn hơn 0 dB.");
        }

        ValidateWave(renderedWave);

        var speechFragments = audit.Fragments
            .Where(fragment =>
                fragment.TrackIndex == trackIndex &&
                fragment.Enabled &&
                fragment.Status is AudioSegmentStatus.Speech or AudioSegmentStatus.Ambiguous &&
                !string.IsNullOrWhiteSpace(fragment.PhraseId))
            .OrderBy(fragment => fragment.TimelineStartFrame)
            .ToArray();

        if (speechFragments.Length == 0)
        {
            throw new InvalidDataException($"Audit không có fragment speech Enabled trên track A{trackIndex}.");
        }

        var results = new List<PhrasePeakValidation>();
        foreach (var phraseGroup in speechFragments.GroupBy(fragment => fragment.PhraseId!, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fragments = phraseGroup.OrderBy(fragment => fragment.TimelineStartFrame).ToArray();
            ValidatePhraseAudit(phraseGroup.Key, fragments);

            var first = fragments[0];
            var measuredPeakDbfs = first.MeasuredPeakDbfs!.Value;
            var appliedGainDb = first.AppliedGainDb!.Value;
            var expectedPeakDbfs =
                measuredPeakDbfs +
                appliedGainDb -
                audit.Preset.PremiereCenterPanCompensationDb;
            if (!first.GainWasCapped &&
                Math.Abs(expectedPeakDbfs - audit.Preset.TargetSamplePeakDbfs) > 0.001)
            {
                throw new InvalidDataException(
                    $"Phrase {phraseGroup.Key} không đạt target hậu routing theo gain trong audit.");
            }
            var maximumAbsoluteSample = 0f;
            var maximumAbsoluteSourceSample = 0f;

            foreach (var fragment in fragments)
            {
                var startSample = checked(fragment.TimelineStartFrame * SamplesPerVideoFrame);
                var endSample = checked(fragment.TimelineEndFrame * SamplesPerVideoFrame);
                if (startSample < 0 || endSample <= startSample)
                {
                    throw new InvalidDataException(
                        $"Phrase {phraseGroup.Key} có vùng timeline không hợp lệ " +
                        $"[{fragment.TimelineStartFrame}, {fragment.TimelineEndFrame}).");
                }

                if (endSample > renderedWave.SampleFrameCount)
                {
                    throw new InvalidDataException(
                        $"WAV kết xuất kết thúc trước phrase {phraseGroup.Key} trên A{trackIndex}; " +
                        "hãy export từ đầu sequence và không cắt đuôi.");
                }

                maximumAbsoluteSample = Math.Max(
                    maximumAbsoluteSample,
                    MeasurePeak(renderedWave, startSample, endSample, cancellationToken));

                if (sourceMediaByFileId is not null)
                {
                    if (!sourceMediaByFileId.TryGetValue(fragment.SourceFileId, out var sourceWave))
                    {
                        throw new InvalidDataException(
                            $"Không tìm thấy media {fragment.SourceFileId} của phrase {phraseGroup.Key} trong XML nguồn.");
                    }

                    ValidateWave(sourceWave);
                    if (fragment.SourceStartSample < 0 ||
                        fragment.SourceEndSample <= fragment.SourceStartSample ||
                        fragment.SourceEndSample > sourceWave.SampleFrameCount)
                    {
                        throw new InvalidDataException(
                            $"Phrase {phraseGroup.Key} có source range ngoài WAV {fragment.SourceFileId}.");
                    }

                    maximumAbsoluteSourceSample = Math.Max(
                        maximumAbsoluteSourceSample,
                        MeasurePeak(
                            sourceWave,
                            fragment.SourceStartSample,
                            fragment.SourceEndSample,
                            cancellationToken));
                }
            }

            var observedPeakDbfs = maximumAbsoluteSample > 0
                ? 20d * Math.Log10(maximumAbsoluteSample)
                : (double?)null;
            var deltaDb = observedPeakDbfs - expectedPeakDbfs;
            var frameRoundedSourcePeakDbfs = sourceMediaByFileId is not null && maximumAbsoluteSourceSample > 0
                ? 20d * Math.Log10(maximumAbsoluteSourceSample)
                : (double?)null;
            var sourceDerivedExpectedPeakDbfs =
                frameRoundedSourcePeakDbfs +
                appliedGainDb -
                audit.Preset.PremiereCenterPanCompensationDb;
            var sourceDerivedDeltaDb = observedPeakDbfs - sourceDerivedExpectedPeakDbfs;
            var gainWithinTolerance = sourceMediaByFileId is not null
                ? sourceDerivedDeltaDb is not null && Math.Abs(sourceDerivedDeltaDb.Value) <= toleranceDb
                : deltaDb is not null && Math.Abs(deltaDb.Value) <= toleranceDb;
            var targetDeltaDb = observedPeakDbfs - audit.Preset.TargetSamplePeakDbfs;
            var targetWithinTolerance = !first.GainWasCapped &&
                targetDeltaDb is not null &&
                Math.Abs(targetDeltaDb.Value) <= toleranceDb;
            var withinTolerance = gainWithinTolerance && (first.GainWasCapped || targetWithinTolerance);
            var resultReason = observedPeakDbfs is null
                ? "rendered-silence"
                : !gainWithinTolerance
                    ? "gain-out-of-tolerance"
                    : !first.GainWasCapped && !targetWithinTolerance
                        ? "uncapped-target-out-of-tolerance"
                        : "within-tolerance";

            results.Add(new(
                phraseGroup.Key,
                trackIndex,
                fragments.Min(fragment => fragment.TimelineStartFrame),
                fragments.Max(fragment => fragment.TimelineEndFrame),
                fragments.Length,
                first.GainWasCapped,
                measuredPeakDbfs,
                appliedGainDb,
                expectedPeakDbfs,
                observedPeakDbfs,
                deltaDb,
                frameRoundedSourcePeakDbfs,
                sourceDerivedExpectedPeakDbfs,
                sourceDerivedDeltaDb,
                targetDeltaDb,
                gainWithinTolerance,
                targetWithinTolerance,
                withinTolerance,
                resultReason));
        }

        var ordered = results.OrderBy(result => result.TimelineStartFrame).ToArray();
        var passedCount = ordered.Count(result => result.WithinTolerance);
        var uncapped = ordered.Where(result => !result.GainWasCapped).ToArray();
        var targetPassedCount = uncapped.Count(result => result.TargetWithinTolerance);
        var finiteDeltas = ordered
            .Where(result => result.DeltaDb is not null)
            .Select(result => result.DeltaDb!.Value)
            .Order()
            .ToArray();
        var medianOffset = Median(finiteDeltas);
        var matchingMedianOffset = medianOffset is null
            ? 0
            : finiteDeltas.Count(delta => Math.Abs(delta - medianOffset.Value) <= toleranceDb);
        var sourceDerivedDeltas = ordered
            .Where(result => result.SourceDerivedDeltaDb is not null)
            .Select(result => result.SourceDerivedDeltaDb!.Value)
            .Order()
            .ToArray();
        var sourceDerivedMedianOffset = Median(sourceDerivedDeltas);
        var matchingSourceDerivedOffset = sourceDerivedMedianOffset is null
            ? 0
            : sourceDerivedDeltas.Count(delta => Math.Abs(delta - sourceDerivedMedianOffset.Value) <= toleranceDb);
        return new(
            trackIndex,
            renderedWave.SampleRate,
            renderedWave.ValidBitsPerSample,
            renderedWave.SampleFrameCount,
            toleranceDb,
            speechFragments.Length,
            ordered.Length,
            passedCount,
            ordered.Length - passedCount,
            uncapped.Length,
            targetPassedCount,
            uncapped.Length - targetPassedCount,
            medianOffset,
            matchingMedianOffset,
            ordered.Length - matchingMedianOffset,
            sourceMediaByFileId is not null,
            sourceDerivedMedianOffset,
            matchingSourceDerivedOffset,
            sourceDerivedDeltas.Length - matchingSourceDerivedOffset,
            passedCount == ordered.Length,
            ordered);
    }

    private float MeasurePeak(
        WaveFileInfo wave,
        long startSample,
        long endSample,
        CancellationToken cancellationToken)
    {
        var maximumAbsoluteSample = 0f;
        _sampleReader.ReadRange(
            wave,
            startSample,
            endSample - startSample,
            samples =>
            {
                foreach (var sample in samples.Span)
                {
                    maximumAbsoluteSample = Math.Max(maximumAbsoluteSample, Math.Abs(sample));
                }
            },
            cancellationToken);
        return maximumAbsoluteSample;
    }

    private static double? Median(IReadOnlyList<double> sortedValues)
    {
        if (sortedValues.Count == 0)
        {
            return null;
        }

        var middle = sortedValues.Count / 2;
        return sortedValues.Count % 2 == 0
            ? (sortedValues[middle - 1] + sortedValues[middle]) / 2d
            : sortedValues[middle];
    }

    private static void ValidateWave(WaveFileInfo wave)
    {
        if (wave.ChannelCount != 1 || wave.SampleRate != SupportedSampleRate)
        {
            throw new InvalidDataException("PCM pilot phải là WAV mono 48 kHz export từ đầu sequence.");
        }

        if (wave.ContainerBitsPerSample is not (16 or 24 or 32) ||
            wave.ValidBitsPerSample is not (16 or 24 or 32) ||
            wave.ValidBitsPerSample > wave.ContainerBitsPerSample)
        {
            throw new InvalidDataException("PCM pilot phải là integer PCM 16/24/32-bit.");
        }
    }

    private static void ValidatePhraseAudit(string phraseId, IReadOnlyList<FragmentAudit> fragments)
    {
        var first = fragments[0];
        if (first.MeasuredPeakDbfs is null || first.AppliedGainDb is null)
        {
            throw new InvalidDataException($"Phrase {phraseId} thiếu peak/gain trong audit.");
        }

        if (fragments.Any(fragment =>
                fragment.MeasuredPeakDbfs != first.MeasuredPeakDbfs ||
                fragment.AppliedGainDb != first.AppliedGainDb ||
                fragment.GainWasCapped != first.GainWasCapped))
        {
            throw new InvalidDataException($"Phrase {phraseId} không dùng một quyết định gain thống nhất.");
        }
    }
}
