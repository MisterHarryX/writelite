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

    /// <summary>
    /// The shortest a word may be and still count as evidence that a split is a real one.
    /// </summary>
    /// <remarks>
    /// Two letters is where Hunspell's word-splitting stops being informative: «ро», «Се»,
    /// «с» are all in a Russian lexicon in some role, so a membership test alone accepts
    /// «ро ботает» for «роботает». Three is the shortest length at which "both halves are
    /// words" carries any weight.
    /// </remarks>
    private const int MinimumSplitPartLength = 3;

    /// <summary>True when the candidate turns one token into several by adding whitespace.</summary>
    public static bool IsSplitCandidate(string? original, string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        var hasInteriorSpace = false;
        for (var i = 1; i < candidate.Length - 1; i++)
        {
            if (char.IsWhiteSpace(candidate[i])) { hasInteriorSpace = true; break; }
        }

        if (!hasInteriorSpace) return false;
        return original is null || !original.Any(char.IsWhiteSpace);
    }

    /// <summary>
    /// Whether a lexicon may offer a candidate that inserts whitespace into a single token.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this gate exists.</b> Hunspell's suggester tries splitting an unknown
    /// word in two, and reports the result as an ordinary candidate string with a space in
    /// it. For «роботает» it offers «ро ботает»; for «Сечас», «Се час» and «Сеча с»; for
    /// «пожалуста», «пожал уста». Nothing downstream distinguished those from an ordinary
    /// one-word suggestion, so a split could become the top candidate, be written into the
    /// user's field, and be drawn on the card as a word broken into pieces — the reported
    /// defect, exactly.</para>
    ///
    /// <para><b>Why it is not a blanket ban.</b> Splitting is sometimes the whole correction:
    /// «необходимобыло» → «необходимо было» is what the user meant, and «потомучто» →
    /// «потому что» is a rule of Russian orthography. The test is therefore evidentiary
    /// rather than categorical — the split must be pure (adding whitespace and changing
    /// nothing else), it must produce exactly two parts, and both parts must be real words
    /// long enough for that to mean something.</para>
    ///
    /// <para>Curated corrections do not come through here. A table that says «вкурсе» →
    /// «в курсе» is a stated fact about Russian, not a guess from an edit-distance ranker,
    /// and the one-letter preposition it produces would fail this test for good reasons that
    /// do not apply to it.</para>
    /// </remarks>
    public static bool IsAdmissibleSplit(string original, string candidate, Func<string, bool> isKnownWord)
    {
        ArgumentNullException.ThrowIfNull(isKnownWord);
        if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(candidate)) return false;

        var parts = candidate.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;

        // A split that also changes letters is two guesses stacked on one another.
        if (!string.Equals(
                string.Concat(parts),
                new string(original.Where(c => !char.IsWhiteSpace(c)).ToArray()),
                StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (part.Length < MinimumSplitPartLength) return false;
            if (!isKnownWord(part)) return false;
        }

        return true;
    }

    /// <summary>
    /// Filters a curated correction, which is trusted to split a token if it says so.
    /// </summary>
    /// <remarks>
    /// «вкурсе» → «в курсе» and «потомучто» → «потому что» come from a hand-written table of
    /// stated Russian orthography, not from an edit-distance ranker, and the one-letter
    /// preposition they produce is the correct answer rather than a suggester artefact. They
    /// still go through the identity and no-op checks; only the split gate — which exists to
    /// judge guesses — is skipped.
    /// </remarks>
    public static IReadOnlyList<string> FilterCuratedSuggestions(
        string original,
        IEnumerable<string?> candidates,
        int max = 5)
        => FilterSuggestions(original, candidates, max, isKnownWord: static _ => true, splitsAlreadyJudged: true);

    /// <param name="isKnownWord">
    /// Membership test used to judge candidates that split the token in two. When null —
    /// the default, for callers with no lexicon to hand — every such candidate is dropped,
    /// because "both halves are words" is the only evidence that makes one safe.
    /// </param>
    /// <param name="splitsAlreadyJudged">
    /// Set by callers filtering candidates that a lexicon has already put through the split
    /// gate, so that a second pass here does not throw away a decision that was made with
    /// more evidence than this one has.
    /// </param>
    public static IReadOnlyList<string> FilterSuggestions(
        string original,
        IEnumerable<string?> candidates,
        int max = 5,
        Func<string, bool>? isKnownWord = null,
        bool splitsAlreadyJudged = false)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in candidates)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var candidate = raw.Trim();
            if (IsMeaninglessSpellingCandidate(original, candidate)) continue;
            if (!splitsAlreadyJudged
                && IsSplitCandidate(original, candidate)
                && (isKnownWord is null || !IsAdmissibleSplit(original, candidate, isKnownWord)))
            {
                continue;
            }

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
