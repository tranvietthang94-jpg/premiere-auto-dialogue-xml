using System.Text;
using System.Xml;
using System.Xml.Linq;
using PremiereAutoDialogueXml.Core.ProjectModel;
using PremiereAutoDialogueXml.Core.Xml;

namespace PremiereAutoDialogueXml.Output.Xml;

internal static class PremiereXmlStreamingWriter
{
    public static async Task WriteAsync(
        PremiereProject project,
        GeneratedPremiereXmlPlan plan,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var source = PremiereXmlDocumentLoader.Load(project.SourceXmlPath, project.SourceXmlSha256);
        var document = source.Document;
        RemoveInsignificantWhitespace(document);
        var root = document.Root ?? throw new InvalidDataException("XML nguồn không có root xmeml.");
        var sequence = root.Elements("sequence").SingleOrDefault()
            ?? throw new InvalidDataException("XML nguồn phải có đúng một sequence.");
        sequence.SetAttributeValue("id", plan.SequenceId);
        SetOrAddSequenceUuid(sequence, plan.SequenceUuid);
        RequiredElement(sequence, "name").Value = plan.SequenceName;

        var media = RequiredElement(sequence, "media");
        var audio = RequiredElement(media, "audio");
        var sourceTracks = audio.Elements("track").ToArray();
        if (sourceTracks.Length != project.Sequence.AudioTracks.Count)
        {
            throw new InvalidDataException("Số audio track nguồn không khớp generation plan.");
        }

        if (plan.AudioFragments.Select(fragment => fragment.ClipItemId)
                .Distinct(StringComparer.Ordinal).Count() != plan.AudioFragments.Count)
        {
            throw new InvalidDataException("Generation plan chứa clipitem ID trùng.");
        }

        var fragmentsBySourceClip = plan.AudioFragments
            .GroupBy(fragment => fragment.SourceClipId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(fragment => fragment.TimelineStartFrame).ToArray(),
                StringComparer.Ordinal);
        var emittedFragmentCount = 0;
        var emittedMarkerCount = 0;

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
        await WriteRootAsync(
            writer,
            root,
            sequence,
            media,
            audio,
            project,
            plan,
            fragmentsBySourceClip,
            count => emittedFragmentCount += count,
            () => emittedMarkerCount++,
            cancellationToken);
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
        await stream.FlushAsync(cancellationToken);

        if (emittedFragmentCount != plan.AudioFragments.Count || emittedMarkerCount != plan.Markers.Count)
        {
            throw new InvalidDataException("Streaming writer không phát đủ fragment/marker trong generation plan.");
        }
    }

    private static async Task WriteRootAsync(
        XmlWriter writer,
        XElement root,
        XElement sequence,
        XElement media,
        XElement audio,
        PremiereProject project,
        GeneratedPremiereXmlPlan plan,
        IReadOnlyDictionary<string, GeneratedAudioFragment[]> fragmentsBySourceClip,
        Action<int> recordFragments,
        Action recordMarker,
        CancellationToken cancellationToken)
    {
        WriteStartElement(writer, root);
        foreach (var node in SignificantNodes(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(node, sequence))
            {
                await WriteSequenceAsync(
                    writer,
                    sequence,
                    media,
                    audio,
                    project,
                    plan,
                    fragmentsBySourceClip,
                    recordFragments,
                    recordMarker,
                    cancellationToken);
            }
            else
            {
                await node.WriteToAsync(writer, cancellationToken);
            }
        }

        await writer.WriteEndElementAsync();
    }

    private static async Task WriteSequenceAsync(
        XmlWriter writer,
        XElement sequence,
        XElement media,
        XElement audio,
        PremiereProject project,
        GeneratedPremiereXmlPlan plan,
        IReadOnlyDictionary<string, GeneratedAudioFragment[]> fragmentsBySourceClip,
        Action<int> recordFragments,
        Action recordMarker,
        CancellationToken cancellationToken)
    {
        WriteStartElement(writer, sequence);
        foreach (var node in SignificantNodes(sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(node, media))
            {
                foreach (var marker in plan.Markers
                             .OrderBy(marker => marker.InFrame)
                             .ThenBy(marker => marker.TrackIndex))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var element = new XElement(
                        "marker",
                        new XElement("name", marker.Name),
                        new XElement("in", marker.InFrame),
                        new XElement("out", marker.OutFrame),
                        new XElement("comment", marker.Comment));
                    await element.WriteToAsync(writer, cancellationToken);
                    recordMarker();
                }

                await WriteMediaAsync(
                    writer,
                    media,
                    audio,
                    project,
                    fragmentsBySourceClip,
                    recordFragments,
                    cancellationToken);
            }
            else
            {
                await node.WriteToAsync(writer, cancellationToken);
            }
        }

        await writer.WriteEndElementAsync();
    }

    private static async Task WriteMediaAsync(
        XmlWriter writer,
        XElement media,
        XElement audio,
        PremiereProject project,
        IReadOnlyDictionary<string, GeneratedAudioFragment[]> fragmentsBySourceClip,
        Action<int> recordFragments,
        CancellationToken cancellationToken)
    {
        WriteStartElement(writer, media);
        foreach (var node in SignificantNodes(media))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(node, audio))
            {
                await WriteAudioAsync(
                    writer,
                    audio,
                    project,
                    fragmentsBySourceClip,
                    recordFragments,
                    cancellationToken);
            }
            else
            {
                await node.WriteToAsync(writer, cancellationToken);
            }
        }

        await writer.WriteEndElementAsync();
    }

    private static async Task WriteAudioAsync(
        XmlWriter writer,
        XElement audio,
        PremiereProject project,
        IReadOnlyDictionary<string, GeneratedAudioFragment[]> fragmentsBySourceClip,
        Action<int> recordFragments,
        CancellationToken cancellationToken)
    {
        WriteStartElement(writer, audio);
        var trackPosition = 0;
        foreach (var node in SignificantNodes(audio))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node is XElement { Name.LocalName: "track" } track)
            {
                if (trackPosition >= project.Sequence.AudioTracks.Count)
                {
                    throw new InvalidDataException("XML nguồn có nhiều audio track hơn project.");
                }

                var emitted = await WriteTrackAsync(
                    writer,
                    track,
                    project.Sequence.AudioTracks[trackPosition++],
                    fragmentsBySourceClip,
                    cancellationToken);
                recordFragments(emitted);
            }
            else
            {
                await node.WriteToAsync(writer, cancellationToken);
            }
        }

        if (trackPosition != project.Sequence.AudioTracks.Count)
        {
            throw new InvalidDataException("Streaming writer không phát đủ audio track.");
        }

        await writer.WriteEndElementAsync();
    }

    private static async Task<int> WriteTrackAsync(
        XmlWriter writer,
        XElement sourceTrack,
        PremiereAudioTrack projectTrack,
        IReadOnlyDictionary<string, GeneratedAudioFragment[]> fragmentsBySourceClip,
        CancellationToken cancellationToken)
    {
        WriteStartElement(writer, sourceTrack);
        var projectClips = projectTrack.Clips.ToDictionary(clip => clip.Id, StringComparer.Ordinal);
        var emittedSourceClips = new HashSet<string>(StringComparer.Ordinal);
        var emittedFragments = 0;
        foreach (var node in SignificantNodes(sourceTrack))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node is XElement { Name.LocalName: "clipitem" } sourceClip)
            {
                var sourceClipId = RequiredAttribute(sourceClip, "id");
                if (!projectClips.ContainsKey(sourceClipId) ||
                    !fragmentsBySourceClip.TryGetValue(sourceClipId, out var fragments) ||
                    fragments.Length == 0 ||
                    !emittedSourceClips.Add(sourceClipId))
                {
                    throw new InvalidDataException($"Generation plan không khớp clip nguồn '{sourceClipId}'.");
                }

                for (var index = 0; index < fragments.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fragment = PremiereXmlGenerator.CreateFragmentElement(
                        sourceClip,
                        fragments[index],
                        firstFragment: index == 0);
                    await fragment.WriteToAsync(writer, cancellationToken);
                    emittedFragments++;
                }
            }
            else
            {
                await node.WriteToAsync(writer, cancellationToken);
            }
        }

        if (emittedSourceClips.Count != projectClips.Count)
        {
            throw new InvalidDataException($"Streaming writer không phát đủ clip nguồn trên track {projectTrack.Index}.");
        }

        await writer.WriteEndElementAsync();
        return emittedFragments;
    }

    private static IEnumerable<XNode> SignificantNodes(XContainer parent) =>
        parent.Nodes().Where(node => node is not XText text || !string.IsNullOrWhiteSpace(text.Value));

    private static void WriteStartElement(XmlWriter writer, XElement element)
    {
        writer.WriteStartElement(null, element.Name.LocalName, element.Name.NamespaceName);
        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
            {
                var isDefault = attribute.Name.LocalName == "xmlns";
                writer.WriteAttributeString(
                    isDefault ? null : "xmlns",
                    isDefault ? "xmlns" : attribute.Name.LocalName,
                    XNamespace.Xmlns.NamespaceName,
                    attribute.Value);
            }
            else
            {
                writer.WriteAttributeString(
                    element.GetPrefixOfNamespace(attribute.Name.Namespace),
                    attribute.Name.LocalName,
                    attribute.Name.NamespaceName,
                    attribute.Value);
            }
        }
    }

    private static void SetOrAddSequenceUuid(XElement sequence, string uuid)
    {
        var element = sequence.Element("uuid");
        if (element is not null)
        {
            element.Value = uuid;
        }
        else
        {
            sequence.AddFirst(new XElement("uuid", uuid));
        }
    }

    private static void RemoveInsignificantWhitespace(XDocument document)
    {
        foreach (var whitespace in document.DescendantNodes()
                     .OfType<XText>()
                     .Where(text => string.IsNullOrWhiteSpace(text.Value))
                     .ToArray())
        {
            whitespace.Remove();
        }
    }

    private static XElement RequiredElement(XElement parent, string name) =>
        parent.Element(name) ?? throw new InvalidDataException($"XML nguồn thiếu phần tử {name}.");

    private static string RequiredAttribute(XElement element, string name) =>
        (string?)element.Attribute(name) is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"XML nguồn thiếu attribute {name}.");
}
