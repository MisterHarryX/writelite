using WriteLite.Models;

namespace WriteLite.Services;

/// <summary>
/// Follows an open correction card across an edit to the text underneath it.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> When the text changed under an open card, nothing invalidated
/// the card. It kept its result, its explanation and its apply action, all describing a span
/// that had moved or vanished; pressing the action then failed the range check deep in the
/// write path and the card reported «Текст изменился. Обновите предложение.» — a message the
/// user can do nothing useful with, on top of a result that was already wrong. The text
/// changing is not a failure, so it must not be reported as one: the card either follows the
/// word to its new position and shows what the new analysis says about it, or it closes.</para>
///
/// <para>The mapping is the same one <see cref="IssueSetRebase"/> uses for an apply, except
/// that the shape of the edit is not known in advance here — the user typed it in another
/// application. It is recovered from the common prefix and suffix of the two texts, which is
/// exact for the single contiguous edit a keystroke makes and conservative for anything else:
/// a span that overlaps the changed region is not guessed at, it is dropped.</para>
/// </remarks>
public static class CorrectionCardRebinder
{
    /// <summary>The span in <paramref name="after"/> that held the same characters, or null.</summary>
    /// <remarks>
    /// Null means the edit reached into the span itself. The characters the card was about
    /// are not there any more, so there is no position to follow it to.
    /// </remarks>
    public static (int Start, int Length)? MapSpan(string before, string after, int start, int length)
    {
        before ??= string.Empty;
        after ??= string.Empty;
        if (start < 0 || length < 0 || start + length > before.Length) return null;
        if (string.Equals(before, after, StringComparison.Ordinal)) return (start, length);

        var prefix = 0;
        var limit = Math.Min(before.Length, after.Length);
        while (prefix < limit && before[prefix] == after[prefix]) prefix++;

        var suffix = 0;
        while (suffix < limit - prefix
               && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix])
        {
            suffix++;
        }

        var changedEnd = before.Length - suffix;
        var end = start + length;

        // Entirely before the edit: the characters did not move.
        if (end <= prefix) return (start, length);

        // Entirely after it: everything shifted by the length the edit added or removed.
        if (start >= changedEnd) return (start + (after.Length - before.Length), length);

        return null;
    }

    /// <summary>
    /// The finding in <paramref name="currentIssues"/> that continues <paramref name="issue"/>,
    /// or null when the card should close.
    /// </summary>
    /// <remarks>
    /// <para>A card is allowed to survive an edit only where the identification is certain:
    /// the span maps forward untouched, and the new analysis reports a finding of the same
    /// category over the same characters. Everything else closes the card. Following a word
    /// on weaker evidence would put the previous result back on screen against text it was
    /// not computed from, which is the defect this is here to remove.</para>
    ///
    /// <para>The returned finding is always one the current analysis produced, never the old
    /// one with a new offset — the card must show what is true of the text now.</para>
    /// </remarks>
    public static TextIssue? Rebind(
        string previousText,
        TextIssue issue,
        string currentText,
        IReadOnlyList<TextIssue> currentIssues)
    {
        ArgumentNullException.ThrowIfNull(issue);
        currentIssues ??= [];

        if (MapSpan(previousText, currentText, issue.Start, issue.Length) is not { } span) return null;
        if (!IssueProtectedRangeFilter.IsCurrentExactRange(currentText, issue with { Start = span.Start }))
        {
            return null;
        }

        var candidate = CorrectionInteractionResolver.FindIssueAtRange(currentIssues, span.Start, span.Length);
        return candidate is not null && candidate.Category == issue.Category ? candidate : null;
    }
}
