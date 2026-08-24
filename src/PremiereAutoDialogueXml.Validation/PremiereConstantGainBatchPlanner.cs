using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PremiereAutoDialogueXml.Validation;

public enum PremiereConstantGainPlanDecisionKind
{
    Selected,
    SkippedConflict,
    SkippedLimit
}

public sealed record PremiereConstantGainPlanDecision(
    PremiereConstantGainBoundaryRequest Request,
    PremiereConstantGainPlanDecisionKind Decision,
    int? ConflictingTrackIndex,
    long? ConflictingBoundaryFrame);

public sealed record PremiereConstantGainBatchPlan(
    string SchemaVersion,
    string Policy,
    string ScannerPolicy,
    int MinimumBoundarySeparationFrames,
    int RequestedMaximumTransitions,
    int ScannerTransientCandidateCount,
    int CapturedTransientCandidateCount,
    int UncapturedTransientCandidateCount,
    int SelectedCount,
    int EnabledToDisabledSelectedCount,
    int DisabledToEnabledSelectedCount,
    int GainChangeSelectedCount,
    string SelectionStreamSha256,
    IReadOnlyList<PremiereConstantGainBoundaryRequest> Selected,
    IReadOnlyList<PremiereConstantGainPlanDecision> Decisions);

public sealed class PremiereConstantGainBatchPlanner
{
    public const string Policy = "phase15-constant-gain-bounded-risk-diverse-conflict-v1";
    public const int MinimumBoundarySeparationFrames = 2;
    public const int MaximumTransitionsPerCandidate = 64;

    public PremiereConstantGainBatchPlan Plan(
        BoundaryDiscontinuityScanReport scan,
        int maximumTransitions)
    {
        ArgumentNullException.ThrowIfNull(scan);
        if (!string.Equals(scan.Policy, BoundaryDiscontinuityScanner.Policy, StringComparison.Ordinal) ||
            scan.TransientScreeningCandidateCount < 0)
        {
            throw new InvalidDataException("Boundary scan không dùng đúng policy Phase 14 đã khóa.");
        }

        if (maximumTransitions is < 1 or > MaximumTransitionsPerCandidate)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTransitions));
        }

        var ranked = scan.TopSamples
            .Where(sample => sample.TransientScreeningCandidate)
            .Select(ToRequest)
            .OrderByDescending(request => request.StepAboveLocalP99Db)
            .ThenByDescending(request => request.RenderedStepDbfs)
            .ThenBy(request => request.TrackIndex)
            .ThenBy(request => request.BoundaryFrame)
            .ToArray();
        if (ranked.Length > scan.TransientScreeningCandidateCount)
        {
            throw new InvalidDataException("Boundary scan có count transient không nhất quán.");
        }

        var duplicate = ranked
            .GroupBy(request => (request.TrackIndex, request.BoundaryFrame))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"Boundary scan lặp A{duplicate.Key.TrackIndex}/frame {duplicate.Key.BoundaryFrame}.");
        }

        var selected = new List<PremiereConstantGainBoundaryRequest>(maximumTransitions);
        var selectedKeys = new HashSet<(int TrackIndex, long BoundaryFrame)>();
        var kindSeeds = ranked
            .GroupBy(request => request.ExpectedKind)
            .Select(group => group.First())
            .OrderByDescending(request => request.StepAboveLocalP99Db)
            .ThenByDescending(request => request.RenderedStepDbfs)
            .ThenBy(request => request.ExpectedKind)
            .ToArray();

        foreach (var seed in kindSeeds)
        {
            if (selected.Count >= maximumTransitions)
            {
                break;
            }

            var candidate = ranked.FirstOrDefault(request =>
                request.ExpectedKind == seed.ExpectedKind &&
                !selectedKeys.Contains((request.TrackIndex, request.BoundaryFrame)) &&
                FindConflict(selected, request) is null);
            if (candidate is not null)
            {
                Select(selected, selectedKeys, candidate);
            }
        }

        foreach (var request in ranked)
        {
            if (selected.Count >= maximumTransitions)
            {
                break;
            }

            if (selectedKeys.Contains((request.TrackIndex, request.BoundaryFrame)) ||
                FindConflict(selected, request) is not null)
            {
                continue;
            }

            Select(selected, selectedKeys, request);
        }

        var orderedSelected = selected
            .OrderBy(request => request.TrackIndex)
            .ThenBy(request => request.BoundaryFrame)
            .ToArray();
        var decisions = ranked.Select(request =>
        {
            if (selectedKeys.Contains((request.TrackIndex, request.BoundaryFrame)))
            {
                return new PremiereConstantGainPlanDecision(
                    request,
                    PremiereConstantGainPlanDecisionKind.Selected,
                    null,
                    null);
            }

            var conflict = FindConflict(orderedSelected, request);
            return conflict is null
                ? new(
                    request,
                    PremiereConstantGainPlanDecisionKind.SkippedLimit,
                    null,
                    null)
                : new(
                    request,
                    PremiereConstantGainPlanDecisionKind.SkippedConflict,
                    conflict.TrackIndex,
                    conflict.BoundaryFrame);
        }).ToArray();

        return new(
            "1.0",
            Policy,
            scan.Policy,
            MinimumBoundarySeparationFrames,
            maximumTransitions,
            scan.TransientScreeningCandidateCount,
            ranked.Length,
            scan.TransientScreeningCandidateCount - ranked.Length,
            orderedSelected.Length,
            orderedSelected.Count(request => request.ExpectedKind == BoundaryTransitionKind.EnabledToDisabled),
            orderedSelected.Count(request => request.ExpectedKind == BoundaryTransitionKind.DisabledToEnabled),
            orderedSelected.Count(request => request.ExpectedKind == BoundaryTransitionKind.GainChange),
            ComputeSelectionHash(orderedSelected),
            orderedSelected,
            decisions);
    }

    private static PremiereConstantGainBoundaryRequest ToRequest(BoundaryDiscontinuitySample sample)
    {
        if (sample.TrackIndex <= 0 ||
            sample.BoundaryFrame <= 0 ||
            !double.IsFinite(sample.RenderedStepDbfs) ||
            !double.IsFinite(sample.RenderedStepAboveLocalP99Db))
        {
            throw new InvalidDataException("Boundary scan chứa transient candidate không hợp lệ.");
        }

        return new(
            sample.TrackIndex,
            sample.BoundaryFrame,
            sample.Kind,
            sample.RenderedStepDbfs,
            sample.RenderedStepAboveLocalP99Db);
    }

    private static void Select(
        ICollection<PremiereConstantGainBoundaryRequest> selected,
        ISet<(int TrackIndex, long BoundaryFrame)> selectedKeys,
        PremiereConstantGainBoundaryRequest request)
    {
        selected.Add(request);
        selectedKeys.Add((request.TrackIndex, request.BoundaryFrame));
    }

    private static PremiereConstantGainBoundaryRequest? FindConflict(
        IEnumerable<PremiereConstantGainBoundaryRequest> selected,
        PremiereConstantGainBoundaryRequest request) =>
        selected.FirstOrDefault(existing =>
            existing.TrackIndex == request.TrackIndex &&
            Math.Abs(existing.BoundaryFrame - request.BoundaryFrame) < MinimumBoundarySeparationFrames);

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
