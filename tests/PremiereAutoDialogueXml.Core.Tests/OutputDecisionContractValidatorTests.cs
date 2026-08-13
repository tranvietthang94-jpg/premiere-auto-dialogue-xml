using PremiereAutoDialogueXml.Audio.Analysis;
using PremiereAutoDialogueXml.Core.Domain;
using PremiereAutoDialogueXml.Output.Validation;
using PremiereAutoDialogueXml.Output.Xml;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class OutputDecisionContractValidatorTests
{
    [TestMethod]
    public void ValidateAcceptsCurrentLockedContract()
    {
        using var fixture = PremiereXmlGeneratorTests.WriterFixture.Create();
        var generated = new PremiereXmlGenerator().GeneratePlan(fixture.Project, fixture.Analysis);

        new OutputDecisionContractValidator().Validate(
            fixture.Project,
            fixture.Analysis,
            DialogueProcessingPreset.Balanced,
            generated);
    }

    [TestMethod]
    public void ValidateRejectsDisabledSpeech()
    {
        using var fixture = PremiereXmlGeneratorTests.WriterFixture.Create();
        var generated = new PremiereXmlGenerator().GeneratePlan(fixture.Project, fixture.Analysis);
        var fragments = generated.AudioFragments.ToArray();
        var position = Array.FindIndex(fragments, fragment => fragment.Enabled);
        fragments[position] = fragments[position] with
        {
            Status = AudioSegmentStatus.Speech,
            Enabled = false
        };
        var tampered = generated with { AudioFragments = fragments };

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new OutputDecisionContractValidator().Validate(
                fixture.Project,
                fixture.Analysis,
                DialogueProcessingPreset.Balanced,
                tampered));
    }

    [TestMethod]
    public void ValidateRejectsMissingAmbiguousMarker()
    {
        using var fixture = PremiereXmlGeneratorTests.WriterFixture.Create();
        var generated = new PremiereXmlGenerator().GeneratePlan(fixture.Project, fixture.Analysis);
        var tampered = generated with
        {
            Markers = generated.Markers.Where(marker => marker.Name != "Cần kiểm tra").ToArray()
        };

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new OutputDecisionContractValidator().Validate(
                fixture.Project,
                fixture.Analysis,
                DialogueProcessingPreset.Balanced,
                tampered));
    }

    [TestMethod]
    public void ValidateRejectsStalePhraseGainMath()
    {
        using var fixture = PremiereXmlGeneratorTests.WriterFixture.Create();
        var track = fixture.Analysis.Tracks.Single();
        var analysis = fixture.Analysis with
        {
            Tracks =
            [
                track with
                {
                    Phrases = track.Phrases.Select(phrase => phrase with
                    {
                        RequiredGainDb = phrase.RequiredGainDb + 1
                    }).ToArray()
                }
            ]
        };
        var generated = new PremiereXmlGenerator().GeneratePlan(fixture.Project, analysis);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new OutputDecisionContractValidator().Validate(
                fixture.Project,
                analysis,
                DialogueProcessingPreset.Balanced,
                generated));
    }

    [TestMethod]
    public void ValidateAllowsPhraseSupersededByFrameAlignedAmbiguousCoverage()
    {
        using var fixture = PremiereXmlGeneratorTests.WriterFixture.Create();
        var generated = new PremiereXmlGenerator().GeneratePlan(fixture.Project, fixture.Analysis);
        var fragments = generated.AudioFragments.Select(fragment =>
            fragment.PhraseId is null
                ? fragment
                : fragment with
                {
                    Status = AudioSegmentStatus.Ambiguous,
                    Enabled = true,
                    PhraseId = null,
                    GainDb = 0,
                    Reason = "ambiguous-frame-supersedes-phrase"
                }).ToArray();
        var markers = fragments
            .Where(fragment => fragment.Status == AudioSegmentStatus.Ambiguous)
            .Select(fragment => new GeneratedSequenceMarker(
                "Cần kiểm tra",
                "test",
                fragment.TimelineStartFrame,
                fragment.TimelineEndFrame,
                fragment.TrackIndex,
                fragment.Reason))
            .ToArray();
        var superseded = generated with { AudioFragments = fragments, Markers = markers };

        new OutputDecisionContractValidator().Validate(
            fixture.Project,
            fixture.Analysis,
            DialogueProcessingPreset.Balanced,
            superseded);
    }

    [TestMethod]
    public void ValidateRejectsPhraseSupersededByDisabledFrame()
    {
        using var fixture = PremiereXmlGeneratorTests.WriterFixture.Create();
        var generated = new PremiereXmlGenerator().GeneratePlan(fixture.Project, fixture.Analysis);
        var fragments = generated.AudioFragments.Select(fragment =>
            fragment.PhraseId is null
                ? fragment
                : fragment with
                {
                    Status = AudioSegmentStatus.Noise,
                    Enabled = false,
                    PhraseId = null,
                    GainDb = null,
                    Reason = "vad-negative-noise"
                }).ToArray();
        var markers = generated.Markers.Where(marker => marker.Name != "Cần kiểm tra").ToArray();
        var superseded = generated with { AudioFragments = fragments, Markers = markers };

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new OutputDecisionContractValidator().Validate(
                fixture.Project,
                fixture.Analysis,
                DialogueProcessingPreset.Balanced,
                superseded));
    }
}
