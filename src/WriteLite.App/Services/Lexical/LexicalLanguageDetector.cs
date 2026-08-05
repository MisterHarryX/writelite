namespace WriteLite.Services.Lexical;

public sealed class LexicalLanguageDetector : ILexicalLanguageDetector
{
    public LexicalLanguage DetectWord(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return LexicalLanguage.Unknown;

        var hasCyrillic = false;
        foreach (var ch in word)
        {
            if (IsCyrillic(ch)) hasCyrillic = true;
        }

        return hasCyrillic ? LexicalLanguage.Russian : LexicalLanguage.Unknown;
    }

    public LexicalLanguage DetectText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return LexicalLanguage.Unknown;
        var cyr = 0;
        foreach (var ch in text)
        {
            if (IsCyrillic(ch)) cyr++;
        }

        return cyr > 0 ? LexicalLanguage.Russian : LexicalLanguage.Unknown;
    }

    public static bool IsCyrillic(char ch)
        => ch is >= '\u0400' and <= '\u04FF' or >= '\u0500' and <= '\u052F';
}
