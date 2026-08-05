using System.Text.RegularExpressions;

namespace WriteLite.Services;

/// <summary>
/// Detects URL / email / path / code spans that must not receive linguistic fixes.
/// </summary>
public static partial class ProtectedTextSpans
{
    public static IReadOnlyList<(int Start, int End)> Find(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return ProtectedSpanRegex()
            .Matches(text)
            .Select(m => (m.Index, m.Index + m.Length))
            .ToList();
    }

    public static bool Overlaps(int start, int length, IReadOnlyList<(int Start, int End)> spans)
    {
        if (spans.Count == 0 || length < 0 || start < 0)
        {
            return false;
        }

        var end = start + length;
        // Zero-length insert: treat as overlapping if inside a protected span.
        if (length == 0)
        {
            return spans.Any(s => start > s.Start && start < s.End);
        }

        return spans.Any(s => start < s.End && end > s.Start);
    }

    public static bool IsTechnicalToken(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= 1)
        {
            return true;
        }

        if (value.Any(char.IsDigit))
        {
            return true;
        }

        if (value.Contains('_') || value.Contains('/') || value.Contains('\\')
            || value.Contains('@') || value.Contains(':'))
        {
            return true;
        }

        // CamelCase / PascalCase identifiers
        if (value.Any(char.IsUpper) && value.Any(char.IsLower) && value.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
        {
            var upperAfterFirst = value.Skip(1).Count(char.IsUpper);
            if (upperAfterFirst > 0 && value.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'))
            {
                return true;
            }
        }

        if (value.All(c => !char.IsLetter(c) || char.IsUpper(c)) && value.Any(c => c is >= 'A' and <= 'Z'))
        {
            return true; // ALLCAPS
        }

        return false;
    }

    // Code blocks, URLs, emails, paths, JSON, commands, hashes, SKUs, IPs,
    // dates, versions and code-like tokens. The detector is deliberately
    // conservative: missing a correction inside technical text is safer than
    // mutating an identifier or command.
    // The order is intentional: broad command/JSON spans are found before their
    // numeric and dotted sub-tokens and the rendering filter treats any overlap
    // as protected.
    [GeneratedRegex(
        @"(?s:```.*?```)|(?m)^\s*(?:(?:dotnet|git|npm|pnpm|yarn|python|python3|pip|powershell|pwsh|cmd|curl|wget|docker|kubectl|cargo|go|java)\s+|(?:\$|PS>)\s*|[A-Za-z0-9_.-]+(?:\.exe)?\s+(?=--?[\w-]+|/[A-Za-z?]))[^\r\n]+|\{[^{}\r\n]{1,4000}\}|https?://[^\s<>\""']+|www\.[^\s<>\""']+|\b[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}\b|[A-Za-z]:\\[^\s<>\""'|]+|\\\\[^\s<>\""'|]+|(?<!\w)/(?:[\w.-]+/)+[\w.-]+|`[^`\r\n]+`|\b(?:sha(?:1|256|512):?)?[A-Fa-f0-9]{32,128}\b|\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}\b|\b[A-ZА-ЯЁ]{2,12}[-_]\d[A-ZА-ЯЁ0-9_-]*\b|\b(?:\d{1,3}\.){3}\d{1,3}\b|\b\d{1,4}[-/.]\d{1,2}[-/.]\d{1,4}\b|\b[vV]?\d+(?:\.\d+){1,4}\b|\b[A-Za-z_][\w-]*(?:\.[A-Za-z_][\w.-]*)+\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex ProtectedSpanRegex();
}
