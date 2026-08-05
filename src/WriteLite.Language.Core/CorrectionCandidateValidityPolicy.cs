using System.Globalization;
using System.Text;

namespace WriteLite.Language.Core;

/// <summary>
/// Central policy: never surface or apply a candidate that does not change the text meaningfully.
/// </summary>
public static class CorrectionCandidateValidityPolicy
{
    public static bool IsIdenticalCorrection(string? original, string? replacement)
    {
        if (original is null || replacement is null) return true;
        if (string.IsNullOrEmpty(replacement) && string.IsNullOrEmpty(original)) return true;
        if (string.Equals(original, replacement, StringComparison.Ordinal)) return true;

        var o = NormalizeForIdentity(original);
        var r = NormalizeForIdentity(replacement);
        if (string.Equals(o, r, StringComparison.Ordinal)) return true;

        // Invisible / format-only differences
        if (string.Equals(StripInvisible(original), StripInvisible(replacement), StringComparison.Ordinal)
            && string.Equals(o, r, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Case-only difference is NOT identical when the rule is capitalization (caller decides).
    /// Default: case-insensitive equality without real letter change is still identical for spelling.
    /// </summary>
    public static bool IsMeaninglessSpellingCandidate(string original, string replacement)
    {
        if (IsIdenticalCorrection(original, replacement)) return true;
        if (replacement is null) return true;
        if (replacement.Length == 0 && original.Length > 0)
        {
            // Empty replacement may be valid only for pure deletion rules (not spelling).
            return true;
        }

        // Whitespace-only differences are meaningful typography (not spelling fold).
        if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(replacement))
        {
            return string.Equals(original, replacement, StringComparison.Ordinal);
        }

        // After lowercasing + ё→е, same form means no orthographic fix
        if (string.Equals(FoldSpelling(original), FoldSpelling(replacement), StringComparison.Ordinal))
        {
            // Allow pure capitalization fixes: Привет vs привет
            if (!string.Equals(original, replacement, StringComparison.Ordinal)
                && string.Equals(original, replacement, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.Equals(original, replacement, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public static IReadOnlyList<string> FilterSuggestions(
        string original,
        IEnumerable<string?> candidates,
        int max = 5)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in candidates)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var candidate = raw.Trim();
            if (IsMeaninglessSpellingCandidate(original, candidate)) continue;
            if (!seen.Add(candidate)) continue;
            result.Add(candidate);
            if (result.Count >= max) break;
        }

        return result;
    }

    public static bool WouldChangeText(string fullText, int start, int length, string replacement)
    {
        if (start < 0 || length < 0 || start > fullText.Length || start + length > fullText.Length)
            return false;
        var original = length == 0 ? string.Empty : fullText.Substring(start, length);
        if (IsIdenticalCorrection(original, replacement)) return false;
        var applied = fullText.Remove(start, length).Insert(start, replacement ?? string.Empty);
        return !string.Equals(fullText, applied, StringComparison.Ordinal);
    }

    public static string NormalizeForIdentity(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var nfc = value.Normalize(NormalizationForm.FormC);
        // Do NOT Trim: pure whitespace corrections ("  " → " ") must remain distinct.
        return nfc.Replace('\u00A0', ' ').Replace("\u200B", string.Empty);
    }

    public static string FoldSpelling(string value)
    {
        // Spelling fold may trim edges of letter tokens only after NFC.
        var n = NormalizeForIdentity(value).ToLower(CultureInfo.InvariantCulture).Replace('ё', 'е');
        return n;
    }

    private static string StripInvisible(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsControl(ch) || ch is '\u200B' or '\uFEFF' or '\u00AD') continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
