using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Core.ProjectModel;
using PremiereAutoDialogueXml.Core.Xml;
using PremiereAutoDialogueXml.Output.Gain;

namespace PremiereAutoDialogueXml.Output.Xml;

public sealed class PremiereXmlGenerator
{
    private const int SampleRate = 48_000;
    private const int FrameRate = 25;
    private const int SamplesPerFrame = SampleRate / FrameRate;
    private readonly Func<Guid> _guidFactory;

    public PremiereXmlGenerator(Func<Guid>? guidFactory = null)
    {
        _guidFactory = guidFactory ?? Guid.NewGuid;
    }

    public GeneratedPremiereXml Generate(
        PremiereProject project,
        ProjectAudioAnalysis analysis,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(analysis);
        ValidateAnalysisContract(project, analysis);

        var source = PremiereXmlDocumentLoader.Load(project.SourceXmlPath, project.SourceXmlSha256);
        var document = source.Document;
        var root = document.Root ?? throw new InvalidDataException("XML nguồn không có root xmeml.");
        var sequence = root.Elements("sequence").Single();
        var usedIds = document.Descendants()
            .Attributes("id")
            .Select(attribute => attribute.Value)
            .ToHashSet(StringComparer.Ordinal);
        var sequenceId = NewUniqueId("sequence-auto", usedIds);
        var sequenceUuid = _guidFactory().ToString();
        var sequenceName = $"{project.Sequence.Name} - AUTO AUDIO";

        sequence.SetAttributeValue("id", sequenceId);
        SetOrAddSequenceUuid(sequence, sequenceUuid);
        RequiredElement(sequence, "name").Value = sequenceName;

        var audio = RequiredElement(RequiredElement(sequence, "media"), "audio");
        var trackElements = audio.Elements("track").ToArray();
        if (trackElements.Length != project.Sequence.AudioTracks.Count)
        {
            throw new InvalidDataException("Số audio track trong XML clone không khớp project đã kiểm tra.");
        }

        var generatedFragments = new List<GeneratedAudioFragment>();
        var generatedMarkers = new List<GeneratedSequenceMarker>();
        var analysesByTrack = analysis.Tracks.ToDictionary(track => track.TrackIndex);

        for (var trackPosition = 0; trackPosition < project.Sequence.AudioTracks.Count; trackPosition++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectTrack = project.Sequence.AudioTracks[trackPosition];
            var analysisTrack = analysesByTrack[projectTrack.Index];
            RewriteTrack(
                trackElements[trackPosition],
                projectTrack,
                analysisTrack,
                usedIds,
                generatedFragments,
                generatedMarkers,
                cancellationToken);
        }

        AddGainCapMarkers(analysis, generatedMarkers);
        InsertMarkers(sequence, generatedMarkers);
        EnsureDocumentType(document);
        RemoveInsignificantWhitespace(document);

        return new(
            document,
            sequenceId,
            sequenceUuid,
            sequenceName,
            generatedFragments,
            generatedMarkers);
    }

    public GeneratedPremiereXmlPlan GeneratePlan(
        PremiereProject project,
        ProjectAudioAnalysis analysis,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(analysis);
        ValidateAnalysisContract(project, analysis);

        var source = PremiereXmlDocumentLoader.Load(project.SourceXmlPath, project.SourceXmlSha256);
        var document = source.Document;
        var sequence = document.Root?.Elements("sequence").SingleOrDefault()
            ?? throw new InvalidDataException("XML nguồn không có đúng một sequence.");
        var audio = RequiredElement(RequiredElement(sequence, "media"), "audio");
        var trackElements = audio.Elements("track").ToArray();
        if (trackElements.Length != project.Sequence.AudioTracks.Count)
        {
            throw new InvalidDataException("Số audio track trong XML clone không khớp project đã kiểm tra.");
        }

        var usedIds = document.Descendants()
            .Attributes("id")
            .Select(attribute => attribute.Value)
            .ToHashSet(StringComparer.Ordinal);
        var sequenceId = NewUniqueId("sequence-auto", usedIds);
        var sequenceUuid = _guidFactory().ToString();
        var sequenceName = $"{project.Sequence.Name} - AUTO AUDIO";
        var generatedFragments = new List<GeneratedAudioFragment>();
        var generatedMarkers = new List<GeneratedSequenceMarker>();
        var analysesByTrack = analysis.Tracks.ToDictionary(track => track.TrackIndex);

        for (var trackPosition = 0; trackPosition < project.Sequence.AudioTracks.Count; trackPosition++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectTrack = project.Sequence.AudioTracks[trackPosition];
            var analysisTrack = analysesByTrack[projectTrack.Index];
            var sourceClipIds = trackElements[trackPosition].Elements("clipitem")
                .Select(element => RequiredAttribute(element, "id"))
                .ToHashSet(StringComparer.Ordinal);
            if (sourceClipIds.Count != projectTrack.Clips.Count)
            {
                throw new InvalidDataException($"Track {projectTrack.Index} có số clip XML khác project đã kiểm tra.");
            }

            foreach (var clip in projectTrack.Clips)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!sourceClipIds.Contains(clip.Id))
                {
                    throw new InvalidDataException($"Không tìm thấy clip nguồn '{clip.Id}' trong XML clone.");
                }

                var segments = analysisTrack.Segments
                    .Where(segment => segment.SourceClipId == clip.Id)
                    .OrderBy(segment => segment.TimelineStartSample)
                    .ToArray();
                ValidateSegmentCoverage(clip, segments);
                foreach (var run in AlignToFrames(clip, segments, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fragment = CreateFragmentAudit(
                        clip,
                        run,
                        NewUniqueId("clipitem-auto", usedIds));
                    generatedFragments.Add(fragment);
                    if (run.Decision.Status == AudioSegmentStatus.Ambiguous)
                    {
                        generatedMarkers.Add(new(
                            "Cần kiểm tra",
                            $"Track {projectTrack.Index} · {run.Decision.Reason}",
                            run.StartFrame,
                            run.EndFrame,
                            projectTrack.Index,
                            run.Decision.Reason));
                    }
                }
            }
        }

        AddGainCapMarkers(analysis, generatedMarkers);
        return new(
            sequenceId,
            sequenceUuid,
            sequenceName,
            generatedFragments,
            generatedMarkers);
    }

    private void RewriteTrack(
        XElement trackElement,
        PremiereAudioTrack projectTrack,
        TrackAudioAnalysis analysisTrack,
        ISet<string> usedIds,
        ICollection<GeneratedAudioFragment> generatedFragments,
        ICollection<GeneratedSequenceMarker> generatedMarkers,
        CancellationToken cancellationToken)
    {
        var sourceElements = trackElement.Elements("clipitem")
            .ToDictionary(
                element => RequiredAttribute(element, "id"),
                StringComparer.Ordinal);
        if (sourceElements.Count != projectTrack.Clips.Count)
        {
            throw new InvalidDataException($"Track {projectTrack.Index} có số clip XML khác project đã kiểm tra.");
        }

        foreach (var clip in projectTrack.Clips)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sourceElements.TryGetValue(clip.Id, out var sourceClipElement))
            {
                throw new InvalidDataException($"Không tìm thấy clip nguồn '{clip.Id}' trong XML clone.");
            }

            var segments = analysisTrack.Segments
                .Where(segment => segment.SourceClipId == clip.Id)
                .OrderBy(segment => segment.TimelineStartSample)
                .ToArray();
            ValidateSegmentCoverage(clip, segments);
            var runs = AlignToFrames(clip, segments, cancellationToken);
            var firstFragment = true;

            foreach (var run in runs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var clipItemId = NewUniqueId("clipitem-auto", usedIds);
                var fragment = CreateFragmentAudit(clip, run, clipItemId);
                var fragmentElement = CreateFragmentElement(
                    sourceClipElement,
                    fragment,
                    firstFragment);
                sourceClipElement.AddBeforeSelf(fragmentElement);
                firstFragment = false;

                generatedFragments.Add(fragment);
                if (run.Decision.Status == AudioSegmentStatus.Ambiguous)
                {
                    generatedMarkers.Add(new(
                        "Cần kiểm tra",
                        $"Track {projectTrack.Index} · {run.Decision.Reason}",
                        run.StartFrame,
                        run.EndFrame,
                        projectTrack.Index,
                        run.Decision.Reason));
                }
            }

            sourceClipElement.Remove();
        }
    }

    private static IReadOnlyList<FrameRun> AlignToFrames(
        PremiereAudioClip clip,
        IReadOnlyList<AnalyzedAudioSegment> segments,
        CancellationToken cancellationToken)
    {
        var frameCountLong = clip.TimelineEndFrame - clip.TimelineStartFrame;
        if (frameCountLong <= 0 || frameCountLong > int.MaxValue)
        {
            throw new InvalidDataException($"Clip '{clip.Id}' có duration frame không hỗ trợ.");
        }

        var frameCount = checked((int)frameCountLong);
        var decisions = new WeightedFrameDecision[frameCount];
        var clipStartSample = checked(clip.TimelineStartFrame * SamplesPerFrame);

        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var startOffset = Math.Max(0, segment.TimelineStartSample - clipStartSample);
            var endOffset = Math.Min(
                checked(frameCountLong * SamplesPerFrame),
                segment.TimelineEndSample - clipStartSample);
            var firstFrame = checked((int)(startOffset / SamplesPerFrame));
            var lastFrameExclusive = checked((int)Math.Min(
                frameCountLong,
                (endOffset + SamplesPerFrame - 1) / SamplesPerFrame));
            var candidate = FrameDecision.FromSegment(segment);

            for (var frame = firstFrame; frame < lastFrameExclusive; frame++)
            {
                var frameStart = checked((long)frame * SamplesPerFrame);
                var frameEnd = frameStart + SamplesPerFrame;
                var overlap = Math.Max(0, Math.Min(endOffset, frameEnd) - Math.Max(startOffset, frameStart));
                var existing = decisions[frame];
                if (!existing.IsAssigned ||
                    candidate.Priority > existing.Decision.Priority ||
                    (candidate.Priority == existing.Decision.Priority && overlap > existing.OverlapSamples))
                {
                    decisions[frame] = new(candidate, overlap, IsAssigned: true);
                }
            }
        }

        if (decisions.Any(decision => !decision.IsAssigned))
        {
            throw new InvalidDataException($"Phân tích không phủ kín toàn bộ frame của clip '{clip.Id}'.");
        }

        var runs = new List<FrameRun>();
        var runStart = 0;
        for (var frame = 1; frame <= decisions.Length; frame++)
        {
            if (frame < decisions.Length && decisions[frame].Decision.Key == decisions[runStart].Decision.Key)
            {
                continue;
            }

            runs.Add(new(
                clip.TimelineStartFrame + runStart,
                clip.TimelineStartFrame + frame,
                decisions[runStart].Decision));
            runStart = frame;
        }

        return runs;
    }

    internal static XElement CreateFragmentElement(
        XElement source,
        GeneratedAudioFragment generated,
        bool firstFragment)
    {
        var fragment = new XElement(source);
        fragment.SetAttributeValue("id", generated.ClipItemId);
        SetValue(fragment, "enabled", generated.Enabled ? "TRUE" : "FALSE");
        SetValue(fragment, "start", Format(generated.TimelineStartFrame));
        SetValue(fragment, "end", Format(generated.TimelineEndFrame));
        SetValue(fragment, "in", Format(generated.SourceInFrame));
        SetValue(fragment, "out", Format(generated.SourceOutFrame));
        SetOrInsertBeforeFile(fragment, "pproTicksIn", Format(generated.PproTicksIn));
        SetOrInsertBeforeFile(fragment, "pproTicksOut", Format(generated.PproTicksOut));

        foreach (var filter in fragment.Elements("filter").ToArray())
        {
            filter.Remove();
        }

        if (!firstFragment)
        {
            foreach (var metadata in fragment.Elements().Where(
                         element => element.Name.LocalName is "logginginfo" or "colorinfo" or "marker").ToArray())
            {
                metadata.Remove();
            }

            var file = fragment.Element("file");
            if (file is not null)
            {
                file.ReplaceWith(new XElement("file", new XAttribute("id", generated.SourceFileId)));
            }
        }

        if (generated.Enabled)
        {
            var gainDb = generated.GainDb ?? 0;
            var filters = PremiereGainFilterFactory.CreateFilters(gainDb);
            var sourceTrack = fragment.Element("sourcetrack");
            if (sourceTrack is not null)
            {
                sourceTrack.AddBeforeSelf(filters.Select(filter => new XElement(filter)));
            }
            else
            {
                fragment.Add(filters.Select(filter => new XElement(filter)));
            }
        }

        return fragment;
    }

    private static GeneratedAudioFragment CreateFragmentAudit(
        PremiereAudioClip clip,
        FrameRun run,
        string clipItemId)
    {
        var totalFrames = clip.TimelineEndFrame - clip.TimelineStartFrame;
        var startOffset = run.StartFrame - clip.TimelineStartFrame;
        var endOffset = run.EndFrame - clip.TimelineStartFrame;
        var sourceFrameDuration = clip.SourceOutFrame - clip.SourceInFrame;
        var sourceSampleDuration = clip.SourceEndSample - clip.SourceStartSample;
        var sourceIn = startOffset == 0
            ? clip.SourceInFrame
            : clip.SourceInFrame + ScaleFloor(sourceFrameDuration, startOffset, totalFrames);
        var sourceOut = endOffset == totalFrames
            ? clip.SourceOutFrame
            : clip.SourceInFrame + ScaleCeiling(sourceFrameDuration, endOffset, totalFrames);
        var sourceStartSample = startOffset == 0
            ? clip.SourceStartSample
            : clip.SourceStartSample + ScaleFloor(sourceSampleDuration, startOffset, totalFrames);
        var sourceEndSample = endOffset == totalFrames
            ? clip.SourceEndSample
            : clip.SourceStartSample + ScaleCeiling(sourceSampleDuration, endOffset, totalFrames);

        return new(
            clipItemId,
            run.Decision.TrackIndex,
            clip.Id,
            clip.SourceFileId,
            Path.GetFileName(clip.SourceMedia.LocalPath),
            run.StartFrame,
            run.EndFrame,
            sourceIn,
            sourceOut,
            ScaleBoundary(clip.PproTicksIn, clip.PproTicksOut, startOffset, totalFrames),
            ScaleBoundary(clip.PproTicksIn, clip.PproTicksOut, endOffset, totalFrames),
            sourceStartSample,
            sourceEndSample,
            run.Decision.Status,
            run.Decision.Enabled,
            run.Decision.PhraseId,
            run.Decision.GainDb,
            run.Decision.Reason,
            run.Decision.BleedEvidence);
    }

    private static void AddGainCapMarkers(
        ProjectAudioAnalysis analysis,
        ICollection<GeneratedSequenceMarker> markers)
    {
        foreach (var track in analysis.Tracks)
        {
            foreach (var phrase in track.Phrases.Where(phrase => phrase.GainWasCapped))
            {
                var start = phrase.CoreStartSample / SamplesPerFrame;
                var end = Math.Max(start + 1, (phrase.CoreEndSample + SamplesPerFrame - 1) / SamplesPerFrame);
                markers.Add(new(
                    "Gain đã giới hạn +18 dB",
                    $"Track {track.TrackIndex} · cần {phrase.RequiredGainDb:0.0} dB · đã áp +18.0 dB",
                    start,
                    end,
                    track.TrackIndex,
                    "gain-capped"));
            }
        }
    }

    private static void InsertMarkers(XElement sequence, IEnumerable<GeneratedSequenceMarker> markers)
    {
        var media = RequiredElement(sequence, "media");
        foreach (var marker in markers.OrderBy(marker => marker.InFrame).ThenBy(marker => marker.TrackIndex))
        {
            media.AddBeforeSelf(new XElement(
                "marker",
                new XElement("name", marker.Name),
                new XElement("in", Format(marker.InFrame)),
                new XElement("out", Format(marker.OutFrame)),
                new XElement("comment", marker.Comment)));
        }
    }

    private static void ValidateAnalysisContract(PremiereProject project, ProjectAudioAnalysis analysis)
    {
        if (project.Sequence.FrameRate != FrameRate || project.Sequence.AudioSampleRate != SampleRate)
        {
            throw new InvalidDataException("Writer chỉ nhận project 25 fps/48 kHz đã qua Phase 02.");
        }

        if (analysis.Tracks.Count != project.Sequence.AudioTracks.Count ||
            analysis.Tracks.Select(track => track.TrackIndex).Distinct().Count() != analysis.Tracks.Count)
        {
            throw new InvalidDataException("Kết quả phân tích không khớp số/index audio track.");
        }

        var projectTrackIndexes = project.Sequence.AudioTracks.Select(track => track.Index).Order().ToArray();
        var analysisTrackIndexes = analysis.Tracks.Select(track => track.TrackIndex).Order().ToArray();
        if (!projectTrackIndexes.SequenceEqual(analysisTrackIndexes))
        {
            throw new InvalidDataException("Kết quả phân tích chứa track không thuộc XML nguồn.");
        }

        var phraseIds = analysis.Tracks.SelectMany(track => track.Phrases).Select(phrase => phrase.Id).ToArray();
        if (phraseIds.Any(string.IsNullOrWhiteSpace) ||
            phraseIds.Distinct(StringComparer.Ordinal).Count() != phraseIds.Length)
        {
            throw new InvalidDataException("Phrase ID trong kết quả phân tích bị trống hoặc trùng.");
        }

        foreach (var projectTrack in project.Sequence.AudioTracks)
        {
            var analysisTrack = analysis.Tracks.Single(track => track.TrackIndex == projectTrack.Index);
            var sourceClipIds = projectTrack.Clips.Select(clip => clip.Id).ToHashSet(StringComparer.Ordinal);
            if (analysisTrack.Phrases.Any(phrase => phrase.TrackIndex != projectTrack.Index) ||
                analysisTrack.Segments.Any(segment => segment.TrackIndex != projectTrack.Index))
            {
                throw new InvalidDataException($"Kết quả phân tích track {projectTrack.Index} chứa quyết định của track khác.");
            }

            if (analysisTrack.Segments.Any(segment => !sourceClipIds.Contains(segment.SourceClipId)))
            {
                throw new InvalidDataException($"Kết quả phân tích track {projectTrack.Index} trỏ clip không thuộc XML nguồn.");
            }

            var trackPhraseIds = analysisTrack.Phrases.Select(phrase => phrase.Id).ToHashSet(StringComparer.Ordinal);
            var referencedPhraseIds = analysisTrack.Segments
                .Where(segment => segment.PhraseId is not null)
                .Select(segment => segment.PhraseId!)
                .ToHashSet(StringComparer.Ordinal);
            if (!trackPhraseIds.SetEquals(referencedPhraseIds))
            {
                throw new InvalidDataException($"Phrase/segment trên track {projectTrack.Index} không tham chiếu đầy đủ lẫn nhau.");
            }

            foreach (var segment in analysisTrack.Segments)
            {
                var hasPhrase = segment.PhraseId is not null;
                if (hasPhrase && !trackPhraseIds.Contains(segment.PhraseId!))
                {
                    throw new InvalidDataException($"Segment trên track {projectTrack.Index} trỏ phrase không tồn tại.");
                }

                if (segment.Status == AudioSegmentStatus.Speech && !hasPhrase)
                {
                    throw new InvalidDataException($"Segment speech trên track {projectTrack.Index} thiếu phrase.");
                }

                if (segment.Status is AudioSegmentStatus.Noise or AudioSegmentStatus.Bleed && hasPhrase)
                {
                    throw new InvalidDataException($"Segment noise/bleed trên track {projectTrack.Index} không được mang phrase.");
                }

                if (hasPhrase)
                {
                    var phrase = analysisTrack.Phrases.Single(item => item.Id == segment.PhraseId);
                    if (segment.GainDb is null || !float.IsFinite(segment.GainDb.Value) ||
                        Math.Abs(segment.GainDb.Value - phrase.AppliedGainDb) > 0.001f)
                    {
                        throw new InvalidDataException($"Gain segment/phrase trên track {projectTrack.Index} không khớp.");
                    }
                }
                else if (segment.GainDb is { } gain && (!float.IsFinite(gain) || Math.Abs(gain) > 0.001f))
                {
                    throw new InvalidDataException($"Segment không có phrase trên track {projectTrack.Index} phải giữ unity gain.");
                }
            }
        }
    }

    private static void ValidateSegmentCoverage(
        PremiereAudioClip clip,
        IReadOnlyList<AnalyzedAudioSegment> segments)
    {
        if (segments.Count == 0)
        {
            throw new InvalidDataException($"Clip '{clip.Id}' không có quyết định phân tích.");
        }

        var expectedStart = checked(clip.TimelineStartFrame * SamplesPerFrame);
        var expectedEnd = checked(clip.TimelineEndFrame * SamplesPerFrame);
        var cursor = expectedStart;
        foreach (var segment in segments)
        {
            if (segment.TimelineStartSample != cursor || segment.TimelineEndSample <= segment.TimelineStartSample)
            {
                throw new InvalidDataException($"Segment của clip '{clip.Id}' không liên tục hoặc không hợp lệ.");
            }

            cursor = segment.TimelineEndSample;
        }

        if (cursor != expectedEnd)
        {
            throw new InvalidDataException($"Segment của clip '{clip.Id}' không phủ đúng timeline clip.");
        }
    }

    private string NewUniqueId(string prefix, ISet<string> usedIds)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var value = $"{prefix}-{_guidFactory():N}";
            if (usedIds.Add(value))
            {
                return value;
            }
        }

        throw new InvalidOperationException("Không thể tạo XML ID duy nhất sau 100 lần thử.");
    }

    private static void SetOrAddSequenceUuid(XElement sequence, string uuid)
    {
        var element = sequence.Element("uuid");
        if (element is not null)
        {
            element.Value = uuid;
            return;
        }

        sequence.AddFirst(new XElement("uuid", uuid));
    }

    private static void EnsureDocumentType(XDocument document)
    {
        document.DocumentType?.Remove();
        document.Root?.AddBeforeSelf(new XDocumentType("xmeml", null, null, null));
        document.Declaration = new("1.0", "UTF-8", null);
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

    private static long ScaleBoundary(long start, long end, long offset, long total)
    {
        if (offset == 0)
        {
            return start;
        }

        if (offset == total)
        {
            return end;
        }

        return checked(start + (long)((BigInteger)(end - start) * offset / total));
    }

    private static long ScaleFloor(long value, long numerator, long denominator) =>
        numerator == 0 ? 0 : checked((long)((BigInteger)value * numerator / denominator));

    private static long ScaleCeiling(long value, long numerator, long denominator)
    {
        if (numerator == 0)
        {
            return 0;
        }

        var scaled = (BigInteger)value * numerator;
        return checked((long)((scaled + denominator - 1) / denominator));
    }

    private static void SetValue(XElement parent, string name, string value)
    {
        var element = RequiredElement(parent, name);
        element.Value = value;
    }

    private static void SetOrInsertBeforeFile(XElement parent, string name, string value)
    {
        var element = parent.Element(name);
        if (element is not null)
        {
            element.Value = value;
            return;
        }

        var created = new XElement(name, value);
        var file = parent.Element("file");
        if (file is null)
        {
            parent.Add(created);
        }
        else
        {
            file.AddBeforeSelf(created);
        }
    }

    private static XElement RequiredElement(XElement parent, string name) =>
        parent.Element(name) ?? throw new InvalidDataException($"XML clone thiếu phần tử {name} trong {parent.Name.LocalName}.");

    private static string RequiredAttribute(XElement element, string name) =>
        (string?)element.Attribute(name) is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"XML clone thiếu attribute {name} trong {element.Name.LocalName}.");

    private static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed record FrameRun(long StartFrame, long EndFrame, FrameDecision Decision);

    private readonly record struct WeightedFrameDecision(
        FrameDecision Decision,
        long OverlapSamples,
        bool IsAssigned);

    private sealed record FrameDecision(
        int TrackIndex,
        AudioSegmentStatus Status,
        bool Enabled,
        string? PhraseId,
        double? GainDb,
        string Reason,
        BleedEvidence? BleedEvidence,
        int Priority,
        string Key)
    {
        public static FrameDecision FromSegment(AnalyzedAudioSegment segment)
        {
            var enabled = segment.Status is AudioSegmentStatus.Speech or AudioSegmentStatus.Ambiguous;
            double? gain = enabled ? segment.GainDb ?? 0 : null;
            var reason = segment.Status switch
            {
                AudioSegmentStatus.Speech => "speech",
                AudioSegmentStatus.Noise => "noise",
                AudioSegmentStatus.Bleed => segment.Reason,
                _ => segment.Reason
            };
            var priority = segment.Status switch
            {
                AudioSegmentStatus.Ambiguous => 4,
                AudioSegmentStatus.Speech => 3,
                AudioSegmentStatus.Bleed => 2,
                _ => 1
            };
            var key = string.Create(
                CultureInfo.InvariantCulture,
                $"{segment.Status}|{segment.PhraseId}|{gain:R}|{reason}|{segment.BleedEvidence?.OtherTrackIndex}");
            return new(
                segment.TrackIndex,
                segment.Status,
                enabled,
                segment.PhraseId,
                gain,
                reason,
                segment.BleedEvidence,
                priority,
                key);
        }
    }
}
