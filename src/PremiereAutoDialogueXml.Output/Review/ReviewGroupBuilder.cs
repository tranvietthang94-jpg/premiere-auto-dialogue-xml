using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Output.Audit;
using PremiereAutoDialogueXml.Output.Xml;

namespace PremiereAutoDialogueXml.Output.Review;

public sealed class ReviewGroupBuilder
{
    public const int DefaultGroupingGapFrames = 12;
    public const string TimecodeBasis = "sequence-relative-ndf";

    public IReadOnlyList<ReviewGroupAudit> Build(
        IReadOnlyList<GeneratedAudioFragment> fragments,
        IReadOnlyList<GeneratedSequenceMarker> markers,
        IReadOnlyList<CrossTrackShadowEvidence> shadowEvidence,
        int frameRate,
        int audioSampleRate,
        int groupingGapFrames = DefaultGroupingGapFrames)
    {
        ArgumentNullException.ThrowIfNull(fragments);
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(shadowEvidence);
        if (frameRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameRate));
        }

        if (audioSampleRate <= 0 || audioSampleRate % frameRate != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(audioSampleRate),
                "Sample rate phải chia hết cho frame rate để ánh xạ review chính xác.");
        }

        if (groupingGapFrames < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(groupingGapFrames));
        }

        var ambiguousMarkers = markers
            .Where(IsAmbiguousMarker)
            .OrderBy(marker => marker.TrackIndex)
            .ThenBy(marker => marker.InFrame)
            .ThenBy(marker => marker.OutFrame)
            .ThenBy(marker => marker.Reason, StringComparer.Ordinal)
            .ToArray();
        var samplesPerFrame = audioSampleRate / frameRate;
        var groups = new List<ReviewGroupAudit>();

        foreach (var trackGroup in ambiguousMarkers.GroupBy(marker => marker.TrackIndex).OrderBy(group => group.Key))
        {
            var trackSequence = 0;
            var current = new List<GeneratedSequenceMarker>();
            var currentOutFrame = -1L;
            foreach (var marker in trackGroup)
            {
                if (current.Count > 0 && marker.InFrame - currentOutFrame > groupingGapFrames)
                {
                    groups.Add(CreateGroup(
                        trackGroup.Key,
                        ++trackSequence,
                        current,
                        fragments,
                        shadowEvidence,
                        frameRate,
                        samplesPerFrame));
                    current.Clear();
                    currentOutFrame = -1;
                }

                current.Add(marker);
                currentOutFrame = Math.Max(currentOutFrame, marker.OutFrame);
            }

            if (current.Count > 0)
            {
                groups.Add(CreateGroup(
                    trackGroup.Key,
                    ++trackSequence,
                    current,
                    fragments,
                    shadowEvidence,
                    frameRate,
                    samplesPerFrame));
            }
        }

        if (groups.Sum(group => group.MarkerCount) != ambiguousMarkers.Length)
        {
            throw new InvalidDataException("Review grouping không phủ đúng toàn bộ marker mơ hồ.");
        }

        return groups;
    }

    public static int CountAmbiguousMarkers(IEnumerable<GeneratedSequenceMarker> markers) =>
        markers.Count(IsAmbiguousMarker);

    private static ReviewGroupAudit CreateGroup(
        int trackIndex,
        int trackSequence,
        IReadOnlyList<GeneratedSequenceMarker> markers,
        IReadOnlyList<GeneratedAudioFragment> fragments,
        IReadOnlyList<CrossTrackShadowEvidence> shadowEvidence,
        int frameRate,
        int samplesPerFrame)
    {
        var inFrame = markers.Min(marker => marker.InFrame);
        var outFrame = markers.Max(marker => marker.OutFrame);
        var reasons = markers
            .Select(marker => marker.Reason)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var sourceFileNames = fragments
            .Where(fragment =>
                fragment.TrackIndex == trackIndex &&
                fragment.Status == AudioSegmentStatus.Ambiguous &&
                fragment.TimelineStartFrame < outFrame &&
                fragment.TimelineEndFrame > inFrame)
            .Select(fragment => Path.GetFileName(fragment.SourceFileName))
            .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourceFileNames.Length == 0)
        {
            throw new InvalidDataException(
                $"Review group A{trackIndex} [{inFrame}, {outFrame}) không khớp fragment ambiguous nào.");
        }

        var inSample = checked(inFrame * samplesPerFrame);
        var outSample = checked(outFrame * samplesPerFrame);
        var groupEvidence = shadowEvidence
            .Where(evidence =>
                evidence.TrackIndex == trackIndex &&
                evidence.TimelineStartSample < outSample &&
                evidence.TimelineEndSample > inSample)
            .OrderByDescending(evidence => OutcomeRisk(evidence.Outcome))
            .ThenByDescending(evidence => evidence.BestComparison?.WaveformCorrelation ?? float.MinValue)
            .ThenBy(evidence => evidence.BestComparison?.OtherTrackIndex ?? int.MaxValue)
            .ThenBy(evidence => evidence.TimelineStartSample)
            .ToArray();
        var representative = groupEvidence.FirstOrDefault();

        return new(
            Id: $"R-T{trackIndex:D2}-{trackSequence:D6}",
            Priority: DeterminePriority(reasons, groupEvidence),
            TrackIndex: trackIndex,
            InFrame: inFrame,
            OutFrame: outFrame,
            InTimecode: FormatTimecode(inFrame, frameRate),
            OutTimecode: FormatTimecode(outFrame, frameRate),
            MarkerCount: markers.Count,
            Reasons: reasons,
            SourceFileNames: sourceFileNames,
            ShadowEvidenceCount: groupEvidence.Length,
            RepresentativeShadowOutcome: representative?.Outcome,
            BestComparison: representative?.BestComparison);
    }

    private static ReviewPriority DeterminePriority(
        IReadOnlyCollection<string> reasons,
        IReadOnlyCollection<CrossTrackShadowEvidence> evidence)
    {
        if (reasons.Any(IsEnergyVadConflictReason))
        {
            return evidence.Count > 0 && evidence.All(item => item.Outcome == CrossTrackShadowOutcome.LikelyBleed)
                ? ReviewPriority.Medium
                : ReviewPriority.High;
        }

        if (reasons.Contains("conflicting-direct-and-bleed-evidence", StringComparer.Ordinal))
        {
            return ReviewPriority.High;
        }

        if (reasons.All(reason => reason == "ambiguous-near-speech"))
        {
            return ReviewPriority.Low;
        }

        if (reasons.Contains("ambiguous-independent", StringComparer.Ordinal))
        {
            return ReviewPriority.Medium;
        }

        return ReviewPriority.High;
    }

    private static int OutcomeRisk(CrossTrackShadowOutcome outcome) => outcome switch
    {
        CrossTrackShadowOutcome.ConflictingEvidence => 4,
        CrossTrackShadowOutcome.NoComparableSpeech => 3,
        CrossTrackShadowOutcome.BelowThreshold => 2,
        _ => 1
    };

    private static string FormatTimecode(long frame, int frameRate)
    {
        if (frame < 0)
        {
            throw new InvalidDataException("Review marker không được có frame âm.");
        }

        var frames = frame % frameRate;
        var totalSeconds = frame / frameRate;
        var seconds = totalSeconds % 60;
        var totalMinutes = totalSeconds / 60;
        var minutes = totalMinutes % 60;
        var hours = totalMinutes / 60;
        return $"{hours:D2}:{minutes:D2}:{seconds:D2}:{frames:D2}";
    }

    private static bool IsAmbiguousMarker(GeneratedSequenceMarker marker) =>
        marker.Name == "Cần kiểm tra" && marker.Reason != "gain-capped";

    private static bool IsEnergyVadConflictReason(string reason) =>
        reason.StartsWith("ambiguous-energy-vad-conflict", StringComparison.Ordinal);
}
