namespace PremiereAutoDialogueXml.Audio.Analysis;

internal sealed class PhraseIntervalIndex
{
    private readonly DialoguePhrase[] _phrases;
    private readonly long[] _maximumEndThroughIndex;

    public PhraseIntervalIndex(IReadOnlyList<DialoguePhrase> phrases)
    {
        ArgumentNullException.ThrowIfNull(phrases);
        _phrases = phrases
            .OrderBy(phrase => phrase.CoreStartSample)
            .ThenBy(phrase => phrase.CoreEndSample)
            .ToArray();
        _maximumEndThroughIndex = new long[_phrases.Length];
        var maximumEnd = long.MinValue;
        for (var index = 0; index < _phrases.Length; index++)
        {
            maximumEnd = Math.Max(maximumEnd, _phrases[index].CoreEndSample);
            _maximumEndThroughIndex[index] = maximumEnd;
        }
    }

    public IEnumerable<DialoguePhrase> Overlapping(long startSample, long endSample)
    {
        var first = FindFirstEndingAfter(startSample);
        for (var index = first; index < _phrases.Length; index++)
        {
            var phrase = _phrases[index];
            if (phrase.CoreStartSample >= endSample)
            {
                yield break;
            }

            if (phrase.CoreEndSample > startSample)
            {
                yield return phrase;
            }
        }
    }

    private int FindFirstEndingAfter(long sample)
    {
        var low = 0;
        var high = _phrases.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (_maximumEndThroughIndex[middle] <= sample)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}
