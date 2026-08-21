namespace WriteLite.Services.Lexical;

public sealed class LexicalLanguageDetector : ILexicalLanguageDetector
{
    public LexicalLanguage DetectWord(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return LexicalLanguage.Unknown;

        var hasCyrillic = false;
        var hasLatin = false;
        foreach (var ch in word)
        {
            if (IsCyrillic(ch)) hasCyrillic = true;
            else if (IsLatin(ch)) hasLatin = true;
        }

        // Cyrillic wins in a mixed token: a Latin letter inside a Russian word is
        // far more often a typo than a genuine language switch.
        if (hasCyrillic) return LexicalLanguage.Russian;
        return hasLatin ? LexicalLanguage.English : LexicalLanguage.Unknown;
    }

    public LexicalLanguage DetectText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return LexicalLanguage.Unknown;
        var cyr = 0;
        var lat = 0;
        foreach (var ch in text)
        {
            if (IsCyrillic(ch)) cyr++;
            else if (IsLatin(ch)) lat++;
        }

        if (cyr > 0) return LexicalLanguage.Russian;
        return lat > 0 ? LexicalLanguage.English : LexicalLanguage.Unknown;
    }

    public static bool IsCyrillic(char ch)
        => ch is >= '\u0400' and <= '\u04FF' or >= '\u0500' and <= '\u052F';

    public static bool IsLatin(char ch)
        => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
}
