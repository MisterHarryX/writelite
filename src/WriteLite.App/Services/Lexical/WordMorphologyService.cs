namespace WriteLite.Services.Lexical;

/// <summary>Facade kept for existing call sites; delegates to rule-based morphology.</summary>
public sealed class WordMorphologyService : IWordMorphologyService
{
    private readonly RuleBasedMorphologyService _inner;

    public WordMorphologyService(Func<string, LexicalLanguage, LexicalEntry?>? lookupEntry = null)
    {
        _inner = new RuleBasedMorphologyService(lookupEntry);
    }

    public string GetLemma(string word, LexicalLanguage language) => _inner.GetLemma(word, language);
    public LexicalPartOfSpeech GetPartOfSpeech(string word, LexicalLanguage language) => _inner.GetPartOfSpeech(word, language);
    public MorphologicalFeatures AnalyzeFeatures(string word, LexicalLanguage language, string? sentence = null, int wordStart = -1)
        => _inner.AnalyzeFeatures(word, language, sentence, wordStart);
    public string ApplySurfaceCase(string original, string replacement) => _inner.ApplySurfaceCase(original, replacement);
    public string? InflectLike(string originalSurface, string lemmaForm, LexicalLanguage language)
        => _inner.InflectLike(originalSurface, lemmaForm, language);
}
