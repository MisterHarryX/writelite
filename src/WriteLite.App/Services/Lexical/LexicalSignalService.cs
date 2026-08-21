using WriteLite.Language.Russian;
using WriteLite.Services.Spelling;

namespace WriteLite.Services.Lexical;

/// <summary>
/// What WriteLite's own lexicon knows about a word, beyond whether it exists.
/// </summary>
/// <remarks>
/// Contains no definitions or translations — those belong to
/// <see cref="ILexicalKnowledgeService"/> and are for the lookup popup. This is the subset
/// that a correction decision can act on, kept small and cheap because it is consulted per
/// flagged token during analysis.
/// </remarks>
/// <param name="IsKnown">Present in the 3.09 M-form Russian index.</param>
/// <param name="IsUserWord">Added by the user. The strongest protection there is.</param>
/// <param name="IsSlang">Marked as slang or internet/gaming vocabulary.</param>
/// <param name="IsObscene">Marked obscene. Recognised, never corrected, never sanitised.</param>
/// <param name="IsProperName">A name — personal, geographic, brand or product.</param>
/// <param name="IsAbbreviation">An abbreviation or acronym.</param>
/// <param name="IsBorrowing">A borrowing/anglicism, which covers most technical vocabulary.</param>
/// <param name="IsInformal">Colloquial register.</param>
/// <param name="IsArchaic">Archaic — known, but a poor replacement suggestion.</param>
/// <param name="Lemma">Dictionary form, empty when unknown.</param>
/// <param name="FrequencyRank">1 is most frequent; 0 means absent from the frequency list.</param>
/// <param name="PartOfSpeech">Coarse part of speech.</param>
public sealed record LexicalSignals(
    bool IsKnown,
    bool IsUserWord,
    bool IsSlang,
    bool IsObscene,
    bool IsProperName,
    bool IsAbbreviation,
    bool IsBorrowing,
    bool IsInformal,
    bool IsArchaic,
    string Lemma,
    int FrequencyRank,
    RussianPartOfSpeech PartOfSpeech)
{
    public static readonly LexicalSignals Unknown = new(
        false, false, false, false, false, false, false, false, false,
        string.Empty, 0, RussianPartOfSpeech.Unknown);

    /// <summary>
    /// True when the word carries a register the general dictionary is likely to
    /// mis-flag: slang, obscenity, names, abbreviations and borrowings.
    /// </summary>
    /// <remarks>
    /// These are exactly the categories that collapsed when LanguageTool was enabled —
    /// slang preservation 0.957 → 0.717, profanity 0.885 → 0.385 — because a general
    /// Russian dictionary does not carry them and reports them as misspellings.
    /// </remarks>
    public bool HasProtectedRegister
        => IsSlang || IsObscene || IsProperName || IsAbbreviation || IsBorrowing;

    /// <summary>True when the corpus frequency list has a rank for this form.</summary>
    public bool HasFrequencyEvidence => FrequencyRank > 0;

    /// <summary>A short, user-facing Russian label for why the word was left alone.</summary>
    public string? RegisterLabel => this switch
    {
        { IsUserWord: true } => "из пользовательского словаря",
        { IsObscene: true } => "обсценная лексика",
        { IsSlang: true } => "разговорное / сленг",
        { IsProperName: true } => "имя собственное",
        { IsAbbreviation: true } => "аббревиатура",
        { IsBorrowing: true } => "заимствование / термин",
        { IsInformal: true } => "разговорное",
        _ => null,
    };
}

/// <summary>Answers <see cref="LexicalSignals"/> queries during analysis.</summary>
public interface ILexicalSignalSource
{
    LexicalSignals For(string word);
}

/// <summary>
/// Reads register and morphology straight off the Russian form index, with the user
/// dictionary layered on top.
/// </summary>
/// <remarks>
/// The index already carries POS, register flags, corpus frequency and lemma for all
/// 3.09 M forms in a memory-mapped array — this class only surfaces it. Nothing is loaded
/// or parsed here, so a lookup is a DAFSA walk plus a fixed-offset read, which is what
/// makes it safe to call per flagged token.
/// </remarks>
public sealed class LexicalSignalService : ILexicalSignalSource
{
    private readonly RussianFormIndex? _index;
    private readonly UserDictionaryService? _userDictionary;

    public LexicalSignalService(RussianFormIndex? index, UserDictionaryService? userDictionary = null)
    {
        _index = index;
        _userDictionary = userDictionary;
    }

    /// <summary>Builds from a spell checker, or returns null when the index is unavailable.</summary>
    public static LexicalSignalService? TryCreate(
        LocalSpellChecker spellChecker,
        UserDictionaryService? userDictionary = null)
    {
        var index = spellChecker.RussianFormIndex;
        return index is null ? null : new LexicalSignalService(index, userDictionary);
    }

    public LexicalSignals For(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return LexicalSignals.Unknown;
        }

        var trimmed = word.Trim();
        var isUserWord = _userDictionary?.Contains(trimmed) == true;

        if (_index is null)
        {
            return LexicalSignals.Unknown with { IsUserWord = isUserWord, IsKnown = isUserWord };
        }

        // One graph walk, not two. GetId resolves the word and its metadata offset in a
        // single traversal; calling Contains as well doubled the cost of a lookup that sits
        // on the analysis path (measured 54 µs → 27 µs per word).
        var id = _index.GetId(trimmed);
        if (id < 0)
        {
            return isUserWord
                ? LexicalSignals.Unknown with { IsKnown = true, IsUserWord = true }
                : LexicalSignals.Unknown;
        }

        var info = _index.GetInfo(id);

        var flags = info.Flags;
        return new LexicalSignals(
            IsKnown: true,
            IsUserWord: isUserWord,
            IsSlang: flags.HasFlag(RussianFormFlags.Slang),
            IsObscene: flags.HasFlag(RussianFormFlags.Obscene),
            IsProperName: flags.HasFlag(RussianFormFlags.ProperName),
            IsAbbreviation: flags.HasFlag(RussianFormFlags.Abbreviation),
            IsBorrowing: flags.HasFlag(RussianFormFlags.Borrowing),
            IsInformal: flags.HasFlag(RussianFormFlags.Informal),
            IsArchaic: flags.HasFlag(RussianFormFlags.Archaic),
            Lemma: info.Lemma ?? string.Empty,
            FrequencyRank: info.FrequencyRank,
            PartOfSpeech: info.PartOfSpeech);
    }
}
