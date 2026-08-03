using System.Globalization;
using System.Xml.Linq;
using PremiereAutoDialogueXml.Core.ProjectModel;
using PremiereAutoDialogueXml.Core.Xml;
using PremiereAutoDialogueXml.Output.Gain;
using PremiereAutoDialogueXml.Output.Xml;

namespace PremiereAutoDialogueXml.Output.Validation;

public sealed class OutputXmlContractValidator
{
    private static readonly HashSet<string> AllowedEffectIds =
    [
        "audiolevels",
        PremiereGainFilterFactory.PremiereGainEffectId
    ];

    public void Validate(
        PremiereProject project,
        GeneratedPremiereXml generated,
        XDocument reloadedOutput)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(generated);
        ArgumentNullException.ThrowIfNull(reloadedOutput);

        var source = PremiereXmlDocumentLoader.Load(project.SourceXmlPath, project.SourceXmlSha256).Document;
        var sourceSequence = RequiredSequence(source);
        var outputSequence = RequiredSequence(reloadedOutput);

        Require(AttributesEqual(source.Root!, reloadedOutput.Root!), "Attribute của root xmeml đã thay đổi.");
        Require(
            AttributesEqualExcept(sourceSequence, outputSequence, "id"),
            "Sequence settings dạng attribute đã thay đổi.");
        Require((string?)outputSequence.Attribute("id") == generated.SequenceId, "Output sequence ID không khớp generation result.");
        Require(outputSequence.Element("uuid")?.Value == generated.SequenceUuid, "Output sequence UUID không khớp.");
        Require(outputSequence.Element("name")?.Value == generated.SequenceName, "Output sequence name không khớp.");
        Require(
            outputSequence.Element("duration")?.Value == sourceSequence.Element("duration")?.Value,
            "Sequence duration đã thay đổi.");
        Require(
            SemanticEquals(outputSequence.Element("rate"), sourceSequence.Element("rate")),
            "Sequence rate đã thay đổi.");
        Require(
            SequenceMetadataEqual(sourceSequence, outputSequence),
            "Sequence metadata/settings ngoài name/uuid/marker/media đã thay đổi.");
        ValidateMarkers(sourceSequence, outputSequence, generated.Markers);

        var sourceMedia = RequiredElement(sourceSequence, "media");
        var outputMedia = RequiredElement(outputSequence, "media");
        Require(
            SemanticEquals(sourceMedia.Element("video"), outputMedia.Element("video")),
            "Video subtree đã thay đổi.");

        var sourceAudio = RequiredElement(sourceMedia, "audio");
        var outputAudio = RequiredElement(outputMedia, "audio");
        foreach (var settingName in new[] { "numOutputChannels", "format", "outputs" })
        {
            Require(
                SemanticEquals(sourceAudio.Element(settingName), outputAudio.Element(settingName)),
                $"Audio setting '{settingName}' đã thay đổi.");
        }

        var sourceTracks = sourceAudio.Elements("track").ToArray();
        var outputTracks = outputAudio.Elements("track").ToArray();
        Require(sourceTracks.Length == outputTracks.Length, "Số audio track đã thay đổi.");
        for (var index = 0; index < sourceTracks.Length; index++)
        {
            Require(
                AttributesEqual(sourceTracks[index], outputTracks[index]),
                $"Attribute/routing của track {index + 1} đã thay đổi.");
            Require(
                NonClipChildrenEqual(sourceTracks[index], outputTracks[index]),
                $"Routing/state ngoài clip của track {index + 1} đã thay đổi.");
        }

        var sourceFiles = BuildFileCatalog(source);
        var outputFiles = BuildFileCatalog(reloadedOutput);
        Require(sourceFiles.Count == outputFiles.Count, "Số media file definition đã thay đổi.");
        foreach (var sourceFile in sourceFiles)
        {
            Require(
                outputFiles.TryGetValue(sourceFile.Key, out var outputPath) && outputPath == sourceFile.Value,
                $"Media pathurl của file '{sourceFile.Key}' đã thay đổi hoặc bị thiếu.");
        }

        var outputClipItems = outputAudio.Descendants("clipitem").ToArray();
        var clipIds = outputClipItems.Select(item => RequiredAttribute(item, "id")).ToArray();
        Require(clipIds.Distinct(StringComparer.Ordinal).Count() == clipIds.Length, "Output clipitem ID bị trùng.");
        Require(clipIds.Length == generated.AudioFragments.Count, "Số output clipitem không khớp fragment audit.");
        var clipItemsById = outputClipItems.ToDictionary(item => RequiredAttribute(item, "id"), StringComparer.Ordinal);
        var sourceClipItemsById = sourceAudio.Descendants("clipitem")
            .ToDictionary(item => RequiredAttribute(item, "id"), StringComparer.Ordinal);

        foreach (var fragment in generated.AudioFragments)
        {
            Require(clipItemsById.TryGetValue(fragment.ClipItemId, out var element), $"Thiếu fragment '{fragment.ClipItemId}'.");
            Require(ReadLong(element!, "start") == fragment.TimelineStartFrame, "Fragment start không khớp.");
            Require(ReadLong(element!, "end") == fragment.TimelineEndFrame, "Fragment end không khớp.");
            Require(ReadLong(element!, "in") == fragment.SourceInFrame, "Fragment in không khớp.");
            Require(ReadLong(element!, "out") == fragment.SourceOutFrame, "Fragment out không khớp.");
            Require(ReadLong(element!, "pproTicksIn") == fragment.PproTicksIn, "Fragment pproTicksIn không khớp.");
            Require(ReadLong(element!, "pproTicksOut") == fragment.PproTicksOut, "Fragment pproTicksOut không khớp.");
            Require(
                element!.Element("enabled")?.Value == (fragment.Enabled ? "TRUE" : "FALSE"),
                "Fragment enabled không khớp.");
            Require(
                (string?)element.Element("file")?.Attribute("id") == fragment.SourceFileId,
                "Fragment file reference đã thay đổi.");
            Require(sourceClipItemsById.TryGetValue(fragment.SourceClipId, out var sourceClip), "Fragment trỏ clip nguồn không tồn tại.");
            Require(
                SemanticEquals(sourceClip!.Element("sourcetrack"), element.Element("sourcetrack")),
                "Fragment sourcetrack đã thay đổi.");
            Require(
                sourceClip.Element("masterclipid")?.Value == element.Element("masterclipid")?.Value,
                "Fragment masterclipid đã thay đổi.");

            foreach (var effectId in element.Descendants("effectid").Select(item => item.Value))
            {
                Require(AllowedEffectIds.Contains(effectId), $"Output chứa effect ngoài hợp đồng: {effectId}.");
            }

            if (!fragment.Enabled)
            {
                Require(!element.Elements("filter").Any(), "Fragment Disable không được mang gain filter.");
            }
        }

        foreach (var projectTrack in project.Sequence.AudioTracks)
        {
            foreach (var clip in projectTrack.Clips)
            {
                var fragments = generated.AudioFragments
                    .Where(fragment => fragment.SourceClipId == clip.Id)
                    .OrderBy(fragment => fragment.TimelineStartFrame)
                    .ToArray();
                Require(fragments.Length > 0, $"Clip '{clip.Id}' không có output fragment.");
                Require(fragments[0].TimelineStartFrame == clip.TimelineStartFrame, "Fragment đầu không giữ clip start.");
                Require(fragments[^1].TimelineEndFrame == clip.TimelineEndFrame, "Fragment cuối không giữ clip end.");
                Require(fragments[0].PproTicksIn == clip.PproTicksIn, "Fragment đầu không giữ pproTicksIn.");
                Require(fragments[^1].PproTicksOut == clip.PproTicksOut, "Fragment cuối không giữ pproTicksOut.");
                Require(fragments[0].SourceInFrame == clip.SourceInFrame, "Fragment đầu không giữ source in.");
                Require(fragments[^1].SourceOutFrame == clip.SourceOutFrame, "Fragment cuối không giữ source out.");
                for (var index = 1; index < fragments.Length; index++)
                {
                    Require(
                        fragments[index - 1].TimelineEndFrame == fragments[index].TimelineStartFrame,
                        "Fragment tạo gap/overlap timeline trong source clip.");
                    Require(
                        fragments[index - 1].PproTicksOut == fragments[index].PproTicksIn,
                        "Fragment tạo gap/overlap pproTicks trong source clip.");
                    Require(
                        fragments[index].SourceInFrame <= fragments[index - 1].SourceOutFrame &&
                        fragments[index - 1].SourceOutFrame - fragments[index].SourceInFrame <= 1,
                        "Fragment tạo gap hoặc overlap source frame quá một frame.");
                }
            }
        }

        foreach (var track in outputTracks)
        {
            var ordered = track.Elements("clipitem").OrderBy(item => ReadLong(item, "start")).ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                Require(
                    ReadLong(ordered[index - 1], "end") <= ReadLong(ordered[index], "start"),
                    "Output tạo overlap mới trên cùng track.");
            }
        }
    }

    private static Dictionary<string, string> BuildFileCatalog(XDocument document) =>
        document.Descendants("file")
            .Where(file => file.Attribute("id") is not null && file.Element("pathurl") is not null)
            .GroupBy(file => RequiredAttribute(file, "id"), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(file => file.Element("pathurl")!.Value).Distinct(StringComparer.Ordinal).Single(),
                StringComparer.Ordinal);

    private static void ValidateMarkers(
        XElement sourceSequence,
        XElement outputSequence,
        IReadOnlyList<GeneratedSequenceMarker> generatedMarkers)
    {
        var expected = sourceSequence.Elements("marker")
            .Select(MarkerFingerprint)
            .Concat(generatedMarkers.Select(marker => string.Join(
                "\u001f",
                marker.Name,
                marker.InFrame.ToString(CultureInfo.InvariantCulture),
                marker.OutFrame.ToString(CultureInfo.InvariantCulture),
                marker.Comment)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actual = outputSequence.Elements("marker")
            .Select(MarkerFingerprint)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Require(expected.SequenceEqual(actual, StringComparer.Ordinal), "Marker nguồn/kết quả bị thiếu hoặc thay đổi.");
    }

    private static string MarkerFingerprint(XElement marker) => string.Join(
        "\u001f",
        marker.Element("name")?.Value ?? string.Empty,
        marker.Element("in")?.Value ?? string.Empty,
        marker.Element("out")?.Value ?? string.Empty,
        marker.Element("comment")?.Value ?? string.Empty);

    private static bool NonClipChildrenEqual(XElement source, XElement output)
    {
        var sourceChildren = source.Elements().Where(element => element.Name.LocalName != "clipitem").ToArray();
        var outputChildren = output.Elements().Where(element => element.Name.LocalName != "clipitem").ToArray();
        return sourceChildren.Length == outputChildren.Length &&
               sourceChildren.Zip(outputChildren).All(pair => SemanticEquals(pair.First, pair.Second));
    }

    private static bool AttributesEqual(XElement left, XElement right)
    {
        var leftAttributes = left.Attributes().OrderBy(attribute => attribute.Name.ToString()).ToArray();
        var rightAttributes = right.Attributes().OrderBy(attribute => attribute.Name.ToString()).ToArray();
        return leftAttributes.Length == rightAttributes.Length &&
               leftAttributes.Zip(rightAttributes).All(pair =>
                   pair.First.Name == pair.Second.Name && pair.First.Value == pair.Second.Value);
    }

    private static bool AttributesEqualExcept(XElement left, XElement right, params string[] excludedNames)
    {
        var excluded = excludedNames.ToHashSet(StringComparer.Ordinal);
        var leftClone = new XElement(left.Name, left.Attributes().Where(attribute => !excluded.Contains(attribute.Name.LocalName)));
        var rightClone = new XElement(right.Name, right.Attributes().Where(attribute => !excluded.Contains(attribute.Name.LocalName)));
        return AttributesEqual(leftClone, rightClone);
    }

    private static bool SequenceMetadataEqual(XElement source, XElement output)
    {
        var ignored = new HashSet<string>(StringComparer.Ordinal) { "uuid", "name", "marker", "media" };
        var sourceChildren = source.Elements().Where(element => !ignored.Contains(element.Name.LocalName)).ToArray();
        var outputChildren = output.Elements().Where(element => !ignored.Contains(element.Name.LocalName)).ToArray();
        return sourceChildren.Length == outputChildren.Length &&
               sourceChildren.Zip(outputChildren).All(pair => SemanticEquals(pair.First, pair.Second));
    }

    private static bool SemanticEquals(XElement? left, XElement? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        var leftClone = new XElement(left);
        var rightClone = new XElement(right);
        RemoveWhitespace(leftClone);
        RemoveWhitespace(rightClone);
        return XNode.DeepEquals(leftClone, rightClone);
    }

    private static void RemoveWhitespace(XElement element)
    {
        foreach (var whitespace in element.DescendantNodes().OfType<XText>()
                     .Where(text => string.IsNullOrWhiteSpace(text.Value)).ToArray())
        {
            whitespace.Remove();
        }
    }

    private static XElement RequiredSequence(XDocument document) =>
        document.Root?.Elements("sequence").SingleOrDefault()
        ?? throw new InvalidDataException("XML phải có đúng một sequence.");

    private static XElement RequiredElement(XElement parent, string name) =>
        parent.Element(name) ?? throw new InvalidDataException($"XML thiếu phần tử {name}.");

    private static string RequiredAttribute(XElement element, string name) =>
        (string?)element.Attribute(name) is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"XML thiếu attribute {name}.");

    private static long ReadLong(XElement parent, string name) =>
        long.TryParse(parent.Element(name)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidDataException($"XML có {name} không hợp lệ.");

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
