namespace WriteLite.Services.Lexical;

/// <summary>
/// Deterministic Russian morphology without external neural models.
/// Uses pack lemmas when available + suffix/ending heuristics for surface features.
/// </summary>
public sealed class RuleBasedMorphologyService : IWordMorphologyService
{
    private readonly Func<string, LexicalLanguage, LexicalEntry?>? _lookupEntry;

    public RuleBasedMorphologyService(Func<string, LexicalLanguage, LexicalEntry?>? lookupEntry = null)
    {
        _lookupEntry = lookupEntry;
    }

    public string GetLemma(string word, LexicalLanguage language)
    {
        if (string.IsNullOrWhiteSpace(word)) return word ?? string.Empty;
        var entry = _lookupEntry?.Invoke(word, language);
        if (entry is not null) return entry.Lemma;

        var lower = word.ToLowerInvariant();
        // Russian light stemming for common endings (not a full stemmer).
        foreach (var ending in RuNounEndings)
        {
            if (lower.Length > ending.Length + 2 && lower.EndsWith(ending, StringComparison.Ordinal))
                return lower[..^ending.Length];
        }

        foreach (var ending in RuVerbEndings)
        {
            if (lower.Length > ending.Length + 2 && lower.EndsWith(ending, StringComparison.Ordinal))
                return lower[..^ending.Length];
        }

        return lower;
    }

    public LexicalPartOfSpeech GetPartOfSpeech(string word, LexicalLanguage language)
    {
        var entry = _lookupEntry?.Invoke(word, language);
        if (entry is not null && entry.PartOfSpeech != LexicalPartOfSpeech.Unknown)
            return entry.PartOfSpeech;

        var lower = (word ?? string.Empty).ToLowerInvariant();
        if (lower.EndsWith("о") || lower.EndsWith("е") && lower.Length > 3)
        {
            // could be adverb or neuter noun — leave unknown unless pack knows
        }

        if (RuVerbEndings.Any(e => lower.EndsWith(e, StringComparison.Ordinal) && lower.Length > e.Length + 2))
            return LexicalPartOfSpeech.Verb;
        if (RuAdjEndings.Any(e => lower.EndsWith(e, StringComparison.Ordinal) && lower.Length > e.Length + 2))
            return LexicalPartOfSpeech.Adjective;

        return LexicalPartOfSpeech.Unknown;
    }

    public MorphologicalFeatures AnalyzeFeatures(string word, LexicalLanguage language, string? sentence = null, int wordStart = -1)
    {
        word ??= string.Empty;
        var entry = _lookupEntry?.Invoke(word, language);
        var lower = word.ToLowerInvariant();
        var pos = entry?.PartOfSpeech ?? GetPartOfSpeech(word, language);

        // Russian heuristics
        string? gender = null;
        string? number = null;
        string? @case = null;
        string? tense = null;
        string? person = null;
        string? aspect = null;

        if (pos is LexicalPartOfSpeech.Noun or LexicalPartOfSpeech.Adjective or LexicalPartOfSpeech.Unknown)
        {
            if (lower.EndsWith("ы") || lower.EndsWith("и") || lower.EndsWith("а") && lower.Length > 4 && lower[^2] is 'к' or 'г')
                number = "мн.?";
            if (lower.EndsWith("ами") || lower.EndsWith("ями")) { @case = "тв."; number = "мн."; }
            else if (lower.EndsWith("ах") || lower.EndsWith("ях")) { @case = "пр."; number = "мн."; }
            else if (lower.EndsWith("ов") || lower.EndsWith("ев") || lower.EndsWith("ей")) { @case = "рд."; number = "мн."; }
            else if (lower.EndsWith("ом") || lower.EndsWith("ем") || lower.EndsWith("ой") || lower.EndsWith("ей")) { @case = "тв."; number = "ед."; }
            else if (lower.EndsWith("у") || lower.EndsWith("ю")) { @case = "дт./вн."; number = "ед."; }
            else if (lower.EndsWith("а") || lower.EndsWith("я")) { @case = "рд./им."; gender = "ж./м."; number = "ед."; }
            else if (lower.EndsWith("о") || lower.EndsWith("е")) { gender = "ср."; number = "ед."; @case = "им./вн."; }
            else if (lower.EndsWith("ый") || lower.EndsWith("ий") || lower.EndsWith("ой")) { gender = "м."; number = "ед."; @case = "им."; pos = LexicalPartOfSpeech.Adjective; }
            else if (lower.EndsWith("ая") || lower.EndsWith("яя")) { gender = "ж."; number = "ед."; @case = "им."; pos = LexicalPartOfSpeech.Adjective; }
            else if (lower.EndsWith("ое") || lower.EndsWith("ее")) { gender = "ср."; number = "ед."; @case = "им."; pos = LexicalPartOfSpeech.Adjective; }
            else { number = "ед."; @case = "им.?"; }
        }

        if (pos == LexicalPartOfSpeech.Verb || RuVerbEndings.Any(e => lower.EndsWith(e, StringComparison.Ordinal)))
        {
            if (lower.EndsWith("л") || lower.EndsWith("ла") || lower.EndsWith("ло") || lower.EndsWith("ли"))
            {
                tense = "прош.";
                gender = lower.EndsWith("ла") ? "ж." : lower.EndsWith("ло") ? "ср." : lower.EndsWith("ли") ? null : "м.";
                number = lower.EndsWith("ли") ? "мн." : "ед.";
            }
            else if (lower.EndsWith("у") || lower.EndsWith("ю")) { person = "1"; number = "ед."; tense = "наст./буд."; }
            else if (lower.EndsWith("ешь") || lower.EndsWith("ёшь") || lower.EndsWith("ишь")) { person = "2"; number = "ед."; tense = "наст./буд."; }
            else if (lower.EndsWith("ет") || lower.EndsWith("ёт") || lower.EndsWith("ит")) { person = "3"; number = "ед."; tense = "наст./буд."; }
            else if (lower.EndsWith("ем") || lower.EndsWith("ём") || lower.EndsWith("им")) { person = "1"; number = "мн."; tense = "наст./буд."; }
            else if (lower.EndsWith("ете") || lower.EndsWith("ёте") || lower.EndsWith("ите")) { person = "2"; number = "мн."; tense = "наст./буд."; }
            else if (lower.EndsWith("ут") || lower.EndsWith("ют") || lower.EndsWith("ат") || lower.EndsWith("ят")) { person = "3"; number = "мн."; tense = "наст./буд."; }
            else if (lower.EndsWith("ть") || lower.EndsWith("ти") || lower.EndsWith("чь")) { tense = "инф."; }
            aspect = lower.StartsWith("по") || lower.StartsWith("с") || lower.StartsWith("на") || lower.StartsWith("вы")
                ? "сов.?"
                : "несов.?";
        }

        return new MorphologicalFeatures(
            Case: @case,
            Number: number,
            Gender: gender,
            Tense: tense,
            Person: person,
            Aspect: aspect);
    }

    public string ApplySurfaceCase(string original, string replacement)
    {
        if (string.IsNullOrEmpty(replacement)) return replacement;
        if (string.IsNullOrEmpty(original)) return replacement;

        if (IsAllUpper(original)) return replacement.ToUpperInvariant();
        if (char.IsUpper(original[0]) && original.Skip(1).All(c => !char.IsLetter(c) || char.IsLower(c)))
        {
            if (replacement.Length == 1) return replacement.ToUpperInvariant();
            return char.ToUpperInvariant(replacement[0]) + replacement[1..];
        }

        if (IsAllLower(original)) return replacement.ToLowerInvariant();
        return replacement;
    }

    public string? InflectLike(string originalSurface, string lemmaForm, LexicalLanguage language)
    {
        if (string.IsNullOrWhiteSpace(lemmaForm)) return null;
        var entry = _lookupEntry?.Invoke(lemmaForm, language)
                    ?? _lookupEntry?.Invoke(originalSurface, language);
        if (entry?.Inflections is { Count: > 0 })
        {
            var lowerOrig = originalSurface.ToLowerInvariant();
            foreach (var form in entry.Inflections)
            {
                if (string.Equals(form, lowerOrig, StringComparison.OrdinalIgnoreCase))
                    return ApplySurfaceCase(originalSurface, form);
            }
        }

        return ApplySurfaceCase(originalSurface, lemmaForm);
    }

    private static readonly string[] RuNounEndings =
    [
        "ами", "ями", "ах", "ях", "ов", "ев", "ей", "ом", "ем", "ой", "ий", "ый", "ая", "ое", "ые",
        "ую", "юю", "ых", "их", "ам", "ям", "у", "ю", "а", "я", "ы", "и", "е", "о"
    ];

    private static readonly string[] RuVerbEndings =
    [
        "ешь", "ёшь", "ишь", "ете", "ёте", "ите", "ут", "ют", "ат", "ят", "ет", "ёт", "ит",
        "ем", "ём", "им", "ла", "ло", "ли", "ть", "ти", "чь", "л", "у", "ю"
    ];

    private static readonly string[] RuAdjEndings =
    [
        "ый", "ий", "ой", "ая", "яя", "ое", "ее", "ые", "ие", "ого", "его", "ой", "ей", "ых", "их"
    ];

    private static bool IsAllUpper(string s)
    {
        var any = false;
        foreach (var ch in s)
        {
            if (!char.IsLetter(ch)) continue;
            any = true;
            if (!char.IsUpper(ch)) return false;
        }

        return any;
    }

    private static bool IsAllLower(string s)
    {
        var any = false;
        foreach (var ch in s)
        {
            if (!char.IsLetter(ch)) continue;
            any = true;
            if (!char.IsLower(ch)) return false;
        }

        return any;
    }
}
