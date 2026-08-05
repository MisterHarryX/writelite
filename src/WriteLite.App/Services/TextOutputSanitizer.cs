using System.Text.RegularExpressions;

namespace WriteLite.Services;

/// <summary>
/// Removes analyzer-injected Markdown / meta commentary from corrections
/// without damaging intentional user Markdown or protected technical spans.
/// </summary>
public static partial class TextOutputSanitizer
{
    private static readonly string[] MetaPrefixes =
    [
        "Анализ вашего текста",
        "Анализ текста",
        "Исправленный вариант",
        "Полностью исправленный текст",
        "Полностью исправленный вариант",
        "Основные ошибки",
        "Основные замечания",
        "Вот исправленная версия",
        "Вы хорошо справились",
        "Corrected text",
        "Fully corrected"
    ];

    private static readonly string[] HedgeWords =
    [
        "возможно",
        "желательн", // желательно / желательна / желателен
        "вероятн",   // вероятно / вероятна
        "в некоторых стилях",
        "может требоваться",
        "может быть",
        " можно " // soft permission; spaces reduce false positives
    ];

    /// <summary>
    /// Sanitize a full corrected document. Returns null if the result is unusable.
    /// </summary>
    public static string? SanitizeCorrection(string originalText, string? correctedText)
    {
        if (correctedText is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(correctedText))
        {
            return null;
        }

        if (LooksLikeMetaCommentary(correctedText))
        {
            CompatibilityLogger.Technical("correction-rejected-markdown", "reason=meta-commentary");
            return null;
        }

        var originalHasMarkdown = ContainsMarkdownMarkers(originalText);
        var cleaned = correctedText;

        if (!originalHasMarkdown)
        {
            var before = cleaned;
            cleaned = StripInjectedMarkdown(cleaned);
            if (!string.Equals(before, cleaned, StringComparison.Ordinal))
            {
                CompatibilityLogger.Technical("correction-sanitized-markdown", $"delta={before.Length - cleaned.Length}");
            }
        }

        // Reject if still looks like a report wrapping the text.
        if (LooksLikeMetaCommentary(cleaned))
        {
            CompatibilityLogger.Technical("correction-rejected-markdown", "reason=meta-after-sanitize");
            return null;
        }

        // Reject if unexpected markdown remains while original had none.
        if (!originalHasMarkdown && ContainsMarkdownMarkers(cleaned))
        {
            // Try one more aggressive cleanup of ** around punctuation.
            cleaned = AggressiveStripStars(cleaned);
            if (ContainsMarkdownMarkers(cleaned))
            {
                CompatibilityLogger.Technical("correction-rejected-markdown", "reason=residual-markers");
                return null;
            }

            CompatibilityLogger.Technical("correction-sanitized-markdown", "reason=aggressive-stars");
        }

        return cleaned;
    }

    /// <summary>
    /// Sanitize a single replacement fragment relative to its original span.
    /// </summary>
    public static string? SanitizeReplacement(string originalFragment, string? replacement)
    {
        if (replacement is null)
        {
            return null;
        }

        if (ContainsMarkdownMarkers(originalFragment))
        {
            return replacement; // user already had markdown in span
        }

        var cleaned = StripInjectedMarkdown(replacement);
        cleaned = AggressiveStripStars(cleaned);

        if (ContainsMarkdownMarkers(cleaned) && !ContainsMarkdownMarkers(originalFragment))
        {
            CompatibilityLogger.Technical("correction-rejected-markdown", "reason=replacement-markers");
            return null;
        }

        if (!string.Equals(replacement, cleaned, StringComparison.Ordinal))
        {
            CompatibilityLogger.Technical("correction-sanitized-markdown", "scope=replacement");
        }

        return cleaned;
    }

    public static bool LooksLikeMetaCommentary(string text)
    {
        var head = text.Length <= 240 ? text : text[..240];
        foreach (var p in MetaPrefixes)
        {
            if (head.Contains(p, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Numbered "report" preamble before body
        if (ReportPreambleRegex().IsMatch(head))
        {
            return true;
        }

        return false;
    }

    public static bool ContainsMarkdownMarkers(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return text.Contains("**", StringComparison.Ordinal)
               || text.Contains("__", StringComparison.Ordinal)
               || text.Contains("```", StringComparison.Ordinal)
               || text.Contains('`')
               || text.Contains("~~", StringComparison.Ordinal)
               || MarkdownLinkRegex().IsMatch(text);
    }

    public static bool MessageContradictsSafeApply(string message, bool safeToApply)
    {
        if (!safeToApply || string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        foreach (var h in HedgeWords)
        {
            if (message.Contains(h, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Standalone «может» (not part of «может быть» already caught): soft hedge.
        if (HedgeMozhetRegex().IsMatch(message))
        {
            return true;
        }

        return false;
    }

    private static string StripInjectedMarkdown(string text)
    {
        var s = text;
        // **word** → word, **,** → ,
        s = BoldWrapRegex().Replace(s, "$1");
        s = ItalicWrapRegex().Replace(s, "$1");
        s = StrikeWrapRegex().Replace(s, "$1");
        // leftover ** around punctuation: меры**,** → меры,
        s = StarAroundPunctRegex().Replace(s, "$1");
        // word**,**word / word** **word patterns left after partial wraps
        s = BrokenStarPunctRegex().Replace(s, "$1");
        s = s.Replace("**", "", StringComparison.Ordinal);
        s = s.Replace("__", "", StringComparison.Ordinal);
        s = s.Replace("~~", "", StringComparison.Ordinal);
        // strip markdown links to visible label only: [text](url) → text
        s = MarkdownLinkUnwrapRegex().Replace(s, "$1");
        // single backticks only if not code fence remnants
        if (!s.Contains("```", StringComparison.Ordinal))
        {
            s = InlineCodeRegex().Replace(s, "$1");
        }
        else
        {
            // accidental fence markers when original had none
            s = s.Replace("```", "", StringComparison.Ordinal);
        }

        return s;
    }

    private static string AggressiveStripStars(string text)
        => text.Replace("*", "", StringComparison.Ordinal);

    [GeneratedRegex(@"\*\*(.+?)\*\*", RegexOptions.CultureInvariant)]
    private static partial Regex BoldWrapRegex();

    [GeneratedRegex(@"__(.+?)__", RegexOptions.CultureInvariant)]
    private static partial Regex ItalicWrapRegex();

    [GeneratedRegex(@"~~(.+?)~~", RegexOptions.CultureInvariant)]
    private static partial Regex StrikeWrapRegex();

    [GeneratedRegex(@"\*{1,2}([,.;:!?…])\*{1,2}", RegexOptions.CultureInvariant)]
    private static partial Regex StarAroundPunctRegex();

    /// <summary>меры**,** → меры,  or  меры** ** → меры</summary>
    [GeneratedRegex(@"\*{1,2}([,.;:!?…])\*{0,2}", RegexOptions.CultureInvariant)]
    private static partial Regex BrokenStarPunctRegex();

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"\[[^\]]+\]\([^)]+\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLinkUnwrapRegex();

    [GeneratedRegex(@"(?is)^\s*(анализ|основные|исправленн|corrected|fully\s+corrected).{0,80}\n\s*1[\.\)]", RegexOptions.CultureInvariant)]
    private static partial Regex ReportPreambleRegex();

    [GeneratedRegex(@"(?i)\bможет\b", RegexOptions.CultureInvariant)]
    private static partial Regex HedgeMozhetRegex();
}
