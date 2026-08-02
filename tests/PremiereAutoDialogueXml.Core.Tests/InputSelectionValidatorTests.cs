using PremiereAutoDialogueXml.Core.Validation;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class InputSelectionValidatorTests
{
    [TestMethod]
    public void ValidSelectionWithUnicodePathPasses()
    {
        var facts = new InputSelectionFacts(
            XmlPath: @"F:\Dựng phim\Tập 01\line nghệ sĩ.xml",
            XmlExists: true,
            XmlLength: 42,
            OutputDirectory: @"F:\Dựng phim\Kết quả",
            OutputDirectoryExists: true);

        var result = InputSelectionValidator.Validate(facts);

        Assert.IsTrue(result.IsValid);
        Assert.HasCount(0, result.Issues);
    }

    [TestMethod]
    public void MissingSelectionsReturnActionableIssues()
    {
        var facts = new InputSelectionFacts(null, false, 0, null, false);

        var result = InputSelectionValidator.Validate(facts);

        Assert.IsFalse(result.IsValid);
        CollectionAssert.AreEquivalent(
            new[] { "xml-required", "output-required" },
            result.Issues.Select(issue => issue.Code).ToArray());
    }

    [TestMethod]
    public void WrongExtensionIsRejectedEvenWhenFileExists()
    {
        var facts = new InputSelectionFacts(
            @"F:\demo\show.prproj",
            true,
            128,
            @"F:\demo\output",
            true);

        var result = InputSelectionValidator.Validate(facts);

        Assert.IsFalse(result.IsValid);
        Assert.HasCount(1, result.Issues.Where(issue => issue.Code == "xml-extension-invalid"));
    }

    [TestMethod]
    public void EmptyXmlIsRejected()
    {
        var facts = new InputSelectionFacts(
            @"F:\demo\empty.xml",
            true,
            0,
            @"F:\demo\output",
            true);

        var result = InputSelectionValidator.Validate(facts);

        Assert.IsFalse(result.IsValid);
        Assert.HasCount(1, result.Issues.Where(issue => issue.Code == "xml-empty"));
    }

    [TestMethod]
    public void MissingOutputDirectoryIsRejected()
    {
        var facts = new InputSelectionFacts(
            @"F:\demo\show.xml",
            true,
            128,
            @"F:\demo\missing",
            false);

        var result = InputSelectionValidator.Validate(facts);

        Assert.IsFalse(result.IsValid);
        Assert.HasCount(1, result.Issues.Where(issue => issue.Code == "output-not-found"));
    }
}
