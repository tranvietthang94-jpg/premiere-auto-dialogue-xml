using System.Xml.Linq;
using PremiereAutoDialogueXml.Output.Validation;
using PremiereAutoDialogueXml.Output.Xml;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class OutputXmlContractValidatorTests
{
    [TestMethod]
    public void ValidateAcceptsGeneratedDocument()
    {
        using var fixture = PremiereXmlGeneratorTests.WriterFixture.Create();
        var generated = new PremiereXmlGenerator().Generate(fixture.Project, fixture.Analysis);

        new OutputXmlContractValidator().Validate(fixture.Project, generated, new XDocument(generated.Document));
    }

    [TestMethod]
    public void ValidateRejectsVideoMutation()
    {
        using var fixture = PremiereXmlGeneratorTests.WriterFixture.Create();
        var generated = new PremiereXmlGenerator().Generate(fixture.Project, fixture.Analysis);
        var tampered = new XDocument(generated.Document);
        tampered.Descendants("video").First().Descendants("width").Single().Value = "1920";

        Assert.ThrowsExactly<InvalidDataException>(
            () => new OutputXmlContractValidator().Validate(fixture.Project, generated, tampered));
    }

    [TestMethod]
    public void ValidateRejectsRoutingMutation()
    {
        using var fixture = PremiereXmlGeneratorTests.WriterFixture.Create();
        var generated = new PremiereXmlGenerator().Generate(fixture.Project, fixture.Analysis);
        var tampered = new XDocument(generated.Document);
        tampered.Descendants("audio").First().Elements("track").Single()
            .SetAttributeValue("customRouting", "changed");

        Assert.ThrowsExactly<InvalidDataException>(
            () => new OutputXmlContractValidator().Validate(fixture.Project, generated, tampered));
    }

    [TestMethod]
    public void ValidateRejectsFragmentDecisionMutation()
    {
        using var fixture = PremiereXmlGeneratorTests.WriterFixture.Create();
        var generated = new PremiereXmlGenerator().Generate(fixture.Project, fixture.Analysis);
        var tampered = new XDocument(generated.Document);
        var firstId = generated.AudioFragments[0].ClipItemId;
        tampered.Descendants("clipitem")
            .Single(item => (string?)item.Attribute("id") == firstId)
            .Element("enabled")!.Value = "TRUE";

        Assert.ThrowsExactly<InvalidDataException>(
            () => new OutputXmlContractValidator().Validate(fixture.Project, generated, tampered));
    }

    [TestMethod]
    public void ValidateRejectsMarkerMutation()
    {
        using var fixture = PremiereXmlGeneratorTests.WriterFixture.Create();
        var generated = new PremiereXmlGenerator().Generate(fixture.Project, fixture.Analysis);
        var tampered = new XDocument(generated.Document);
        tampered.Root!.Element("sequence")!.Elements("marker").Single()
            .Element("comment")!.Value = "changed";

        Assert.ThrowsExactly<InvalidDataException>(
            () => new OutputXmlContractValidator().Validate(fixture.Project, generated, tampered));
    }
}
