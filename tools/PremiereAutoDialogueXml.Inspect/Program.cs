using System.Text.Json;
using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Audio.Pcm;
using PremiereAutoDialogueXml.Audio.Vad;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.Xml;
using PremiereAutoDialogueXml.Output;

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
                    .Sum(segment => segment.TimelineEndSample - segment.TimelineStartSample)) / 48_000d)
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

    const int sourceSampleRate = 48_000;
    const int sequenceFrameRate = 25;
    const int samplesPerFrame = sourceSampleRate / sequenceFrameRate;
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
        var track = project.Sequence.AudioTracks.SingleOrDefault(candidate => candidate.Index == trackIndex)
            ?? throw new InvalidDataException($"Không tìm thấy track {trackIndex} trong XML.");
        if (endFrame > project.Sequence.DurationFrames)
        {
            throw new InvalidDataException("Khoảng chẩn đoán vượt quá duration của sequence.");
        }

        var preset = DialogueProcessingPreset.Balanced;
        using var detector = new SileroVoiceActivityDetector();
        var observations = new TrackAudioScanner(new PcmWaveSampleReader())
            .Scan(track, detector, preset);
        var evidence = new AudioFrameEvidenceBuilder().Build(observations, preset);
        var startSample = checked(startFrame * samplesPerFrame);
        var endSample = checked(endFrame * samplesPerFrame);
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
                StartTimecode = ToTimecode(startFrame, sequenceFrameRate),
                EndTimecode = ToTimecode(endFrame, sequenceFrameRate),
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
                TimelineStartFrame = frame.Observation.TimelineStartSample / (double)samplesPerFrame,
                TimelineEndFrame = frame.Observation.TimelineEndSample / (double)samplesPerFrame,
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

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
