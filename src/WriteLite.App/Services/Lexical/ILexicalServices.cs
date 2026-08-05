namespace WriteLite.Services.Lexical;

public interface ILexicalKnowledgeService
{
    Task<LexicalLookupResult> LookupAsync(LexicalLookupRequest request, CancellationToken cancellationToken = default);
    string? PackVersion { get; }
    bool IsPackLoaded { get; }
}

public interface IWordRangeResolver
{
    WordRange? ResolveFromText(string text, int caretOrClickIndex);
    WordRange? ResolveFromSelection(string text, int selectionStart, int selectionLength);
}

public interface ILexicalLanguageDetector
{
    LexicalLanguage DetectWord(string word);
    LexicalLanguage DetectText(string text);
}

public interface IWordMorphologyService
{
    string GetLemma(string word, LexicalLanguage language);
    LexicalPartOfSpeech GetPartOfSpeech(string word, LexicalLanguage language);
    MorphologicalFeatures AnalyzeFeatures(string word, LexicalLanguage language, string? sentence = null, int wordStart = -1);
    string ApplySurfaceCase(string original, string replacement);
    string? InflectLike(string originalSurface, string lemmaForm, LexicalLanguage language);
}

public interface ISyntacticRoleAnalyzer
{
    SyntacticAnalysis Analyze(string word, int start, int length, string sentence, LexicalPartOfSpeech pos, LexicalLanguage language);
}

public interface IContextualLexicalRanker
{
    IReadOnlyList<LexicalSuggestion> RankSynonyms(
        IReadOnlyList<LexicalSuggestion> candidates,
        string word,
        string sentence,
        LexicalPartOfSpeech partOfSpeech);

}

public interface ILexicalReplacementService
{
    LexicalReplacementResult TryReplace(LexicalReplacementRequest request);
}

/// <summary>Optional licensed Ozhegov dictionary. Never fabricates articles.</summary>
public interface IOzhegovDictionaryProvider
{
    bool IsAvailable { get; }
    string? StatusMessage { get; }
    IReadOnlyList<LexicalDefinition> LookupDefinitions(string lemma);
}

public interface IHistoricalDictionaryProvider
{
    bool IsAvailable { get; }
    string? EditionLabel { get; }
    IReadOnlyList<LexicalDefinition> LookupHistoricalDefinitions(string lemma);
}
