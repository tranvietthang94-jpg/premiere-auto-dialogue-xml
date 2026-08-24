using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PremiereAutoDialogueXml.Validation;

public enum PremiereConstantGainSafePlanDecisionKind
{
    Selected,
    SkippedUnsafe,
    SkippedBoundaryConflict,
    SkippedPhraseConflict,
    SkippedLimit
}

public sealed record PremiereConstantGainSafePlanDecision(
    PremiereConstantGainBoundaryRequest Request,
    PremiereConstantGainSafePlanDecisionKind Decision,
    string Reason,
    int? ConflictingTrackIndex,
    long? ConflictingBoundaryFrame,
    string? ConflictingPhraseId);

public sealed record PremiereConstantGainSafeBatchPlan(
    string SchemaVersion,
    string Policy,
    string SafetyPolicy,
    int RequestedMaximumTransitions,
    int ScannerTransientCandidateCount,
    int CapturedTransientCandidateCount,
    int UncapturedTransientCandidateCount,
    int SafetyEligibleCount,
    int SafetyRejectedCount,
    int SelectedCount,
    int EnabledToDisabledSelectedCount,
    int DisabledToEnabledSelectedCount,
    int GainChangeSelectedCount,
    string SelectionStreamSha256,
    IReadOnlyList<PremiereConstantGainBoundaryRequest> Selected,
    IReadOnlyList<PremiereConstantGainSafePlanDecision> Decisions);

public sealed class PremiereConstantGainSafeBatchPlanner
{
    public const string Policy = "phase16-gain-safe-risk-diverse-phrase-conflict-v1";

    public PremiereConstantGainSafeBatchPlan Plan(
        PremiereTransitionGainSafetyReport safety,
        int maximumTransitions)
    {
        ArgumentNullException.ThrowIfNull(safety);
        if (!string.Equals(safety.Policy, PremiereTransitionGainSafetyGate.Policy, StringComparison.Ordinal) ||
            safety.CapturedTransientCandidateCount != safety.Decisions.Count ||
            safety.EligibleCount != safety.Decisions.Count(item =>
                item.Decision == PremiereTransitionGainSafetyDecisionKind.Eligible) ||
            safety.RejectedCount != safety.Decisions.Count(item =>
                item.Decision != PremiereTransitionGainSafetyDecisionKind.Eligible))
        {
            throw new InvalidDataException("Gain safety report không nhất quán.");
        }

        if (maximumTransitions is < 1 or > PremiereConstantGainBatchPlanner.MaximumTransitionsPerCandidate)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTransitions));
        }

        var duplicate = safety.Decisions
            .GroupBy(item => (item.Request.TrackIndex, item.Request.BoundaryFrame))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"Gain safety report lặp A{duplicate.Key.TrackIndex}/frame {duplicate.Key.BoundaryFrame}.");
        }

        var eligible = safety.Decisions
            .Where(item => item.Decision == PremiereTransitionGainSafetyDecisionKind.Eligible)
            .OrderByDescending(item => item.Request.StepAboveLocalP99Db)
            .ThenByDescending(item => item.Request.RenderedStepDbfs)
            .ThenBy(item => item.Request.TrackIndex)
            .ThenBy(item => item.Request.BoundaryFrame)
            .ToArray();
        var selected = new List<PremiereTransitionGainSafetyDecision>(maximumTransitions);
        var selectedKeys = new HashSet<(int TrackIndex, long BoundaryFrame)>();
        var selectedPhrases = new HashSet<(int TrackIndex, string PhraseId)>();

        var seeds = eligible
            .GroupBy(item => item.Request.ExpectedKind)
            .Select(group => group.First())
            .OrderByDescending(item => item.Request.StepAboveLocalP99Db)
            .ThenByDescending(item => item.Request.RenderedStepDbfs)
            .ThenBy(item => item.Request.ExpectedKind)
            .ToArray();
        foreach (var seed in seeds)
        {
            if (selected.Count >= maximumTransitions)
            {
                break;
            }

            var candidate = eligible.FirstOrDefault(item =>
                item.Request.ExpectedKind == seed.Request.ExpectedKind &&
                CanSelect(item, selected, selectedKeys, selectedPhrases));
            if (candidate is not null)
            {
                Select(candidate, selected, selectedKeys, selectedPhrases);
            }
        }

        foreach (var candidate in eligible)
        {
            if (selected.Count >= maximumTransitions)
            {
                break;
            }

            if (CanSelect(candidate, selected, selectedKeys, selectedPhrases))
            {
                Select(candidate, selected, selectedKeys, selectedPhrases);
            }
        }

        var orderedSelected = selected
            .OrderBy(item => item.Request.TrackIndex)
            .ThenBy(item => item.Request.BoundaryFrame)
            .ToArray();
        var decisions = safety.Decisions
            .OrderBy(item => item.Request.TrackIndex)
            .ThenBy(item => item.Request.BoundaryFrame)
            .Select(item => ToPlanDecision(item, orderedSelected, selectedKeys, maximumTransitions))
            .ToArray();
        var requests = orderedSelected.Select(item => item.Request).ToArray();
        return new(
            "1.0",
            Policy,
            safety.Policy,
            maximumTransitions,
            safety.ScannerTransientCandidateCount,
            safety.CapturedTransientCandidateCount,
            safety.UncapturedTransientCandidateCount,
            safety.EligibleCount,
            safety.RejectedCount,
            requests.Length,
            requests.Count(item => item.ExpectedKind == BoundaryTransitionKind.EnabledToDisabled),
            requests.Count(item => item.ExpectedKind == BoundaryTransitionKind.DisabledToEnabled),
            requests.Count(item => item.ExpectedKind == BoundaryTransitionKind.GainChange),
            ComputeSelectionHash(requests),
            requests,
            decisions);
    }

    private static bool CanSelect(
        PremiereTransitionGainSafetyDecision candidate,
        IReadOnlyList<PremiereTransitionGainSafetyDecision> selected,
        ISet<(int TrackIndex, long BoundaryFrame)> selectedKeys,
        ISet<(int TrackIndex, string PhraseId)> selectedPhrases) =>
        !selectedKeys.Contains((candidate.Request.TrackIndex, candidate.Request.BoundaryFrame)) &&
        FindBoundaryConflict(selected, candidate) is null &&
        candidate.AffectedPhraseIds.All(phraseId =>
            !selectedPhrases.Contains((candidate.Request.TrackIndex, phraseId)));

    private static void Select(
        PremiereTransitionGainSafetyDecision candidate,
        ICollection<PremiereTransitionGainSafetyDecision> selected,
        ISet<(int TrackIndex, long BoundaryFrame)> selectedKeys,
        ISet<(int TrackIndex, string PhraseId)> selectedPhrases)
    {
        selected.Add(candidate);
        selectedKeys.Add((candidate.Request.TrackIndex, candidate.Request.BoundaryFrame));
        foreach (var phraseId in candidate.AffectedPhraseIds)
        {
            selectedPhrases.Add((candidate.Request.TrackIndex, phraseId));
        }
    }

    private static PremiereConstantGainSafePlanDecision ToPlanDecision(
        PremiereTransitionGainSafetyDecision item,
        IReadOnlyList<PremiereTransitionGainSafetyDecision> selected,
        ISet<(int TrackIndex, long BoundaryFrame)> selectedKeys,
        int maximumTransitions)
    {
        if (item.Decision != PremiereTransitionGainSafetyDecisionKind.Eligible)
        {
            return new(
                item.Request,
                PremiereConstantGainSafePlanDecisionKind.SkippedUnsafe,
                item.Reason,
                null,
                null,
                null);
        }

        if (selectedKeys.Contains((item.Request.TrackIndex, item.Request.BoundaryFrame)))
        {
            return new(
                item.Request,
                PremiereConstantGainSafePlanDecisionKind.Selected,
                "selected",
                null,
                null,
                null);
        }

        var boundaryConflict = FindBoundaryConflict(selected, item);
        if (boundaryConflict is not null)
        {
            return new(
                item.Request,
                PremiereConstantGainSafePlanDecisionKind.SkippedBoundaryConflict,
                "boundary-too-close",
                boundaryConflict.Request.TrackIndex,
                boundaryConflict.Request.BoundaryFrame,
                null);
        }

        var phraseConflict = selected.FirstOrDefault(existing =>
            existing.Request.TrackIndex == item.Request.TrackIndex &&
            existing.AffectedPhraseIds.Intersect(item.AffectedPhraseIds, StringComparer.Ordinal).Any());
        if (phraseConflict is not null)
        {
            var phraseId = phraseConflict.AffectedPhraseIds
                .Intersect(item.AffectedPhraseIds, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .First();
            return new(
                item.Request,
                PremiereConstantGainSafePlanDecisionKind.SkippedPhraseConflict,
                "phrase-already-affected",
                phraseConflict.Request.TrackIndex,
                phraseConflict.Request.BoundaryFrame,
                phraseId);
        }

        return new(
            item.Request,
            PremiereConstantGainSafePlanDecisionKind.SkippedLimit,
            selected.Count >= maximumTransitions ? "maximum-transitions-reached" : "not-selected",
            null,
            null,
            null);
    }

    private static PremiereTransitionGainSafetyDecision? FindBoundaryConflict(
        IEnumerable<PremiereTransitionGainSafetyDecision> selected,
        PremiereTransitionGainSafetyDecision candidate) =>
        selected.FirstOrDefault(existing =>
            existing.Request.TrackIndex == candidate.Request.TrackIndex &&
            Math.Abs(existing.Request.BoundaryFrame - candidate.Request.BoundaryFrame) <
            PremiereConstantGainBatchPlanner.MinimumBoundarySeparationFrames);

    private static string ComputeSelectionHash(
        IEnumerable<PremiereConstantGainBoundaryRequest> selected)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var request in selected)
        {
            var line = string.Join('|',
                request.TrackIndex.ToString(CultureInfo.InvariantCulture),
                request.BoundaryFrame.ToString(CultureInfo.InvariantCulture),
                request.ExpectedKind,
                request.RenderedStepDbfs.ToString("R", CultureInfo.InvariantCulture),
                request.StepAboveLocalP99Db.ToString("R", CultureInfo.InvariantCulture)) + "\n";
            hash.AppendData(Encoding.UTF8.GetBytes(line));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
