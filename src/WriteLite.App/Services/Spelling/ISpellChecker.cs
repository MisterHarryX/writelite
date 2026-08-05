namespace WriteLite.Services.Spelling;

public interface ISpellChecker
{
    bool IsFullDictionaryLoaded { get; }

    SpellDictionaryStats Stats { get; }

    SpellCheckResult CheckWord(
        string word,
        SpellingLanguage language,
        CancellationToken cancellationToken = default);

    ValueTask<SpellCheckResult> CheckWordAsync(
        string word,
        SpellingLanguage language,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(CheckWord(word, language, cancellationToken));
    }
}

public enum SpellingLanguage
{
    Russian
}

public sealed record SpellCheckResult(
    string Word,
    SpellingLanguage Language,
    bool IsKnown,
    IReadOnlyList<string> Suggestions);

public sealed record SpellDictionaryStats(
    int RussianWordCount,
    // Retained as an ABI-compatible diagnostic field. Russian-only packs always report zero.
    int EnglishWordCount,
    TimeSpan InitialLoadTime,
    string Source,
    string License,
    bool IsFullDictionaryLoaded);
