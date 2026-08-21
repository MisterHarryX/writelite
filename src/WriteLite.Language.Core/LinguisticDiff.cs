namespace WriteLite.Language.Core;

/// <summary>What kind of language change an edit represents.</summary>
/// <remarks>
/// The type is not decoration: it decides how the edit is presented, what confidence it
/// deserves, and whether a zero-length span is legitimate. An insertion with an empty
/// original is a real correction when the inserted material is a comma, and a bug when it
/// is two letters sliced out of the middle of a word.
/// </remarks>
public enum TextEditType
{
    /// <summary>One word replaced by another: <c>был → были</c>.</summary>
    WordReplacement = 0,

    /// <summary>A word the original did not have.</summary>
    WordInsertion = 1,

    /// <summary>A word removed, usually a repetition.</summary>
    WordDeletion = 2,

    /// <summary>A punctuation mark the original did not have — the missing comma case.</summary>
    PunctuationInsertion = 3,

    /// <summary>A punctuation mark removed.</summary>
    PunctuationDeletion = 4,

    /// <summary>One mark exchanged for another: <c>. → ?</c>, <c>- → —</c>.</summary>
    PunctuationReplacement = 5,

    /// <summary>Spacing only: a missing space after a comma, a doubled space.</summary>
    WhitespaceCorrection = 6,

    /// <summary>
    /// Several adjacent words replaced together because the correction is not separable:
    /// <c>не смотря на → несмотря на</c>.
    /// </summary>
    PhraseReplacement = 7,
}

/// <summary>
/// One linguistic edit against the original text: an apply-safe span plus what it means.
/// </summary>
/// <param name="Start">Offset into the original string, in UTF-16 code units.</param>
/// <param name="Length">Length of the replaced span; zero for an insertion.</param>
/// <param name="Original">Exactly <c>original.Substring(Start, Length)</c>.</param>
/// <param name="Replacement">The text that takes its place; empty for a deletion.</param>
/// <param name="EditType">What kind of change this is.</param>
/// <param name="WordCount">Significant tokens the edit spans, on the wider of the two sides.</param>
public readonly record struct TextEdit(
    int Start,
    int Length,
    string Original,
    string Replacement,
    TextEditType EditType,
    int WordCount)
{
    /// <summary>True when the edit touches more than one significant token on either side.</summary>
    public bool IsPhraseLevel => EditType is TextEditType.PhraseReplacement;

    /// <summary>
    /// An insertion whose inserted material is not punctuation and not a whole word — the
    /// shape a character diff produces and a linguistic one must not.
    /// </summary>
    public bool IsSubWordFragment
        => Length == 0
           && Replacement.Length > 0
           && EditType is not (TextEditType.PunctuationInsertion or TextEditType.WhitespaceCorrection)
           && !Replacement.Any(char.IsLetterOrDigit);
}

/// <summary>
/// Turns "the model rewrote this sentence" into edits a person would recognise as
/// corrections.
/// </summary>
/// <remarks>
/// The path this replaces trimmed the common character prefix and suffix first and only
/// then looked for tokens, which is why <c>был → были</c> arrived as <c>'' → 'ы'</c> and
/// <c>сторонами → сторон нами</c> arrived as <c>'сторон' → 'нами'</c>. Eighteen per cent of
/// the local model's false positives in the Phase 4 corpus were that shape — fragments
/// manufactured here, not mistakes the model made.
///
/// So alignment happens on tokens from the start. Spans therefore land on word boundaries
/// by construction rather than by being snapped back to them afterwards, and an empty
/// original span can only mean a genuine insertion.
///
/// Two failure modes are deliberately guarded against, because they are opposites and it is
/// easy to fix one by causing the other:
///
/// <list type="bullet">
/// <item>Fragmenting — one linguistic correction arriving as several character edits. Token
/// alignment removes it structurally.</item>
/// <item>Over-merging — two independent corrections arriving as one sentence-wide rewrite.
/// A missing comma and a wrong ending in the same sentence must stay two edits, because a
/// user who agrees with one may not agree with the other. Alignment on significant tokens
/// keeps them separate, and <see cref="SplitPairwise"/> re-separates a wide hunk whose sides
/// pair up word for word.</item>
/// </list>
/// </remarks>
public static class LinguisticDiff
{
    /// <summary>
    /// Above this many significant tokens on either side, alignment is abandoned rather than
    /// run at O(n·m) on text the model was never supposed to be handed.
    /// </summary>
    /// <remarks>
    /// Sized against what can actually reach here rather than against the algorithm: the
    /// local model runs with a 768-token context, so anything it rewrote is well inside this,
    /// and a caller that hands over more than a page has already lost the thread. The
    /// quadratic table at this cap is about 5 MB and a few milliseconds — bounded, and off
    /// the keystroke path in any case, since this only runs on a model response.
    /// </remarks>
    internal const int MaxAlignableTokens = 1200;

    /// <summary>
    /// A phrase edit wider than this stops being a correction and starts being a rewrite.
    /// Reported through <see cref="TextEdit.WordCount"/> so the benchmark can measure it.
    /// </summary>
    public const int OverWideWordCount = 6;

    /// <summary>Aligns two versions of a text and reports what changed, in language units.</summary>
    /// <remarks>
    /// Returns an empty list rather than a whole-text block when the two sides are too far
    /// apart to align. A caller that wants the block anyway can construct it; a caller that
    /// gets one it did not ask for reports a sentence rewrite as a spelling correction.
    /// </remarks>
    public static IReadOnlyList<TextEdit> Compute(string original, string corrected)
    {
        if (string.IsNullOrEmpty(original) || corrected is null)
        {
            return [];
        }

        if (string.Equals(original, corrected, StringComparison.Ordinal))
        {
            return [];
        }

        var a = Tokenize(original);
        var b = Tokenize(corrected);
        if (a.Count > MaxAlignableTokens || b.Count > MaxAlignableTokens)
        {
            return [];
        }

        if (a.Count == 0 || b.Count == 0)
        {
            // One side has no words or punctuation at all. There is nothing to align against,
            // and the whole-span replacement that would result is exactly the block rewrite
            // this type exists to avoid.
            return [];
        }

        var anchors = LongestCommonSubsequence(a, b);

        // Too little in common to be an alignment. Below half the shorter side matched, this
        // is a different sentence rather than a corrected one, and the only edits derivable
        // from it are the whole-text block rewrites this type exists to avoid. A one-token
        // side is exempt: that is an ordinary word replacement, not a rewrite.
        var shorterSide = Math.Min(a.Count, b.Count);
        if (shorterSide > 1 && anchors.Count * 2 < shorterSide)
        {
            return [];
        }

        var edits = new List<TextEdit>();

        var ai = 0;
        var bi = 0;
        var anchor = 0;

        while (ai < a.Count || bi < b.Count)
        {
            if (anchor < anchors.Count && ai == anchors[anchor].A && bi == anchors[anchor].B)
            {
                // Matched pair: the tokens are identical, but the gap in front of them may
                // not be — "привет ,мир" and "привет, мир" have the same tokens throughout.
                AddGapEditIfAny(edits, original, corrected, a, b, ai, bi);
                ai++;
                bi++;
                anchor++;
                continue;
            }

            var aStart = ai;
            var bStart = bi;
            var nextA = anchor < anchors.Count ? anchors[anchor].A : a.Count;
            var nextB = anchor < anchors.Count ? anchors[anchor].B : b.Count;

            while (ai < nextA) ai++;
            while (bi < nextB) bi++;

            AddHunk(edits, original, corrected, a, b, aStart, ai, bStart, bi);
        }

        return edits;
    }

    /// <summary>
    /// A run of tokens present on one side and not the other, turned into one or more edits.
    /// </summary>
    private static void AddHunk(
        List<TextEdit> edits,
        string original,
        string corrected,
        IReadOnlyList<Token> a,
        IReadOnlyList<Token> b,
        int aStart,
        int aEnd,
        int bStart,
        int bEnd)
    {
        var aCount = aEnd - aStart;
        var bCount = bEnd - bStart;
        if (aCount == 0 && bCount == 0)
        {
            return;
        }

        // Pure insertion. The span is empty and sits at the end of the preceding token, so
        // "думаю" + "," reads as "insert a comma after думаю" rather than as a replacement
        // of whatever character happened to follow.
        if (aCount == 0)
        {
            var punctuationOnly = AllPunctuation(b, bStart, bEnd);
            var at = aStart > 0 ? a[aStart - 1].End : 0;
            var inserted = JoinInserted(corrected, b, bStart, bEnd, punctuationOnly, atStartOfText: aStart == 0);
            if (inserted.Length == 0)
            {
                return;
            }

            edits.Add(new TextEdit(
                at,
                0,
                string.Empty,
                inserted,
                punctuationOnly ? TextEditType.PunctuationInsertion : TextEditType.WordInsertion,
                bCount));
            return;
        }

        var spanStart = a[aStart].Start;
        var spanEnd = a[aEnd - 1].End;

        // Pure deletion. The span swallows one adjacent separator, or removing the second
        // "очень" from "очень очень важно" leaves the two spaces that surrounded it.
        if (bCount == 0)
        {
            if (aStart > 0)
            {
                spanStart = a[aStart - 1].End;
            }
            else if (aEnd < a.Count)
            {
                spanEnd = a[aEnd].Start;
            }

            edits.Add(new TextEdit(
                spanStart,
                spanEnd - spanStart,
                original[spanStart..spanEnd],
                string.Empty,
                AllPunctuation(a, aStart, aEnd) ? TextEditType.PunctuationDeletion : TextEditType.WordDeletion,
                aCount));
            return;
        }

        var originalText = original[spanStart..spanEnd];

        var replacement = corrected[b[bStart].Start..b[bEnd - 1].End];

        // Independent corrections that merely landed in the same hunk are separated back
        // out: two words replaced side by side are two edits a user can accept separately.
        if (aCount == bCount && aCount > 1 && SplitPairwise(edits, original, corrected, a, b, aStart, bStart, aCount))
        {
            return;
        }

        edits.Add(new TextEdit(
            spanStart,
            spanEnd - spanStart,
            originalText,
            replacement,
            ClassifyReplacement(a, aStart, aEnd, b, bStart, bEnd),
            Math.Max(aCount, bCount)));
    }

    /// <summary>
    /// Splits an equal-length hunk into per-token edits when each pair is recognisably the
    /// same word corrected, rather than a phrase rewritten.
    /// </summary>
    /// <remarks>
    /// Returns false — leaving the hunk whole — when any pair is unrelated, because that is
    /// the case where the phrase really is the unit of correction and splitting it would
    /// offer the user two edits that only make sense together.
    /// </remarks>
    private static bool SplitPairwise(
        List<TextEdit> edits,
        string original,
        string corrected,
        IReadOnlyList<Token> a,
        IReadOnlyList<Token> b,
        int aStart,
        int bStart,
        int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (!AreRelatedForms(a[aStart + i].Value, b[bStart + i].Value))
            {
                return false;
            }
        }

        for (var i = 0; i < count; i++)
        {
            var at = a[aStart + i];
            var bt = b[bStart + i];
            if (string.Equals(at.Value, bt.Value, StringComparison.Ordinal))
            {
                continue;
            }

            edits.Add(new TextEdit(
                at.Start,
                at.Length,
                original.Substring(at.Start, at.Length),
                corrected.Substring(bt.Start, bt.Length),
                at.IsPunctuation || bt.IsPunctuation
                    ? TextEditType.PunctuationReplacement
                    : TextEditType.WordReplacement,
                1));
        }

        return true;
    }

    /// <summary>
    /// Two tokens are the same word corrected when they are close enough that one is
    /// plausibly a misspelling of the other, not a different word.
    /// </summary>
    private static bool AreRelatedForms(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;

        var longer = Math.Max(a.Length, b.Length);
        if (longer == 0) return true;

        // Both punctuation is always a pairing: "." for "?" is one decision, not a phrase.
        if (IsPunctuationToken(a) && IsPunctuationToken(b)) return true;
        if (IsPunctuationToken(a) != IsPunctuationToken(b)) return false;

        var budget = longer <= 4 ? 1 : longer <= 8 ? 2 : 3;
        return Levenshtein(a, b, budget) <= budget;
    }

    /// <summary>
    /// Emits an edit when two identical tokens are separated differently — a missing space
    /// after a comma, a doubled space, a space before a full stop.
    /// </summary>
    private static void AddGapEditIfAny(
        List<TextEdit> edits,
        string original,
        string corrected,
        IReadOnlyList<Token> a,
        IReadOnlyList<Token> b,
        int ai,
        int bi)
    {
        var aGapStart = ai > 0 ? a[ai - 1].End : 0;
        var aGapEnd = a[ai].Start;
        var bGapStart = bi > 0 ? b[bi - 1].End : 0;
        var bGapEnd = b[bi].Start;

        // Only the space between two tokens is a whitespace correction. Leading text before
        // the first token belongs to whoever owns the paragraph, not to this diff.
        if (ai == 0 || bi == 0)
        {
            return;
        }

        var aGap = original[aGapStart..aGapEnd];
        var bGap = corrected[bGapStart..bGapEnd];
        if (string.Equals(aGap, bGap, StringComparison.Ordinal))
        {
            return;
        }

        // A gap that is not pure whitespace on both sides is punctuation that alignment
        // already handled or should have; do not double-report it here.
        if (!IsWhitespaceOnly(aGap) || !IsWhitespaceOnly(bGap))
        {
            return;
        }

        edits.Add(new TextEdit(
            aGapStart,
            aGap.Length,
            aGap,
            bGap,
            TextEditType.WhitespaceCorrection,
            1));
    }

    private static TextEditType ClassifyReplacement(
        IReadOnlyList<Token> a,
        int aStart,
        int aEnd,
        IReadOnlyList<Token> b,
        int bStart,
        int bEnd)
    {
        if (aEnd - aStart == 1 && bEnd - bStart == 1)
        {
            return a[aStart].IsPunctuation && b[bStart].IsPunctuation
                ? TextEditType.PunctuationReplacement
                : TextEditType.WordReplacement;
        }

        return TextEditType.PhraseReplacement;
    }

    /// <summary>
    /// Renders inserted tokens with the spacing they need to read correctly once applied.
    /// </summary>
    /// <remarks>
    /// A comma attaches to the word before it and takes no space at all; an inserted word
    /// needs one, or "думаю что" becomes "думаючто". At the very start of the text there is
    /// no preceding word to attach to, so the space goes on the other side.
    /// </remarks>
    private static string JoinInserted(
        string corrected,
        IReadOnlyList<Token> b,
        int bStart,
        int bEnd,
        bool punctuationOnly,
        bool atStartOfText)
    {
        var text = corrected[b[bStart].Start..b[bEnd - 1].End];
        if (punctuationOnly)
        {
            return text;
        }

        return atStartOfText ? text + " " : " " + text;
    }

    private static bool AllPunctuation(IReadOnlyList<Token> tokens, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (!tokens[i].IsPunctuation) return false;
        }

        return end > start;
    }

    private static bool IsWhitespaceOnly(string value)
    {
        foreach (var ch in value)
        {
            if (!char.IsWhiteSpace(ch)) return false;
        }

        return true;
    }

    private static bool IsPunctuationToken(string value)
        => value.Length > 0 && !value.Any(char.IsLetterOrDigit);

    /// <summary>
    /// Significant tokens only: words and punctuation, with whitespace left in the gaps
    /// between them.
    /// </summary>
    /// <remarks>
    /// Aligning on whitespace as if it were a token makes an inserted comma compete with the
    /// space beside it for the same anchor, and the alignment that wins is arbitrary.
    /// Whitespace differences are recovered afterwards by comparing the gaps between tokens
    /// that did align.
    /// </remarks>
    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            var start = i;
            if (IsWordChar(text[i]))
            {
                while (i < text.Length && IsWordChar(text[i]))
                {
                    i++;
                }

                // A hyphen only stays inside the word when a letter follows it, so
                // "чёрно-белый" is one token and "слово —" is not.
                while (i + 1 < text.Length && (text[i] == '-' || text[i] == '\'') && IsWordChar(text[i + 1]))
                {
                    i++;
                    while (i < text.Length && IsWordChar(text[i])) i++;
                }

                tokens.Add(new Token(start, text[start..i], IsPunctuation: false));
                continue;
            }

            i++;
            tokens.Add(new Token(start, text[start..i], IsPunctuation: true));
        }

        return tokens;
    }

    private static bool IsWordChar(char ch) => char.IsLetterOrDigit(ch) || ch == '_';

    /// <summary>
    /// Token-level LCS. Case and ё/е folding are deliberately absent: <c>придет → придёт</c>
    /// is a correction this project ships, and folding it away here would hide it.
    /// </summary>
    private static List<(int A, int B)> LongestCommonSubsequence(
        IReadOnlyList<Token> a,
        IReadOnlyList<Token> b)
    {
        var n = a.Count;
        var m = b.Count;
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                dp[i, j] = string.Equals(a[i].Value, b[j].Value, StringComparison.Ordinal)
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var path = new List<(int, int)>();
        var x = 0;
        var y = 0;
        while (x < n && y < m)
        {
            if (string.Equals(a[x].Value, b[y].Value, StringComparison.Ordinal))
            {
                path.Add((x, y));
                x++;
                y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                x++;
            }
            else
            {
                y++;
            }
        }

        return path;
    }

    /// <summary>Levenshtein distance, abandoned once it exceeds <paramref name="budget"/>.</summary>
    private static int Levenshtein(string a, string b, int budget)
    {
        if (Math.Abs(a.Length - b.Length) > budget) return budget + 1;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var best = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                if (current[j] < best) best = current[j];
            }

            if (best > budget) return budget + 1;
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private readonly record struct Token(int Start, string Value, bool IsPunctuation)
    {
        public int Length => Value.Length;

        public int End => Start + Value.Length;
    }
}
