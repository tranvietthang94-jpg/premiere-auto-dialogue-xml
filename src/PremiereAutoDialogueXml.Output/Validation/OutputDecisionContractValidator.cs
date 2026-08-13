using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.ProjectModel;
using PremiereAutoDialogueXml.Output.Xml;

namespace PremiereAutoDialogueXml.Output.Validation;

public sealed class OutputDecisionContractValidator
{
    private const float GainToleranceDb = 0.001f;
    private const string NoiseBoundarySafetyReason = "ambiguous-noise-boundary-disagreement";
    private const string NoiseBoundaryCandidateOnlyFallbackReason =
        "ambiguous-noise-boundary-candidate-only-in-baseline-fallback";

    public void Validate(
        PremiereProject project,
        ProjectAudioAnalysis analysis,
        DialogueProcessingPreset preset,
        GeneratedPremiereXmlPlan generated)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(generated);

        ValidateNoiseBoundarySafety(analysis, preset);
        ValidateCalibratedBleedSafety(project, analysis, preset);
        var phrases = analysis.Tracks
            .SelectMany(track => track.Phrases)
            .ToDictionary(phrase => phrase.Id, StringComparer.Ordinal);
        ValidatePhrases(phrases.Values, preset);
        ValidateFragments(project, generated.AudioFragments, phrases, preset);
        ValidateMarkers(generated, phrases.Values, project.Sequence.FrameRate);
    }

    private static void ValidateNoiseBoundarySafety(
        ProjectAudioAnalysis analysis,
        DialogueProcessingPreset preset)
    {
        Require(
            (analysis.VadFrontEndComparison is null) == (analysis.NoiseBoundaryComparison is null),
            "Audit shadow VAD và noise-boundary phải cùng hiện diện trước publication.");
        if (analysis.NoiseBoundaryComparison is not { } projectComparison)
        {
            return;
        }

        var expectedTrackIndexes = analysis.Tracks
            .Select(track => track.TrackIndex)
            .Order()
            .ToArray();
        var expectedFrontEnds = new[] { "AntiAliasFir", "LegacyStride3" };
        Require(
            projectComparison.FrontEnds.Select(frontEnd => frontEnd.Resampling).Order().SequenceEqual(expectedFrontEnds),
            "Noise-boundary audit phải có đúng LegacyStride3 và AntiAliasFir.");
        Require(
            projectComparison.BaselineEnabledFinalDisabledCount == 0,
            "Noise-boundary merge đã làm mất vùng Enabled của baseline.");

        foreach (var frontEnd in projectComparison.FrontEnds)
        {
            Require(
                frontEnd.Tracks.Select(track => track.TrackIndex).Order().SequenceEqual(expectedTrackIndexes),
                $"Noise-boundary audit '{frontEnd.Resampling}' không phủ đúng toàn bộ track.");
            foreach (var track in frontEnd.Tracks)
            {
                Require(
                    track.BaselineMode == NoiseBoundaryAnalysisMode.Phase09Baseline &&
                    track.CandidateMode == NoiseBoundaryAnalysisMode.NoiseBoundaryCandidate,
                    $"Noise-boundary track {track.TrackIndex} sai mode provenance.");
                Require(
                    track.BaselinePolicyVersion == "phase09-adaptive-p20-v1" &&
                    track.CandidatePolicyVersion == "phase10-background-eligible-p20-v1" &&
                    track.BaselineBoundaryPolicyVersion == "phase09-vad-single-threshold-v1" &&
                    track.CandidateBoundaryPolicyVersion == "phase10-vad-start050-continue040-v1" &&
                    NoiseFloorPolicyMatches(track.BaselinePolicy, isCandidate: false) &&
                    NoiseFloorPolicyMatches(track.CandidatePolicy, isCandidate: true) &&
                    BoundaryPolicyMatches(track.BaselineBoundaryPolicy, isCandidate: false) &&
                    BoundaryPolicyMatches(track.CandidateBoundaryPolicy, isCandidate: true),
                    $"Noise-boundary track {track.TrackIndex} sai policy provenance.");
                Require(
                    track.ObservationCount >= 0 &&
                    track.ChangedFrameCount >= 0 &&
                    track.ChangedFrameCount <= track.ObservationCount &&
                    track.PhraseDifferenceCount >= 0 &&
                    track.SegmentDifferenceCount >= 0 &&
                    track.BaselineEnabledCandidateDisabledCount >= 0 &&
                    track.BaselineDisabledCandidateEnabledCount >= 0 &&
                    track.TrainingEligibilityDifferenceCount >= 0 &&
                    track.TrainingEligibilityDifferenceCount <= track.ChangedFrameCount &&
                    float.IsFinite(track.MaximumNoiseFloorDeltaDb) &&
                    track.MaximumNoiseFloorDeltaDb >= 0,
                    $"Noise-boundary track {track.TrackIndex} có thống kê frame không hợp lệ.");
                Require(
                    track.CapturedFrameDifferenceCount == track.FrameDifferenceSamples.Count &&
                    track.CapturedFrameDifferenceCount <= 64 &&
                    track.CapturedFrameDifferenceCount <= track.ChangedFrameCount &&
                    (track.ChangedFrameCount == 0 || track.CapturedFrameDifferenceCount > 0) &&
                    track.FrameTraceSha256.Length == 64 &&
                    track.FrameTraceSha256.All(Uri.IsHexDigit) &&
                    track.PhraseDifferenceCount == track.PhraseDifferences.Count &&
                    track.DecisionDifferences.Count == 0,
                    $"Noise-boundary track {track.TrackIndex} có comparison count không nhất quán.");

                var maximumFloorDeltaDb = 0f;
                var hasEligibilityDifferenceSample = false;
                foreach (var difference in track.FrameDifferenceSamples)
                {
                    var baseline = difference.Baseline;
                    var candidate = difference.Candidate;
                    Require(
                        baseline.TimelineStartSample == candidate.TimelineStartSample &&
                        baseline.TimelineEndSample == candidate.TimelineEndSample &&
                        baseline.TimelineEndSample > baseline.TimelineStartSample &&
                        Math.Abs(baseline.VadProbability - candidate.VadProbability) <= 0.0001f &&
                        Math.Abs(baseline.RmsDbfs - candidate.RmsDbfs) <= 0.0001f &&
                        float.IsFinite(baseline.NoiseFloorBeforeDbfs) &&
                        float.IsFinite(baseline.NoiseFloorAfterDbfs) &&
                        float.IsFinite(candidate.NoiseFloorBeforeDbfs) &&
                        float.IsFinite(candidate.NoiseFloorAfterDbfs),
                        $"Noise-boundary track {track.TrackIndex} không dùng cùng observation hoặc có floor sai.");
                    maximumFloorDeltaDb = Math.Max(
                        maximumFloorDeltaDb,
                        Math.Max(
                            Math.Abs(baseline.NoiseFloorBeforeDbfs - candidate.NoiseFloorBeforeDbfs),
                            Math.Abs(baseline.NoiseFloorAfterDbfs - candidate.NoiseFloorAfterDbfs)));
                    if (IsTrainingEligible(baseline.NoiseFloorTrainingDecision) !=
                        IsTrainingEligible(candidate.NoiseFloorTrainingDecision))
                    {
                        hasEligibilityDifferenceSample = true;
                    }
                }

                var frameSummary = track.FrameDifferenceSummary;
                var frameSummaryCounts = new[]
                {
                    frameSummary.NoiseFloorBeforeDifferenceCount,
                    frameSummary.NoiseFloorAfterDifferenceCount,
                    frameSummary.NoiseFloorReadyBeforeDifferenceCount,
                    frameSummary.NoiseFloorReadyAfterDifferenceCount,
                    frameSummary.TrainingDecisionDifferenceCount,
                    frameSummary.VadSpeechDifferenceCount,
                    frameSummary.DirectEvidenceDifferenceCount,
                    frameSummary.DirectEnergyThresholdDifferenceCount,
                    frameSummary.WarmupUncertainDifferenceCount,
                    frameSummary.BoundaryStateDifferenceCount
                };
                Require(
                    Math.Abs(maximumFloorDeltaDb - track.MaximumNoiseFloorDeltaDb) <= 0.0001f &&
                    frameSummaryCounts.All(count => count >= 0 && count <= track.ChangedFrameCount) &&
                    frameSummaryCounts.Any(count => count > 0) == (track.ChangedFrameCount > 0) &&
                    frameSummary.TrainingDecisionDifferenceCount >= track.TrainingEligibilityDifferenceCount &&
                    frameSummary.VadSpeechDifferenceCount == 0 &&
                    (track.TrainingEligibilityDifferenceCount == 0 || hasEligibilityDifferenceSample),
                    $"Noise-boundary track {track.TrackIndex} có floor/eligibility aggregate không nhất quán.");

                foreach (var difference in track.PhraseDifferences)
                {
                    Require(
                        (difference.ChangeKind is
                            "candidate-only" or
                            "baseline-only" or
                            "boundary-or-gain-changed" or
                            "split" or
                            "merge" or
                            "resegmented") &&
                        difference.BaselinePhrases.Count + difference.CandidatePhrases.Count > 0,
                        $"Noise-boundary track {track.TrackIndex} có phrase difference không hợp lệ.");
                    foreach (var phrase in difference.BaselinePhrases.Concat(difference.CandidatePhrases))
                    {
                        ValidateNoiseBoundaryPhrase(track.TrackIndex, phrase, preset);
                    }
                }

                foreach (var difference in track.DecisionDifferences)
                {
                    Require(
                        difference.TimelineEndSample > difference.TimelineStartSample &&
                        difference.BaselineEnabled == IsEnabled(difference.BaselineStatus) &&
                        difference.CandidateEnabled == IsEnabled(difference.CandidateStatus) &&
                        IsFiniteOptional(difference.BaselineGainDb) &&
                        IsFiniteOptional(difference.CandidateGainDb),
                        $"Noise-boundary track {track.TrackIndex} có decision difference không hợp lệ.");
                }

                var lostBaselineCount = 0;
                foreach (var difference in track.FinalDifferences)
                {
                    Require(
                        difference.TimelineEndSample > difference.TimelineStartSample &&
                        difference.BaselineEnabled == IsEnabled(difference.BaselineStatus) &&
                        difference.CandidateEnabled == IsEnabled(difference.CandidateStatus) &&
                        difference.FinalEnabled == IsEnabled(difference.FinalStatus) &&
                        IsFiniteOptional(difference.BaselineGainDb) &&
                        IsFiniteOptional(difference.CandidateGainDb) &&
                        IsFiniteOptional(difference.FinalGainDb),
                        $"Noise-boundary track {track.TrackIndex} có final difference không hợp lệ.");
                    if (difference.BaselineEnabled && !difference.FinalEnabled)
                    {
                        lostBaselineCount++;
                    }

                    if (difference.BaselineEnabled && !difference.CandidateEnabled)
                    {
                        Require(
                            difference.FinalEnabled &&
                            difference.FinalStatus == AudioSegmentStatus.Ambiguous &&
                            difference.FinalReason == NoiseBoundarySafetyReason,
                            $"Noise-boundary track {track.TrackIndex} không fail-safe vùng baseline Enabled.");
                    }

                    if (difference.FinalReason is
                        NoiseBoundarySafetyReason or
                        NoiseBoundaryCandidateOnlyFallbackReason)
                    {
                        Require(
                            difference.FinalStatus == AudioSegmentStatus.Ambiguous && difference.FinalEnabled,
                            $"Noise-boundary track {track.TrackIndex} có safety reason nhưng không Ambiguous/Enabled.");
                    }
                }

                Require(
                    lostBaselineCount == track.BaselineEnabledFinalDisabledCount && lostBaselineCount == 0,
                    $"Noise-boundary track {track.TrackIndex} làm mất vùng baseline Enabled.");
                Require(
                    (track.SegmentDifferenceCount == 0 || track.FinalDifferences.Count > 0) &&
                    (track.BaselineEnabledCandidateDisabledCount == 0 || track.FinalDifferences.Any(difference =>
                        difference.BaselineEnabled && !difference.CandidateEnabled)) &&
                    (track.BaselineDisabledCandidateEnabledCount == 0 || track.FinalDifferences.Any(difference =>
                        !difference.BaselineEnabled && difference.CandidateEnabled)),
                    $"Noise-boundary track {track.TrackIndex} thiếu final interval provenance.");
            }
        }
    }

    private static void ValidateCalibratedBleedSafety(
        PremiereProject project,
        ProjectAudioAnalysis analysis,
        DialogueProcessingPreset preset)
    {
        if (analysis.CalibratedBleedShadow is not { } shadow)
        {
            Require(
                project.Sequence.AudioTracks.Count < 2,
                "Dự án đa mic thiếu calibrated bleed shadow trước publication.");
            return;
        }

        var expectedCalibrationPolicy = DirectionalBleedCalibrationPolicy.From(preset);
        var expectedScoringPolicy = CalibratedBleedScoringPolicy.From(
            preset,
            expectedCalibrationPolicy);
        Require(
            shadow.Calibration.Policy == expectedCalibrationPolicy &&
            shadow.ScoringPolicy == expectedScoringPolicy,
            "Calibrated bleed shadow sai policy provenance.");

        var tracksByIndex = analysis.Tracks.ToDictionary(track => track.TrackIndex);
        var sequenceTrackIndexes = project.Sequence.AudioTracks
            .Select(track => track.Index)
            .Order()
            .ToArray();
        Require(
            tracksByIndex.Keys.Order().SequenceEqual(sequenceTrackIndexes),
            "Calibrated bleed shadow không cùng tập track với sequence.");

        var expectedPairs = sequenceTrackIndexes
            .SelectMany(source => sequenceTrackIndexes
                .Where(target => target != source)
                .Select(target => (Source: source, Target: target)))
            .OrderBy(pair => pair.Source)
            .ThenBy(pair => pair.Target)
            .ToArray();
        var fingerprints = shadow.Calibration.Fingerprints
            .OrderBy(fingerprint => fingerprint.SourceTrackIndex)
            .ThenBy(fingerprint => fingerprint.TargetTrackIndex)
            .ToArray();
        Require(
            fingerprints.Select(fingerprint =>
                    (Source: fingerprint.SourceTrackIndex, Target: fingerprint.TargetTrackIndex))
                .SequenceEqual(expectedPairs),
            "Calibration bleed không phủ đúng mọi cặp track có hướng.");

        var stableFingerprints = new Dictionary<(int Source, int Target), DirectionalBleedFingerprint>();
        foreach (var fingerprint in fingerprints)
        {
            ValidateDirectionalFingerprint(fingerprint, tracksByIndex, shadow.Calibration.Policy);
            if (fingerprint.Status == DirectionalBleedCalibrationStatus.Stable)
            {
                stableFingerprints.Add(
                    (fingerprint.SourceTrackIndex, fingerprint.TargetTrackIndex),
                    fingerprint);
            }
        }

        var expectedCandidates = analysis.Tracks
            .OrderBy(track => track.TrackIndex)
            .SelectMany(track => track.Segments
                .Where(segment => segment.Status is AudioSegmentStatus.Speech or AudioSegmentStatus.Ambiguous)
                .OrderBy(segment => segment.TimelineStartSample)
                .ThenBy(segment => segment.TimelineEndSample)
                .ThenBy(segment => segment.SourceClipId, StringComparer.Ordinal))
            .ToArray();
        Require(
            shadow.Candidates.Count == expectedCandidates.Length,
            "Calibrated bleed shadow không phủ đúng mọi segment Enabled của Phase 10.");
        for (var index = 0; index < expectedCandidates.Length; index++)
        {
            ValidateCalibratedCandidate(
                shadow.Candidates[index],
                expectedCandidates[index],
                tracksByIndex,
                stableFingerprints,
                shadow.ScoringPolicy);
        }

        Require(
            shadow.ProductionChangedSegmentCount == 0,
            "Calibrated bleed shadow đã thay đổi quyết định production của Phase 10.");
    }

    private static void ValidateDirectionalFingerprint(
        DirectionalBleedFingerprint fingerprint,
        IReadOnlyDictionary<int, TrackAudioAnalysis> tracks,
        DirectionalBleedCalibrationPolicy policy)
    {
        var sourcePhraseCount = tracks[fingerprint.SourceTrackIndex].Phrases.Count;
        var rejectionCounts = new[]
        {
            fingerprint.Rejections.NoConfirmedSourceSpeech,
            fingerprint.Rejections.NoMatchingBaselineBleed,
            fingerprint.Rejections.TooShort,
            fingerprint.Rejections.ExcludedCandidateRegion,
            fingerprint.Rejections.IncompleteMedia,
            fingerprint.Rejections.Clipped,
            fingerprint.Rejections.PolarityInverted,
            fingerprint.Rejections.BelowAdvantage,
            fingerprint.Rejections.BelowCorrelation,
            fingerprint.Rejections.LagOutOfRange,
            fingerprint.Rejections.ConflictingResidual
        };
        Require(
            fingerprint.SourceTrackIndex != fingerprint.TargetTrackIndex &&
            fingerprint.SourcePhraseCount == sourcePhraseCount &&
            fingerprint.AcceptedAnchorCount >= 0 &&
            rejectionCounts.All(count => count >= 0) &&
            fingerprint.AcceptedAnchorCount + fingerprint.Rejections.Total == sourcePhraseCount &&
            fingerprint.RetainedAnchorCount == Math.Min(
                fingerprint.AcceptedAnchorCount,
                policy.MaximumRetainedAnchorCount) &&
            fingerprint.ConsistentAnchorCount >= 0 &&
            fingerprint.ConsistentAnchorCount <= fingerprint.RetainedAnchorCount &&
            fingerprint.OutlierAnchorCount ==
                fingerprint.RetainedAnchorCount - fingerprint.ConsistentAnchorCount &&
            fingerprint.AnchorSamples.Count == Math.Min(
                fingerprint.RetainedAnchorCount,
                policy.MaximumAnchorSampleCount) &&
            IsSha256(fingerprint.EvidenceSha256) &&
            IsFiniteOptional(fingerprint.MedianLagMilliseconds) &&
            IsFiniteOptional(fingerprint.LagSpreadMilliseconds) &&
            IsFiniteOptional(fingerprint.MedianAttenuationDb) &&
            IsFiniteOptional(fingerprint.AttenuationSpreadDb) &&
            IsFiniteOptional(fingerprint.MedianCorrelation) &&
            IsFiniteOptional(fingerprint.MaximumResidualToTargetDb),
            $"Calibration bleed {fingerprint.SourceTrackIndex}->{fingerprint.TargetTrackIndex} có aggregate không hợp lệ.");

        var expectedReason = fingerprint.Status switch
        {
            DirectionalBleedCalibrationStatus.Stable => "stable-directional-fingerprint",
            DirectionalBleedCalibrationStatus.InsufficientSupport => "insufficient-eligible-anchors",
            DirectionalBleedCalibrationStatus.UnstableFingerprint
                when fingerprint.ConsistentAnchorCount < policy.MinimumAnchorCount =>
                    "insufficient-consistent-anchors",
            DirectionalBleedCalibrationStatus.UnstableFingerprint =>
                "consistent-support-ratio-below-threshold",
            _ => string.Empty
        };
        var hasStatistics = fingerprint.RetainedAnchorCount > 0;
        var statistics = new float?[]
        {
            fingerprint.MedianLagMilliseconds,
            fingerprint.LagSpreadMilliseconds,
            fingerprint.MedianAttenuationDb,
            fingerprint.AttenuationSpreadDb,
            fingerprint.MedianCorrelation,
            fingerprint.MaximumResidualToTargetDb
        };
        var unstableAsExpected =
            fingerprint.ConsistentAnchorCount < policy.MinimumAnchorCount ||
            fingerprint.ConsistentAnchorCount / (double)fingerprint.RetainedAnchorCount <
                policy.MinimumConsistentSupportRatio;
        Require(
            fingerprint.Reason == expectedReason &&
            statistics.All(value => value is not null) == hasStatistics &&
            (fingerprint.Status == DirectionalBleedCalibrationStatus.Stable
                ? fingerprint.RetainedAnchorCount >= policy.MinimumAnchorCount &&
                  fingerprint.ConsistentAnchorCount >= policy.MinimumAnchorCount &&
                  fingerprint.ConsistentAnchorCount / (double)fingerprint.RetainedAnchorCount >=
                      policy.MinimumConsistentSupportRatio
                : fingerprint.Status == DirectionalBleedCalibrationStatus.InsufficientSupport
                    ? fingerprint.RetainedAnchorCount < policy.MinimumAnchorCount
                    : unstableAsExpected),
            $"Calibration bleed {fingerprint.SourceTrackIndex}->{fingerprint.TargetTrackIndex} sai status/reason.");

        var sourcePhraseIds = tracks[fingerprint.SourceTrackIndex].Phrases
            .Select(phrase => phrase.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var anchor in fingerprint.AnchorSamples)
        {
            Require(
                sourcePhraseIds.Contains(anchor.SourcePhraseId) &&
                !string.IsNullOrWhiteSpace(anchor.TargetSourceClipId) &&
                anchor.TimelineEndSample > anchor.TimelineStartSample &&
                anchor.TimelineEndSample - anchor.TimelineStartSample <=
                    MillisecondsToSamples(policy.MaximumAnchorWindowMilliseconds) &&
                float.IsFinite(anchor.OtherMicAdvantageDb) &&
                anchor.OtherMicAdvantageDb >= policy.MinimumOtherMicAdvantageDb &&
                float.IsFinite(anchor.WaveformCorrelation) &&
                anchor.WaveformCorrelation >= policy.MinimumCorrelation &&
                Math.Abs(anchor.LagMilliseconds) <= policy.MaximumLagMilliseconds &&
                float.IsFinite(anchor.TransferScaleToTarget) &&
                anchor.TransferScaleToTarget > 0 &&
                float.IsFinite(anchor.ResidualToTargetDb) &&
                anchor.ResidualToTargetDb <= policy.MaximumResidualToTargetDb,
                $"Calibration bleed {fingerprint.SourceTrackIndex}->{fingerprint.TargetTrackIndex} có anchor sample không hợp lệ.");
        }
    }

    private static void ValidateCalibratedCandidate(
        CalibratedBleedCandidateEvidence candidate,
        AnalyzedAudioSegment baseline,
        IReadOnlyDictionary<int, TrackAudioAnalysis> tracks,
        IReadOnlyDictionary<(int Source, int Target), DirectionalBleedFingerprint> stableFingerprints,
        CalibratedBleedScoringPolicy policy)
    {
        Require(
            candidate.TargetTrackIndex == baseline.TrackIndex &&
            candidate.TargetSourceClipId == baseline.SourceClipId &&
            candidate.TimelineStartSample == baseline.TimelineStartSample &&
            candidate.TimelineEndSample == baseline.TimelineEndSample &&
            candidate.BaselineStatus == baseline.Status &&
            candidate.BaselineReason == baseline.Reason &&
            candidate.FinalStatus == baseline.Status &&
            candidate.FinalReason == baseline.Reason &&
            candidate.FinalEnabled == IsEnabled(baseline.Status) &&
            candidate.FinalEnabled &&
            candidate.AvailableWindowCount >= 0 &&
            candidate.EvaluatedWindowCount == candidate.WindowSamples.Count &&
            candidate.EvaluatedWindowCount <= policy.MaximumRetainedWindowCount &&
            candidate.AvailableWindowCount >= candidate.EvaluatedWindowCount &&
            IsSha256(candidate.WindowEvidenceSha256) &&
            candidate.WindowEvidenceSha256 == ComputeWindowEvidenceSha256(candidate, policy),
            $"Calibrated bleed candidate track {baseline.TrackIndex} không giữ nguyên Phase 10.");

        var passing = candidate.WindowSamples.Count(window =>
            window.Disposition == CalibratedBleedWindowDisposition.Pass);
        var conflicting = candidate.WindowSamples.Count(window => window.Disposition is
            CalibratedBleedWindowDisposition.ConflictingResidual or
            CalibratedBleedWindowDisposition.PolarityInverted);
        Require(
            candidate.PassingWindowCount == passing &&
            candidate.ConflictingWindowCount == conflicting,
            "Calibrated bleed candidate có aggregate cửa sổ không khớp.");

        if (candidate.Outcome == CalibratedBleedShadowOutcome.NoCalibration)
        {
            Require(
                candidate.OutcomeReason == "no-stable-overlapping-calibration" &&
                candidate.SourceTrackIndex is null &&
                candidate.FingerprintEvidenceSha256 is null &&
                candidate.AvailableWindowCount == 0 &&
                candidate.EvaluatedWindowCount == 0 &&
                candidate.PassingWindowCount == 0 &&
                candidate.ConflictingWindowCount == 0,
                "Calibrated bleed NoCalibration có provenance không hợp lệ.");
            return;
        }

        Require(
            candidate.SourceTrackIndex is not null &&
            candidate.EvaluatedWindowCount > 0,
            "Calibrated bleed candidate không trỏ đúng stable fingerprint.");
        var sourceTrackIndex = candidate.SourceTrackIndex.GetValueOrDefault();
        var fingerprintKey = (sourceTrackIndex, candidate.TargetTrackIndex);
        Require(
            sourceTrackIndex != candidate.TargetTrackIndex &&
            stableFingerprints.ContainsKey(fingerprintKey),
            "Calibrated bleed candidate không trỏ đúng stable fingerprint.");
        var fingerprint = stableFingerprints[fingerprintKey];
        Require(
            candidate.FingerprintEvidenceSha256 == fingerprint.EvidenceSha256,
            "Calibrated bleed candidate sai fingerprint evidence hash.");

        var sourcePhrases = tracks[sourceTrackIndex].Phrases
            .ToDictionary(phrase => phrase.Id, StringComparer.Ordinal);
        foreach (var window in candidate.WindowSamples)
        {
            ValidateCalibratedWindow(window, candidate, sourcePhrases, fingerprint, policy);
        }

        var meetsThreshold = passing >= policy.MinimumPassingWindowCount &&
                             passing / (double)candidate.EvaluatedWindowCount >=
                             policy.MinimumPassingWindowRatio;
        var directSpeechConflict = baseline.Status == AudioSegmentStatus.Speech && passing > 0;
        var expectedOutcome = conflicting > 0 || directSpeechConflict
            ? CalibratedBleedShadowOutcome.ConflictingEvidence
            : meetsThreshold
                ? CalibratedBleedShadowOutcome.CalibratedLikelyBleed
                : CalibratedBleedShadowOutcome.BelowCalibratedThreshold;
        var expectedReason = expectedOutcome switch
        {
            CalibratedBleedShadowOutcome.ConflictingEvidence when directSpeechConflict =>
                "baseline-direct-speech-conflicts-with-calibrated-bleed",
            CalibratedBleedShadowOutcome.ConflictingEvidence =>
                "multi-window-direct-or-polarity-conflict",
            CalibratedBleedShadowOutcome.CalibratedLikelyBleed =>
                "multi-window-matches-directional-fingerprint",
            _ => "multi-window-support-below-threshold"
        };
        Require(
            candidate.Outcome == expectedOutcome && candidate.OutcomeReason == expectedReason,
            "Calibrated bleed candidate sai outcome/reason tổng hợp.");
    }

    private static void ValidateCalibratedWindow(
        CalibratedBleedWindowEvidence window,
        CalibratedBleedCandidateEvidence candidate,
        IReadOnlyDictionary<string, DialoguePhrase> sourcePhrases,
        DirectionalBleedFingerprint fingerprint,
        CalibratedBleedScoringPolicy policy)
    {
        Require(sourcePhrases.ContainsKey(window.SourcePhraseId),
            "Calibrated bleed có cửa sổ trỏ source phrase không tồn tại.");
        var sourcePhrase = sourcePhrases[window.SourcePhraseId];
        Require(
            window.TimelineStartSample >= sourcePhrase.CoreStartSample &&
            window.TimelineEndSample <= sourcePhrase.CoreEndSample &&
            window.TimelineStartSample >= candidate.TimelineStartSample &&
            window.TimelineEndSample <= candidate.TimelineEndSample &&
            window.TimelineEndSample > window.TimelineStartSample &&
            window.TimelineEndSample - window.TimelineStartSample >=
                MillisecondsToSamples(policy.MinimumWindowMilliseconds) &&
            window.TimelineEndSample - window.TimelineStartSample <=
                MillisecondsToSamples(policy.MaximumWindowMilliseconds) &&
            float.IsFinite(window.OtherMicAdvantageDb) &&
            float.IsFinite(window.WaveformCorrelation) &&
            window.WaveformCorrelation is >= -1 and <= 1 &&
            float.IsFinite(window.TransferScaleToTarget) &&
            float.IsFinite(window.ResidualToTargetDb),
            "Calibrated bleed có cửa sổ bằng chứng không hợp lệ.");

        if (window.Disposition == CalibratedBleedWindowDisposition.Pass)
        {
            Require(
                fingerprint.MedianLagMilliseconds is { } expectedLag &&
                fingerprint.MedianAttenuationDb is { } expectedAttenuation &&
                window.WaveformCorrelation >= policy.MinimumCorrelation &&
                Math.Abs(window.LagMilliseconds - expectedLag) <=
                    policy.MaximumLagDeviationMilliseconds &&
                Math.Abs(window.OtherMicAdvantageDb - expectedAttenuation) <=
                    policy.MaximumAttenuationDeviationDb &&
                window.TransferScaleToTarget > 0 &&
                window.ResidualToTargetDb <= policy.MaximumResidualToTargetDb,
                "Calibrated bleed có cửa sổ Pass không đạt policy.");
        }

        if (window.Disposition == CalibratedBleedWindowDisposition.ConflictingResidual)
        {
            Require(
                window.TransferScaleToTarget > 0 &&
                window.ResidualToTargetDb > policy.MaximumResidualToTargetDb,
                "Calibrated bleed có residual conflict không hợp lệ.");
        }

        if (window.Disposition == CalibratedBleedWindowDisposition.PolarityInverted)
        {
            Require(
                window.TransferScaleToTarget <= 0,
                "Calibrated bleed có polarity conflict không hợp lệ.");
        }
    }

    private static string ComputeWindowEvidenceSha256(
        CalibratedBleedCandidateEvidence candidate,
        CalibratedBleedScoringPolicy policy)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendScoringPolicy(hash, policy);
        AppendInt32(hash, candidate.TargetTrackIndex);
        if (candidate.Outcome != CalibratedBleedShadowOutcome.NoCalibration)
        {
            AppendInt32(hash, candidate.SourceTrackIndex.GetValueOrDefault());
        }

        AppendString(hash, candidate.TargetSourceClipId);
        AppendInt64(hash, candidate.TimelineStartSample);
        AppendInt64(hash, candidate.TimelineEndSample);
        if (candidate.Outcome != CalibratedBleedShadowOutcome.NoCalibration)
        {
            AppendString(hash, candidate.FingerprintEvidenceSha256 ?? string.Empty);
            AppendInt32(hash, candidate.AvailableWindowCount);
            foreach (var window in candidate.WindowSamples)
            {
                AppendString(hash, window.SourcePhraseId);
                AppendInt64(hash, window.TimelineStartSample);
                AppendInt64(hash, window.TimelineEndSample);
                AppendInt32(hash, (int)window.Disposition);
                AppendSingle(hash, window.OtherMicAdvantageDb);
                AppendSingle(hash, window.WaveformCorrelation);
                AppendInt32(hash, window.LagMilliseconds);
                AppendSingle(hash, window.TransferScaleToTarget);
                AppendSingle(hash, window.ResidualToTargetDb);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendScoringPolicy(
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

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static long MillisecondsToSamples(int milliseconds) =>
        checked(milliseconds * 48L);

    private static bool IsEnabled(AudioSegmentStatus? status) =>
        status is AudioSegmentStatus.Speech or AudioSegmentStatus.Ambiguous;

    private static bool IsFiniteOptional(float? value) => value is null || float.IsFinite(value.Value);

    private static bool IsTrainingEligible(NoiseFloorTrainingDecision decision) => decision is
        NoiseFloorTrainingDecision.WarmupCandidate or
        NoiseFloorTrainingDecision.EligibleVadNegative or
        NoiseFloorTrainingDecision.EligibleBackground or
        NoiseFloorTrainingDecision.EligibleStableFloorStep;

    private static bool NoiseFloorPolicyMatches(
        NoiseFloorPolicyDescriptor? policy,
        bool isCandidate) =>
        policy is not null &&
        policy.Version == (isCandidate
            ? "phase10-background-eligible-p20-v1"
            : "phase09-adaptive-p20-v1") &&
        policy.WindowFrameCount == 512 &&
        Math.Abs(policy.Percentile - 0.20) <= 0.000001 &&
        Math.Abs(policy.InitialFloorDbfs - (-90f)) <= 0.0001f &&
        policy.WarmupFrameCount == (isCandidate ? 8 : 1) &&
        Math.Abs(policy.RiseSmoothing - (isCandidate ? 0.05f : 0.15f)) <= 0.0001f &&
        Math.Abs(policy.FallSmoothing - (isCandidate ? 0.20f : 0.15f)) <= 0.0001f &&
        policy.StableStepFrameCount == (isCandidate ? 16 : 0) &&
        Math.Abs(policy.StableStepMaximumSpreadDb - (isCandidate ? 3f : 0f)) <= 0.0001f;

    private static bool BoundaryPolicyMatches(
        VadBoundaryPolicyDescriptor? policy,
        bool isCandidate) =>
        policy is not null &&
        policy.Version == (isCandidate
            ? "phase10-vad-start050-continue040-v1"
            : "phase09-vad-single-threshold-v1") &&
        Math.Abs(policy.StartThreshold - 0.50) <= 0.000001 &&
        Math.Abs(policy.ContinueThreshold - (isCandidate ? 0.40 : 0.50)) <= 0.000001 &&
        policy.PhraseBreakMilliseconds == 350 &&
        policy.MinimumStartEvidenceMilliseconds == 120 &&
        policy.MinimumDirectEvidenceMilliseconds == 120;

    private static void ValidateNoiseBoundaryPhrase(
        int trackIndex,
        NoiseBoundaryPhraseSnapshot phrase,
        DialogueProcessingPreset preset)
    {
        Require(
            phrase.CoreEndSample > phrase.CoreStartSample &&
            phrase.PaddedStartSample <= phrase.CoreStartSample &&
            phrase.PaddedEndSample >= phrase.CoreEndSample &&
            float.IsFinite(phrase.MeasuredPeakDbfs) &&
            float.IsFinite(phrase.RequiredGainDb) &&
            float.IsFinite(phrase.AppliedGainDb),
            $"Noise-boundary track {trackIndex} có phrase snapshot không hợp lệ.");
        var required = (float)(
            preset.TargetSamplePeakDbfs -
            phrase.MeasuredPeakDbfs +
            preset.PremiereCenterPanCompensationDb);
        var applied = MathF.Min(required, (float)preset.MaximumBoostDb);
        Require(
            Math.Abs(phrase.RequiredGainDb - required) <= GainToleranceDb &&
            Math.Abs(phrase.AppliedGainDb - applied) <= GainToleranceDb &&
            phrase.GainWasCapped == (required > preset.MaximumBoostDb),
            $"Noise-boundary track {trackIndex} có phrase snapshot sai gain/target/cap.");
    }

    private static void ValidatePhrases(
        IEnumerable<DialoguePhrase> phrases,
        DialogueProcessingPreset preset)
    {
        foreach (var phrase in phrases)
        {
            Require(
                float.IsFinite(phrase.MeasuredPeakDbfs) &&
                float.IsFinite(phrase.RequiredGainDb) &&
                float.IsFinite(phrase.AppliedGainDb),
                $"Phrase '{phrase.Id}' có peak/gain không hữu hạn.");
            var required = (float)(
                preset.TargetSamplePeakDbfs -
                phrase.MeasuredPeakDbfs +
                preset.PremiereCenterPanCompensationDb);
            var applied = MathF.Min(required, (float)preset.MaximumBoostDb);
            Require(
                Math.Abs(phrase.RequiredGainDb - required) <= GainToleranceDb,
                $"Phrase '{phrase.Id}' có required gain sai hợp đồng hậu routing.");
            Require(
                Math.Abs(phrase.AppliedGainDb - applied) <= GainToleranceDb,
                $"Phrase '{phrase.Id}' có applied gain sai hoặc vượt +{preset.MaximumBoostDb:0.#} dB.");
            Require(
                phrase.GainWasCapped == (required > preset.MaximumBoostDb),
                $"Phrase '{phrase.Id}' có cờ gain-capped không khớp.");
            var predictedPostRoutingPeak =
                phrase.MeasuredPeakDbfs + phrase.AppliedGainDb - (float)preset.PremiereCenterPanCompensationDb;
            Require(
                predictedPostRoutingPeak <= preset.TargetSamplePeakDbfs + GainToleranceDb,
                $"Phrase '{phrase.Id}' dự đoán nóng hơn target hậu routing.");
            if (!phrase.GainWasCapped)
            {
                Require(
                    Math.Abs(predictedPostRoutingPeak - preset.TargetSamplePeakDbfs) <= GainToleranceDb,
                    $"Phrase '{phrase.Id}' không cap nhưng không đạt target dự đoán.");
            }
        }
    }

    private static void ValidateFragments(
        PremiereProject project,
        IReadOnlyList<GeneratedAudioFragment> fragments,
        IReadOnlyDictionary<string, DialoguePhrase> phrases,
        DialogueProcessingPreset preset)
    {
        var referencedPhraseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fragment in fragments)
        {
            var shouldBeEnabled = fragment.Status is AudioSegmentStatus.Speech or AudioSegmentStatus.Ambiguous;
            Require(
                fragment.Enabled == shouldBeEnabled,
                $"Fragment '{fragment.ClipItemId}' có Enabled không khớp status {fragment.Status}.");
            Require(
                fragment.TimelineEndFrame > fragment.TimelineStartFrame &&
                fragment.SourceEndSample >= fragment.SourceStartSample,
                $"Fragment '{fragment.ClipItemId}' có range không hợp lệ.");

            if (fragment.PhraseId is { } phraseId)
            {
                Require(
                    phrases.TryGetValue(phraseId, out var phrase),
                    $"Fragment '{fragment.ClipItemId}' trỏ phrase không tồn tại.");
                Require(
                    fragment.GainDb is { } gain &&
                    double.IsFinite(gain) &&
                    Math.Abs(gain - phrase!.AppliedGainDb) <= GainToleranceDb &&
                    gain <= preset.MaximumBoostDb + GainToleranceDb,
                    $"Fragment '{fragment.ClipItemId}' có gain không khớp phrase.");
                referencedPhraseIds.Add(phraseId);
            }
            else
            {
                Require(
                    fragment.Status != AudioSegmentStatus.Speech,
                    $"Fragment speech '{fragment.ClipItemId}' thiếu phrase.");
                Require(
                    fragment.GainDb is null ||
                    (double.IsFinite(fragment.GainDb.Value) && Math.Abs(fragment.GainDb.Value) <= GainToleranceDb),
                    $"Fragment không phrase '{fragment.ClipItemId}' phải giữ unity gain.");
            }

            if (!fragment.Enabled)
            {
                Require(
                    fragment.Status is AudioSegmentStatus.Noise or AudioSegmentStatus.Bleed,
                    $"Chỉ noise/bleed mới được Disable: '{fragment.ClipItemId}'.");
            }
        }

        foreach (var phrase in phrases.Values.Where(phrase => !referencedPhraseIds.Contains(phrase.Id)))
        {
            var coreStartFrame = phrase.CoreStartSample * project.Sequence.FrameRate / project.Sequence.AudioSampleRate;
            var coreEndFrame = Math.Max(
                coreStartFrame + 1,
                (phrase.CoreEndSample * project.Sequence.FrameRate + project.Sequence.AudioSampleRate - 1) /
                project.Sequence.AudioSampleRate);
            var covering = fragments
                .Where(fragment =>
                    fragment.TrackIndex == phrase.TrackIndex &&
                    fragment.TimelineStartFrame < coreEndFrame &&
                    fragment.TimelineEndFrame > coreStartFrame)
                .OrderBy(fragment => fragment.TimelineStartFrame)
                .ToArray();
            var cursor = coreStartFrame;
            foreach (var fragment in covering)
            {
                Require(
                    fragment.Enabled && fragment.Status is AudioSegmentStatus.Speech or AudioSegmentStatus.Ambiguous,
                    $"Phrase '{phrase.Id}' mất phrase ID nhưng core có frame bị Disable.");
                if (fragment.TimelineStartFrame > cursor)
                {
                    break;
                }

                cursor = Math.Max(cursor, fragment.TimelineEndFrame);
            }

            Require(
                cursor >= coreEndFrame,
                $"Phrase '{phrase.Id}' mất phrase ID và không được fragment Enabled phủ kín core.");
        }

        foreach (var track in project.Sequence.AudioTracks)
        {
            foreach (var clip in track.Clips)
            {
                var clipFragments = fragments
                    .Where(fragment => fragment.SourceClipId == clip.Id)
                    .OrderBy(fragment => fragment.TimelineStartFrame)
                    .ToArray();
                Require(clipFragments.Length > 0, $"Clip '{clip.Id}' không có fragment.");
                var cursor = clip.TimelineStartFrame;
                foreach (var fragment in clipFragments)
                {
                    Require(
                        fragment.TimelineStartFrame == cursor,
                        $"Clip '{clip.Id}' có gap/overlap fragment.");
                    cursor = fragment.TimelineEndFrame;
                }

                Require(cursor == clip.TimelineEndFrame, $"Clip '{clip.Id}' không được phủ đủ frame.");
            }
        }
    }

    private static void ValidateMarkers(
        GeneratedPremiereXmlPlan generated,
        IEnumerable<DialoguePhrase> phrases,
        int frameRate)
    {
        var expectedAmbiguous = generated.AudioFragments
            .Where(fragment => fragment.Status == AudioSegmentStatus.Ambiguous)
            .Select(fragment => string.Join(
                "\u001f",
                fragment.TrackIndex,
                fragment.TimelineStartFrame,
                fragment.TimelineEndFrame,
                fragment.Reason))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualAmbiguous = generated.Markers
            .Where(marker => marker.Name == "Cần kiểm tra")
            .Select(marker => string.Join(
                "\u001f",
                marker.TrackIndex,
                marker.InFrame,
                marker.OutFrame,
                marker.Reason))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Require(
            expectedAmbiguous.SequenceEqual(actualAmbiguous, StringComparer.Ordinal),
            "Marker Cần kiểm tra không phủ đúng fragment ambiguous.");

        const int sampleRate = 48_000;
        Require(sampleRate % frameRate == 0, "Sample rate/frame rate không hỗ trợ marker gain-capped.");
        var samplesPerFrame = sampleRate / frameRate;
        var expectedGainCapped = phrases
            .Where(phrase => phrase.GainWasCapped)
            .Select(phrase => string.Join(
                "\u001f",
                phrase.TrackIndex,
                phrase.CoreStartSample / samplesPerFrame,
                Math.Max(
                    phrase.CoreStartSample / samplesPerFrame + 1,
                    (phrase.CoreEndSample + samplesPerFrame - 1) / samplesPerFrame)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualGainCapped = generated.Markers
            .Where(marker => marker.Reason == "gain-capped")
            .Select(marker => string.Join(
                "\u001f",
                marker.TrackIndex,
                marker.InFrame,
                marker.OutFrame))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Require(
            expectedGainCapped.SequenceEqual(actualGainCapped, StringComparer.Ordinal),
            "Marker gain-capped không phủ đúng phrase bị giới hạn.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
