using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.ProjectModel;
using PremiereAutoDialogueXml.Core.Timing;
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
        var frameGrid = PremiereNdfFrameGrid.Create(
            project.Sequence.FrameRate,
            project.Sequence.AudioSampleRate);

        ValidateNoiseBoundarySafety(analysis, preset);
        var phrases = analysis.Tracks
            .SelectMany(track => track.Phrases)
            .ToDictionary(phrase => phrase.Id, StringComparer.Ordinal);
        ValidatePhrases(phrases.Values, preset);
        ValidateFragments(project, generated.AudioFragments, phrases, preset, frameGrid);
        ValidateMarkers(generated, phrases.Values, frameGrid);
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
        DialogueProcessingPreset preset,
        PremiereNdfFrameGrid frameGrid)
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
            var coreStartFrame = frameGrid.SampleToFrameFloor(phrase.CoreStartSample);
            var coreEndFrame = Math.Max(
                coreStartFrame + 1,
                frameGrid.SampleToFrameCeiling(phrase.CoreEndSample));
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
        PremiereNdfFrameGrid frameGrid)
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

        var expectedGainCapped = phrases
            .Where(phrase => phrase.GainWasCapped)
            .Select(phrase => string.Join(
                "\u001f",
                phrase.TrackIndex,
                frameGrid.SampleToFrameFloor(phrase.CoreStartSample),
                Math.Max(
                    frameGrid.SampleToFrameFloor(phrase.CoreStartSample) + 1,
                    frameGrid.SampleToFrameCeiling(phrase.CoreEndSample))))
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
