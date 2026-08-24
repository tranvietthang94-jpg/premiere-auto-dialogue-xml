using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Audio.Pcm;
using PremiereAutoDialogueXml.Audio.Vad;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.Timing;
using PremiereAutoDialogueXml.Core.Xml;
using PremiereAutoDialogueXml.Output;
using PremiereAutoDialogueXml.Output.Audit;
using PremiereAutoDialogueXml.Validation;

if (args.Length > 0 && args[0].Equals("--constant-gain-batch-candidate", StringComparison.OrdinalIgnoreCase))
{
    return await WriteConstantGainBatchCandidateAsync(args);
}

if (args.Length > 0 && args[0].Equals("--constant-gain-candidate", StringComparison.OrdinalIgnoreCase))
{
    return await WriteConstantGainCandidateAsync(args);
}

if (args.Length > 0 && args[0].Equals("--boundary-report", StringComparison.OrdinalIgnoreCase))
{
    return await WriteBoundaryReportAsync(args);
}

if (args.Length > 0 && args[0].Equals("--vad-window", StringComparison.OrdinalIgnoreCase))
{
    return await WriteVadWindowReportAsync(args);
}

var writeOutput = args.Length > 0 && args[0].Equals("--write", StringComparison.OrdinalIgnoreCase);
var analyzeAudio = writeOutput || (args.Length > 0 && args[0].Equals("--analyze", StringComparison.OrdinalIgnoreCase));
var xmlPaths = writeOutput
    ? args.Skip(1).Take(1).ToArray()
    : analyzeAudio ? args.Skip(1).ToArray() : args;
if (xmlPaths.Length == 0)
{
    Console.Error.WriteLine("Usage: PremiereAutoDialogueXml.Inspect [--analyze] <premiere.xml> [more.xml]");
    Console.Error.WriteLine("       PremiereAutoDialogueXml.Inspect --write <premiere.xml> <existing-output-parent>");
    Console.Error.WriteLine("       PremiereAutoDialogueXml.Inspect --vad-window <premiere.xml> <track> <start-frame> <end-frame> <new-report.json>");
    Console.Error.WriteLine("       PremiereAutoDialogueXml.Inspect --boundary-report <audit.json> <source.xml> <new-report.json>");
    Console.Error.WriteLine("       PremiereAutoDialogueXml.Inspect --constant-gain-candidate <audit.json> <generated.xml> <track> <boundary-frame> <new-output.xml>");
    Console.Error.WriteLine("       PremiereAutoDialogueXml.Inspect --constant-gain-batch-candidate <audit.json> <source.xml> <generated.xml> <max-transitions> <new-output.xml> <new-report.json>");
    return 2;
}

if (writeOutput && args.Length != 3)
{
    Console.Error.WriteLine("--write cần đúng một XML và một thư mục output cha đã tồn tại.");
    return 2;
}

var inspector = new PremiereXmlInspector();
var failed = false;
foreach (var xmlPath in xmlPaths)
{
    var startedAt = DateTimeOffset.UtcNow;
    var result = inspector.Inspect(xmlPath);
    var project = result.Project;
    var clips = project?.Sequence.AudioTracks.SelectMany(track => track.Clips).ToList() ?? [];
    object? audioSummary = null;
    object? outputSummary = null;
    if (analyzeAudio && result.CanProceed && project is not null)
    {
        var progress = new InlineProgress<AudioAnalysisProgress>(update =>
            Console.Error.WriteLine($"[{update.CompletedTracks}/{update.TotalTracks}] {update.Message}"));
        var analysis = await new AudioProjectAnalyzer().AnalyzeAsync(
            project,
            DialogueProcessingPreset.Balanced,
            progress);
        audioSummary = new
        {
            analysis.ModelVersion,
            analysis.ModelSha256,
            PeakWorkingSetMegabytes = Math.Round(
                System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1024d / 1024d,
                1),
            ManagedMemoryMegabytes = Math.Round(GC.GetTotalMemory(forceFullCollection: false) / 1024d / 1024d, 1),
            Phrases = analysis.Tracks.Sum(track => track.Phrases.Count),
            GainCappedPhrases = analysis.Tracks.Sum(track => track.Phrases.Count(phrase => phrase.GainWasCapped)),
            Segments = Enum.GetValues<AudioSegmentStatus>().ToDictionary(
                status => status.ToString(),
                status => analysis.Tracks.Sum(track => track.Segments.Count(segment => segment.Status == status))),
            DurationSeconds = Enum.GetValues<AudioSegmentStatus>().ToDictionary(
                status => status.ToString(),
                status => analysis.Tracks.Sum(track => track.Segments
                    .Where(segment => segment.Status == status)
                    .Sum(segment => segment.TimelineEndSample - segment.TimelineStartSample)) / 48_000d),
            VadFrontEnd = analysis.VadFrontEndComparison is null
                ? null
                : new
                {
                    analysis.VadFrontEndComparison.LegacyResampling,
                    analysis.VadFrontEndComparison.CandidateResampling,
                    analysis.VadFrontEndComparison.ObservationCount,
                    analysis.VadFrontEndComparison.ChangedObservationCount,
                    analysis.VadFrontEndComparison.SegmentDifferenceCount,
                    analysis.VadFrontEndComparison.LegacyEnabledCandidateDisabledCount,
                    analysis.VadFrontEndComparison.LegacyDisabledCandidateEnabledCount,
                    MaximumProbabilityDelta = analysis.VadFrontEndComparison.Tracks
                        .Select(track => track.MaximumProbabilityDelta)
                        .DefaultIfEmpty()
                        .Max()
                },
            NoiseBoundary = analysis.NoiseBoundaryComparison is null
                ? null
                : new
                {
                    FrontEnds = analysis.NoiseBoundaryComparison.FrontEnds.Select(frontEnd => new
                    {
                        frontEnd.Resampling,
                        frontEnd.ObservationCount,
                        frontEnd.ChangedFrameCount,
                        frontEnd.PhraseDifferenceCount,
                        frontEnd.SegmentDifferenceCount,
                        frontEnd.BaselineEnabledCandidateDisabledCount,
                        frontEnd.BaselineDisabledCandidateEnabledCount,
                        frontEnd.BaselineEnabledFinalDisabledCount,
                        TrainingEligibilityDifferenceCount = frontEnd.Tracks.Sum(track =>
                            track.TrainingEligibilityDifferenceCount),
                        FinalDifferenceCount = frontEnd.Tracks.Sum(track => track.FinalDifferenceCount),
                        CapturedFinalDifferenceCount = frontEnd.Tracks.Sum(track =>
                            track.CapturedFinalDifferenceCount),
                        MaximumNoiseFloorDeltaDb = frontEnd.Tracks
                            .Select(track => track.MaximumNoiseFloorDeltaDb)
                            .DefaultIfEmpty()
                            .Max()
                    }).ToArray(),
                    analysis.NoiseBoundaryComparison.ObservationCount,
                    analysis.NoiseBoundaryComparison.ChangedFrameCount,
                    analysis.NoiseBoundaryComparison.PhraseDifferenceCount,
                    analysis.NoiseBoundaryComparison.SegmentDifferenceCount,
                    analysis.NoiseBoundaryComparison.BaselineEnabledCandidateDisabledCount,
                    analysis.NoiseBoundaryComparison.BaselineDisabledCandidateEnabledCount,
                    analysis.NoiseBoundaryComparison.BaselineEnabledFinalDisabledCount,
                    FrameDifferenceRecordCount = analysis.NoiseBoundaryComparison.FrontEnds.Sum(frontEnd =>
                        frontEnd.Tracks.Sum(track => track.FrameDifferenceSamples.Count)),
                    PhraseDifferenceRecordCount = analysis.NoiseBoundaryComparison.FrontEnds.Sum(frontEnd =>
                        frontEnd.Tracks.Sum(track => track.PhraseDifferenceCount)),
                    CapturedPhraseDifferenceRecordCount = analysis.NoiseBoundaryComparison.FrontEnds.Sum(frontEnd =>
                        frontEnd.Tracks.Sum(track => track.CapturedPhraseDifferenceCount)),
                    FinalDifferenceRecordCount = analysis.NoiseBoundaryComparison.FrontEnds.Sum(frontEnd =>
                        frontEnd.Tracks.Sum(track => track.FinalDifferenceCount)),
                    CapturedFinalDifferenceRecordCount = analysis.NoiseBoundaryComparison.FrontEnds.Sum(frontEnd =>
                        frontEnd.Tracks.Sum(track => track.CapturedFinalDifferenceCount))
                }
        };

        if (writeOutput)
        {
            var output = await new OutputPackageWriter().WriteAsync(new(
                project,
                analysis,
                DialogueProcessingPreset.Balanced,
                args[2]));
            outputSummary = new
            {
                RunDirectory = Path.GetFileName(output.RunDirectory),
                Xml = Path.GetFileName(output.XmlPath),
                Audit = Path.GetFileName(output.AuditPath),
                Review = Path.GetFileName(output.ReviewCsvPath),
                output.OutputXmlSha256,
                output.FragmentCount,
                output.MarkerCount,
                output.ReviewGroupCount,
                TotalPeakWorkingSetMegabytes = Math.Round(
                    System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1024d / 1024d,
                    1)
            };
        }
    }

    var summary = new
    {
        Xml = Path.GetFileName(xmlPath),
        result.CanProceed,
        Sequence = project?.Sequence.Name,
        FrameRate = project?.Sequence.FrameRate,
        DurationFrames = project?.Sequence.DurationFrames,
        AudioTracks = project?.Sequence.AudioTracks.Count,
        Clips = clips.Count,
        Media = clips.Select(clip => clip.SourceFileId).Distinct(StringComparer.Ordinal).Count(),
        result.WarningCount,
        result.ErrorCount,
        AudioAnalysis = audioSummary,
        Output = outputSummary,
        Issues = result.Issues.Select(issue => new
        {
            Severity = issue.Severity.ToString(),
            issue.Code,
            issue.Message
        }),
        ElapsedMilliseconds = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds
    };

    Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    failed |= !result.CanProceed;
}

return failed ? 1 : 0;

static async Task<int> WriteConstantGainBatchCandidateAsync(string[] arguments)
{
    if (arguments.Length != 7 ||
        !int.TryParse(arguments[4], out var maximumTransitions) ||
        maximumTransitions is < 1 or > PremiereConstantGainBatchPlanner.MaximumTransitionsPerCandidate)
    {
        Console.Error.WriteLine(
            "Cách dùng: --constant-gain-batch-candidate <audit.json> <source.xml> <generated.xml> <max-transitions> <new-output.xml> <new-report.json>");
        return 2;
    }

    var auditPath = Path.GetFullPath(arguments[1]);
    var sourceXmlPath = Path.GetFullPath(arguments[2]);
    var generatedXmlPath = Path.GetFullPath(arguments[3]);
    var outputXmlPath = Path.GetFullPath(arguments[5]);
    var reportPath = Path.GetFullPath(arguments[6]);
    if (!File.Exists(auditPath) || !File.Exists(sourceXmlPath) || !File.Exists(generatedXmlPath))
    {
        Console.Error.WriteLine("Không tìm thấy audit, source XML hoặc generated XML đầu vào.");
        return 2;
    }

    var outputDirectory = Path.GetDirectoryName(outputXmlPath);
    var reportDirectory = Path.GetDirectoryName(reportPath);
    if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory) ||
        string.IsNullOrWhiteSpace(reportDirectory) || !Directory.Exists(reportDirectory))
    {
        Console.Error.WriteLine("Thư mục chứa batch candidate và report phải tồn tại.");
        return 2;
    }

    var inputPaths = new[] { auditPath, sourceXmlPath, generatedXmlPath };
    if (File.Exists(outputXmlPath) || File.Exists(reportPath) ||
        inputPaths.Any(path => string.Equals(path, outputXmlPath, StringComparison.OrdinalIgnoreCase)) ||
        inputPaths.Any(path => string.Equals(path, reportPath, StringComparison.OrdinalIgnoreCase)) ||
        string.Equals(outputXmlPath, reportPath, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Batch candidate/report phải là hai file mới, riêng biệt và không trùng input.");
        return 2;
    }

    var jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    var candidateCreated = false;
    try
    {
        OutputAudit audit;
        await using (var auditStream = new FileStream(
                         auditPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         bufferSize: 1024 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            audit = await JsonSerializer.DeserializeAsync<OutputAudit>(auditStream, jsonOptions)
                ?? throw new InvalidDataException("Audit JSON rỗng hoặc không hợp lệ.");
        }

        var inspection = new PremiereXmlInspector().Inspect(sourceXmlPath);
        if (!inspection.CanProceed || inspection.Project is null)
        {
            throw new InvalidDataException("XML nguồn không qua inspector; từ chối lập batch candidate.");
        }

        var auditSha256 = await ComputeSha256Async(auditPath);
        var generatedXmlSha256 = await ComputeSha256Async(generatedXmlPath);
        var scan = new BoundaryDiscontinuityScanner().Scan(
            audit,
            inspection.Project,
            maximumCapturedSamples: 1_024);
        var plan = new PremiereConstantGainBatchPlanner().Plan(scan, maximumTransitions);
        if (plan.SelectedCount == 0)
        {
            throw new InvalidDataException("Boundary scan không chọn được transition an toàn nào.");
        }

        var candidate = await new PremiereConstantGainBatchCandidateWriter().WriteAsync(
            generatedXmlPath,
            generatedXmlSha256,
            audit,
            auditSha256,
            outputXmlPath,
            plan.Selected);
        candidateCreated = true;
        var report = new
        {
            SchemaVersion = "1.0",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            AuditFileName = Path.GetFileName(auditPath),
            AuditSha256 = auditSha256,
            SourceXmlFileName = Path.GetFileName(sourceXmlPath),
            inspection.Project.SourceXmlSha256,
            GeneratedXmlFileName = Path.GetFileName(generatedXmlPath),
            GeneratedXmlSha256 = generatedXmlSha256,
            OutputXmlFileName = Path.GetFileName(outputXmlPath),
            Scan = new
            {
                scan.SchemaVersion,
                scan.Policy,
                scan.ScreeningThresholdDbfs,
                scan.SourceClipCount,
                scan.AppCreatedBoundaryCount,
                scan.TransitionCount,
                scan.EnabledToDisabledCount,
                scan.DisabledToEnabledCount,
                scan.GainChangeCount,
                scan.TransientScreeningCandidateCount,
                scan.MaximumRenderedStepDbfs,
                scan.MaximumRenderedStepAboveLocalP99Db,
                scan.TransitionStreamSha256,
                scan.CapturedSampleCount
            },
            Plan = plan,
            Candidate = candidate
        };
        var tempPath = Path.Combine(
            reportDirectory,
            $".{Path.GetFileName(reportPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, report, jsonOptions);
                await stream.FlushAsync();
            }

            File.Move(tempPath, reportPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Candidate = Path.GetFileName(outputXmlPath),
            Report = Path.GetFileName(reportPath),
            candidate.OutputXmlSha256,
            plan.Policy,
            plan.RequestedMaximumTransitions,
            plan.ScannerTransientCandidateCount,
            plan.CapturedTransientCandidateCount,
            plan.UncapturedTransientCandidateCount,
            plan.SelectedCount,
            plan.EnabledToDisabledSelectedCount,
            plan.DisabledToEnabledSelectedCount,
            plan.GainChangeSelectedCount,
            plan.SelectionStreamSha256
        }, jsonOptions));
        return 0;
    }
    catch (Exception exception) when (
        exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or
        PremiereXmlLoadException or ArgumentException or OverflowException)
    {
        if (candidateCreated && File.Exists(outputXmlPath) && !File.Exists(reportPath))
        {
            File.Delete(outputXmlPath);
        }

        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}

static async Task<int> WriteConstantGainCandidateAsync(string[] arguments)
{
    if (arguments.Length != 6 ||
        !int.TryParse(arguments[3], out var trackIndex) || trackIndex <= 0 ||
        !long.TryParse(arguments[4], out var boundaryFrame) || boundaryFrame <= 0)
    {
        Console.Error.WriteLine(
            "Cách dùng: --constant-gain-candidate <audit.json> <generated.xml> <track> <boundary-frame> <new-output.xml>");
        return 2;
    }

    var auditPath = Path.GetFullPath(arguments[1]);
    var inputPath = Path.GetFullPath(arguments[2]);
    var outputPath = Path.GetFullPath(arguments[5]);
    try
    {
        if (!File.Exists(auditPath) || !File.Exists(inputPath))
        {
            Console.Error.WriteLine("Không tìm thấy audit hoặc generated XML đầu vào.");
            return 2;
        }

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        await using var auditStream = new FileStream(
            auditPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var audit = await JsonSerializer.DeserializeAsync<OutputAudit>(auditStream, jsonOptions)
            ?? throw new InvalidDataException("Audit JSON rỗng hoặc không hợp lệ.");
        var auditSha256 = await ComputeSha256Async(auditPath);
        var inputSha256 = await ComputeSha256Async(inputPath);
        var result = await new PremiereConstantGainCandidateWriter().WriteAsync(
            inputPath,
            inputSha256,
            audit,
            auditSha256,
            outputPath,
            trackIndex,
            boundaryFrame);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            PremiereConstantGainCandidateWriter.Policy,
            Audit = Path.GetFileName(auditPath),
            Input = Path.GetFileName(inputPath),
            Output = Path.GetFileName(outputPath),
            result.InputXmlSha256,
            result.InputAuditSha256,
            result.AuditRunId,
            result.OutputXmlSha256,
            result.TrackIndex,
            result.BoundaryFrame,
            Timecode = ToTimecode(result.BoundaryFrame, result.FrameRate),
            result.FrameRate,
            result.FrameTicks,
            result.HalfFrameTicks,
            result.LeftClipItemId,
            result.RightClipItemId,
            result.LeftEnabled,
            result.RightEnabled,
            result.EffectName,
            result.EffectId
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    catch (Exception exception) when (
        exception is IOException or UnauthorizedAccessException or InvalidDataException or
        PremiereXmlLoadException or JsonException or ArgumentException or OverflowException)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}

static async Task<int> WriteBoundaryReportAsync(string[] arguments)
{
    if (arguments.Length != 4)
    {
        Console.Error.WriteLine(
            "Cách dùng: --boundary-report <audit.json> <source.xml> <new-report.json>");
        return 2;
    }

    var auditPath = Path.GetFullPath(arguments[1]);
    var sourceXmlPath = Path.GetFullPath(arguments[2]);
    var reportPath = Path.GetFullPath(arguments[3]);
    if (!File.Exists(auditPath) || !File.Exists(sourceXmlPath))
    {
        Console.Error.WriteLine("Không tìm thấy audit hoặc XML nguồn.");
        return 2;
    }

    var reportDirectory = Path.GetDirectoryName(reportPath);
    if (string.IsNullOrWhiteSpace(reportDirectory) || !Directory.Exists(reportDirectory))
    {
        Console.Error.WriteLine("Thư mục chứa report phải tồn tại.");
        return 2;
    }

    if (File.Exists(reportPath) ||
        string.Equals(reportPath, auditPath, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(reportPath, sourceXmlPath, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Boundary report phải là file mới và không được trùng input.");
        return 2;
    }

    var jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    try
    {
        await using var auditStream = new FileStream(
            auditPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var audit = await JsonSerializer.DeserializeAsync<OutputAudit>(auditStream, jsonOptions)
            ?? throw new InvalidDataException("Audit JSON rỗng hoặc không hợp lệ.");
        var inspection = new PremiereXmlInspector().Inspect(sourceXmlPath);
        if (!inspection.CanProceed || inspection.Project is null)
        {
            throw new InvalidDataException("XML nguồn không qua inspector; từ chối quét boundary.");
        }

        var scan = new BoundaryDiscontinuityScanner().Scan(audit, inspection.Project);
        var report = new
        {
            SchemaVersion = "1.1",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            AuditFileName = Path.GetFileName(auditPath),
            AuditSha256 = await ComputeSha256Async(auditPath),
            SourceXmlFileName = Path.GetFileName(sourceXmlPath),
            inspection.Project.SourceXmlSha256,
            scan
        };
        var tempPath = Path.Combine(
            reportDirectory,
            $".{Path.GetFileName(reportPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, report, jsonOptions);
                await stream.FlushAsync();
            }

            File.Move(tempPath, reportPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Report = Path.GetFileName(reportPath),
            scan.Policy,
            scan.SourceClipCount,
            scan.AppCreatedBoundaryCount,
            scan.TransitionCount,
            scan.EnabledToDisabledCount,
            scan.DisabledToEnabledCount,
            scan.GainChangeCount,
            scan.ScreeningCandidateCount,
            scan.TransientScreeningCandidateCount,
            scan.MaximumExcessStepDbfs,
            scan.P95ExcessStepDbfs,
            scan.MaximumRenderedStepAboveLocalP99Db,
            scan.TransitionStreamSha256,
            scan.CapturedSampleCount
        }, jsonOptions));
        return 0;
    }
    catch (Exception exception) when (
        exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or
        ArgumentException or OverflowException)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}

static async Task<int> WriteVadWindowReportAsync(string[] arguments)
{
    if (arguments.Length != 6 ||
        !int.TryParse(arguments[2], out var trackIndex) || trackIndex <= 0 ||
        !long.TryParse(arguments[3], out var startFrame) || startFrame < 0 ||
        !long.TryParse(arguments[4], out var endFrame) || endFrame <= startFrame)
    {
        Console.Error.WriteLine(
            "Cách dùng: --vad-window <premiere.xml> <track> <start-frame> <end-frame> <new-report.json>");
        return 2;
    }

    var xmlPath = Path.GetFullPath(arguments[1]);
    var reportPath = Path.GetFullPath(arguments[5]);
    if (!File.Exists(xmlPath))
    {
        Console.Error.WriteLine("Không tìm thấy XML nguồn.");
        return 2;
    }

    var reportDirectory = Path.GetDirectoryName(reportPath);
    if (string.IsNullOrWhiteSpace(reportDirectory) || !Directory.Exists(reportDirectory))
    {
        Console.Error.WriteLine("Thư mục chứa report phải tồn tại.");
        return 2;
    }

    if (File.Exists(reportPath) || string.Equals(xmlPath, reportPath, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Report phải là file mới và không được trùng XML nguồn.");
        return 2;
    }

    try
    {
        var inspection = new PremiereXmlInspector().Inspect(xmlPath);
        if (!inspection.CanProceed || inspection.Project is null)
        {
            throw new InvalidDataException("XML nguồn không qua được inspector; từ chối chẩn đoán VAD.");
        }

        var project = inspection.Project;
        var frameGrid = PremiereNdfFrameGrid.Create(
            project.Sequence.FrameRate,
            project.Sequence.AudioSampleRate);
        var track = project.Sequence.AudioTracks.SingleOrDefault(candidate => candidate.Index == trackIndex)
            ?? throw new InvalidDataException($"Không tìm thấy track {trackIndex} trong XML.");
        if (endFrame > project.Sequence.DurationFrames)
        {
            throw new InvalidDataException("Khoảng chẩn đoán vượt quá duration của sequence.");
        }

        var preset = DialogueProcessingPreset.Balanced;
        using var detector = new SileroVoiceActivityDetector();
        var observations = new TrackAudioScanner(new PcmWaveSampleReader(), frameGrid)
            .Scan(track, detector, preset);
        var evidence = new AudioFrameEvidenceBuilder().Build(observations, preset);
        var startSample = frameGrid.FrameToSample(startFrame);
        var endSample = frameGrid.FrameToSample(endFrame);
        var window = evidence
            .Where(frame => frame.Observation.TimelineStartSample < endSample &&
                            frame.Observation.TimelineEndSample > startSample)
            .ToArray();

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        var report = new
        {
            SchemaVersion = "1.0",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            SourceXmlFileName = Path.GetFileName(xmlPath),
            project.SourceXmlSha256,
            project.Sequence.Name,
            TrackIndex = trackIndex,
            Window = new
            {
                StartFrame = startFrame,
                EndFrame = endFrame,
                StartTimecode = ToTimecode(startFrame, frameGrid.FramesPerSecond),
                EndTimecode = ToTimecode(endFrame, frameGrid.FramesPerSecond),
                StartSample = startSample,
                EndSample = endSample
            },
            Model = new
            {
                Version = SileroVadModelInfo.Version,
                Sha256 = SileroVadModelInfo.Sha256
            },
            Thresholds = new
            {
                preset.VadThreshold,
                VadAmbiguityLowerBound = preset.VadThreshold - 0.15,
                preset.DirectVoiceAboveNoiseDb,
                preset.MinimumSpeechMilliseconds
            },
            Summary = new
            {
                ObservationCount = window.Length,
                MaximumVadProbability = window.Select(frame => frame.Observation.VadProbability).DefaultIfEmpty().Max(),
                MaximumRmsDbfs = window.Select(frame => frame.Observation.RmsDbfs).DefaultIfEmpty(-144f).Max(),
                MaximumSamplePeakDbfs = window.Select(frame => frame.Observation.SamplePeakDbfs).DefaultIfEmpty(-144f).Max(),
                VadSpeechObservationCount = window.Count(frame => frame.IsVadSpeech),
                DirectEvidenceObservationCount = window.Count(frame => frame.IsDirectEvidence),
                BorderlineHighEnergyObservationCount = window.Count(frame =>
                    !frame.IsVadSpeech &&
                    frame.Observation.VadProbability >= preset.VadThreshold - 0.15 &&
                    frame.IsAboveDirectEnergyThreshold),
                VadNegativeHighEnergyObservationCount = window.Count(frame =>
                    !frame.IsVadSpeech && frame.IsAboveDirectEnergyThreshold)
            },
            Observations = window.Select(frame => new
            {
                frame.Observation.TimelineStartSample,
                frame.Observation.TimelineEndSample,
                TimelineStartFrame =
                    frame.Observation.TimelineStartSample / (double)frameGrid.SamplesPerFrame,
                TimelineEndFrame =
                    frame.Observation.TimelineEndSample / (double)frameGrid.SamplesPerFrame,
                frame.Observation.VadProbability,
                frame.Observation.RmsDbfs,
                frame.Observation.SamplePeakDbfs,
                frame.AdaptiveNoiseFloorDbfs,
                frame.IsVadSpeech,
                frame.IsDirectEvidence,
                frame.IsAboveDirectEnergyThreshold
            })
        };

        var tempPath = Path.Combine(
            reportDirectory,
            $".{Path.GetFileName(reportPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, report, jsonOptions);
                await stream.FlushAsync();
            }

            File.Move(tempPath, reportPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Report = Path.GetFileName(reportPath),
            report.Summary.ObservationCount,
            report.Summary.MaximumVadProbability,
            report.Summary.VadSpeechObservationCount,
            report.Summary.DirectEvidenceObservationCount,
            report.Summary.BorderlineHighEnergyObservationCount,
            report.Summary.VadNegativeHighEnergyObservationCount
        }));
        return 0;
    }
    catch (Exception exception) when (
        exception is IOException or UnauthorizedAccessException or InvalidDataException or
        ArgumentException or OverflowException)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}

static string ToTimecode(long frame, int frameRate)
{
    var framesPerHour = checked(frameRate * 60 * 60);
    var framesPerMinute = checked(frameRate * 60);
    var hours = frame / framesPerHour;
    var remainder = frame % framesPerHour;
    var minutes = remainder / framesPerMinute;
    remainder %= framesPerMinute;
    var seconds = remainder / frameRate;
    var frames = remainder % frameRate;
    return $"{hours:00}:{minutes:00}:{seconds:00}:{frames:00}";
}

static async Task<string> ComputeSha256Async(string path)
{
    await using var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 1024 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    return Convert.ToHexString(await SHA256.HashDataAsync(stream));
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
