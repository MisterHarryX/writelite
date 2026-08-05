using System.Text.RegularExpressions;
using WriteLite.AI.Contracts;

namespace WriteLite.AI.Local;

/// <summary>
/// Decides when WriteLite-Qwen should run. Rules/spell/LT always run separately.
/// Neural path is opt-in and skipped for trivial / protected-only / cached inputs.
/// </summary>
public sealed partial class AiCallRouter
{
    public int MinChars { get; set; } = 12;

    public int MaxChars { get; set; } = 12_000;

    public bool RequireSentenceBoundary { get; set; } = false;

    public bool ShouldCallNeural(
        string text,
        AiModelProfile profile,
        bool neuralAvailable,
        bool forceDeepCheck = false)
    {
        if (!neuralAvailable || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // Explicit Lite without force → no neural.
        if (profile == AiModelProfile.Lite && !forceDeepCheck)
        {
            return false;
        }

        var t = text.Trim();
        if (t.Length < MinChars || t.Length > MaxChars)
        {
            return false;
        }

        // Single glyph / emoji-only
        if (t.Length <= 2)
        {
            return false;
        }

        if (IsProtectedOnly(t))
        {
            return false;
        }

        if (LooksLikeCodeOrJson(t))
        {
            return false;
        }

        // Prefer completed thoughts: sentence end, newline, or multi-word message.
        var words = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 3)
        {
            return false;
        }

        if (RequireSentenceBoundary && !EndsWithBoundary(t) && words.Length < 8)
        {
            return false;
        }

        // Standard/Quality/Auto with multi-word prose → call neural.
        // forceDeepCheck (PreferQwen) still requires ≥3 words above.
        if (forceDeepCheck || profile is AiModelProfile.Standard or AiModelProfile.Quality or AiModelProfile.Auto)
        {
            return t.Any(char.IsLetter);
        }

        // Ambiguous / multi-clause heuristics that benefit from context model.
        if (NeedsContextModel(t))
        {
            return true;
        }

        // Default: call for multi-word prose that is not trivial single-typo length.
        return words.Length >= 4 && t.Any(char.IsLetter);
    }

    public static bool IsProtectedOnly(string text)
    {
        var t = text.Trim();
        if (UrlOnly().IsMatch(t) || EmailOnly().IsMatch(t) || PathOnly().IsMatch(t))
        {
            return true;
        }

        // Pure number / version
        if (VersionOrNumberOnly().IsMatch(t))
        {
            return true;
        }

        return false;
    }

    public static bool LooksLikeCodeOrJson(string text)
    {
        var t = text.Trim();
        if (t.StartsWith('{') && t.EndsWith('}'))
        {
            return true;
        }

        if (t.Contains("```", StringComparison.Ordinal))
        {
            return true;
        }

        if (t.Contains("function ", StringComparison.Ordinal)
            || t.Contains("=>", StringComparison.Ordinal)
            || t.Contains("public class ", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    public static bool NeedsContextModel(string text)
    {
        // Missing punctuation across multi-clause Russian chat text.
        var letters = text.Count(char.IsLetter);
        var punct = text.Count(ch => ch is ',' or '.' or '!' or '?' or ';' or ':');
        if (letters >= 24 && punct == 0)
        {
            return true;
        }

        // Common conjunction chains without commas.
        if (ConjunctionNoComma().IsMatch(text))
        {
            return true;
        }

        // Mixed sentence starts without separators.
        if (text.Count(ch => ch == ' ') >= 6 && !text.Contains('.') && !text.Contains('?') && !text.Contains('!'))
        {
            return true;
        }

        return false;
    }

    private static bool EndsWithBoundary(string t)
        => t.Length > 0 && t[^1] is '.' or '!' or '?' or '…' or '\n';

    [GeneratedRegex(@"^https?://\S+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlOnly();

    [GeneratedRegex(@"^[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailOnly();

    [GeneratedRegex(@"^(?:[A-Za-z]:\\|\\\\|/)[^\r\n]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PathOnly();

    [GeneratedRegex(@"^[\d.\-_/]+$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionOrNumberOnly();

    [GeneratedRegex(@"\b(и|но|а|что|потому что|когда|если|and|but|because|when|if)\b[^,.!?]{8,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConjunctionNoComma();
}
