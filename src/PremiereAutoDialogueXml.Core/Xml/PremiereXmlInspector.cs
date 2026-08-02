using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using PremiereAutoDialogueXml.Core.Inspection;
using PremiereAutoDialogueXml.Core.Media;
using PremiereAutoDialogueXml.Core.ProjectModel;

namespace PremiereAutoDialogueXml.Core.Xml;

public sealed class PremiereXmlInspector
{
    private const int RequiredFrameRate = 25;
    private const int RequiredSampleRate = 48_000;
    private const int RequiredOutputChannels = 2;
    private static readonly string[] UnsupportedElementNames =
    [
        "filter",
        "transitionitem",
        "generatoritem",
        "multiclip",
        "multiclipitem",
        "multicam",
        "keyframe",
        "timeremap",
        "speed",
        "link"
    ];

    private readonly WaveFileInspector _waveInspector;

    public PremiereXmlInspector()
        : this(new WaveFileInspector())
    {
    }

    public PremiereXmlInspector(WaveFileInspector waveInspector)
    {
        _waveInspector = waveInspector ?? throw new ArgumentNullException(nameof(waveInspector));
    }

    public PremiereProjectInspectionResult Inspect(string xmlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xmlPath);

        try
        {
            var source = PremiereXmlDocumentLoader.Load(xmlPath);
            return InspectDocument(source.Document, xmlPath, source.Sha256);
        }
        catch (PremiereXmlLoadException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (XmlContractException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (XmlException exception)
        {
            return Failure("xml-malformed", $"XML không hợp lệ tại dòng {exception.LineNumber}: {exception.Message}");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            FormatException or OverflowException or ArgumentException)
        {
            return Failure("xml-read-failed", $"Không thể kiểm tra XML: {exception.Message}");
        }
    }

    private PremiereProjectInspectionResult InspectDocument(XDocument document, string xmlPath, string sourceHash)
    {
        var issues = new List<InspectionIssue>();
        var root = document.Root;
        if (root?.Name.LocalName != "xmeml" || root.Name.Namespace != XNamespace.None)
        {
            return Failure("xml-root-unsupported", "Phần tử gốc phải là xmeml không namespace.");
        }

        var sequences = root.Elements("sequence").ToList();
        if (sequences.Count != 1)
        {
            return Failure("sequence-count-unsupported", $"MVP yêu cầu đúng một sequence; XML có {sequences.Count}.");
        }

        var sequenceElement = sequences[0];
        if (sequenceElement.Descendants("sequence").Any())
        {
            issues.Add(Error("nested-sequence-unsupported", "Nested sequence chưa được hỗ trợ."));
        }

        foreach (var elementName in UnsupportedElementNames)
        {
            if (sequenceElement.Descendants(elementName).Any())
            {
                issues.Add(Error(
                    $"{elementName}-unsupported",
                    $"XML chứa '{elementName}', nằm ngoài hợp đồng track nghệ sĩ đơn giản của MVP."));
            }
        }

        if (issues.Any(issue => issue.Severity == InspectionSeverity.Error))
        {
            return new(null, issues);
        }

        var sequenceRate = ParseRate(RequiredElement(sequenceElement, "rate", "sequence-rate-missing"));
        if (sequenceRate != RequiredFrameRate)
        {
            issues.Add(Error("sequence-rate-unsupported", $"Sequence phải là 25 fps; XML báo {sequenceRate} fps."));
        }

        var sequenceDuration = RequiredNonNegativeLong(sequenceElement, "duration");
        if (sequenceDuration <= 0)
        {
            issues.Add(Error("sequence-duration-invalid", "Duration của sequence phải lớn hơn 0."));
        }

        var mediaElement = RequiredElement(sequenceElement, "media", "sequence-media-missing");
        var audioElement = RequiredElement(mediaElement, "audio", "sequence-audio-missing");
        var outputChannelCount = RequiredInt(audioElement, "numOutputChannels");
        if (outputChannelCount != RequiredOutputChannels)
        {
            issues.Add(Error("master-channels-unsupported", $"Master phải là stereo; XML báo {outputChannelCount} output channel."));
        }

        var masterSampleCharacteristics = audioElement.Element("format")?.Element("samplecharacteristics")
            ?? throw new XmlContractException("master-format-missing", "Thiếu sample characteristics của master audio.");
        var masterSampleRate = RequiredInt(masterSampleCharacteristics, "samplerate");
        if (masterSampleRate != RequiredSampleRate)
        {
            issues.Add(Error("master-sample-rate-unsupported", $"Master phải là 48 kHz; XML báo {masterSampleRate} Hz."));
        }

        ValidateStereoOutputs(audioElement, issues);
        if (issues.Any(issue => issue.Severity == InspectionSeverity.Error))
        {
            return new(null, issues);
        }

        var fileCatalog = BuildFileCatalog(root);
        var waveCache = new Dictionary<string, WaveInspectionResult>(StringComparer.OrdinalIgnoreCase);
        var clipIds = new HashSet<string>(StringComparer.Ordinal);
        var tracks = new List<PremiereAudioTrack>();

        var trackIndex = 0;
        foreach (var trackElement in audioElement.Elements("track"))
        {
            trackIndex++;
            var track = ParseTrack(
                trackElement,
                trackIndex,
                sequenceDuration,
                sequenceRate,
                fileCatalog,
                waveCache,
                clipIds,
                issues);
            if (track is not null)
            {
                tracks.Add(track);
            }
        }

        if (trackIndex == 0)
        {
            issues.Add(Error("audio-tracks-missing", "Sequence không có audio track nghệ sĩ."));
        }

        if (issues.Any(issue => issue.Severity == InspectionSeverity.Error))
        {
            return new(null, issues);
        }

        var project = new PremiereProject(
            Path.GetFullPath(xmlPath),
            sourceHash,
            new PremiereSequence(
                RequiredAttribute(sequenceElement, "id"),
                RequiredText(sequenceElement, "uuid"),
                RequiredText(sequenceElement, "name"),
                sequenceRate,
                sequenceDuration,
                outputChannelCount,
                masterSampleRate,
                tracks));

        return new(project, issues);
    }

    private PremiereAudioTrack? ParseTrack(
        XElement trackElement,
        int trackIndex,
        long sequenceDuration,
        int sequenceRate,
        IReadOnlyDictionary<string, XElement> fileCatalog,
        IDictionary<string, WaveInspectionResult> waveCache,
        ISet<string> clipIds,
        ICollection<InspectionIssue> issues)
    {
        var trackType = (string?)trackElement.Attribute("premiereTrackType");
        if (!string.Equals(trackType, "Stereo", StringComparison.Ordinal))
        {
            issues.Add(Error("audio-track-type-unsupported", $"Track A{trackIndex} không phải Premiere Stereo hoặc là submix."));
        }

        if (!ReadBoolean(trackElement, "enabled", defaultValue: true))
        {
            issues.Add(Error("audio-track-disabled", $"Track A{trackIndex} đang bị Disable trước khi xử lý."));
        }

        if (ReadBoolean(trackElement, "locked", defaultValue: false))
        {
            issues.Add(Error("audio-track-locked", $"Track A{trackIndex} đang bị khóa."));
        }

        var outputChannelIndex = RequiredInt(trackElement, "outputchannelindex");
        if (outputChannelIndex is not (1 or 2))
        {
            issues.Add(Error("audio-routing-unsupported", $"Track A{trackIndex} route tới output channel {outputChannelIndex}, ngoài master stereo 1/2."));
        }

        var clipElements = trackElement.Elements("clipitem").ToList();
        var clips = new List<PremiereAudioClip>();
        foreach (var clipElement in clipElements)
        {
            var clip = ParseClip(
                clipElement,
                trackIndex,
                sequenceDuration,
                sequenceRate,
                fileCatalog,
                waveCache,
                clipIds,
                issues);
            if (clip is not null)
            {
                clips.Add(clip);
            }
        }

        if (clipElements.Count == 0)
        {
            issues.Add(new(
                "audio-track-empty",
                InspectionSeverity.Warning,
                $"Track A{trackIndex} không có clip và sẽ được giữ nguyên."));
        }

        clips.Sort((left, right) => left.TimelineStartFrame.CompareTo(right.TimelineStartFrame));
        for (var index = 1; index < clips.Count; index++)
        {
            if (clips[index].TimelineStartFrame < clips[index - 1].TimelineEndFrame)
            {
                issues.Add(Error(
                    "audio-overlap-unsupported",
                    $"Track A{trackIndex} có overlap giữa '{clips[index - 1].Name}' và '{clips[index].Name}'."));
            }
        }

        return new(trackIndex, outputChannelIndex, clips);
    }

    private PremiereAudioClip? ParseClip(
        XElement clipElement,
        int trackIndex,
        long sequenceDuration,
        int sequenceRate,
        IReadOnlyDictionary<string, XElement> fileCatalog,
        IDictionary<string, WaveInspectionResult> waveCache,
        ISet<string> clipIds,
        ICollection<InspectionIssue> issues)
    {
        var clipId = RequiredAttribute(clipElement, "id");
        var clipName = RequiredText(clipElement, "name");
        if (!clipIds.Add(clipId))
        {
            issues.Add(Error("clip-id-duplicate", $"Clip ID '{clipId}' bị trùng trong sequence."));
        }

        var enabled = ReadBoolean(clipElement, "enabled", defaultValue: true);
        if (!enabled)
        {
            issues.Add(Error("clip-disabled-input", $"Clip '{clipName}' trên A{trackIndex} đã bị Disable trước khi xử lý."));
        }

        var channelType = (string?)clipElement.Attribute("premiereChannelType");
        if (!string.Equals(channelType, "mono", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(Error("clip-channel-type-unsupported", $"Clip '{clipName}' không phải source mono."));
        }

        var clipRate = ParseRate(RequiredElement(clipElement, "rate", "clip-rate-missing"));
        if (clipRate != sequenceRate)
        {
            issues.Add(Error("clip-rate-unsupported", $"Clip '{clipName}' có rate khác sequence, có thể là retime."));
        }

        var start = RequiredNonNegativeLong(clipElement, "start");
        var end = RequiredNonNegativeLong(clipElement, "end");
        var sourceIn = RequiredNonNegativeLong(clipElement, "in");
        var sourceOut = RequiredNonNegativeLong(clipElement, "out");
        if (end <= start || sourceOut <= sourceIn)
        {
            issues.Add(Error("clip-range-invalid", $"Clip '{clipName}' có start/end hoặc in/out không hợp lệ."));
            return null;
        }

        if (end > sequenceDuration)
        {
            issues.Add(Error("clip-outside-sequence", $"Clip '{clipName}' kết thúc ngoài duration sequence."));
        }

        var timelineFrameDuration = end - start;
        var sourceFrameDuration = sourceOut - sourceIn;
        var frameDurationDifference = Math.Abs(timelineFrameDuration - sourceFrameDuration);
        if (frameDurationDifference > 1)
        {
            issues.Add(Error("clip-retime-unsupported", $"Clip '{clipName}' có timeline duration khác source duration."));
        }
        else if (frameDurationDifference == 1)
        {
            issues.Add(new(
                "clip-frame-rounding",
                InspectionSeverity.Warning,
                $"Clip '{clipName}' lệch một frame giữa timeline và source trim; pproTicks được dùng để giữ biên audio chính xác."));
        }

        var sourceTrack = RequiredElement(clipElement, "sourcetrack", "source-track-missing");
        if (!string.Equals(RequiredText(sourceTrack, "mediatype"), "audio", StringComparison.OrdinalIgnoreCase) ||
            RequiredInt(sourceTrack, "trackindex") != 1)
        {
            issues.Add(Error("source-track-unsupported", $"Clip '{clipName}' phải dùng audio source track 1."));
        }

        var fileReference = RequiredElement(clipElement, "file", "clip-file-missing");
        var fileId = RequiredAttribute(fileReference, "id");
        if (!fileCatalog.TryGetValue(fileId, out var fileElement))
        {
            issues.Add(Error("file-reference-unresolved", $"Clip '{clipName}' tham chiếu file ID '{fileId}' không có pathurl."));
            return null;
        }

        var pathUrl = RequiredText(fileElement, "pathurl");
        var localPath = DecodeFilePath(pathUrl);
        if (!string.Equals(Path.GetExtension(localPath), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(Error("media-extension-unsupported", $"Media '{Path.GetFileName(localPath)}' không phải WAV."));
            return null;
        }

        if (!File.Exists(localPath))
        {
            issues.Add(Error("media-missing", $"Không tìm thấy media '{Path.GetFileName(localPath)}'."));
            return null;
        }

        if (!waveCache.TryGetValue(localPath, out var waveResult))
        {
            waveResult = _waveInspector.Inspect(localPath);
            waveCache.Add(localPath, waveResult);
        }

        foreach (var waveIssue in waveResult.Issues)
        {
            issues.Add(waveIssue with { Message = $"{Path.GetFileName(localPath)}: {waveIssue.Message}" });
        }

        if (!waveResult.IsSupported || waveResult.File is null)
        {
            return null;
        }

        CompareXmlMediaMetadata(fileElement, waveResult.File, issues);
        var (ticksIn, ticksOut, sourceStartSample, sourceEndSample) = ResolveSourceRange(
            clipElement,
            clipName,
            sourceIn,
            sourceOut,
            timelineFrameDuration,
            sequenceRate,
            waveResult.File,
            issues);
        if (sourceEndSample > waveResult.File.SampleFrameCount)
        {
            issues.Add(Error(
                "source-range-outside-media",
                $"Clip '{clipName}' cần sample {sourceEndSample:N0} nhưng WAV chỉ có {waveResult.File.SampleFrameCount:N0} sample frame hoàn chỉnh."));
        }

        var sourceMedia = new PremiereSourceMedia(
            fileId,
            OptionalText(fileElement, "name") ?? Path.GetFileName(localPath),
            pathUrl,
            localPath,
            waveResult.File);

        return new(
            clipId,
            clipName,
            enabled,
            start,
            end,
            sourceIn,
            sourceOut,
            ticksIn,
            ticksOut,
            sourceStartSample,
            sourceEndSample,
            fileId,
            sourceMedia);
    }

    private static (long TicksIn, long TicksOut, long StartSample, long EndSample) ResolveSourceRange(
        XElement clipElement,
        string clipName,
        long sourceIn,
        long sourceOut,
        long timelineDuration,
        int frameRate,
        WaveFileInfo wave,
        ICollection<InspectionIssue> issues)
    {
        var ticksInText = OptionalText(clipElement, "pproTicksIn");
        var ticksOutText = OptionalText(clipElement, "pproTicksOut");
        if ((ticksInText is null) != (ticksOutText is null))
        {
            throw new XmlContractException("ppro-ticks-incomplete", $"Clip '{clipName}' chỉ có một đầu pproTicks.");
        }

        long ticksIn;
        long ticksOut;
        if (ticksInText is not null && ticksOutText is not null)
        {
            ticksIn = ParseNonNegativeLong(ticksInText, "pproTicksIn");
            ticksOut = ParseNonNegativeLong(ticksOutText, "pproTicksOut");
            if (ticksOut <= ticksIn)
            {
                throw new XmlContractException("ppro-ticks-invalid", $"Clip '{clipName}' có pproTicksIn/Out không hợp lệ.");
            }

            var expectedTicks = PremiereTimeMath.ScaleFloor(timelineDuration, PremiereTimeMath.TicksPerSecond, frameRate);
            var tickDurationDifference = Math.Abs((ticksOut - ticksIn) - expectedTicks);
            var ticksPerFrame = PremiereTimeMath.TicksPerSecond / frameRate;
            if (tickDurationDifference > ticksPerFrame)
            {
                issues.Add(Error("ppro-duration-mismatch", $"Clip '{clipName}' có pproTicks duration khác timeline duration."));
            }
            else if (tickDurationDifference > 0)
            {
                issues.Add(new(
                    "ppro-frame-rounding",
                    InspectionSeverity.Warning,
                    $"Clip '{clipName}' có pproTicks lệch dưới một frame so với timeline; app giữ pproTicks làm biên source chính xác."));
            }
        }
        else
        {
            ticksIn = PremiereTimeMath.ScaleFloor(sourceIn, PremiereTimeMath.TicksPerSecond, frameRate);
            ticksOut = PremiereTimeMath.ScaleFloor(sourceOut, PremiereTimeMath.TicksPerSecond, frameRate);
            issues.Add(new(
                "ppro-ticks-missing",
                InspectionSeverity.Warning,
                $"Clip '{clipName}' thiếu pproTicksIn/Out; Phase 02 dùng source in/out ở {frameRate} fps."));
        }

        var startSample = PremiereTimeMath.ScaleFloor(ticksIn, wave.SampleRate, PremiereTimeMath.TicksPerSecond);
        var endSample = PremiereTimeMath.ScaleCeiling(ticksOut, wave.SampleRate, PremiereTimeMath.TicksPerSecond);
        var ticksPerSample = PremiereTimeMath.TicksPerSecond / wave.SampleRate;
        if (ticksIn % ticksPerSample != 0 || ticksOut % ticksPerSample != 0)
        {
            issues.Add(new(
                "ppro-ticks-sample-rounded",
                InspectionSeverity.Warning,
                $"Clip '{clipName}' không thẳng biên sample; vùng đọc được nới ra tối đa một sample ở mỗi đầu."));
        }

        return (ticksIn, ticksOut, startSample, endSample);
    }

    private static void CompareXmlMediaMetadata(
        XElement fileElement,
        WaveFileInfo wave,
        ICollection<InspectionIssue> issues)
    {
        var audio = fileElement.Element("media")?.Element("audio");
        var characteristics = audio?.Element("samplecharacteristics");
        CompareOptionalMetadata(characteristics, "samplerate", wave.SampleRate, "sample rate", fileElement, issues);
        CompareOptionalMetadata(characteristics, "depth", wave.ValidBitsPerSample, "bit depth", fileElement, issues);
        CompareOptionalMetadata(audio, "channelcount", wave.ChannelCount, "channel count", fileElement, issues);
    }

    private static void CompareOptionalMetadata(
        XElement? parent,
        string elementName,
        int headerValue,
        string label,
        XElement fileElement,
        ICollection<InspectionIssue> issues)
    {
        var value = OptionalInt(parent, elementName);
        if (value is null || value == headerValue)
        {
            return;
        }

        var name = OptionalText(fileElement, "name") ?? RequiredAttribute(fileElement, "id");
        issues.Add(new(
            "xml-wav-metadata-mismatch",
            InspectionSeverity.Warning,
            $"'{name}' có {label} trong XML là {value}, header WAV là {headerValue}; app dùng header WAV."));
    }

    private static IReadOnlyDictionary<string, XElement> BuildFileCatalog(XElement root)
    {
        var catalog = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var group in root.Descendants("file")
                     .Where(element => element.Attribute("id") is not null)
                     .GroupBy(element => (string)element.Attribute("id")!, StringComparer.Ordinal))
        {
            var definitions = group.Where(element => element.Element("pathurl") is not null).ToList();
            if (definitions.Count == 0)
            {
                continue;
            }

            var distinctPaths = definitions
                .Select(element => RequiredText(element, "pathurl"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (distinctPaths.Count != 1)
            {
                throw new XmlContractException("file-id-conflict", $"File ID '{group.Key}' trỏ tới nhiều pathurl khác nhau.");
            }

            catalog.Add(group.Key, definitions[0]);
        }

        return catalog;
    }

    private static void ValidateStereoOutputs(XElement audioElement, ICollection<InspectionIssue> issues)
    {
        var groups = audioElement.Element("outputs")?.Elements("group").ToList() ?? [];
        if (groups.Count != 2 ||
            groups.Any(group => RequiredInt(group, "numchannels") != 1) ||
            !groups.Select(group => RequiredInt(group, "index")).Order().SequenceEqual([1, 2]))
        {
            issues.Add(Error("master-routing-unsupported", "Master audio phải có hai output group mono với index 1 và 2."));
        }
    }

    private static string DecodeFilePath(string pathUrl)
    {
        if (!Uri.TryCreate(pathUrl, UriKind.Absolute, out var uri) || !uri.IsFile)
        {
            throw new XmlContractException("media-pathurl-invalid", "Media pathurl phải là file URL tuyệt đối.");
        }

        var localPath = string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            ? DecodePremiereLocalhostPath(uri)
            : uri.LocalPath;
        if (string.IsNullOrWhiteSpace(localPath))
        {
            throw new XmlContractException("media-pathurl-invalid", "Media pathurl không tạo được đường dẫn Windows.");
        }

        return Path.GetFullPath(localPath);
    }

    private static string DecodePremiereLocalhostPath(Uri uri)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath).Replace('/', Path.DirectorySeparatorChar);
        if (path.Length >= 4 &&
            path[0] == Path.DirectorySeparatorChar &&
            char.IsAsciiLetter(path[1]) &&
            path[2] == ':' &&
            path[3] == Path.DirectorySeparatorChar)
        {
            return path[1..];
        }

        throw new XmlContractException(
            "media-pathurl-invalid",
            "file://localhost phải chứa đường dẫn ổ đĩa Windows tuyệt đối, ví dụ F%3A/path/file.wav.");
    }

    private static int ParseRate(XElement rateElement)
    {
        var timebase = RequiredInt(rateElement, "timebase");
        var ntsc = OptionalText(rateElement, "ntsc");
        if (ntsc is not null && !string.Equals(ntsc, "FALSE", StringComparison.OrdinalIgnoreCase))
        {
            throw new XmlContractException("ntsc-rate-unsupported", "MVP yêu cầu rate NDF với ntsc=FALSE.");
        }

        return timebase;
    }

    private static bool ReadBoolean(XElement parent, string name, bool defaultValue)
    {
        var value = OptionalText(parent, name);
        if (value is null)
        {
            return defaultValue;
        }

        if (string.Equals(value, "TRUE", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "FALSE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new XmlContractException("xml-boolean-invalid", $"Giá trị {name} phải là TRUE hoặc FALSE.");
    }

    private static XElement RequiredElement(XElement parent, string name, string code) =>
        parent.Element(name) ?? throw new XmlContractException(code, $"Thiếu phần tử {name} trong {parent.Name.LocalName}.");

    private static string RequiredAttribute(XElement element, string name) =>
        ((string?)element.Attribute(name))?.Trim() is { Length: > 0 } value
            ? value
            : throw new XmlContractException("xml-attribute-missing", $"Thiếu attribute {name} trong {element.Name.LocalName}.");

    private static string RequiredText(XElement parent, string name) =>
        OptionalText(parent, name) ?? throw new XmlContractException("xml-value-missing", $"Thiếu giá trị {name} trong {parent.Name.LocalName}.");

    private static string? OptionalText(XElement? parent, string name)
    {
        var value = (string?)parent?.Element(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int RequiredInt(XElement parent, string name)
    {
        var value = RequiredText(parent, name);
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new XmlContractException("xml-number-invalid", $"Giá trị {name} không phải số nguyên hợp lệ.");
        }

        return parsed;
    }

    private static int? OptionalInt(XElement? parent, string name)
    {
        var value = OptionalText(parent, name);
        if (value is null)
        {
            return null;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new XmlContractException("xml-number-invalid", $"Giá trị {name} không phải số nguyên hợp lệ.");
        }

        return parsed;
    }

    private static long RequiredNonNegativeLong(XElement parent, string name) =>
        ParseNonNegativeLong(RequiredText(parent, name), name);

    private static long ParseNonNegativeLong(string value, string name)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            throw new XmlContractException("xml-number-invalid", $"Giá trị {name} phải là số nguyên 64-bit không âm.");
        }

        return parsed;
    }

    private static PremiereProjectInspectionResult Failure(string code, string message) =>
        new(null, [Error(code, message)]);

    private static InspectionIssue Error(string code, string message) =>
        new(code, InspectionSeverity.Error, message);

    private sealed class XmlContractException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
