namespace WriteLite.AI.Local;

/// <summary>Small tail context for interactive neural analysis; offsets are original UTF-16 offsets.</summary>
public readonly record struct TextAnalysisContext(int Start, int Length, string Text)
{
    public static TextAnalysisContext ExtractInteractive(string text, int maxChars = 480)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= maxChars) return new(0, text.Length, text);
        var start = Math.Max(0, text.Length - maxChars);
        var boundary = -1;
        for (var i = start; i < Math.Min(text.Length, start + 120); i++)
        {
            if (text[i] is '.' or '!' or '?' or '\n' or '\r') { boundary = i + 1; break; }
        }
        if (boundary >= 0 && boundary < text.Length) start = boundary;
        if (start > 0 && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1])) start++;
        return new(start, text.Length - start, text[start..]);
    }
}
