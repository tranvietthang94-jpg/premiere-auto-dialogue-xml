using System.Globalization;
using PremiereAutoDialogueXml.Core.Timing;
using PremiereAutoDialogueXml.Core.Xml;
using PremiereAutoDialogueXml.Output.Audit;

namespace PremiereAutoDialogueXml.Validation;

public sealed record PremiereConstantGainBatchCandidateResult(
    string SchemaVersion,
    string Policy,
    string InputXmlSha256,
    string InputAuditSha256,
    string AuditRunId,
    string OutputXmlSha256,
    int FrameRate,
    long FrameTicks,
    long HalfFrameTicks,
    int TransitionCount,
    IReadOnlyList<PremiereConstantGainBoundaryResult> Boundaries);

public sealed class PremiereConstantGainBatchCandidateWriter
{
    public const string Policy = "phase15-constant-gain-one-frame-batch-no-pre-normalization-v1";
    private const int SupportedAudioSampleRate = 48_000;

    public async Task<PremiereConstantGainBatchCandidateResult> WriteAsync(
        string inputXmlPath,
        string expectedInputXmlSha256,
        OutputAudit audit,
        string inputAuditSha256,
        string outputXmlPath,
        IReadOnlyList<PremiereConstantGainBoundaryRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputXmlPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedInputXmlSha256);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputAuditSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputXmlPath);
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count is < 1 or > PremiereConstantGainBatchPlanner.MaximumTransitionsPerCandidate)
        {
            throw new ArgumentOutOfRangeException(nameof(requests));
        }

        ValidateRequests(requests);
        var inputPath = Path.GetFullPath(inputXmlPath);
        var outputPath = Path.GetFullPath(outputXmlPath);
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Không tìm thấy XML đầu vào.", inputPath);
        }

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            throw new DirectoryNotFoundException("Thư mục chứa batch candidate XML phải tồn tại.");
        }

        if (File.Exists(outputPath) ||
            string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Batch candidate XML phải là file mới và không được trùng đầu vào.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var source = PremiereXmlDocumentLoader.LoadGeneratedOutput(inputPath, expectedInputXmlSha256);
        var document = source.Document;
        PremiereConstantGainCandidateWriter.RemoveInsignificantWhitespace(document);
        var root = document.Root ?? throw new InvalidDataException("Batch candidate XML thiếu root xmeml.");
        if (root.Name.LocalName != "xmeml")
        {
            throw new InvalidDataException("Batch candidate XML không có root xmeml.");
        }

        var sequence = root.Elements("sequence").SingleOrDefault()
            ?? throw new InvalidDataException("Batch candidate writer yêu cầu đúng một sequence trực tiếp.");
        var frameRate = PremiereConstantGainCandidateWriter.ReadSequenceFrameRate(sequence);
        var frameGrid = PremiereNdfFrameGrid.Create(frameRate, SupportedAudioSampleRate);
        PremiereConstantGainCandidateWriter.ValidateAuditEnvelope(audit, source.Sha256, frameGrid);
        var frameTicks = frameGrid.FrameToTicks(1);
        if (frameTicks <= 0 || frameTicks % 2 != 0)
        {
            throw new InvalidDataException("Frame tick không hỗ trợ transition giữa frame.");
        }

        var audio = PremiereConstantGainCandidateWriter.RequiredElement(
            PremiereConstantGainCandidateWriter.RequiredElement(sequence, "media"),
            "audio");
        var sampleCharacteristics = PremiereConstantGainCandidateWriter.RequiredElement(
            PremiereConstantGainCandidateWriter.RequiredElement(audio, "format"),
            "samplecharacteristics");
        if (!long.TryParse(
                PremiereConstantGainCandidateWriter.RequiredElement(sampleCharacteristics, "samplerate").Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var sampleRate) ||
            sampleRate != SupportedAudioSampleRate)
        {
            throw new InvalidDataException("Batch candidate chỉ hỗ trợ sequence audio 48 kHz đã khóa.");
        }

        var tracks = audio.Elements("track").ToArray();
        if (tracks.SelectMany(track => track.Elements("transitionitem")).Any())
        {
            throw new InvalidDataException("Generated XML đã có audio transition; từ chối batch chèn chồng.");
        }

        var boundaries = new List<PremiereConstantGainBoundaryResult>(requests.Count);
        foreach (var request in requests.OrderBy(item => item.TrackIndex).ThenBy(item => item.BoundaryFrame))
        {
            if (request.TrackIndex > tracks.Length)
            {
                throw new InvalidDataException($"Không tồn tại audio track A{request.TrackIndex}.");
            }

            boundaries.Add(PremiereConstantGainCandidateWriter.InsertValidatedBoundary(
                tracks[request.TrackIndex - 1],
                audit,
                frameGrid,
                request.TrackIndex,
                request.BoundaryFrame,
                request.ExpectedKind));
        }

        var tempPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await PremiereConstantGainCandidateWriter.WriteDocumentAsync(
                document,
                tempPath,
                cancellationToken);
            var outputSha256 = await PremiereConstantGainCandidateWriter.ComputeSha256Async(
                tempPath,
                cancellationToken);
            PremiereXmlDocumentLoader.ValidateGeneratedOutputSyntax(tempPath, outputSha256);
            File.Move(tempPath, outputPath);
            return new(
                "1.0",
                Policy,
                source.Sha256,
                inputAuditSha256,
                audit.RunId,
                outputSha256,
                frameRate,
                frameTicks,
                frameTicks / 2,
                boundaries.Count,
                boundaries);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static void ValidateRequests(IReadOnlyList<PremiereConstantGainBoundaryRequest> requests)
    {
        foreach (var request in requests)
        {
            if (request.TrackIndex <= 0 ||
                request.BoundaryFrame <= 0 ||
                !double.IsFinite(request.RenderedStepDbfs) ||
                !double.IsFinite(request.StepAboveLocalP99Db))
            {
                throw new InvalidDataException("Batch request chứa boundary không hợp lệ.");
            }
        }

        var ordered = requests
            .OrderBy(request => request.TrackIndex)
            .ThenBy(request => request.BoundaryFrame)
            .ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            var left = ordered[index - 1];
            var right = ordered[index];
            if (left.TrackIndex == right.TrackIndex &&
                right.BoundaryFrame - left.BoundaryFrame <
                PremiereConstantGainBatchPlanner.MinimumBoundarySeparationFrames)
            {
                throw new InvalidDataException(
                    $"Batch request xung đột tại A{right.TrackIndex}/frame {left.BoundaryFrame}-{right.BoundaryFrame}.");
            }
        }
    }
}
