namespace WriteLite.Language.Core;

public enum LanguageCode
{
    Unknown = 0,
    Russian = 1,
    English = 2
}

public interface ISpellingLexicon
{
    string Name { get; }
    LanguageCode Language { get; }
    bool IsReady { get; }
    int ApproximateWordCount { get; }
    bool ContainsExact(string word);
    IReadOnlyList<string> Suggest(string word, int maxSuggestions = 5);
}

public interface ISpellingCandidateGenerator
{
    IReadOnlyList<string> Generate(string word, LanguageCode language, int maxCandidates = 32);
}

public interface ISpellingCandidateRanker
{
    IReadOnlyList<string> Rank(string original, IEnumerable<string> candidates, LanguageCode language, int take = 5);
}

public interface ILanguageDetector
{
    LanguageCode DetectWord(string word);
}

public interface IProtectedTokenDetector
{
    bool IsProtectedToken(string token);
}
