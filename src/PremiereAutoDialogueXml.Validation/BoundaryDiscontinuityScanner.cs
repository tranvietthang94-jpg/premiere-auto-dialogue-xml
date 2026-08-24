using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Audio.Pcm;
using PremiereAutoDialogueXml.Core.Media;
using PremiereAutoDialogueXml.Core.ProjectModel;
using PremiereAutoDialogueXml.Output.Audit;

namespace PremiereAutoDialogueXml.Validation;

public enum BoundaryTransitionKind
{
    EnabledToDisabled,
    DisabledToEnabled,
    GainChange
}

public sealed record BoundaryDiscontinuitySample(
    int TrackIndex,
    string SourceClipId,
    string SourceFileId,
    string SourceFileName,
    long BoundaryFrame,
    BoundaryTransitionKind Kind,
    AudioSegmentStatus LeftStatus,
    AudioSegmentStatus RightStatus,
    bool LeftEnabled,
    bool RightEnabled,
    double LeftGainDb,
    double RightGainDb,
    long LeftSourceSampleIndex,
    long RightSourceSampleIndex,
    float LeftSourceSample,
    float RightSourceSample,
    double LeftRenderedSample,
    double RightRenderedSample,
    double SourceStep,
    double RenderedStep,
    double ExcessStep,
    double SourceStepDbfs,
    double RenderedStepDbfs,
    double ExcessStepDbfs,
    bool ScreeningCandidate,
    double LocalP99RenderedDerivativeDbfs,
    double RenderedStepAboveLocalP99Db,
    bool TransientScreeningCandidate);

public sealed record BoundaryDiscontinuityScanReport(
    string SchemaVersion,
    string Policy,
    double ScreeningThresholdDbfs,
    int SourceClipCount,
    int AppCreatedBoundaryCount,
    int IgnoredContinuousBoundaryCount,
    int TransitionCount,
    int EnabledToDisabledCount,
    int DisabledToEnabledCount,
    int GainChangeCount,
    int ScreeningCandidateCount,
    int TransientScreeningCandidateCount,
    double MaximumRenderedStepDbfs,
    double MaximumExcessStepDbfs,
    double P95ExcessStepDbfs,
    double MaximumRenderedStepAboveLocalP99Db,
    string TransitionStreamSha256,
    int CapturedSampleCount,
    IReadOnlyList<BoundaryDiscontinuitySample> TopSamples);

public sealed class BoundaryDiscontinuityScanner
{
    public const string Policy = "phase14-app-created-boundary-discontinuity-screen-v2";
    public const double DefaultScreeningThresholdDbfs = -40;
    public const double DefaultLocalOutlierThresholdDb = 12;
    public const int LocalContextRadiusMilliseconds = 10;
    public const int DefaultMaximumCapturedSamples = 64;
    private const double MinimumDbfs = -120;
    private const double GainEqualityToleranceDb = 0.000_001;

    public BoundaryDiscontinuityScanReport Scan(
        OutputAudit audit,
        PremiereProject project,
        double screeningThresholdDbfs = DefaultScreeningThresholdDbfs,
        int maximumCapturedSamples = DefaultMaximumCapturedSamples)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(project);
        if (!double.IsFinite(screeningThresholdDbfs) || screeningThresholdDbfs is < MinimumDbfs or > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(screeningThresholdDbfs));
        }

        if (maximumCapturedSamples is < 1 or > 1_024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCapturedSamples));
        }

        ValidateAuditContract(audit, project);
        var clips = project.Sequence.AudioTracks
            .SelectMany(track => track.Clips.Select(clip => new ClipKey(track.Index, clip.Id, clip)))
            .ToDictionary(item => (item.TrackIndex, item.SourceClipId));
        var media = BuildMediaMap(project);
        var drafts = new List<BoundaryDraft>();
        var appCreatedBoundaryCount = 0;
        var ignoredContinuousBoundaryCount = 0;

        var auditGroups = audit.Fragments
            .GroupBy(fragment => (fragment.TrackIndex, fragment.SourceClipId))
            .ToDictionary(group => group.Key, group => group
                .OrderBy(fragment => fragment.TimelineStartFrame)
                .ToArray());

        foreach (var auditGroup in auditGroups.Keys)
        {
            if (!clips.ContainsKey(auditGroup))
            {
                throw new InvalidDataException(
                    $"Audit trỏ source clip không tồn tại: A{auditGroup.TrackIndex}/{auditGroup.SourceClipId}.");
            }
        }

        foreach (var item in clips.Values.OrderBy(item => item.TrackIndex).ThenBy(item => item.Clip.TimelineStartFrame))
        {
            if (!auditGroups.TryGetValue((item.TrackIndex, item.SourceClipId), out var fragments))
            {
                throw new InvalidDataException(
                    $"Audit thiếu fragment cho source clip A{item.TrackIndex}/{item.SourceClipId}.");
            }

            ValidateFragmentCoverage(item, fragments);
            for (var index = 0; index + 1 < fragments.Length; index++)
            {
                var left = fragments[index];
                var right = fragments[index + 1];
                appCreatedBoundaryCount++;
                var kind = Classify(left, right);
                if (kind is null)
                {
                    ignoredContinuousBoundaryCount++;
                    continue;
                }

                if (!media.TryGetValue(left.SourceFileId, out var wave))
                {
                    throw new InvalidDataException($"Không tìm thấy media {left.SourceFileId} của boundary.");
                }

                var leftSampleIndex = checked(left.SourceEndSample - 1);
                var rightSampleIndex = right.SourceStartSample;
                ValidateSampleIndex(wave, leftSampleIndex, left.SourceFileId);
                ValidateSampleIndex(wave, rightSampleIndex, right.SourceFileId);
                drafts.Add(new(left, right, wave, kind.Value, leftSampleIndex, rightSampleIndex));
            }
        }

        var samples = ReadRequestedSamples(drafts);
        var localMeasurements = ReadLocalMeasurements(drafts);
        var captured = new PriorityQueue<BoundaryDiscontinuitySample, double>();
        var excessStepDbfsValues = new List<double>(drafts.Count);
        using var transitionHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var enabledToDisabledCount = 0;
        var disabledToEnabledCount = 0;
        var gainChangeCount = 0;
        var screeningCandidateCount = 0;
        var transientScreeningCandidateCount = 0;
        var maximumRenderedStepDbfs = MinimumDbfs;
        var maximumExcessStepDbfs = MinimumDbfs;
        var maximumRenderedStepAboveLocalP99Db = 0d;

        for (var draftIndex = 0; draftIndex < drafts.Count; draftIndex++)
        {
            var draft = drafts[draftIndex];
            var leftSourceSample = samples[new(draft.Left.SourceFileId, draft.LeftSampleIndex)];
            var rightSourceSample = samples[new(draft.Right.SourceFileId, draft.RightSampleIndex)];
            var leftGainDb = EffectiveGainDb(draft.Left);
            var rightGainDb = EffectiveGainDb(draft.Right);
            var leftRenderedSample = draft.Left.Enabled
                ? leftSourceSample * DbToLinear(leftGainDb)
                : 0;
            var rightRenderedSample = draft.Right.Enabled
                ? rightSourceSample * DbToLinear(rightGainDb)
                : 0;
            var sourceStep = Math.Abs(rightSourceSample - leftSourceSample);
            var renderedStep = Math.Abs(rightRenderedSample - leftRenderedSample);
            var excessStep = Math.Max(0, renderedStep - sourceStep);
            var sourceStepDbfs = ToDbfs(sourceStep);
            var renderedStepDbfs = ToDbfs(renderedStep);
            var excessStepDbfs = ToDbfs(excessStep);
            var screeningCandidate = excessStepDbfs >= screeningThresholdDbfs;
            var localP99RenderedDerivativeDbfs = ToDbfs(localMeasurements[draftIndex].LocalP99RenderedDerivative);
            var renderedStepAboveLocalP99Db = renderedStepDbfs - localP99RenderedDerivativeDbfs;
            var transientScreeningCandidate =
                renderedStepDbfs >= screeningThresholdDbfs &&
                renderedStepAboveLocalP99Db >= DefaultLocalOutlierThresholdDb;
            var sample = new BoundaryDiscontinuitySample(
                draft.Left.TrackIndex,
                draft.Left.SourceClipId,
                draft.Left.SourceFileId,
                draft.Left.SourceFileName,
                draft.Right.TimelineStartFrame,
                draft.Kind,
                draft.Left.Status,
                draft.Right.Status,
                draft.Left.Enabled,
                draft.Right.Enabled,
                leftGainDb,
                rightGainDb,
                draft.LeftSampleIndex,
                draft.RightSampleIndex,
                leftSourceSample,
                rightSourceSample,
                leftRenderedSample,
                rightRenderedSample,
                sourceStep,
                renderedStep,
                excessStep,
                sourceStepDbfs,
                renderedStepDbfs,
                excessStepDbfs,
                screeningCandidate,
                localP99RenderedDerivativeDbfs,
                renderedStepAboveLocalP99Db,
                transientScreeningCandidate);

            switch (draft.Kind)
            {
                case BoundaryTransitionKind.EnabledToDisabled:
                    enabledToDisabledCount++;
                    break;
                case BoundaryTransitionKind.DisabledToEnabled:
                    disabledToEnabledCount++;
                    break;
                case BoundaryTransitionKind.GainChange:
                    gainChangeCount++;
                    break;
                default:
                    throw new InvalidOperationException("Boundary transition kind không hợp lệ.");
            }

            if (screeningCandidate)
            {
                screeningCandidateCount++;
            }

            if (transientScreeningCandidate)
            {
                transientScreeningCandidateCount++;
            }

            maximumRenderedStepDbfs = Math.Max(maximumRenderedStepDbfs, renderedStepDbfs);
            maximumExcessStepDbfs = Math.Max(maximumExcessStepDbfs, excessStepDbfs);
            maximumRenderedStepAboveLocalP99Db = Math.Max(
                maximumRenderedStepAboveLocalP99Db,
                renderedStepAboveLocalP99Db);
            excessStepDbfsValues.Add(excessStepDbfs);
            AppendCanonicalTransition(transitionHash, sample);
            captured.Enqueue(sample, excessStep);
            if (captured.Count > maximumCapturedSamples)
            {
                captured.Dequeue();
            }
        }

        var topSamples = captured.UnorderedItems
            .Select(item => item.Element)
            .OrderByDescending(sample => sample.ExcessStep)
            .ThenByDescending(sample => sample.RenderedStep)
            .ThenBy(sample => sample.TrackIndex)
            .ThenBy(sample => sample.BoundaryFrame)
            .ToArray();
        var transitionStreamSha256 = Convert.ToHexString(transitionHash.GetHashAndReset());
        return new(
            "1.1",
            Policy,
            screeningThresholdDbfs,
            clips.Count,
            appCreatedBoundaryCount,
            ignoredContinuousBoundaryCount,
            drafts.Count,
            enabledToDisabledCount,
            disabledToEnabledCount,
            gainChangeCount,
            screeningCandidateCount,
            transientScreeningCandidateCount,
            maximumRenderedStepDbfs,
            maximumExcessStepDbfs,
            Percentile95(excessStepDbfsValues),
            maximumRenderedStepAboveLocalP99Db,
            transitionStreamSha256,
            topSamples.Length,
            topSamples);
    }

    private static void ValidateAuditContract(OutputAudit audit, PremiereProject project)
    {
        if (!string.Equals(audit.SourceXmlSha256, project.SourceXmlSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("SHA-256 XML nguồn không khớp audit boundary.");
        }

        if (audit.Fragments.Count == 0)
        {
            throw new InvalidDataException("Audit không có fragment để quét boundary.");
        }

        if (Version.TryParse(audit.SchemaVersion, out var schemaVersion) && schemaVersion >= new Version(1, 8))
        {
            var timing = audit.SequenceTiming
                ?? throw new InvalidDataException("Audit Phase 12+ thiếu sequence timing.");
            if (timing.FrameRate != project.Sequence.FrameRate ||
                timing.AudioSampleRate != project.Sequence.AudioSampleRate ||
                timing.Ntsc ||
                timing.SamplesPerFrame != project.Sequence.AudioSampleRate / project.Sequence.FrameRate ||
                timing.FrameGridPolicy != SequenceTimingAudit.ExactFrameGridPolicy)
            {
                throw new InvalidDataException("Sequence timing trong audit không khớp XML nguồn.");
            }
        }

        foreach (var fragment in audit.Fragments)
        {
            var expectedEnabled = fragment.Status is AudioSegmentStatus.Speech or AudioSegmentStatus.Ambiguous;
            if (fragment.Enabled != expectedEnabled)
            {
                throw new InvalidDataException(
                    $"Fragment A{fragment.TrackIndex}/{fragment.ClipItemId} có status/enabled không hợp lệ.");
            }

            if (fragment.TimelineStartFrame >= fragment.TimelineEndFrame ||
                fragment.SourceStartSample < 0 ||
                fragment.SourceStartSample >= fragment.SourceEndSample)
            {
                throw new InvalidDataException(
                    $"Fragment A{fragment.TrackIndex}/{fragment.ClipItemId} có range không hợp lệ.");
            }

            if (fragment.Enabled &&
                fragment.AppliedGainDb is { } gain &&
                !double.IsFinite(gain))
            {
                throw new InvalidDataException(
                    $"Fragment A{fragment.TrackIndex}/{fragment.ClipItemId} có gain không hữu hạn.");
            }
        }
    }

    private static IReadOnlyDictionary<string, WaveFileInfo> BuildMediaMap(PremiereProject project)
    {
        var result = new Dictionary<string, WaveFileInfo>(StringComparer.Ordinal);
        foreach (var clip in project.Sequence.AudioTracks.SelectMany(track => track.Clips))
        {
            if (result.TryGetValue(clip.SourceFileId, out var existing))
            {
                if (!string.Equals(existing.Path, clip.SourceMedia.Wave.Path, StringComparison.OrdinalIgnoreCase) ||
                    existing != clip.SourceMedia.Wave)
                {
                    throw new InvalidDataException($"Source file ID {clip.SourceFileId} trỏ nhiều WAV khác nhau.");
                }

                continue;
            }

            result.Add(clip.SourceFileId, clip.SourceMedia.Wave);
        }

        return result;
    }

    private static void ValidateFragmentCoverage(ClipKey item, IReadOnlyList<FragmentAudit> fragments)
    {
        if (fragments[0].TimelineStartFrame != item.Clip.TimelineStartFrame ||
            fragments[^1].TimelineEndFrame != item.Clip.TimelineEndFrame)
        {
            throw new InvalidDataException(
                $"Fragment audit không phủ đúng source clip A{item.TrackIndex}/{item.SourceClipId}.");
        }

        FragmentAudit? previous = null;
        foreach (var fragment in fragments)
        {
            if (fragment.SourceFileId != item.Clip.SourceFileId ||
                !string.Equals(
                    fragment.SourceFileName,
                    Path.GetFileName(item.Clip.SourceMedia.LocalPath),
                    StringComparison.OrdinalIgnoreCase) ||
                fragment.SourceStartSample < item.Clip.SourceStartSample ||
                fragment.SourceEndSample > item.Clip.SourceEndSample)
            {
                throw new InvalidDataException(
                    $"Fragment audit không khớp source mapping A{item.TrackIndex}/{item.SourceClipId}.");
            }

            if (previous is not null && previous.TimelineEndFrame != fragment.TimelineStartFrame)
            {
                throw new InvalidDataException(
                    $"Fragment audit không liên tục trên A{item.TrackIndex}/{item.SourceClipId}.");
            }

            previous = fragment;
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

    private static Dictionary<SampleKey, float> ReadRequestedSamples(IReadOnlyList<BoundaryDraft> drafts)
    {
        var waves = drafts
            .GroupBy(draft => draft.Left.SourceFileId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Wave, StringComparer.Ordinal);
        var requests = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        foreach (var draft in drafts)
        {
            if (!requests.TryGetValue(draft.Left.SourceFileId, out var indexes))
            {
                indexes = [];
                requests.Add(draft.Left.SourceFileId, indexes);
            }

            indexes.Add(draft.LeftSampleIndex);
            indexes.Add(draft.RightSampleIndex);
        }

        var result = new Dictionary<SampleKey, float>();
        foreach (var request in requests.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var wave = waves[request.Key];
            using var stream = new FileStream(
                wave.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.RandomAccess);
            var bytes = new byte[wave.BlockAlign];
            var decoded = new float[1];
            foreach (var sampleIndex in request.Value.Order())
            {
                ValidateSampleIndex(wave, sampleIndex, request.Key);
                var byteOffset = checked(wave.DataOffset + (sampleIndex * wave.BlockAlign));
                if (stream.Position != byteOffset)
                {
                    stream.Position = byteOffset;
                }

                stream.ReadExactly(bytes);
                PcmWaveSampleReader.DecodeMono(bytes, decoded, wave);
                result.Add(new(request.Key, sampleIndex), decoded[0]);
            }
        }

        return result;
    }

    private static IReadOnlyList<LocalBoundaryMeasurement> ReadLocalMeasurements(
        IReadOnlyList<BoundaryDraft> drafts)
    {
        var result = new LocalBoundaryMeasurement[drafts.Count];
        var streams = new Dictionary<string, FileStream>(StringComparer.Ordinal);
        try
        {
            for (var index = 0; index < drafts.Count; index++)
            {
                var draft = drafts[index];
                if (!streams.TryGetValue(draft.Left.SourceFileId, out var stream))
                {
                    stream = new FileStream(
                        draft.Wave.Path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 64 * 1024,
                        FileOptions.RandomAccess);
                    streams.Add(draft.Left.SourceFileId, stream);
                }

                var contextRadiusSamples = checked(
                    draft.Wave.SampleRate * LocalContextRadiusMilliseconds / 1_000);
                result[index] = MeasureLocalRenderedDerivative(
                    stream,
                    draft,
                    contextRadiusSamples);
            }
        }
        finally
        {
            foreach (var stream in streams.Values)
            {
                stream.Dispose();
            }
        }

        return result;
    }

    private static LocalBoundaryMeasurement MeasureLocalRenderedDerivative(
        FileStream stream,
        BoundaryDraft draft,
        int contextRadiusSamples)
    {
        var wave = draft.Wave;
        var leftStart = Math.Max(0, draft.LeftSampleIndex - contextRadiusSamples);
        var leftCount = checked((int)(draft.LeftSampleIndex - leftStart + 1));
        var rightEndExclusive = Math.Min(
            wave.SampleFrameCount,
            draft.RightSampleIndex + contextRadiusSamples + 1);
        var rightCount = checked((int)(rightEndExclusive - draft.RightSampleIndex));
        var derivativeCapacity = Math.Max(1, leftCount + rightCount - 2);
        var derivatives = ArrayPool<double>.Shared.Rent(derivativeCapacity);
        try
        {
            var derivativeCount = 0;
            derivativeCount += ReadRenderedDerivatives(
                stream,
                wave,
                leftStart,
                leftCount,
                draft.Left.Enabled ? DbToLinear(EffectiveGainDb(draft.Left)) : 0,
                derivatives.AsSpan(derivativeCount));
            derivativeCount += ReadRenderedDerivatives(
                stream,
                wave,
                draft.RightSampleIndex,
                rightCount,
                draft.Right.Enabled ? DbToLinear(EffectiveGainDb(draft.Right)) : 0,
                derivatives.AsSpan(derivativeCount));
            if (derivativeCount == 0)
            {
                return new(0);
            }

            Array.Sort(derivatives, 0, derivativeCount);
            var p99Index = Math.Clamp(
                (int)Math.Ceiling(derivativeCount * 0.99) - 1,
                0,
                derivativeCount - 1);
            return new(derivatives[p99Index]);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(derivatives, clearArray: true);
        }
    }

    private static int ReadRenderedDerivatives(
        FileStream stream,
        WaveFileInfo wave,
        long startSample,
        int sampleCount,
        double linearGain,
        Span<double> destination)
    {
        if (sampleCount < 2)
        {
            return 0;
        }

        var byteCount = checked(sampleCount * wave.BlockAlign);
        var bytes = ArrayPool<byte>.Shared.Rent(byteCount);
        var decoded = ArrayPool<float>.Shared.Rent(sampleCount);
        try
        {
            stream.Position = checked(wave.DataOffset + (startSample * wave.BlockAlign));
            stream.ReadExactly(bytes.AsSpan(0, byteCount));
            PcmWaveSampleReader.DecodeMono(
                bytes.AsSpan(0, byteCount),
                decoded.AsSpan(0, sampleCount),
                wave);
            for (var index = 1; index < sampleCount; index++)
            {
                destination[index - 1] = Math.Abs(
                    (decoded[index] * linearGain) -
                    (decoded[index - 1] * linearGain));
            }

            return sampleCount - 1;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(decoded, clearArray: true);
            ArrayPool<byte>.Shared.Return(bytes, clearArray: true);
        }
    }

    private static void ValidateSampleIndex(WaveFileInfo wave, long sampleIndex, string sourceFileId)
    {
        if (sampleIndex < 0 || sampleIndex >= wave.SampleFrameCount)
        {
            throw new InvalidDataException(
                $"Boundary sample {sampleIndex} vượt WAV {sourceFileId} có {wave.SampleFrameCount} frame.");
        }
    }

    private static double EffectiveGainDb(FragmentAudit fragment) =>
        fragment.Enabled ? fragment.AppliedGainDb ?? 0 : 0;

    private static double DbToLinear(double gainDb) => Math.Pow(10, gainDb / 20);

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

    private static void AppendCanonicalTransition(
        IncrementalHash hash,
        BoundaryDiscontinuitySample sample)
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
            sample.LeftSourceSampleIndex.ToString(CultureInfo.InvariantCulture),
            sample.RightSourceSampleIndex.ToString(CultureInfo.InvariantCulture),
            sample.LeftSourceSample.ToString("R", CultureInfo.InvariantCulture),
            sample.RightSourceSample.ToString("R", CultureInfo.InvariantCulture),
            sample.SourceStep.ToString("R", CultureInfo.InvariantCulture),
            sample.RenderedStep.ToString("R", CultureInfo.InvariantCulture),
            sample.ExcessStep.ToString("R", CultureInfo.InvariantCulture),
            sample.ScreeningCandidate ? "1" : "0",
            sample.LocalP99RenderedDerivativeDbfs.ToString("R", CultureInfo.InvariantCulture),
            sample.RenderedStepAboveLocalP99Db.ToString("R", CultureInfo.InvariantCulture),
            sample.TransientScreeningCandidate ? "1" : "0") + "\n";
        hash.AppendData(Encoding.UTF8.GetBytes(line));
    }

    private sealed record ClipKey(int TrackIndex, string SourceClipId, PremiereAudioClip Clip);

    private readonly record struct SampleKey(string SourceFileId, long SampleIndex);

    private sealed record BoundaryDraft(
        FragmentAudit Left,
        FragmentAudit Right,
        WaveFileInfo Wave,
        BoundaryTransitionKind Kind,
        long LeftSampleIndex,
        long RightSampleIndex);

    private readonly record struct LocalBoundaryMeasurement(double LocalP99RenderedDerivative);
}
