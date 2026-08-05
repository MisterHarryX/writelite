using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace WriteLite.Services.LanguageEngine;

/// <summary>
/// Cleans engine messages for UI: strip HTML, URLs, third-party brand, limit length.
/// </summary>
public static partial class WriteLiteIssueNormalization
{
    private static readonly string[] BrandTokens =
    [
        "LanguageTool",
        "languagetool",
        "LANGUAGETOOL",
        "language tool"
    ];

    public static string NormalizeTitle(string? shortMessage, string? message, string fallback)
    {
        var title = FirstNonEmpty(shortMessage, message, fallback);
        title = CleanUserFacingText(title);
        return Truncate(title, 80);
    }

    public static string NormalizeExplanation(string? message, string? shortMessage, string fallback)
    {
        var text = FirstNonEmpty(message, shortMessage, fallback);
        text = CleanUserFacingText(text);
        return Truncate(text, 280);
    }

    public static string CleanUserFacingText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value;
        text = HtmlTagRegex().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = UrlRegex().Replace(text, " ");
        text = MarkdownLinkRegex().Replace(text, "$1");

        foreach (var brand in BrandTokens)
        {
            text = text.Replace(brand, "проверка", StringComparison.OrdinalIgnoreCase);
        }

        // Collapse whitespace
        text = WhitespaceRegex().Replace(text, " ").Trim();
        return text;
    }

    public static bool ContainsThirdPartyBrand(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var brand in BrandTokens)
        {
            if (value.Contains(brand, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value;
        }

        return value[..(max - 1)].TrimEnd() + "…";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
            {
                return v.Trim();
            }
        }

        return string.Empty;
    }

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^\)]+\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
