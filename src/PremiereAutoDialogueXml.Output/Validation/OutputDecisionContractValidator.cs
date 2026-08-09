using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Core.ProjectModel;
using PremiereAutoDialogueXml.Output.Xml;

namespace PremiereAutoDialogueXml.Output.Validation;

public sealed class OutputDecisionContractValidator
{
    private const float GainToleranceDb = 0.001f;

    public void Validate(
        PremiereProject project,
        ProjectAudioAnalysis analysis,
        DialogueProcessingPreset preset,
        GeneratedPremiereXmlPlan generated)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(generated);

        var phrases = analysis.Tracks
            .SelectMany(track => track.Phrases)
            .ToDictionary(phrase => phrase.Id, StringComparer.Ordinal);
        ValidatePhrases(phrases.Values, preset);
        ValidateFragments(project, generated.AudioFragments, phrases, preset);
        ValidateMarkers(generated, phrases.Values, project.Sequence.FrameRate);
    }

    private static void ValidatePhrases(
        IEnumerable<DialoguePhrase> phrases,
        DialogueProcessingPreset preset)
    {
        foreach (var phrase in phrases)
        {
            Require(
                float.IsFinite(phrase.MeasuredPeakDbfs) &&
                float.IsFinite(phrase.RequiredGainDb) &&
                float.IsFinite(phrase.AppliedGainDb),
                $"Phrase '{phrase.Id}' có peak/gain không hữu hạn.");
            var required = (float)(
                preset.TargetSamplePeakDbfs -
                phrase.MeasuredPeakDbfs +
                preset.PremiereCenterPanCompensationDb);
            var applied = MathF.Min(required, (float)preset.MaximumBoostDb);
            Require(
                Math.Abs(phrase.RequiredGainDb - required) <= GainToleranceDb,
                $"Phrase '{phrase.Id}' có required gain sai hợp đồng hậu routing.");
            Require(
                Math.Abs(phrase.AppliedGainDb - applied) <= GainToleranceDb,
                $"Phrase '{phrase.Id}' có applied gain sai hoặc vượt +{preset.MaximumBoostDb:0.#} dB.");
            Require(
                phrase.GainWasCapped == (required > preset.MaximumBoostDb),
                $"Phrase '{phrase.Id}' có cờ gain-capped không khớp.");
            var predictedPostRoutingPeak =
                phrase.MeasuredPeakDbfs + phrase.AppliedGainDb - (float)preset.PremiereCenterPanCompensationDb;
            Require(
                predictedPostRoutingPeak <= preset.TargetSamplePeakDbfs + GainToleranceDb,
                $"Phrase '{phrase.Id}' dự đoán nóng hơn target hậu routing.");
            if (!phrase.GainWasCapped)
            {
                Require(
                    Math.Abs(predictedPostRoutingPeak - preset.TargetSamplePeakDbfs) <= GainToleranceDb,
                    $"Phrase '{phrase.Id}' không cap nhưng không đạt target dự đoán.");
            }
        }
    }

    private static void ValidateFragments(
        PremiereProject project,
        IReadOnlyList<GeneratedAudioFragment> fragments,
        IReadOnlyDictionary<string, DialoguePhrase> phrases,
        DialogueProcessingPreset preset)
    {
        var referencedPhraseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fragment in fragments)
        {
            var shouldBeEnabled = fragment.Status is AudioSegmentStatus.Speech or AudioSegmentStatus.Ambiguous;
            Require(
                fragment.Enabled == shouldBeEnabled,
                $"Fragment '{fragment.ClipItemId}' có Enabled không khớp status {fragment.Status}.");
            Require(
                fragment.TimelineEndFrame > fragment.TimelineStartFrame &&
                fragment.SourceEndSample >= fragment.SourceStartSample,
                $"Fragment '{fragment.ClipItemId}' có range không hợp lệ.");

            if (fragment.PhraseId is { } phraseId)
            {
                Require(
                    phrases.TryGetValue(phraseId, out var phrase),
                    $"Fragment '{fragment.ClipItemId}' trỏ phrase không tồn tại.");
                Require(
                    fragment.GainDb is { } gain &&
                    double.IsFinite(gain) &&
                    Math.Abs(gain - phrase!.AppliedGainDb) <= GainToleranceDb &&
                    gain <= preset.MaximumBoostDb + GainToleranceDb,
                    $"Fragment '{fragment.ClipItemId}' có gain không khớp phrase.");
                referencedPhraseIds.Add(phraseId);
            }
            else
            {
                Require(
                    fragment.Status != AudioSegmentStatus.Speech,
                    $"Fragment speech '{fragment.ClipItemId}' thiếu phrase.");
                Require(
                    fragment.GainDb is null ||
                    (double.IsFinite(fragment.GainDb.Value) && Math.Abs(fragment.GainDb.Value) <= GainToleranceDb),
                    $"Fragment không phrase '{fragment.ClipItemId}' phải giữ unity gain.");
            }

            if (!fragment.Enabled)
            {
                Require(
                    fragment.Status is AudioSegmentStatus.Noise or AudioSegmentStatus.Bleed,
                    $"Chỉ noise/bleed mới được Disable: '{fragment.ClipItemId}'.");
            }
        }

        Require(
            referencedPhraseIds.SetEquals(phrases.Keys),
            "Generation plan không tham chiếu đúng toàn bộ phrase.");

        foreach (var track in project.Sequence.AudioTracks)
        {
            foreach (var clip in track.Clips)
            {
                var clipFragments = fragments
                    .Where(fragment => fragment.SourceClipId == clip.Id)
                    .OrderBy(fragment => fragment.TimelineStartFrame)
                    .ToArray();
                Require(clipFragments.Length > 0, $"Clip '{clip.Id}' không có fragment.");
                var cursor = clip.TimelineStartFrame;
                foreach (var fragment in clipFragments)
                {
                    Require(
                        fragment.TimelineStartFrame == cursor,
                        $"Clip '{clip.Id}' có gap/overlap fragment.");
                    cursor = fragment.TimelineEndFrame;
                }

                Require(cursor == clip.TimelineEndFrame, $"Clip '{clip.Id}' không được phủ đủ frame.");
            }
        }
    }

    private static void ValidateMarkers(
        GeneratedPremiereXmlPlan generated,
        IEnumerable<DialoguePhrase> phrases,
        int frameRate)
    {
        var expectedAmbiguous = generated.AudioFragments
            .Where(fragment => fragment.Status == AudioSegmentStatus.Ambiguous)
            .Select(fragment => string.Join(
                "\u001f",
                fragment.TrackIndex,
                fragment.TimelineStartFrame,
                fragment.TimelineEndFrame,
                fragment.Reason))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualAmbiguous = generated.Markers
            .Where(marker => marker.Name == "Cần kiểm tra")
            .Select(marker => string.Join(
                "\u001f",
                marker.TrackIndex,
                marker.InFrame,
                marker.OutFrame,
                marker.Reason))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Require(
            expectedAmbiguous.SequenceEqual(actualAmbiguous, StringComparer.Ordinal),
            "Marker Cần kiểm tra không phủ đúng fragment ambiguous.");

        const int sampleRate = 48_000;
        Require(sampleRate % frameRate == 0, "Sample rate/frame rate không hỗ trợ marker gain-capped.");
        var samplesPerFrame = sampleRate / frameRate;
        var expectedGainCapped = phrases
            .Where(phrase => phrase.GainWasCapped)
            .Select(phrase => string.Join(
                "\u001f",
                phrase.TrackIndex,
                phrase.CoreStartSample / samplesPerFrame,
                Math.Max(
                    phrase.CoreStartSample / samplesPerFrame + 1,
                    (phrase.CoreEndSample + samplesPerFrame - 1) / samplesPerFrame)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualGainCapped = generated.Markers
            .Where(marker => marker.Reason == "gain-capped")
            .Select(marker => string.Join(
                "\u001f",
                marker.TrackIndex,
                marker.InFrame,
                marker.OutFrame))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Require(
            expectedGainCapped.SequenceEqual(actualGainCapped, StringComparer.Ordinal),
            "Marker gain-capped không phủ đúng phrase bị giới hạn.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
