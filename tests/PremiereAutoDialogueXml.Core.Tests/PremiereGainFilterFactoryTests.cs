using PremiereAutoDialogueXml.Output.Gain;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class PremiereGainFilterFactoryTests
{
    [TestMethod]
    public void CreateFiltersEncodesReductionAsOneLinearAudioLevel()
    {
        var filters = PremiereGainFilterFactory.CreateFilters(-6);

        Assert.HasCount(1, filters);
        Assert.AreEqual("audiolevels", filters[0].Descendants("effectid").Single().Value);
        Assert.AreEqual("0.501187234", filters[0].Descendants("value").Single().Value);
    }

    [TestMethod]
    public void CreateFiltersEncodesTwelveDbAsOneAudioLevel()
    {
        var filters = PremiereGainFilterFactory.CreateFilters(12);

        Assert.HasCount(1, filters);
        Assert.AreEqual("3.981071706", filters[0].Descendants("value").Single().Value);
    }

    [TestMethod]
    public void CreateFiltersEncodesEighteenDbAsAcceptedLevelPlusGainFactor()
    {
        var filters = PremiereGainFilterFactory.CreateFilters(18);

        Assert.HasCount(2, filters);
        Assert.AreEqual(PremiereGainFilterFactory.PremiereGainEffectId, filters[0].Descendants("effectid").Single().Value);
        Assert.AreEqual("1.995262315", filters[0].Descendants("value").Single().Value);
        Assert.AreEqual("audiolevels", filters[1].Descendants("effectid").Single().Value);
        Assert.AreEqual("3.981071706", filters[1].Descendants("value").Single().Value);
    }

    [TestMethod]
    public void CreateFiltersRejectsBoostAboveLockedMaximum()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PremiereGainFilterFactory.CreateFilters(18.1));
    }
}
