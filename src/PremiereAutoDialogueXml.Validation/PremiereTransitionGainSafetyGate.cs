using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Audio.Pcm;
using PremiereAutoDialogueXml.Core.Media;
using PremiereAutoDialogueXml.Core.ProjectModel;
using PremiereAutoDialogueXml.Output.Audit;

namespace PremiereAutoDialogueXml.Validation;

public enum PremiereTransitionGainSafetyDecisionKind
{
    Eligible,
    RejectedMissingPhraseEvidence,
    RejectedSourcePeakMismatch,
    RejectedNoRetainedPeak,
    RejectedExpectedPeakLoss
}

public sealed record PremiereTransitionPhrasePeakEvidence(
    string PhraseId,
    int TrackIndex,
    bool GainWasCapped,
    double? MeasuredPeakDbfs,
    double? AppliedGainDb,
    double? ExpectedPostRoutingPeakDbfs,
    double? FullSourcePeakDbfs,
    double? FullSourcePeakDeltaDb,
    double? RetainedSourcePeakDbfs,
    double? RetainedPostRoutingPeakDbfs,
    double? RetainedPeakDeltaDb,
    long ExcludedSampleCount,
    long RetainedSampleCount,
    string Reason);

public sealed record PremiereTransitionGainSafetyDecision(
    PremiereConstantGainBoundaryRequest Request,
    PremiereTransitionGainSafetyDecisionKind Decision,
    string Reason,
    IReadOnlyList<string> AffectedPhraseIds,
    IReadOnlyList<PremiereTransitionPhrasePeakEvidence> PhraseEvidence);

public sealed record PremiereTransitionGainSafetyReport(
    string SchemaVersion,
    string Policy,
    string ScannerPolicy,
    int GuardFramesPerEnabledSide,
    double PeakToleranceDb,
    int ScannerTransientCandidateCount,
    int CapturedTransientCandidateCount,
    int UncapturedTransientCandidateCount,
    int EligibleCount,
    int RejectedCount,
    int RejectedMissingPhraseEvidenceCount,
    int RejectedSourcePeakMismatchCount,
    int RejectedNoRetainedPeakCount,
    int RejectedExpectedPeakLossCount,
    string DecisionStreamSha256,
    IReadOnlyList<PremiereTransitionGainSafetyDecision> Decisions);

public sealed class PremiereTransitionGainSafetyGate
{
    public const string Policy = "phase16-source-peak-one-frame-retention-v1";
    public const int GuardFramesPerEnabledSide = 1;
    public const double DefaultPeakToleranceDb = 0.1;
    private const double MinimumDbfs = -120;
    private const double NumericTolerance = 0.000_001;

    private readonly PcmWaveSampleReader _sampleReader;

    public PremiereTransitionGainSafetyGate(PcmWaveSampleReader? sampleReader = null)
    {
        _sampleReader = sampleReader ?? new PcmWaveSampleReader();
    }

    public PremiereTransitionGainSafetyReport Evaluate(
        BoundaryDiscontinuityScanReport scan,
        OutputAudit audit,
        PremiereProject project,
        double peakToleranceDb = DefaultPeakToleranceDb,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(project);
        if (!double.IsFinite(peakToleranceDb) || peakToleranceDb <= 0 || peakToleranceDb > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(peakToleranceDb));
        }

        ValidateEnvelope(scan, audit, project);
        var timing = audit.SequenceTiming!;
        var media = BuildMediaMap(project);
        var auditGroups = audit.Fragments
            .GroupBy(fragment => (fragment.TrackIndex, fragment.SourceClipId))
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(fragment => fragment.TimelineStartFrame).ToArray());
        var phraseGroups = audit.Fragments
            .Where(fragment => fragment.Enabled && !string.IsNullOrWhiteSpace(fragment.PhraseId))
            .GroupBy(fragment => (fragment.TrackIndex, PhraseId: fragment.PhraseId!))
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(fragment => fragment.TimelineStartFrame).ToArray());

        var transientSamples = scan.TopSamples
            .Where(sample => sample.TransientScreeningCandidate)
            .OrderBy(sample => sample.TrackIndex)
            .ThenBy(sample => sample.BoundaryFrame)
            .ToArray();
        if (transientSamples.Length > scan.TransientScreeningCandidateCount)
        {
            throw new InvalidDataException("Boundary scan có transient count không nhất quán.");
        }

        var decisions = new List<PremiereTransitionGainSafetyDecision>(transientSamples.Length);
        foreach (var sample in transientSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pair = ResolveBoundary(sample, auditGroups);
            var request = ToRequest(sample);
            var affected = ResolveAffectedPhrases(pair);
            if (affected.Count == 0 || affected.Any(item => string.IsNullOrWhiteSpace(item.PhraseId)))
            {
                decisions.Add(new(
                    request,
                    PremiereTransitionGainSafetyDecisionKind.RejectedMissingPhraseEvidence,
                    "enabled-side-missing-phrase-id",
                    [],
                    []));
                continue;
            }

            var phraseIds = affected
                .Select(item => item.PhraseId!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var evidence = new List<PremiereTransitionPhrasePeakEvidence>(phraseIds.Length);
            foreach (var phraseId in phraseIds)
            {
                if (!phraseGroups.TryGetValue((sample.TrackIndex, phraseId), out var fragments))
                {
                    evidence.Add(MissingEvidence(sample.TrackIndex, phraseId, "phrase-fragments-not-found"));
                    continue;
                }

                var windows = BuildGuardWindows(pair, phraseId, timing.SamplesPerFrame);
                evidence.Add(MeasurePhrase(
                    sample.TrackIndex,
                    phraseId,
                    fragments,
                    windows,
                    media,
                    audit.Preset.PremiereCenterPanCompensationDb,
                    peakToleranceDb,
                    cancellationToken));
            }

            var decisionKind = ResolveDecision(evidence);
            decisions.Add(new(
                request,
                decisionKind,
                DecisionReason(decisionKind),
                phraseIds,
                evidence));
        }

        var ordered = decisions
            .OrderBy(decision => decision.Request.TrackIndex)
            .ThenBy(decision => decision.Request.BoundaryFrame)
            .ToArray();
        return new(
            "1.0",
            Policy,
            scan.Policy,
            GuardFramesPerEnabledSide,
            peakToleranceDb,
            scan.TransientScreeningCandidateCount,
            ordered.Length,
            scan.TransientScreeningCandidateCount - ordered.Length,
            ordered.Count(decision => decision.Decision == PremiereTransitionGainSafetyDecisionKind.Eligible),
            ordered.Count(decision => decision.Decision != PremiereTransitionGainSafetyDecisionKind.Eligible),
            ordered.Count(decision => decision.Decision == PremiereTransitionGainSafetyDecisionKind.RejectedMissingPhraseEvidence),
            ordered.Count(decision => decision.Decision == PremiereTransitionGainSafetyDecisionKind.RejectedSourcePeakMismatch),
            ordered.Count(decision => decision.Decision == PremiereTransitionGainSafetyDecisionKind.RejectedNoRetainedPeak),
            ordered.Count(decision => decision.Decision == PremiereTransitionGainSafetyDecisionKind.RejectedExpectedPeakLoss),
            ComputeDecisionHash(ordered),
            ordered);
    }

    private static void ValidateEnvelope(
        BoundaryDiscontinuityScanReport scan,
        OutputAudit audit,
        PremiereProject project)
    {
        if (!string.Equals(scan.Policy, BoundaryDiscontinuityScanner.Policy, StringComparison.Ordinal) ||
            scan.CapturedSampleCount != scan.TopSamples.Count ||
            scan.TransientScreeningCandidateCount < 0)
        {
            throw new InvalidDataException("Boundary scan không dùng đúng policy/counter Phase 14.");
        }

        if (!string.Equals(audit.SourceXmlSha256, project.SourceXmlSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("SHA-256 XML nguồn không khớp audit gain safety.");
        }

        var timing = audit.SequenceTiming
            ?? throw new InvalidDataException("Audit thiếu sequence timing cho gain safety.");
        if (timing.Ntsc ||
            timing.FrameRate != project.Sequence.FrameRate ||
            timing.AudioSampleRate != project.Sequence.AudioSampleRate ||
            timing.AudioSampleRate != 48_000 ||
            timing.SamplesPerFrame != timing.AudioSampleRate / timing.FrameRate ||
            !string.Equals(
                timing.FrameGridPolicy,
                SequenceTimingAudit.ExactFrameGridPolicy,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Sequence timing không thuộc frame-grid Phase 12 đã khóa.");
        }
    }

    private static IReadOnlyDictionary<string, WaveFileInfo> BuildMediaMap(PremiereProject project)
    {
        var result = new Dictionary<string, WaveFileInfo>(StringComparer.Ordinal);
        foreach (var clip in project.Sequence.AudioTracks.SelectMany(track => track.Clips))
        {
            if (result.TryGetValue(clip.SourceFileId, out var existing))
            {
                if (!string.Equals(existing.Path, clip.SourceMedia.Wave.Path, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Source file ID {clip.SourceFileId} trỏ nhiều WAV khác nhau.");
                }

                continue;
            }

            result.Add(clip.SourceFileId, clip.SourceMedia.Wave);
        }

        return result;
    }

    private static BoundaryPair ResolveBoundary(
        BoundaryDiscontinuitySample sample,
        IReadOnlyDictionary<(int TrackIndex, string SourceClipId), FragmentAudit[]> auditGroups)
    {
        if (!auditGroups.TryGetValue((sample.TrackIndex, sample.SourceClipId), out var fragments))
        {
            throw new InvalidDataException(
                $"Không tìm thấy fragment A{sample.TrackIndex}/{sample.SourceClipId} của gain safety.");
        }

        var left = fragments.SingleOrDefault(fragment => fragment.TimelineEndFrame == sample.BoundaryFrame);
        var right = fragments.SingleOrDefault(fragment => fragment.TimelineStartFrame == sample.BoundaryFrame);
        if (left is null || right is null ||
            left.SourceFileId != sample.SourceFileId || right.SourceFileId != sample.SourceFileId ||
            left.SourceEndSample - 1 != sample.LeftSourceSampleIndex ||
            right.SourceStartSample != sample.RightSourceSampleIndex ||
            left.Status != sample.LeftStatus || right.Status != sample.RightStatus ||
            left.Enabled != sample.LeftEnabled || right.Enabled != sample.RightEnabled ||
            Math.Abs(EffectiveGainDb(left) - sample.LeftGainDb) > NumericTolerance ||
            Math.Abs(EffectiveGainDb(right) - sample.RightGainDb) > NumericTolerance)
        {
            throw new InvalidDataException(
                $"Boundary A{sample.TrackIndex}/frame {sample.BoundaryFrame} không khớp audit/scan.");
        }

        return new(left, right);
    }

    private static IReadOnlyList<FragmentAudit> ResolveAffectedPhrases(BoundaryPair pair)
    {
        var result = new List<FragmentAudit>(2);
        if (pair.Left.Enabled)
        {
            result.Add(pair.Left);
        }

        if (pair.Right.Enabled)
        {
            result.Add(pair.Right);
        }

        return result;
    }

    private static IReadOnlyList<GuardWindow> BuildGuardWindows(
        BoundaryPair pair,
        string phraseId,
        int samplesPerFrame)
    {
        var windows = new List<GuardWindow>(2);
        if (pair.Left.Enabled && string.Equals(pair.Left.PhraseId, phraseId, StringComparison.Ordinal))
        {
            windows.Add(new(
                pair.Left.SourceFileId,
                Math.Max(pair.Left.SourceStartSample, pair.Left.SourceEndSample - samplesPerFrame),
                pair.Left.SourceEndSample));
        }

        if (pair.Right.Enabled && string.Equals(pair.Right.PhraseId, phraseId, StringComparison.Ordinal))
        {
            windows.Add(new(
                pair.Right.SourceFileId,
                pair.Right.SourceStartSample,
                Math.Min(pair.Right.SourceEndSample, pair.Right.SourceStartSample + samplesPerFrame)));
        }

        return windows;
    }

    private PremiereTransitionPhrasePeakEvidence MeasurePhrase(
        int trackIndex,
        string phraseId,
        IReadOnlyList<FragmentAudit> fragments,
        IReadOnlyList<GuardWindow> windows,
        IReadOnlyDictionary<string, WaveFileInfo> media,
        double centerPanCompensationDb,
        double peakToleranceDb,
        CancellationToken cancellationToken)
    {
        var first = fragments[0];
        if (first.MeasuredPeakDbfs is not { } measuredPeakDbfs || !double.IsFinite(measuredPeakDbfs) ||
            first.AppliedGainDb is not { } appliedGainDb || !double.IsFinite(appliedGainDb) ||
            fragments.Any(fragment =>
                fragment.MeasuredPeakDbfs is not { } fragmentPeak || !double.IsFinite(fragmentPeak) ||
                Math.Abs(fragmentPeak - measuredPeakDbfs) > NumericTolerance ||
                fragment.AppliedGainDb is not { } fragmentGain || !double.IsFinite(fragmentGain) ||
                Math.Abs(fragmentGain - appliedGainDb) > NumericTolerance ||
                fragment.GainWasCapped != first.GainWasCapped))
        {
            return MissingEvidence(trackIndex, phraseId, "phrase-gain-provenance-incomplete");
        }

        var fullMaximum = 0f;
        var retainedMaximum = 0f;
        long excludedSampleCount = 0;
        long retainedSampleCount = 0;
        foreach (var fragment in fragments)
        {
            if (!media.TryGetValue(fragment.SourceFileId, out var wave) ||
                fragment.SourceStartSample < 0 ||
                fragment.SourceEndSample <= fragment.SourceStartSample ||
                fragment.SourceEndSample > wave.SampleFrameCount)
            {
                return MissingEvidence(trackIndex, phraseId, "phrase-source-mapping-invalid");
            }

            var position = fragment.SourceStartSample;
            _sampleReader.ReadRange(
                wave,
                fragment.SourceStartSample,
                fragment.SourceEndSample - fragment.SourceStartSample,
                samples =>
                {
                    foreach (var value in samples.Span)
                    {
                        var absolute = Math.Abs(value);
                        fullMaximum = Math.Max(fullMaximum, absolute);
                        if (windows.Any(window =>
                                window.SourceFileId == fragment.SourceFileId &&
                                position >= window.StartSample && position < window.EndSample))
                        {
                            excludedSampleCount++;
                        }
                        else
                        {
                            retainedMaximum = Math.Max(retainedMaximum, absolute);
                            retainedSampleCount++;
                        }

                        position++;
                    }
                },
                cancellationToken);
        }

        var expectedPeakDbfs = measuredPeakDbfs + appliedGainDb - centerPanCompensationDb;
        var fullPeakDbfs = ToDbfs(fullMaximum);
        var fullPeakDeltaDb = fullPeakDbfs - measuredPeakDbfs;
        if (Math.Abs(fullPeakDeltaDb) > peakToleranceDb)
        {
            return new(
                phraseId,
                trackIndex,
                first.GainWasCapped,
                measuredPeakDbfs,
                appliedGainDb,
                expectedPeakDbfs,
                fullPeakDbfs,
                fullPeakDeltaDb,
                null,
                null,
                null,
                excludedSampleCount,
                retainedSampleCount,
                "source-peak-does-not-match-audit");
        }

        if (retainedSampleCount == 0 || retainedMaximum <= 0)
        {
            return new(
                phraseId,
                trackIndex,
                first.GainWasCapped,
                measuredPeakDbfs,
                appliedGainDb,
                expectedPeakDbfs,
                fullPeakDbfs,
                fullPeakDeltaDb,
                null,
                null,
                null,
                excludedSampleCount,
                retainedSampleCount,
                "no-nonzero-peak-outside-transition-window");
        }

        var retainedPeakDbfs = ToDbfs(retainedMaximum);
        var retainedExpectedPeakDbfs = retainedPeakDbfs + appliedGainDb - centerPanCompensationDb;
        var retainedDeltaDb = retainedExpectedPeakDbfs - expectedPeakDbfs;
        return new(
            phraseId,
            trackIndex,
            first.GainWasCapped,
            measuredPeakDbfs,
            appliedGainDb,
            expectedPeakDbfs,
            fullPeakDbfs,
            fullPeakDeltaDb,
            retainedPeakDbfs,
            retainedExpectedPeakDbfs,
            retainedDeltaDb,
            excludedSampleCount,
            retainedSampleCount,
            Math.Abs(retainedDeltaDb) <= peakToleranceDb
                ? "retained-peak-within-tolerance"
                : "retained-peak-loss-exceeds-tolerance");
    }

    private static PremiereTransitionPhrasePeakEvidence MissingEvidence(
        int trackIndex,
        string phraseId,
        string reason) => new(
            phraseId,
            trackIndex,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            0,
            0,
            reason);

    private static PremiereTransitionGainSafetyDecisionKind ResolveDecision(
        IReadOnlyList<PremiereTransitionPhrasePeakEvidence> evidence)
    {
        if (evidence.Any(item => item.Reason is
                "phrase-fragments-not-found" or
                "phrase-gain-provenance-incomplete" or
                "phrase-source-mapping-invalid"))
        {
            return PremiereTransitionGainSafetyDecisionKind.RejectedMissingPhraseEvidence;
        }

        if (evidence.Any(item => item.Reason == "source-peak-does-not-match-audit"))
        {
            return PremiereTransitionGainSafetyDecisionKind.RejectedSourcePeakMismatch;
        }

        if (evidence.Any(item => item.Reason == "no-nonzero-peak-outside-transition-window"))
        {
            return PremiereTransitionGainSafetyDecisionKind.RejectedNoRetainedPeak;
        }

        return evidence.All(item => item.Reason == "retained-peak-within-tolerance")
            ? PremiereTransitionGainSafetyDecisionKind.Eligible
            : PremiereTransitionGainSafetyDecisionKind.RejectedExpectedPeakLoss;
    }

    private static string DecisionReason(PremiereTransitionGainSafetyDecisionKind decision) => decision switch
    {
        PremiereTransitionGainSafetyDecisionKind.Eligible => "all-affected-phrases-retain-peak",
        PremiereTransitionGainSafetyDecisionKind.RejectedMissingPhraseEvidence => "missing-phrase-evidence",
        PremiereTransitionGainSafetyDecisionKind.RejectedSourcePeakMismatch => "source-peak-mismatch",
        PremiereTransitionGainSafetyDecisionKind.RejectedNoRetainedPeak => "no-retained-peak",
        PremiereTransitionGainSafetyDecisionKind.RejectedExpectedPeakLoss => "expected-peak-loss",
        _ => throw new ArgumentOutOfRangeException(nameof(decision))
    };

    private static PremiereConstantGainBoundaryRequest ToRequest(BoundaryDiscontinuitySample sample) => new(
        sample.TrackIndex,
        sample.BoundaryFrame,
        sample.Kind,
        sample.RenderedStepDbfs,
        sample.RenderedStepAboveLocalP99Db);

    private static double EffectiveGainDb(FragmentAudit fragment) =>
        fragment.Enabled ? fragment.AppliedGainDb ?? 0 : 0;

    private static double ToDbfs(double amplitude) =>
        amplitude <= 0 ? MinimumDbfs : Math.Max(MinimumDbfs, 20 * Math.Log10(amplitude));

    private static string ComputeDecisionHash(
        IEnumerable<PremiereTransitionGainSafetyDecision> decisions)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var decision in decisions)
        {
            Append(hash, string.Join('|',
                decision.Request.TrackIndex.ToString(CultureInfo.InvariantCulture),
                decision.Request.BoundaryFrame.ToString(CultureInfo.InvariantCulture),
                decision.Request.ExpectedKind,
                decision.Decision,
                decision.Reason,
                string.Join(',', decision.AffectedPhraseIds)));
            foreach (var evidence in decision.PhraseEvidence.OrderBy(item => item.PhraseId, StringComparer.Ordinal))
            {
                Append(hash, string.Join('|',
                    evidence.PhraseId,
                    Format(evidence.MeasuredPeakDbfs),
                    Format(evidence.AppliedGainDb),
                    Format(evidence.FullSourcePeakDbfs),
                    Format(evidence.RetainedSourcePeakDbfs),
                    Format(evidence.RetainedPeakDeltaDb),
                    evidence.ExcludedSampleCount.ToString(CultureInfo.InvariantCulture),
                    evidence.RetainedSampleCount.ToString(CultureInfo.InvariantCulture),
                    evidence.Reason));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));

    private static string Format(double? value) =>
        value?.ToString("R", CultureInfo.InvariantCulture) ?? "null";

    private sealed record BoundaryPair(FragmentAudit Left, FragmentAudit Right);

    private sealed record GuardWindow(string SourceFileId, long StartSample, long EndSample);
}
