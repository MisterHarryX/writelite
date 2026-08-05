using WriteLite.Services.Spelling;

namespace WriteLite.Services.Morphology;

public sealed class DictionaryMorphologyService(ISpellChecker spellChecker) : IMorphologyService
{
    public MorphologyAnalysis Analyze(string word)
    {
        if (!word.Any(SpellTextAnalyzer.IsRussianLetter))
        {
            return new MorphologyAnalysis(word, false, null, null, new Dictionary<string, string>());
        }

        var result = spellChecker.CheckWord(word, SpellingLanguage.Russian);
        return new MorphologyAnalysis(word, result.IsKnown, null, null, new Dictionary<string, string>());
    }

    public string? GetLemma(string word) => Analyze(word).Lemma;
    public string? GetPartOfSpeech(string word) => Analyze(word).PartOfSpeech;
    public IReadOnlyDictionary<string, string> GetGrammaticalFeatures(string word) => Analyze(word).Features;
    public bool IsKnownForm(string word) => Analyze(word).IsKnownForm;
}
