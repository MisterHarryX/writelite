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

internal static class RussianTokens
{
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
}
