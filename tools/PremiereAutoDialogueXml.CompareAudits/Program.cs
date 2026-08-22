using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: PremiereAutoDialogueXml.CompareAudits <baseline-audit.json> <candidate-audit.json>");
    return 2;
}

var baselinePath = Path.GetFullPath(args[0]);
var candidatePath = Path.GetFullPath(args[1]);

try
{
    var baseline = await ReadAuditAsync(baselinePath);
    var candidate = await ReadAuditAsync(candidatePath);
    var comparison = Compare(baseline, candidate, baselinePath, candidatePath);

    Console.WriteLine(JsonSerializer.Serialize(comparison, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    }));

    return comparison.Passed ? 0 : 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

static async Task<Audit> ReadAuditAsync(string path)
{
    await using var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 1024 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    var audit = await JsonSerializer.DeserializeAsync<Audit>(stream, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });

    return audit ?? throw new InvalidDataException($"Audit is empty: {path}");
}

static AuditComparison Compare(Audit baseline, Audit candidate, string baselinePath, string candidatePath)
{
    ValidateFragments(baseline.Fragments, "baseline");
    ValidateFragments(candidate.Fragments, "candidate");

    var failures = new List<string>();
    if (!StringComparer.OrdinalIgnoreCase.Equals(baseline.SourceXmlSha256, candidate.SourceXmlSha256))
    {
        failures.Add("source-xml-hash-mismatch");
    }

    var baselineDescription = DescribeAudit(baseline, baselinePath);
    var candidateDescription = DescribeAudit(candidate, candidatePath);
    if (!baselineDescription.OutputXmlHashMatches)
    {
        failures.Add("baseline-output-xml-hash-mismatch");
    }

    if (!candidateDescription.OutputXmlHashMatches)
    {
        failures.Add("candidate-output-xml-hash-mismatch");
    }

    if (candidateDescription.TemporaryFileCount > 0)
    {
        failures.Add("candidate-temporary-files-remain");
    }

    var candidateNoiseBoundary = SummarizeNoiseBoundary(candidate.NoiseBoundaryComparison);
    if (SchemaAtLeast(candidate.SchemaVersion, 1, 6) && candidateNoiseBoundary is null)
    {
        failures.Add("candidate-noise-boundary-comparison-missing");
    }
    else if (candidateNoiseBoundary?.BaselineEnabledFinalDisabledCount > 0)
    {
        failures.Add("candidate-noise-boundary-baseline-enabled-lost");
    }

    if (SchemaAtLeast(candidate.SchemaVersion, 1, 8) && !ValidSequenceTiming(candidate.SequenceTiming))
    {
        failures.Add("candidate-sequence-timing-invalid");
    }

    var baselineTracks = baseline.Fragments
        .GroupBy(fragment => fragment.TrackIndex)
        .ToDictionary(group => group.Key, group => group.OrderBy(fragment => fragment.TimelineStartFrame).ToArray());
    var candidateTracks = candidate.Fragments
        .GroupBy(fragment => fragment.TrackIndex)
        .ToDictionary(group => group.Key, group => group.OrderBy(fragment => fragment.TimelineStartFrame).ToArray());

    var trackIndexes = baselineTracks.Keys.Union(candidateTracks.Keys).Order().ToArray();
    long lostEnabledIntervals = 0;
    long lostEnabledFrames = 0;
    long newlyEnabledIntervals = 0;
    long newlyEnabledFrames = 0;
    long coverageMismatchIntervals = 0;
    long coverageMismatchFrames = 0;
    var transitions = new Dictionary<string, TransitionCount>(StringComparer.Ordinal);

    foreach (var trackIndex in trackIndexes)
    {
        baselineTracks.TryGetValue(trackIndex, out var baselineFragments);
        candidateTracks.TryGetValue(trackIndex, out var candidateFragments);
        baselineFragments ??= [];
        candidateFragments ??= [];

        var boundaries = baselineFragments
            .SelectMany(fragment => new[] { fragment.TimelineStartFrame, fragment.TimelineEndFrame })
            .Concat(candidateFragments.SelectMany(fragment => new[] { fragment.TimelineStartFrame, fragment.TimelineEndFrame }))
            .Distinct()
            .Order()
            .ToArray();

        var baselineIndex = 0;
        var candidateIndex = 0;
        for (var boundaryIndex = 0; boundaryIndex + 1 < boundaries.Length; boundaryIndex++)
        {
            var start = boundaries[boundaryIndex];
            var end = boundaries[boundaryIndex + 1];
            if (start == end)
            {
                continue;
            }

            var baselineFragment = FindCovering(baselineFragments, ref baselineIndex, start, end);
            var candidateFragment = FindCovering(candidateFragments, ref candidateIndex, start, end);
            if (baselineFragment is null && candidateFragment is null)
            {
                continue;
            }

            var frames = end - start;
            if (baselineFragment is null || candidateFragment is null)
            {
                coverageMismatchIntervals++;
                coverageMismatchFrames += frames;
                continue;
            }

            var transitionKey = $"{baselineFragment.Status}->{candidateFragment.Status}";
            if (!transitions.TryGetValue(transitionKey, out var transition))
            {
                transition = new TransitionCount();
                transitions.Add(transitionKey, transition);
            }

            transition.Intervals++;
            transition.Frames += frames;

            if (baselineFragment.Enabled && !candidateFragment.Enabled)
            {
                lostEnabledIntervals++;
                lostEnabledFrames += frames;
            }
            else if (!baselineFragment.Enabled && candidateFragment.Enabled)
            {
                newlyEnabledIntervals++;
                newlyEnabledFrames += frames;
            }
        }
    }

    if (coverageMismatchIntervals > 0)
    {
        failures.Add("timeline-coverage-mismatch");
    }

    if (lostEnabledIntervals > 0)
    {
        failures.Add("baseline-enabled-audio-lost");
    }

    return new AuditComparison(
        Passed: failures.Count == 0,
        Failures: failures,
        Baseline: baselineDescription,
        Candidate: candidateDescription,
        SourceXmlHashesMatch: StringComparer.OrdinalIgnoreCase.Equals(baseline.SourceXmlSha256, candidate.SourceXmlSha256),
        CoverageMismatchIntervals: coverageMismatchIntervals,
        CoverageMismatchFrames: coverageMismatchFrames,
        LostEnabledIntervals: lostEnabledIntervals,
        LostEnabledFrames: lostEnabledFrames,
        NewlyEnabledIntervals: newlyEnabledIntervals,
        NewlyEnabledFrames: newlyEnabledFrames,
        Transitions: transitions.OrderBy(item => item.Key).ToDictionary(item => item.Key, item => item.Value),
        CandidateVadFrontEnd: SummarizeVad(candidate.VadFrontEndComparison),
        CandidateNoiseBoundary: candidateNoiseBoundary);
}

static bool SchemaAtLeast(string schemaVersion, int major, int minor) =>
    Version.TryParse(schemaVersion, out var parsed) && parsed >= new Version(major, minor);

static bool ValidSequenceTiming(SequenceTiming? timing) =>
    timing is not null &&
    timing.FrameRate is 24 or 25 or 30 &&
    !timing.Ntsc &&
    timing.AudioSampleRate == 48_000 &&
    timing.AudioSampleRate % timing.FrameRate == 0 &&
    timing.SamplesPerFrame == timing.AudioSampleRate / timing.FrameRate &&
    timing.FrameGridPolicy == "phase12-integer-ndf-exact-frame-grid-v1";

static Fragment? FindCovering(Fragment[] fragments, ref int index, long start, long end)
{
    while (index < fragments.Length && fragments[index].TimelineEndFrame <= start)
    {
        index++;
    }

    if (index >= fragments.Length)
    {
        return null;
    }

    var fragment = fragments[index];
    return fragment.TimelineStartFrame <= start && fragment.TimelineEndFrame >= end
        ? fragment
        : null;
}

static void ValidateFragments(IReadOnlyList<Fragment> fragments, string label)
{
    foreach (var fragment in fragments)
    {
        if (fragment.TimelineStartFrame >= fragment.TimelineEndFrame)
        {
            throw new InvalidDataException($"{label} contains an empty or reversed fragment on track {fragment.TrackIndex}.");
        }

        var expectedEnabled = fragment.Status is "speech" or "ambiguous";
        if (fragment.Enabled != expectedEnabled)
        {
            throw new InvalidDataException($"{label} has an invalid status/enabled pair on track {fragment.TrackIndex}.");
        }
    }

    foreach (var track in fragments.GroupBy(fragment => fragment.TrackIndex))
    {
        Fragment? previous = null;
        foreach (var fragment in track.OrderBy(fragment => fragment.TimelineStartFrame))
        {
            if (previous is not null && fragment.TimelineStartFrame < previous.TimelineEndFrame)
            {
                throw new InvalidDataException($"{label} has overlapping fragments on track {track.Key}.");
            }

            previous = fragment;
        }
    }
}

static AuditDescription DescribeAudit(Audit audit, string path)
{
    var directory = Path.GetDirectoryName(path)
        ?? throw new InvalidDataException($"Audit path has no parent directory: {path}");
    var outputXmlPath = Path.Combine(directory, audit.OutputXmlFileName);
    var auditSha256 = ComputeSha256(path);
    var outputXmlSha256 = File.Exists(outputXmlPath) ? ComputeSha256(outputXmlPath) : null;
    var temporaryFileCount = Directory
        .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Count(file => file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));

    return new AuditDescription(
        Path: path,
        SchemaVersion: audit.SchemaVersion,
        SourceXmlSha256: audit.SourceXmlSha256,
        EmbeddedOutputXmlSha256: audit.OutputXmlSha256,
        ActualOutputXmlSha256: outputXmlSha256,
        OutputXmlHashMatches: StringComparer.OrdinalIgnoreCase.Equals(audit.OutputXmlSha256, outputXmlSha256),
        AuditSha256: auditSha256,
        FragmentCount: audit.Fragments.Count,
        TemporaryFileCount: temporaryFileCount);
}

static string ComputeSha256(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}

static VadSummary? SummarizeVad(VadFrontEndComparison? comparison)
{
    if (comparison is null)
    {
        return null;
    }

    return new VadSummary(
        comparison.LegacyResampling,
        comparison.CandidateResampling,
        comparison.Tracks.Sum(track => track.ObservationCount),
        comparison.Tracks.Sum(track => track.ChangedObservationCount),
        comparison.Tracks.Sum(track => track.SegmentDifferenceCount),
        comparison.Tracks.Sum(track => track.LegacyEnabledCandidateDisabledCount),
        comparison.Tracks.Sum(track => track.LegacyDisabledCandidateEnabledCount),
        comparison.Tracks.Count == 0 ? 0 : comparison.Tracks.Max(track => track.MaximumProbabilityDelta));
}

static NoiseBoundarySummary? SummarizeNoiseBoundary(NoiseBoundaryProjectComparison? comparison)
{
    if (comparison is null)
    {
        return null;
    }

    var tracks = comparison.FrontEnds.SelectMany(frontEnd => frontEnd.Tracks).ToArray();
    return new(
        comparison.FrontEnds.Select(frontEnd => new NoiseBoundaryFrontEndSummary(
            frontEnd.Resampling,
            frontEnd.Tracks.Sum(track => track.ObservationCount),
            frontEnd.Tracks.Sum(track => track.ChangedFrameCount),
            frontEnd.Tracks.Sum(track => track.PhraseDifferenceCount),
            frontEnd.Tracks.Sum(track => track.SegmentDifferenceCount),
            frontEnd.Tracks.Sum(track => track.BaselineEnabledCandidateDisabledCount),
            frontEnd.Tracks.Sum(track => track.BaselineDisabledCandidateEnabledCount),
            frontEnd.Tracks.Sum(track => track.BaselineEnabledFinalDisabledCount),
            frontEnd.Tracks.Sum(track => track.TrainingEligibilityDifferenceCount),
            frontEnd.Tracks.Sum(track => track.FrameDifferenceSamples.Count),
            frontEnd.Tracks.Sum(track => track.FinalDifferences.Count),
            frontEnd.Tracks.Select(track => track.MaximumNoiseFloorDeltaDb).DefaultIfEmpty().Max())).ToArray(),
        tracks.Sum(track => track.ObservationCount),
        tracks.Sum(track => track.ChangedFrameCount),
        tracks.Sum(track => track.PhraseDifferenceCount),
        tracks.Sum(track => track.SegmentDifferenceCount),
        tracks.Sum(track => track.BaselineEnabledCandidateDisabledCount),
        tracks.Sum(track => track.BaselineDisabledCandidateEnabledCount),
        tracks.Sum(track => track.BaselineEnabledFinalDisabledCount),
        tracks.Sum(track => track.TrainingEligibilityDifferenceCount),
        tracks.Sum(track => track.FrameDifferenceSamples.Count),
        tracks.Sum(track => track.FinalDifferences.Count),
        tracks.Select(track => track.MaximumNoiseFloorDeltaDb).DefaultIfEmpty().Max(),
        tracks.Select(track => track.FrameTraceSha256).ToArray());
}

sealed class Audit
{
    public string SchemaVersion { get; init; } = "";
    public string SourceXmlSha256 { get; init; } = "";
    public string OutputXmlFileName { get; init; } = "";
    public string OutputXmlSha256 { get; init; } = "";
    public List<Fragment> Fragments { get; init; } = [];
    public VadFrontEndComparison? VadFrontEndComparison { get; init; }
    public NoiseBoundaryProjectComparison? NoiseBoundaryComparison { get; init; }
    public SequenceTiming? SequenceTiming { get; init; }
}

sealed class SequenceTiming
{
    public int FrameRate { get; init; }
    public bool Ntsc { get; init; }
    public int AudioSampleRate { get; init; }
    public int SamplesPerFrame { get; init; }
    public string FrameGridPolicy { get; init; } = "";
}

sealed class Fragment
{
    public int TrackIndex { get; init; }
    public long TimelineStartFrame { get; init; }
    public long TimelineEndFrame { get; init; }
    public string Status { get; init; } = "";
    public bool Enabled { get; init; }
}

sealed class VadFrontEndComparison
{
    public string LegacyResampling { get; init; } = "";
    public string CandidateResampling { get; init; } = "";
    public List<VadTrackComparison> Tracks { get; init; } = [];
}

sealed class VadTrackComparison
{
    public long ObservationCount { get; init; }
    public long ChangedObservationCount { get; init; }
    public long SegmentDifferenceCount { get; init; }
    public long LegacyEnabledCandidateDisabledCount { get; init; }
    public long LegacyDisabledCandidateEnabledCount { get; init; }
    public double MaximumProbabilityDelta { get; init; }
}

sealed class NoiseBoundaryProjectComparison
{
    public List<NoiseBoundaryFrontEndComparison> FrontEnds { get; init; } = [];
}

sealed class NoiseBoundaryFrontEndComparison
{
    public string Resampling { get; init; } = "";
    public List<NoiseBoundaryTrackComparison> Tracks { get; init; } = [];
}

sealed class NoiseBoundaryTrackComparison
{
    public long ObservationCount { get; init; }
    public long ChangedFrameCount { get; init; }
    public long PhraseDifferenceCount { get; init; }
    public long SegmentDifferenceCount { get; init; }
    public long BaselineEnabledCandidateDisabledCount { get; init; }
    public long BaselineDisabledCandidateEnabledCount { get; init; }
    public long BaselineEnabledFinalDisabledCount { get; init; }
    public long TrainingEligibilityDifferenceCount { get; init; }
    public double MaximumNoiseFloorDeltaDb { get; init; }
    public string FrameTraceSha256 { get; init; } = "";
    public List<JsonElement> FrameDifferenceSamples { get; init; } = [];
    public List<JsonElement> FinalDifferences { get; init; } = [];
}

sealed record AuditDescription(
    string Path,
    string SchemaVersion,
    string SourceXmlSha256,
    string EmbeddedOutputXmlSha256,
    string? ActualOutputXmlSha256,
    bool OutputXmlHashMatches,
    string AuditSha256,
    int FragmentCount,
    int TemporaryFileCount);

sealed class TransitionCount
{
    public long Intervals { get; set; }
    public long Frames { get; set; }
}

sealed record VadSummary(
    string LegacyResampling,
    string CandidateResampling,
    long ObservationCount,
    long ChangedObservationCount,
    long SegmentDifferenceCount,
    long LegacyEnabledCandidateDisabledCount,
    long LegacyDisabledCandidateEnabledCount,
    double MaximumProbabilityDelta);

sealed record NoiseBoundaryFrontEndSummary(
    string Resampling,
    long ObservationCount,
    long ChangedFrameCount,
    long PhraseDifferenceCount,
    long SegmentDifferenceCount,
    long BaselineEnabledCandidateDisabledCount,
    long BaselineDisabledCandidateEnabledCount,
    long BaselineEnabledFinalDisabledCount,
    long TrainingEligibilityDifferenceCount,
    long FrameDifferenceSampleCount,
    long FinalDifferenceCount,
    double MaximumNoiseFloorDeltaDb);

sealed record NoiseBoundarySummary(
    IReadOnlyList<NoiseBoundaryFrontEndSummary> FrontEnds,
    long ObservationCount,
    long ChangedFrameCount,
    long PhraseDifferenceCount,
    long SegmentDifferenceCount,
    long BaselineEnabledCandidateDisabledCount,
    long BaselineDisabledCandidateEnabledCount,
    long BaselineEnabledFinalDisabledCount,
    long TrainingEligibilityDifferenceCount,
    long FrameDifferenceSampleCount,
    long FinalDifferenceCount,
    double MaximumNoiseFloorDeltaDb,
    IReadOnlyList<string> FrameTraceSha256);

sealed record AuditComparison(
    bool Passed,
    IReadOnlyList<string> Failures,
    AuditDescription Baseline,
    AuditDescription Candidate,
    bool SourceXmlHashesMatch,
    long CoverageMismatchIntervals,
    long CoverageMismatchFrames,
    long LostEnabledIntervals,
    long LostEnabledFrames,
    long NewlyEnabledIntervals,
    long NewlyEnabledFrames,
    IReadOnlyDictionary<string, TransitionCount> Transitions,
    VadSummary? CandidateVadFrontEnd,
    NoiseBoundarySummary? CandidateNoiseBoundary);
