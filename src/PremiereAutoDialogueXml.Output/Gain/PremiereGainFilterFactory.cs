using System.Globalization;
using System.Xml.Linq;

namespace PremiereAutoDialogueXml.Output.Gain;

public static class PremiereGainFilterFactory
{
    public const double SplitThresholdDb = 12.0;
    public const double MaximumBoostDb = 18.0;
    public const string PremiereGainEffectId = "{61756678, 4761696e, 4b657947}";

    public static IReadOnlyList<XElement> CreateFilters(double gainDb)
    {
        if (!double.IsFinite(gainDb) || gainDb > MaximumBoostDb + 0.0001)
        {
            throw new ArgumentOutOfRangeException(nameof(gainDb), "Gain XML phải hữu hạn và không vượt +18 dB.");
        }

        if (gainDb <= SplitThresholdDb)
        {
            return [CreateAudioLevels(gainDb)];
        }

        var remainderDb = gainDb - SplitThresholdDb;
        return
        [
            CreatePremiereGain(remainderDb),
            CreateAudioLevels(SplitThresholdDb)
        ];
    }

    public static double DbToLinear(double gainDb) => Math.Pow(10, gainDb / 20);

    private static XElement CreateAudioLevels(double gainDb) =>
        new(
            "filter",
            new XElement(
                "effect",
                new XElement("name", "Audio Levels"),
                new XElement("effectid", "audiolevels"),
                new XElement("effectcategory", "audiolevels"),
                new XElement("effecttype", "audiolevels"),
                new XElement("mediatype", "audio"),
                new XElement("pproBypass", "false"),
                new XElement(
                    "parameter",
                    new XAttribute("authoringApp", "PremierePro"),
                    new XElement("parameterid", "level"),
                    new XElement("name", "Level"),
                    new XElement("valuemin", "0"),
                    new XElement("valuemax", "3.98109"),
                    new XElement("value", FormatFactor(DbToLinear(gainDb))))));

    private static XElement CreatePremiereGain(double gainDb) =>
        new(
            "filter",
            new XElement(
                "effect",
                new XElement("name", "Gain"),
                new XElement("effectid", PremiereGainEffectId),
                new XElement("effecttype", "filter"),
                new XElement("mediatype", "audio"),
                new XElement(
                    "parameter",
                    new XAttribute("authoringApp", "PremierePro"),
                    new XElement("parameterid", "Gain(dB)"),
                    new XElement("name", "Gain(dB)"),
                    new XElement("valuemin", "-96"),
                    new XElement("valuemax", "96"),
                    new XElement("value", FormatFactor(DbToLinear(gainDb))))));

    private static string FormatFactor(double factor) =>
        factor.ToString("0.#########", CultureInfo.InvariantCulture);
}
