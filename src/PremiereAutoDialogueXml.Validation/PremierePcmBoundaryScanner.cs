using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Audio.Pcm;
using PremiereAutoDialogueXml.Core.Media;
using PremiereAutoDialogueXml.Core.Timing;
using PremiereAutoDialogueXml.Output.Audit;

namespace PremiereAutoDialogueXml.Validation;

public sealed record PremierePcmBoundarySample(
    int TrackIndex,
    string SourceClipId,
    string SourceFileId,
    string SourceFileName,
    long BoundaryFrame,
    string BoundaryTimecode,
    long BoundarySampleIndex,
    BoundaryTransitionKind Kind,
    AudioSegmentStatus LeftStatus,
    AudioSegmentStatus RightStatus,
    bool LeftEnabled,
    bool RightEnabled,
    double LeftGainDb,
    double RightGainDb,
    float LeftPcmSample,
    float RightPcmSample,
    double ActualStep,
    double ActualStepDbfs,
    double LocalP99DerivativeDbfs,
    double LocalMaximumDerivativeDbfs,
    double StepAboveLocalP99Db,
    bool TransientScreeningCandidate);

public sealed record PremierePcmBoundaryScanReport(
    string SchemaVersion,
    string Policy,
    int TrackIndex,
    int FrameRate,
    int SampleRate,
    int SamplesPerFrame,
    int ContextRadiusSamples,
    double StepThresholdDbfs,
    double OutlierThresholdDb,
    int SourceClipCount,
    int AppCreatedBoundaryCount,
    int IgnoredContinuousBoundaryCount,
    int TransitionCount,
    int EnabledToDisabledCount,
    int DisabledToEnabledCount,
    int GainChangeCount,
    int TransientScreeningCandidateCount,
    int EnabledToDisabledCandidateCount,
    int DisabledToEnabledCandidateCount,
    int GainChangeCandidateCount,
    double MaximumActualStepDbfs,
    double P95ActualStepDbfs,
    double MaximumStepAboveLocalP99Db,
    string TransitionStreamSha256,
    long? FocusedBoundaryFrame,
    PremierePcmBoundarySample? FocusedSample,
    int CapturedSampleCount,
    IReadOnlyList<PremierePcmBoundarySample> TopSamples);

public sealed class PremierePcmBoundaryScanner
{
    public const string Policy = "phase14-premiere-pcm-boundary-transient-screen-v1";
    public const int DefaultContextRadiusSamples = 480;
    public const double DefaultStepThresholdDbfs = -40;
    public const double DefaultOutlierThresholdDb = 12;
    public const int DefaultMaximumCapturedSamples = 64;
    private const int SupportedSampleRate = 48_000;
    private const double MinimumDbfs = -120;
    private const double GainEqualityToleranceDb = 0.000_001;

    public PremierePcmBoundaryScanReport Scan(
        OutputAudit audit,
        WaveFileInfo renderedWave,
        int trackIndex,
        int contextRadiusSamples = DefaultContextRadiusSamples,
        double stepThresholdDbfs = DefaultStepThresholdDbfs,
        double outlierThresholdDb = DefaultOutlierThresholdDb,
        int maximumCapturedSamples = DefaultMaximumCapturedSamples,
        long? focusedBoundaryFrame = null)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(renderedWave);
        if (trackIndex <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackIndex));
        }

        if (contextRadiusSamples is < 16 or > SupportedSampleRate)
        {
            throw new ArgumentOutOfRangeException(nameof(contextRadiusSamples));
        }

        if (!double.IsFinite(stepThresholdDbfs) || stepThresholdDbfs is < MinimumDbfs or > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stepThresholdDbfs));
        }

        if (!double.IsFinite(outlierThresholdDb) || outlierThresholdDb is < 0 or > 120)
        {
            throw new ArgumentOutOfRangeException(nameof(outlierThresholdDb));
        }

        if (maximumCapturedSamples is < 1 or > 1_024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCapturedSamples));
        }

        if (focusedBoundaryFrame is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(focusedBoundaryFrame));
        }

        ValidateWave(renderedWave);
        var frameGrid = ResolveFrameGrid(audit);
        var trackFragments = audit.Fragments
            .Where(fragment => fragment.TrackIndex == trackIndex)
            .ToArray();
        if (trackFragments.Length == 0)
        {
            throw new InvalidDataException($"Audit không có fragment trên track A{trackIndex}.");
        }

        ValidateAuditFragments(trackFragments);
        var maximumTimelineEndFrame = audit.Fragments.Max(fragment => fragment.TimelineEndFrame);
        var minimumRequiredSamples = frameGrid.FrameToSample(maximumTimelineEndFrame);
        if (renderedWave.SampleFrameCount < minimumRequiredSamples)
        {
            throw new InvalidDataException(
                $"PCM A{trackIndex} kết thúc trước timeline audit; cần tối thiểu {minimumRequiredSamples} sample frame.");
        }

        var groups = trackFragments
            .GroupBy(fragment => fragment.SourceClipId, StringComparer.Ordinal)
            .OrderBy(group => group.Min(fragment => fragment.TimelineStartFrame))
            .ToArray();
        var drafts = new List<BoundaryDraft>();
        var appCreatedBoundaryCount = 0;
        var ignoredContinuousBoundaryCount = 0;
        foreach (var group in groups)
        {
            var fragments = group.OrderBy(fragment => fragment.TimelineStartFrame).ToArray();
            for (var index = 0; index + 1 < fragments.Length; index++)
            {
                var left = fragments[index];
                var right = fragments[index + 1];
                if (left.TimelineEndFrame != right.TimelineStartFrame)
                {
                    throw new InvalidDataException(
                        $"Audit không liên tục tại A{trackIndex}/{group.Key}.");
                }

                appCreatedBoundaryCount++;
                var kind = Classify(left, right);
                if (kind is null)
                {
                    ignoredContinuousBoundaryCount++;
                    continue;
                }

                var boundarySampleIndex = frameGrid.FrameToSample(right.TimelineStartFrame);
                if (boundarySampleIndex <= 0 || boundarySampleIndex >= renderedWave.SampleFrameCount)
                {
                    throw new InvalidDataException(
                        $"Boundary A{trackIndex}/{group.Key} nằm ngoài PCM pilot.");
                }

                drafts.Add(new(left, right, kind.Value, boundarySampleIndex));
            }
        }

        var captured = new PriorityQueue<PremierePcmBoundarySample, double>();
        var actualStepDbfsValues = new List<double>(drafts.Count);
        using var transitionHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(
            renderedWave.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.RandomAccess);
        var enabledToDisabledCount = 0;
        var disabledToEnabledCount = 0;
        var gainChangeCount = 0;
        var candidateCount = 0;
        var enabledToDisabledCandidateCount = 0;
        var disabledToEnabledCandidateCount = 0;
        var gainChangeCandidateCount = 0;
        var maximumActualStepDbfs = MinimumDbfs;
        var maximumStepAboveLocalP99Db = 0d;
        PremierePcmBoundarySample? focusedSample = null;

        foreach (var draft in drafts)
        {
            var measurement = MeasureBoundary(
                stream,
                renderedWave,
                draft.BoundarySampleIndex,
                contextRadiusSamples);
            var actualStepDbfs = ToDbfs(measurement.ActualStep);
            var localP99Dbfs = ToDbfs(measurement.LocalP99Derivative);
            var localMaximumDbfs = ToDbfs(measurement.LocalMaximumDerivative);
            var stepAboveLocalP99Db = actualStepDbfs - localP99Dbfs;
            var candidate = actualStepDbfs >= stepThresholdDbfs &&
                stepAboveLocalP99Db >= outlierThresholdDb;
            var sample = new PremierePcmBoundarySample(
                trackIndex,
                draft.Left.SourceClipId,
                draft.Left.SourceFileId,
                draft.Left.SourceFileName,
                draft.Right.TimelineStartFrame,
                ToTimecode(draft.Right.TimelineStartFrame, frameGrid.FramesPerSecond),
                draft.BoundarySampleIndex,
                draft.Kind,
                draft.Left.Status,
                draft.Right.Status,
                draft.Left.Enabled,
                draft.Right.Enabled,
                EffectiveGainDb(draft.Left),
                EffectiveGainDb(draft.Right),
                measurement.LeftSample,
                measurement.RightSample,
                measurement.ActualStep,
                actualStepDbfs,
                localP99Dbfs,
                localMaximumDbfs,
                stepAboveLocalP99Db,
                candidate);
            if (focusedBoundaryFrame == sample.BoundaryFrame)
            {
                if (focusedSample is not null)
                {
                    throw new InvalidDataException("Có nhiều transition trùng focused boundary frame.");
                }

                focusedSample = sample;
            }

            switch (draft.Kind)
            {
                case BoundaryTransitionKind.EnabledToDisabled:
                    enabledToDisabledCount++;
                    if (candidate)
                    {
                        enabledToDisabledCandidateCount++;
                    }

                    break;
                case BoundaryTransitionKind.DisabledToEnabled:
                    disabledToEnabledCount++;
                    if (candidate)
                    {
                        disabledToEnabledCandidateCount++;
                    }

                    break;
                case BoundaryTransitionKind.GainChange:
                    gainChangeCount++;
                    if (candidate)
                    {
                        gainChangeCandidateCount++;
                    }

                    break;
                default:
                    throw new InvalidOperationException("Boundary transition kind không hợp lệ.");
            }

            if (candidate)
            {
                candidateCount++;
            }

            maximumActualStepDbfs = Math.Max(maximumActualStepDbfs, actualStepDbfs);
            maximumStepAboveLocalP99Db = Math.Max(maximumStepAboveLocalP99Db, stepAboveLocalP99Db);
            actualStepDbfsValues.Add(actualStepDbfs);
            AppendCanonicalTransition(transitionHash, sample);
            captured.Enqueue(sample, measurement.ActualStep);
            if (captured.Count > maximumCapturedSamples)
            {
                captured.Dequeue();
            }
        }

        var topSamples = captured.UnorderedItems
            .Select(item => item.Element)
            .OrderByDescending(sample => sample.ActualStep)
            .ThenByDescending(sample => sample.StepAboveLocalP99Db)
            .ThenBy(sample => sample.BoundaryFrame)
            .ToArray();
        if (focusedBoundaryFrame is not null && focusedSample is null)
        {
            throw new InvalidDataException("Không tìm thấy transition tại focused boundary frame.");
        }

        return new(
            "1.1",
            Policy,
            trackIndex,
            frameGrid.FramesPerSecond,
            renderedWave.SampleRate,
            frameGrid.SamplesPerFrame,
            contextRadiusSamples,
            stepThresholdDbfs,
            outlierThresholdDb,
            groups.Length,
            appCreatedBoundaryCount,
            ignoredContinuousBoundaryCount,
            drafts.Count,
            enabledToDisabledCount,
            disabledToEnabledCount,
            gainChangeCount,
            candidateCount,
            enabledToDisabledCandidateCount,
            disabledToEnabledCandidateCount,
            gainChangeCandidateCount,
            maximumActualStepDbfs,
            Percentile95(actualStepDbfsValues),
            maximumStepAboveLocalP99Db,
            Convert.ToHexString(transitionHash.GetHashAndReset()),
            focusedBoundaryFrame,
            focusedSample,
            topSamples.Length,
            topSamples);
    }

    private static BoundaryMeasurement MeasureBoundary(
        FileStream stream,
        WaveFileInfo wave,
        long boundarySampleIndex,
        int contextRadiusSamples)
    {
        var startSample = Math.Max(0, boundarySampleIndex - contextRadiusSamples - 1);
        var endSample = Math.Min(wave.SampleFrameCount, boundarySampleIndex + contextRadiusSamples + 1);
        var sampleCount = checked((int)(endSample - startSample));
        var centerIndex = checked((int)(boundarySampleIndex - startSample));
        if (centerIndex <= 0 || centerIndex >= sampleCount)
        {
            throw new InvalidDataException("Không đủ PCM hai phía boundary để đo.");
        }

        var byteCount = checked(sampleCount * wave.BlockAlign);
        var bytes = ArrayPool<byte>.Shared.Rent(byteCount);
        var samples = ArrayPool<float>.Shared.Rent(sampleCount);
        var derivatives = ArrayPool<double>.Shared.Rent(sampleCount - 2);
        try
        {
            stream.Position = checked(wave.DataOffset + (startSample * wave.BlockAlign));
            stream.ReadExactly(bytes.AsSpan(0, byteCount));
            PcmWaveSampleReader.DecodeMono(
                bytes.AsSpan(0, byteCount),
                samples.AsSpan(0, sampleCount),
                wave);
            var derivativeCount = 0;
            var localMaximum = 0d;
            for (var index = 1; index < sampleCount; index++)
            {
                if (index == centerIndex)
                {
                    continue;
                }

                var derivative = Math.Abs(samples[index] - samples[index - 1]);
                derivatives[derivativeCount++] = derivative;
                localMaximum = Math.Max(localMaximum, derivative);
            }

            Array.Sort(derivatives, 0, derivativeCount);
            var p99Index = Math.Clamp(
                (int)Math.Ceiling(derivativeCount * 0.99) - 1,
                0,
                derivativeCount - 1);
            var leftSample = samples[centerIndex - 1];
            var rightSample = samples[centerIndex];
            return new(
                leftSample,
                rightSample,
                Math.Abs(rightSample - leftSample),
                derivatives[p99Index],
                localMaximum);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(derivatives, clearArray: true);
            ArrayPool<float>.Shared.Return(samples, clearArray: true);
            ArrayPool<byte>.Shared.Return(bytes, clearArray: true);
        }
    }

    private static void ValidateAuditFragments(IReadOnlyList<FragmentAudit> fragments)
    {
        foreach (var fragment in fragments)
        {
            var expectedEnabled = fragment.Status is AudioSegmentStatus.Speech or AudioSegmentStatus.Ambiguous;
            if (fragment.Enabled != expectedEnabled ||
                fragment.TimelineStartFrame >= fragment.TimelineEndFrame)
            {
                throw new InvalidDataException(
                    $"Fragment A{fragment.TrackIndex}/{fragment.ClipItemId} không hợp lệ cho PCM boundary scan.");
            }

            if (fragment.Enabled && fragment.AppliedGainDb is { } gain && !double.IsFinite(gain))
            {
                throw new InvalidDataException(
                    $"Fragment A{fragment.TrackIndex}/{fragment.ClipItemId} có gain không hữu hạn.");
            }
        }
    }

    private static PremiereNdfFrameGrid ResolveFrameGrid(OutputAudit audit)
    {
        var timing = audit.SequenceTiming
            ?? throw new InvalidDataException("PCM boundary scan yêu cầu audit có sequenceTiming Phase 12+.");
        if (timing.Ntsc ||
            !string.Equals(
                timing.FrameGridPolicy,
                SequenceTimingAudit.ExactFrameGridPolicy,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Audit không dùng frame-grid NDF nguyên đã khóa.");
        }

        PremiereNdfFrameGrid frameGrid;
        try
        {
            frameGrid = PremiereNdfFrameGrid.Create(timing.FrameRate, timing.AudioSampleRate);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("sequenceTiming không tạo được frame-grid PCM.", exception);
        }

        if (timing.AudioSampleRate != SupportedSampleRate ||
            timing.SamplesPerFrame != frameGrid.SamplesPerFrame)
        {
            throw new InvalidDataException("sequenceTiming không khớp PCM mono 48 kHz.");
        }

        return frameGrid;
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

    private static BoundaryTransitionKind? Classify(FragmentAudit left, FragmentAudit right)
    {
        if (left.Enabled && !right.Enabled)
        {
            return BoundaryTransitionKind.EnabledToDisabled;
        }

        if (!left.Enabled && right.Enabled)
        {
            return BoundaryTransitionKind.DisabledToEnabled;
        }

        return left.Enabled && right.Enabled &&
               Math.Abs(EffectiveGainDb(left) - EffectiveGainDb(right)) > GainEqualityToleranceDb
            ? BoundaryTransitionKind.GainChange
            : null;
    }

    private static double EffectiveGainDb(FragmentAudit fragment) =>
        fragment.Enabled ? fragment.AppliedGainDb ?? 0 : 0;

    private static double ToDbfs(double amplitude) =>
        amplitude <= 0 ? MinimumDbfs : Math.Max(MinimumDbfs, 20 * Math.Log10(amplitude));

    private static double Percentile95(List<double> values)
    {
        if (values.Count == 0)
        {
            return MinimumDbfs;
        }

        values.Sort();
        var index = Math.Clamp((int)Math.Ceiling(values.Count * 0.95) - 1, 0, values.Count - 1);
        return values[index];
    }

    private static string ToTimecode(long frame, int frameRate)
    {
        var hours = frame / (frameRate * 60L * 60L);
        var minutes = frame / (frameRate * 60L) % 60;
        var seconds = frame / frameRate % 60;
        var frames = frame % frameRate;
        return $"{hours:00}:{minutes:00}:{seconds:00}:{frames:00}";
    }

    private static void AppendCanonicalTransition(
        IncrementalHash hash,
        PremierePcmBoundarySample sample)
    {
        var line = string.Join('|',
            sample.TrackIndex.ToString(CultureInfo.InvariantCulture),
            sample.SourceClipId,
            sample.SourceFileId,
            sample.BoundaryFrame.ToString(CultureInfo.InvariantCulture),
            sample.Kind,
            sample.LeftStatus,
            sample.RightStatus,
            sample.LeftGainDb.ToString("R", CultureInfo.InvariantCulture),
            sample.RightGainDb.ToString("R", CultureInfo.InvariantCulture),
            sample.LeftPcmSample.ToString("R", CultureInfo.InvariantCulture),
            sample.RightPcmSample.ToString("R", CultureInfo.InvariantCulture),
            sample.ActualStep.ToString("R", CultureInfo.InvariantCulture),
            sample.LocalP99DerivativeDbfs.ToString("R", CultureInfo.InvariantCulture),
            sample.StepAboveLocalP99Db.ToString("R", CultureInfo.InvariantCulture),
            sample.TransientScreeningCandidate ? "1" : "0") + "\n";
        hash.AppendData(Encoding.UTF8.GetBytes(line));
    }

    private sealed record BoundaryDraft(
        FragmentAudit Left,
        FragmentAudit Right,
        BoundaryTransitionKind Kind,
        long BoundarySampleIndex);

    private readonly record struct BoundaryMeasurement(
        float LeftSample,
        float RightSample,
        double ActualStep,
        double LocalP99Derivative,
        double LocalMaximumDerivative);
}
