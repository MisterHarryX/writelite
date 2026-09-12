using System.Text.RegularExpressions;

namespace WriteLite.Services.Grammar;

/// <summary>
/// Word spans with their offsets, for analyzers that reason about a short window of tokens
/// rather than about a regular expression over the whole text.
/// </summary>
/// <remarks>
/// The vocative, case-government and sentence-boundary rules all need the same three things:
/// where each word starts, where it ends, and what separates it from the next one. Doing that
/// with a regex per rule produced three slightly different definitions of "word" during
/// development — hyphenated forms in one, not in another — and the disagreements only showed
/// up as findings appearing at the wrong offsets. One definition, used by all of them.
/// </remarks>
internal readonly record struct RussianToken(int Start, int Length, string Value)
{
    public int End => Start + Length;
}

internal static partial class RussianTokens
{
    /// <summary>A maximal run of letters — the faction of a word that stays a word when
    /// style scoring or an AI diff skips digits, underscores and markup.</summary>
    [GeneratedRegex(@"\p{L}+", RegexOptions.CultureInvariant)]
    public static partial Regex LettersTokenRegex();
    /// <summary>Letter runs, with an internal hyphen allowed («кто-то», «по-моему»).</summary>
    public static List<RussianToken> Split(string text, int from = 0, int to = -1)
    {
        var tokens = new List<RussianToken>();
        if (string.IsNullOrEmpty(text)) return tokens;

        var end = to < 0 ? text.Length : Math.Min(to, text.Length);
        var i = Math.Max(0, from);
        while (i < end)
        {
            if (!char.IsLetter(text[i])) { i++; continue; }

            var start = i;
            while (i < end && (char.IsLetter(text[i]) || (text[i] == '-' && i + 1 < end && char.IsLetter(text[i + 1]))))
            {
                i++;
            }

            tokens.Add(new RussianToken(start, i - start, text[start..i]));
        }

        return tokens;
    }

    /// <summary>
    /// True when nothing but spaces separates the two tokens.
    /// </summary>
    /// <remarks>
    /// Every rule in this folder that reads more than one token depends on this: a comma, a
    /// dash or a bracket between two words ends whatever relation the rule was about to
    /// claim. Checking the gap rather than the tokens is what makes «вопреки, кажется, новым
    /// правилам» invisible to the case-government rule instead of a special case in it.
    /// </remarks>
    public static bool OnlySpacesBetween(string text, RussianToken left, RussianToken right)
    {
        for (var i = left.End; i < right.Start; i++)
        {
            if (text[i] != ' ') return false;
        }

        return right.Start >= left.End;
    }

    /// <summary>The sentence-terminating characters this project treats as a boundary.</summary>
    public static bool IsTerminator(char value) => value is '.' or '!' or '?' or '…';

    /// <summary>Nominative personal pronouns — the only pronouns that can be a clause subject.</summary>
    public static readonly HashSet<string> NominativePronouns = new(StringComparer.OrdinalIgnoreCase)
    {
        "я", "ты", "он", "она", "оно", "мы", "вы", "они",
    };

    /// <summary>True when <paramref name="index"/> opens the text or follows a terminator.</summary>
    public static bool IsSentenceStart(string text, int index)
    {
        var i = index - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i]))
        {
            if (text[i] is '\n' or '\r') return true;
            i--;
        }

        if (i < 0) return true;
        return IsTerminator(text[i]);
    }

    /// <summary>
    /// True when <paramref name="index"/> opens a clause: the text start, a line break, or any
    /// of the clause-opening punctuation (sentence terminators, quotes, brackets, colons, dashes).
    /// The analyzers use it to stop a rule at a clause boundary instead of across one.
    /// </summary>
    public static bool IsClauseStart(string text, int index)
    {
        if (index <= 0) return true;

        var i = index - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i]))
        {
            if (text[i] is '\n' or '\r') return true;
            i--;
        }

        if (i < 0) return true;
        return text[i] is '.' or '!' or '?' or '…' or ':' or ';' or '—' or '(' or '«' or '"' or '“';
    }

    /// <summary>Index of the previous non-whitespace character, or -1 at the text start.</summary>
    public static int PreviousNonWhitespaceIndex(string text, int index)
    {
        while (index >= 0)
        {
            if (!char.IsWhiteSpace(text[index])) return index;
            index--;
        }

        return -1;
    }

    /// <summary>The previous non-whitespace character, or <c>'\0'</c> at the text start.</summary>
    public static char PreviousNonWhitespace(string text, int index)
    {
        var i = PreviousNonWhitespaceIndex(text, index);
        return i < 0 ? '\0' : text[i];
    }

    /// <summary>Sentence spans cut at `. ! ? … \n`, the terminator included in the span.</summary>
    public static IEnumerable<(int Start, int Length)> Sentences(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('.' or '!' or '?' or '…' or '\n')) continue;
            if (i + 1 > start) yield return (start, i + 1 - start);
            start = i + 1;
        }

        if (start < text.Length) yield return (start, text.Length - start);
    }

    /// <summary>Capitalises the first letter, for a word being moved to a sentence start.</summary>
    public static string Capitalize(string value)
    {
        if (value.Length == 0 || char.IsUpper(value[0])) return value;
        var culture = new System.Globalization.CultureInfo("ru-RU");
        return char.ToUpper(value[0], culture) + value[1..];
    }

    /// <summary>Restores an initial capital when the source token carried one.</summary>
    public static string MatchLeadingCase(string source, string replacement)
    {
        if (source.Length == 0 || replacement.Length == 0) return replacement;
        if (!char.IsUpper(source[0])) return replacement;
        var culture = new System.Globalization.CultureInfo("ru-RU");
        return char.ToUpper(replacement[0], culture) + replacement[1..];
    }

    /// <summary>
    /// Restores the case shape of <paramref name="replacement"/> to match <paramref name="source"/>:
    /// a whole word in capitals stays fully capitalised, a leading capital becomes a leading
    /// capital, and anything else is returned untouched.
    /// </summary>
    /// <remarks>
    /// Used where the source is a single word token (e.g. the spell checker): an all-caps
    /// typo must be repaired as all-caps, not demoted to a leading capital. Kept alongside
    /// <see cref="MatchLeadingCase"/>, which only ever moves a word to a sentence start and
    /// must not re-case multi-capital input the analyzers feed it.
    /// </remarks>
    public static string MatchCase(string source, string replacement)
    {
        if (source.Length == 0 || replacement.Length == 0) return replacement;
        var culture = new System.Globalization.CultureInfo("ru-RU");
        if (source.All(c => !char.IsLetter(c) || char.IsUpper(c)))
        {
            return replacement.ToUpper(culture);
        }

        if (char.IsUpper(source[0]) && source.Skip(1).All(c => !char.IsLetter(c) || char.IsLower(c)))
        {
            return char.ToUpper(replacement[0], culture) + replacement[1..];
        }

        return replacement;
    }
}
