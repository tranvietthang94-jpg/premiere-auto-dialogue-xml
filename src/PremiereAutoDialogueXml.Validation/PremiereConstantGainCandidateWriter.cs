using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Core.Timing;
using PremiereAutoDialogueXml.Core.Xml;
using PremiereAutoDialogueXml.Output.Audit;

namespace PremiereAutoDialogueXml.Validation;

public sealed record PremiereConstantGainCandidateResult(
    string InputXmlSha256,
    string InputAuditSha256,
    string AuditRunId,
    string OutputXmlSha256,
    int TrackIndex,
    long BoundaryFrame,
    int FrameRate,
    long FrameTicks,
    long HalfFrameTicks,
    string LeftClipItemId,
    string RightClipItemId,
    bool LeftEnabled,
    bool RightEnabled,
    string EffectName,
    string EffectId);

public sealed record PremiereConstantGainBoundaryRequest(
    int TrackIndex,
    long BoundaryFrame,
    BoundaryTransitionKind ExpectedKind,
    double RenderedStepDbfs,
    double StepAboveLocalP99Db);

public sealed record PremiereConstantGainBoundaryResult(
    int TrackIndex,
    long BoundaryFrame,
    BoundaryTransitionKind Kind,
    string LeftClipItemId,
    string RightClipItemId,
    bool LeftEnabled,
    bool RightEnabled,
    string EffectName,
    string EffectId);

public sealed class PremiereConstantGainCandidateWriter
{
    public const string EffectName = "Cross Fade ( 0dB)";
    public const string EffectId = "KGAudioTransCrossFade0dB";
    public const string Policy = "phase14-constant-gain-one-frame-import-candidate-no-pre-normalization-v2";
    private const int SupportedAudioSampleRate = 48_000;
    private const double GainEqualityToleranceDb = 0.000_001;

    public async Task<PremiereConstantGainCandidateResult> WriteAsync(
        string inputXmlPath,
        string expectedInputXmlSha256,
        OutputAudit audit,
        string inputAuditSha256,
        string outputXmlPath,
        int trackIndex,
        long boundaryFrame,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputXmlPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedInputXmlSha256);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputAuditSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputXmlPath);
        if (trackIndex <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackIndex));
        }

        if (boundaryFrame <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(boundaryFrame));
        }

        var inputPath = Path.GetFullPath(inputXmlPath);
        var outputPath = Path.GetFullPath(outputXmlPath);
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Không tìm thấy XML đầu vào.", inputPath);
        }

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            throw new DirectoryNotFoundException("Thư mục chứa candidate XML phải tồn tại.");
        }

        if (File.Exists(outputPath) ||
            string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Candidate XML phải là file mới và không được trùng đầu vào.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var source = PremiereXmlDocumentLoader.LoadGeneratedOutput(inputPath, expectedInputXmlSha256);
        var document = source.Document;
        RemoveInsignificantWhitespace(document);
        var root = document.Root ?? throw new InvalidDataException("Candidate XML thiếu root xmeml.");
        if (root.Name.LocalName != "xmeml")
        {
            throw new InvalidDataException("Candidate XML không có root xmeml.");
        }

        var sequence = root.Elements("sequence").SingleOrDefault()
            ?? throw new InvalidDataException("Candidate writer yêu cầu đúng một sequence trực tiếp.");
        var frameRate = ReadSequenceFrameRate(sequence);
        var frameGrid = PremiereNdfFrameGrid.Create(frameRate, SupportedAudioSampleRate);
        ValidateAuditEnvelope(audit, source.Sha256, frameGrid);
        var frameTicks = frameGrid.FrameToTicks(1);
        if (frameTicks <= 0 || frameTicks % 2 != 0)
        {
            throw new InvalidDataException("Frame tick không hỗ trợ transition giữa frame.");
        }

        var halfFrameTicks = frameTicks / 2;
        var audio = RequiredElement(RequiredElement(sequence, "media"), "audio");
        var sampleRate = ReadLong(RequiredElement(RequiredElement(audio, "format"), "samplecharacteristics"), "samplerate");
        if (sampleRate != SupportedAudioSampleRate)
        {
            throw new InvalidDataException("Candidate chỉ hỗ trợ sequence audio 48 kHz đã khóa.");
        }

        var tracks = audio.Elements("track").ToArray();
        if (trackIndex > tracks.Length)
        {
            throw new InvalidDataException($"Không tồn tại audio track A{trackIndex}.");
        }

        var track = tracks[trackIndex - 1];
        if (track.Elements("transitionitem").Any())
        {
            throw new InvalidDataException("Track candidate đã có transition; từ chối chèn chồng.");
        }

        var boundary = InsertValidatedBoundary(
            track,
            audit,
            frameGrid,
            trackIndex,
            boundaryFrame,
            expectedKind: null);

        var tempPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await WriteDocumentAsync(document, tempPath, cancellationToken);
            var outputSha256 = await ComputeSha256Async(tempPath, cancellationToken);
            PremiereXmlDocumentLoader.ValidateGeneratedOutputSyntax(tempPath, outputSha256);
            File.Move(tempPath, outputPath);
            return new(
                source.Sha256,
                inputAuditSha256,
                audit.RunId,
                outputSha256,
                trackIndex,
                boundaryFrame,
                frameRate,
                frameTicks,
                halfFrameTicks,
                boundary.LeftClipItemId,
                boundary.RightClipItemId,
                boundary.LeftEnabled,
                boundary.RightEnabled,
                EffectName,
                EffectId);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    internal static PremiereConstantGainBoundaryResult InsertValidatedBoundary(
        XElement track,
        OutputAudit audit,
        PremiereNdfFrameGrid frameGrid,
        int trackIndex,
        long boundaryFrame,
        BoundaryTransitionKind? expectedKind)
    {
        var clips = track.Elements("clipitem").ToArray();
        var leftMatches = clips.Where(clip => ReadLong(clip, "end") == boundaryFrame).ToArray();
        var rightMatches = clips.Where(clip => ReadLong(clip, "start") == boundaryFrame).ToArray();
        if (leftMatches.Length != 1 || rightMatches.Length != 1)
        {
            throw new InvalidDataException(
                $"A{trackIndex} frame {boundaryFrame} không có đúng một cặp clip chạm nhau.");
        }

        var left = leftMatches[0];
        var right = rightMatches[0];
        var leftIndex = Array.IndexOf(clips, left);
        if (leftIndex < 0 || leftIndex + 1 >= clips.Length || !ReferenceEquals(clips[leftIndex + 1], right))
        {
            throw new InvalidDataException("Hai clip candidate không liền nhau trong track.");
        }

        var leftStart = ReadLong(left, "start");
        var rightEnd = ReadLong(right, "end");
        if (leftStart < 0 || rightEnd <= boundaryFrame || boundaryFrame - leftStart < 1 || rightEnd - boundaryFrame < 1)
        {
            throw new InvalidDataException("Hai phía candidate phải còn ít nhất một frame.");
        }

        var leftFileId = RequiredAttribute(RequiredElement(left, "file"), "id");
        var rightFileId = RequiredAttribute(RequiredElement(right, "file"), "id");
        var leftMasterClipId = RequiredElement(left, "masterclipid").Value;
        var rightMasterClipId = RequiredElement(right, "masterclipid").Value;
        if (!string.Equals(leftFileId, rightFileId, StringComparison.Ordinal) ||
            !string.Equals(leftMasterClipId, rightMasterClipId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Candidate chỉ được đặt giữa hai fragment cùng source clip/media.");
        }

        var halfFrameTicks = frameGrid.FrameToTicks(1) / 2;
        var leftPproTicksIn = ReadLong(left, "pproTicksIn");
        var leftPproTicksOut = ReadLong(left, "pproTicksOut");
        var rightPproTicksIn = ReadLong(right, "pproTicksIn");
        var rightPproTicksOut = ReadLong(right, "pproTicksOut");
        if (leftPproTicksOut != rightPproTicksIn ||
            leftPproTicksOut < halfFrameTicks ||
            checked(leftPproTicksOut + halfFrameTicks) > rightPproTicksOut ||
            rightPproTicksIn - halfFrameTicks < leftPproTicksIn)
        {
            throw new InvalidDataException("Source tick hai phía không đủ liên tục cho Constant Gain một frame.");
        }

        var leftEnabled = ReadBoolean(left, "enabled");
        var rightEnabled = ReadBoolean(right, "enabled");
        var leftId = RequiredAttribute(left, "id");
        var rightId = RequiredAttribute(right, "id");
        var kind = ValidateAuditBoundary(
            audit,
            trackIndex,
            boundaryFrame,
            leftId,
            rightId,
            leftFileId,
            rightFileId,
            leftEnabled,
            rightEnabled,
            leftPproTicksIn,
            leftPproTicksOut,
            rightPproTicksIn,
            rightPproTicksOut);
        if (expectedKind is not null && kind != expectedKind)
        {
            throw new InvalidDataException(
                $"Loại boundary A{trackIndex}/frame {boundaryFrame} không khớp scan đã khóa.");
        }

        var boundaryTicks = frameGrid.FrameToTicks(boundaryFrame);
        left.AddAfterSelf(CreateTransition(
            boundaryFrame,
            frameGrid.FramesPerSecond,
            checked(boundaryTicks - halfFrameTicks),
            checked(boundaryTicks + halfFrameTicks),
            halfFrameTicks));
        return new(
            trackIndex,
            boundaryFrame,
            kind,
            leftId,
            rightId,
            leftEnabled,
            rightEnabled,
            EffectName,
            EffectId);
    }

    private static XElement CreateTransition(
        long boundaryFrame,
        int frameRate,
        long ticksIn,
        long ticksOut,
        long halfFrameTicks) => new(
            "transitionitem",
            new XElement("start", boundaryFrame),
            new XElement("end", boundaryFrame),
            new XElement("pproTicksIn", ticksIn),
            new XElement("pproTicksOut", ticksOut),
            new XElement("alignment", "center"),
            new XElement("cutPointTicks", halfFrameTicks),
            new XElement(
                "rate",
                new XElement("timebase", frameRate),
                new XElement("ntsc", "FALSE")),
            new XElement(
                "effect",
                new XElement("name", EffectName),
                new XElement("effectid", EffectId),
                new XElement("effecttype", "transition"),
                new XElement("mediatype", "audio"),
                new XElement("wipecode", 0),
                new XElement("wipeaccuracy", 100),
                new XElement("startratio", 0),
                new XElement("endratio", 1),
                new XElement("reverse", "FALSE")));

    internal static void ValidateAuditEnvelope(
        OutputAudit audit,
        string inputXmlSha256,
        PremiereNdfFrameGrid frameGrid)
    {
        if (!string.Equals(audit.OutputXmlSha256, inputXmlSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Audit không thuộc generated XML đầu vào.");
        }

        var timing = audit.SequenceTiming
            ?? throw new InvalidDataException("Candidate yêu cầu audit Phase 12+ có sequence timing.");
        if (timing.FrameRate != frameGrid.FramesPerSecond ||
            timing.Ntsc ||
            timing.AudioSampleRate != frameGrid.AudioSampleRate ||
            timing.SamplesPerFrame != frameGrid.SamplesPerFrame ||
            !string.Equals(timing.FrameGridPolicy, SequenceTimingAudit.ExactFrameGridPolicy, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Sequence timing trong audit không khớp generated XML.");
        }
    }

    private static BoundaryTransitionKind ValidateAuditBoundary(
        OutputAudit audit,
        int trackIndex,
        long boundaryFrame,
        string leftClipItemId,
        string rightClipItemId,
        string leftFileId,
        string rightFileId,
        bool leftEnabled,
        bool rightEnabled,
        long leftPproTicksIn,
        long leftPproTicksOut,
        long rightPproTicksIn,
        long rightPproTicksOut)
    {
        var leftMatches = audit.Fragments.Where(fragment =>
            fragment.TrackIndex == trackIndex &&
            fragment.TimelineEndFrame == boundaryFrame).ToArray();
        var rightMatches = audit.Fragments.Where(fragment =>
            fragment.TrackIndex == trackIndex &&
            fragment.TimelineStartFrame == boundaryFrame).ToArray();
        if (leftMatches.Length != 1 || rightMatches.Length != 1)
        {
            throw new InvalidDataException("Audit không có đúng một cặp fragment tại candidate boundary.");
        }

        var left = leftMatches[0];
        var right = rightMatches[0];
        if (!string.Equals(left.ClipItemId, leftClipItemId, StringComparison.Ordinal) ||
            !string.Equals(right.ClipItemId, rightClipItemId, StringComparison.Ordinal) ||
            !string.Equals(left.SourceClipId, right.SourceClipId, StringComparison.Ordinal) ||
            !string.Equals(left.SourceFileId, right.SourceFileId, StringComparison.Ordinal) ||
            !string.Equals(left.SourceFileId, leftFileId, StringComparison.Ordinal) ||
            !string.Equals(right.SourceFileId, rightFileId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Audit không chứng minh hai fragment thuộc cùng source clip/media.");
        }

        if (left.Enabled != leftEnabled ||
            right.Enabled != rightEnabled ||
            left.PproTicksIn != leftPproTicksIn ||
            left.PproTicksOut != leftPproTicksOut ||
            right.PproTicksIn != rightPproTicksIn ||
            right.PproTicksOut != rightPproTicksOut ||
            left.SourceEndSample != right.SourceStartSample)
        {
            throw new InvalidDataException("State/tick/source sample của audit không khớp XML candidate.");
        }

        var expectedLeftEnabled = left.Status is AudioSegmentStatus.Speech or AudioSegmentStatus.Ambiguous;
        var expectedRightEnabled = right.Status is AudioSegmentStatus.Speech or AudioSegmentStatus.Ambiguous;
        if (left.Enabled != expectedLeftEnabled || right.Enabled != expectedRightEnabled)
        {
            throw new InvalidDataException("Status/enabled trong audit candidate không hợp lệ.");
        }

        var changesState = left.Enabled != right.Enabled;
        var changesGain = left.Enabled && right.Enabled &&
            Math.Abs((left.AppliedGainDb ?? 0) - (right.AppliedGainDb ?? 0)) > GainEqualityToleranceDb;
        if (!changesState && !changesGain)
        {
            throw new InvalidDataException("Boundary audit không đổi state hoặc gain; từ chối transition.");
        }

        if (left.Enabled && !right.Enabled)
        {
            return BoundaryTransitionKind.EnabledToDisabled;
        }

        if (!left.Enabled && right.Enabled)
        {
            return BoundaryTransitionKind.DisabledToEnabled;
        }

        return BoundaryTransitionKind.GainChange;
    }

    internal static int ReadSequenceFrameRate(XElement sequence)
    {
        var rate = RequiredElement(sequence, "rate");
        if (!string.Equals(RequiredElement(rate, "ntsc").Value, "FALSE", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(RequiredElement(rate, "timebase").Value, NumberStyles.None, CultureInfo.InvariantCulture, out var frameRate) ||
            frameRate is not (24 or 25 or 30))
        {
            throw new InvalidDataException("Candidate chỉ hỗ trợ sequence 24/25/30 fps NDF.");
        }

        return frameRate;
    }

    private static long ReadLong(XElement parent, string name)
    {
        var value = RequiredElement(parent, name).Value;
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidDataException($"Giá trị {name} không phải số nguyên hợp lệ.");
    }

    private static bool ReadBoolean(XElement parent, string name)
    {
        var value = RequiredElement(parent, name).Value;
        if (string.Equals(value, "TRUE", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "FALSE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new InvalidDataException($"Giá trị {name} không phải TRUE/FALSE.");
    }

    internal static XElement RequiredElement(XElement parent, string name) =>
        parent.Element(name) ?? throw new InvalidDataException($"XML thiếu phần tử {name}.");

    private static string RequiredAttribute(XElement element, string name) =>
        (string?)element.Attribute(name) is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"XML thiếu attribute {name}.");

    internal static void RemoveInsignificantWhitespace(XDocument document)
    {
        foreach (var whitespace in document.DescendantNodes()
                     .OfType<XText>()
                     .Where(text => string.IsNullOrWhiteSpace(text.Value))
                     .ToArray())
        {
            whitespace.Remove();
        }
    }

    internal static async Task WriteDocumentAsync(
        XDocument document,
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            });
        var settings = new XmlWriterSettings
        {
            Async = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            IndentChars = "\t",
            NewLineChars = "\r\n",
            NewLineHandling = NewLineHandling.Replace,
            OmitXmlDeclaration = false,
            CloseOutput = false
        };
        await using var writer = XmlWriter.Create(stream, settings);
        await writer.WriteStartDocumentAsync();
        await writer.WriteRawAsync("\r\n<!DOCTYPE xmeml>\r\n");
        await document.Root!.WriteToAsync(writer, cancellationToken);
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
        await stream.FlushAsync(cancellationToken);
    }

    internal static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Access = FileAccess.Read,
                Mode = FileMode.Open,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}
