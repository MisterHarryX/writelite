namespace WriteLite.Services.Translation;

/// <summary>A run of text in one language: "ru" or "en".</summary>
public readonly record struct LanguageSegment(int Start, int Length, string Language);

/// <summary>
/// Splits mixed Russian/English text into same-language runs at sentence boundaries.
/// </summary>
/// <remarks>
/// Decided by script, per sentence: a sentence is English when Latin letters clearly dominate
/// («I goes home yesterday»), and Russian otherwise — a Russian sentence with an English brand or
/// term in it («Открой GitHub и проверь») stays Russian, as it should. Adjacent sentences of the
/// same language are merged so the engine sees whole paragraphs, and the segments always tile the
/// text exactly, so offsets inside a segment map back by adding its start.
/// </remarks>
public static class LanguageSegmenter
{
    /// <summary>When off, every sentence is treated as Russian (Настройки → «Проверять английский»).</summary>
    public static bool EnglishEnabled { get; set; } = true;

    public static IReadOnlyList<LanguageSegment> Split(string text)
    {
        var result = new List<LanguageSegment>();
        if (string.IsNullOrEmpty(text)) return result;

        var sentenceStart = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var boundary = c == '\n'
                           || (c is '.' or '!' or '?' or '…' && (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1])));
            if (!boundary && i != text.Length - 1) continue;

            var end = i + 1;
            while (end < text.Length && char.IsWhiteSpace(text[end]) && text[end] != '\n') end++;
            Add(result, text, sentenceStart, end - sentenceStart);
            sentenceStart = end;
            i = end - 1;
        }

        if (sentenceStart < text.Length) Add(result, text, sentenceStart, text.Length - sentenceStart);
        return result;
    }

    public static string Classify(string sentence)
    {
        int latin = 0, cyrillic = 0;
        foreach (var c in sentence)
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') latin++;
            else if (c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё') cyrillic++;
        }

        if (latin == 0 && cyrillic == 0) return string.Empty;
        return EnglishEnabled && latin >= 4 && latin > cyrillic * 2 ? "en" : "ru";
    }

    private static void Add(List<LanguageSegment> result, string text, int start, int length)
    {
        if (length <= 0) return;
        var language = Classify(text.Substring(start, length));
        if (result.Count > 0)
        {
            var last = result[^1];
            if (language.Length == 0 || last.Language == language)
            {
                result[^1] = last with { Length = last.Length + length };
                return;
            }
        }

        result.Add(new LanguageSegment(start, length, language.Length == 0 ? "ru" : language));
    }
}
