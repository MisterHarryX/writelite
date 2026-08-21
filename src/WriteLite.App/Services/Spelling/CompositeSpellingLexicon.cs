using WriteLite.Language.Core;

namespace WriteLite.Services.Spelling;

/// <summary>
/// Hunspell primary + seed fallback (always knows built-in critical words).
/// </summary>
public sealed class CompositeSpellingLexicon : ISpellingLexicon
{
    private readonly ISpellingLexicon? _primary;
    private readonly ISpellingLexicon? _formIndex;
    private readonly SeedSpellDictionary _seed;
    private readonly SpellingLanguage _language;

    public CompositeSpellingLexicon(
        ISpellingLexicon? primary,
        SeedSpellDictionary seed,
        SpellingLanguage language,
        ISpellingLexicon? formIndex = null)
    {
        _primary = primary;
        _formIndex = formIndex;
        _seed = seed;
        _language = language;
        Language = LanguageCode.Russian;
        Name = primary?.Name ?? formIndex?.Name ?? "seed";
        ApproximateWordCount = Math.Max(primary?.ApproximateWordCount ?? 0, formIndex?.ApproximateWordCount ?? 0)
            + _seed.Words(language).Count;
        IsReady = true;
    }

    public string Name { get; }
    public LanguageCode Language { get; }
    public bool IsReady { get; }
    public int ApproximateWordCount { get; }
    public bool PrimaryReady => _primary?.IsReady == true;

    public bool ContainsExact(string word)
    {
        if (string.IsNullOrEmpty(word)) return false;
        var folded = CorrectionCandidateValidityPolicy.FoldSpelling(word);
        if (_seed.Contains(folded, _language) || _seed.Contains(word, _language))
            return true;
        // The form index already folds case and ё/е internally.
        if (_formIndex?.IsReady == true && _formIndex.ContainsExact(word))
            return true;
        if (_primary?.IsReady == true && (_primary.ContainsExact(word) || _primary.ContainsExact(folded)))
            return true;
        // Hunspell usually accepts original casing variants via Check
        if (_primary?.IsReady == true && word.Length > 0 && char.IsUpper(word[0]))
        {
            var lower = char.ToLowerInvariant(word[0]) + word[1..];
            if (_primary.ContainsExact(lower)) return true;
        }

        return false;
    }

    public IReadOnlyList<string> Suggest(string word, int maxSuggestions = 5)
    {
        IEnumerable<string> raw = [];
        if (_formIndex?.IsReady == true)
        {
            raw = _formIndex.Suggest(word, maxSuggestions * 2);
        }

        if (!raw.Any() && _primary?.IsReady == true)
        {
            raw = _primary.Suggest(word, maxSuggestions * 2);
        }

        return CorrectionCandidateValidityPolicy.FilterSuggestions(
            word, raw, maxSuggestions, isKnownWord: ContainsExact);
    }
}
