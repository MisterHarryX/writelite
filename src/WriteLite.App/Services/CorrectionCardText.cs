using System.Windows.Media;
using WriteLite.Language.Core;
using WriteLite.Models;
using Brush = System.Windows.Media.Brush;

namespace WriteLite.Services;

/// <summary>
/// Shared display formatting for correction cards.
/// </summary>
/// <remarks>
/// The in-app editor panel and the system-wide suggestions window show the same
/// correction, so they must format it identically — same whitespace glyphs, same
/// truncation, same category wording and colour. This type is the single source
/// for that; it holds presentation only and never alters the issue itself.
/// </remarks>
public static class CorrectionCardText
{
    private const int MaxFragmentLength = 80;

    /// <summary>Uppercase category name for the mono eyebrow on a card.</summary>
    public static string CategoryLabelUpper(IssueCategory category) => category switch
    {
        IssueCategory.Orthography => "ОРФОГРАФИЯ",
        IssueCategory.Grammar => "ГРАММАТИКА",
        IssueCategory.Punctuation => "ПУНКТУАЦИЯ",
        IssueCategory.Style => "СТИЛЬ",
        IssueCategory.Readability => "ОФОРМЛЕНИЕ",
        _ => "ЗАМЕЧАНИЕ"
    };

    /// <summary>Sentence-case category name for prose contexts.</summary>
    public static string CategoryLabel(IssueCategory category) => category switch
    {
        IssueCategory.Orthography => "Орфография",
        IssueCategory.Grammar => "Грамматика",
        IssueCategory.Punctuation => "Пунктуация",
        IssueCategory.Style => "Стиль",
        IssueCategory.Readability => "Оформление",
        _ => "Замечание"
    };

    /// <summary>
    /// The ERROR / WARNING / STYLE / INFORMATION badge, as the user reads it.
    /// </summary>
    /// <remarks>
    /// §14: a stylistic recommendation must not look like a grammatical error on the card.
    /// The category eyebrow says «СТИЛЬ» for both a redundancy suggestion and a repeated-word
    /// warning; this is the second half — whether WriteLite is claiming the text is wrong.
    /// </remarks>
    public static string ClassLabel(IssueClass issueClass) => issueClass switch
    {
        IssueClass.Error => "Ошибка",
        IssueClass.Warning => "Возможная ошибка",
        IssueClass.Style => "Рекомендация",
        _ => "Замечание",
    };

    /// <summary>How firmly the correction is held — §12.</summary>
    public static string CertaintyLabel(CorrectionCertainty certainty) => certainty switch
    {
        CorrectionCertainty.Certain => "Точно",
        CorrectionCertainty.Likely => "Вероятно",
        _ => "На ваше усмотрение",
    };

    public static (Brush Foreground, Brush Background) CategoryBrushes(IssueCategory category) => category switch
    {
        IssueCategory.Orthography => (ThemeResource.Brush("WlCatSpelling"), ThemeResource.Brush("WlCatSpellingBg")),
        IssueCategory.Punctuation => (ThemeResource.Brush("WlCatPunctuation"), ThemeResource.Brush("WlCatPunctuationBg")),
        IssueCategory.Grammar => (ThemeResource.Brush("WlCatGrammar"), ThemeResource.Brush("WlCatGrammarBg")),
        IssueCategory.Style => (ThemeResource.Brush("WlCatStyle"), ThemeResource.Brush("WlCatStyleBg")),
        IssueCategory.Readability => (ThemeResource.Brush("WlCatFormatting"), ThemeResource.Brush("WlCatFormattingBg")),
        _ => (ThemeResource.Brush("WlTextSecondary"), ThemeResource.Brush("WlRaised"))
    };

    /// <summary>
    /// The "before" fragment. An insertion has nothing to strike through, so it shows an
    /// em dash rather than an empty gap.
    /// </summary>
    public static string OriginalDisplay(TextIssue issue) =>
        IsInsertion(issue) ? "—" : Present(issue.Original, issue.Replacement ?? string.Empty);

    /// <summary>The "after" fragment, or a human phrase for a punctuation insertion.</summary>
    public static string ReplacementDisplay(TextIssue issue) =>
        IsInsertion(issue)
            ? CorrectionPresentation.FormatChipLabel(issue)
            : Present(issue.Replacement ?? string.Empty, issue.Original);

    /// <summary>
    /// The shared and changed fragments of a correction, for a card that emphasises what
    /// actually differs.
    /// </summary>
    /// <remarks>
    /// Presentation only. The text that gets written is <see cref="TextIssue.Replacement"/>,
    /// and nothing here is ever reassembled into it — see <see cref="CorrectionDiff"/>.
    /// </remarks>
    public static CorrectionDisplayDiff Diff(TextIssue issue)
        => CorrectionDiff.Compute(issue.Original, issue.Replacement ?? string.Empty);

    public static bool IsInsertion(TextIssue issue) =>
        CorrectionPresentation.IsTerminalPunctuationInsert(issue) || issue.Length == 0;

    /// <summary>
    /// Whether this finding has nothing a card could honestly draw as a change.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a display-level test and not just the data-level one.</b>
    /// <see cref="CorrectionCandidateValidityPolicy.IsIdenticalCorrection"/> compares the
    /// strings the analyzer produced, and every rendering path already filters on it. It is
    /// not sufficient, because this class deliberately transforms both sides before they
    /// reach the user: emphasis markers an analyzer injected are stripped, CR/LF and tabs
    /// collapse to one glyph each, and anything past
    /// <see cref="MaxFragmentLength"/> characters is truncated to a common ellipsis. Two
    /// strings that differ can therefore render as one and the same fragment — and the card
    /// then shows «Проверяем → Проверяем» over an orange apply button, which tells the user
    /// that pressing it will change the word when it will not.</para>
    ///
    /// <para>The rule is that the card is judged on what it draws. If both sides draw the
    /// same, there is no correction to offer, whatever the underlying strings say.</para>
    ///
    /// <para>An insertion is never a no-op: it has no "before" side to compare against, and
    /// its own guard is that <see cref="TextIssue.Replacement"/> is non-empty.</para>
    /// </remarks>
    public static bool IsNoOpForDisplay(TextIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        if (string.IsNullOrWhiteSpace(issue.Replacement)) return true;
        if (CorrectionCandidateValidityPolicy.IsIdenticalCorrection(issue.Original, issue.Replacement)) return true;
        if (IsInsertion(issue)) return false;

        return string.Equals(OriginalDisplay(issue), ReplacementDisplay(issue), StringComparison.Ordinal);
    }

    /// <summary>Where the issue came from, for the small provenance label on a card.</summary>
    public static string SourceLabel(TextIssue issue) =>
        issue.RuleId.StartsWith("ru.spelling", StringComparison.Ordinal)
        || issue.RuleId.StartsWith("en.spelling", StringComparison.Ordinal)
            ? "СЛОВАРЬ"
            : "ПРАВИЛА";

    private static string Present(string value, string counterpart)
    {
        var side = OneLine(StripMarkdownDecorations(value));
        var other = OneLine(StripMarkdownDecorations(counterpart));
        return VisualizeWhitespace(Cap(TrimSharedEdges(side, other)), Cap(other));
    }

    private static string OneLine(string value) => value.Replace("\r", " ").Replace("\n", " ");

    private static string Cap(string value) =>
        value.Length <= MaxFragmentLength ? value : value[..MaxFragmentLength] + "...";

    /// <summary>
    /// Drops edge whitespace that both sides have, and keeps edge whitespace that differs.
    /// </summary>
    /// <remarks>
    /// <para>The fragment used to be <c>Trim()</c>ed unconditionally, which deleted the only
    /// thing some corrections consist of: «,» → «, » arrived at the card as «,» → «,», and a
    /// doubled space became two empty sides with an arrow between them. The marker glyph that
    /// was supposed to rescue those cases never saw them, because the trim ran first.</para>
    ///
    /// <para>Whitespace shared with the other side is still dropped. It sits against the card
    /// border, it is the same on both sides, and marking it would say a change happened where
    /// none did — which is the same class of lie as marking the space inside «в общем».</para>
    /// </remarks>
    private static string TrimSharedEdges(string value, string counterpart)
    {
        if (value.Length == 0 || value.All(char.IsWhiteSpace)) return value;

        var start = 0;
        var sharedLead = Math.Min(LeadingWhitespace(value), LeadingWhitespace(counterpart));
        if (LeadingWhitespace(value) == LeadingWhitespace(counterpart)) start = sharedLead;

        var end = value.Length;
        if (TrailingWhitespace(value) == TrailingWhitespace(counterpart))
        {
            end -= Math.Min(TrailingWhitespace(value), TrailingWhitespace(counterpart));
        }

        return end > start ? value[start..end] : value;
    }

    private static int LeadingWhitespace(string value)
    {
        var count = 0;
        while (count < value.Length && char.IsWhiteSpace(value[count])) count++;
        return count;
    }

    private static int TrailingWhitespace(string value)
    {
        var count = 0;
        while (count < value.Length && char.IsWhiteSpace(value[value.Length - 1 - count])) count++;
        return count;
    }

    /// <summary>
    /// Marks whitespace only where whitespace is the thing being corrected.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is not a blanket substitution.</b> It used to be: every space in a
    /// fragment became a middle dot, so that a missing space would not render as an empty
    /// card. That is right for the edit it was written for and wrong for every correction
    /// whose replacement is more than one word — «вообщем» → «в общем» reached the user as
    /// «в·общем», and «потомучто» → «потому·что». A correct answer drawn as a broken one is
    /// the same defect as a broken answer, because the user cannot tell which they are
    /// looking at, and it breaks the rule the product is held to: what the card shows and
    /// what gets written must agree.</para>
    ///
    /// <para>The test is now whether the space is <em>invisible</em> rather than whether it
    /// is a space. A fragment that is nothing but whitespace has no other content to show;
    /// whitespace at either edge sits against a card border and cannot be seen; a run of two
    /// or more spaces is indistinguishable from one. Those are marked. A single interior
    /// space between two visible characters is left alone, because a reader can already see
    /// it, and because it is almost always part of the answer rather than the change.</para>
    ///
    /// <para>Tabs and line breaks are always marked: unlike a space they have no plausible
    /// reading as ordinary word spacing, and a card is one line.</para>
    /// </remarks>
    private static string VisualizeWhitespace(string value, string counterpart)
    {
        if (value.Length == 0) return value;

        var structural = value
            .Replace("\r\n", "↵")
            .Replace('\n', '↵')
            .Replace('\r', '↵')
            .Replace('\t', '→');

        if (structural.All(char.IsWhiteSpace) || counterpart.All(char.IsWhiteSpace))
        {
            return structural.Replace(' ', '·');
        }

        var builder = new System.Text.StringBuilder(structural.Length);
        for (var i = 0; i < structural.Length; i++)
        {
            if (structural[i] != ' ')
            {
                builder.Append(structural[i]);
                continue;
            }

            var atEdge = i == 0 || i == structural.Length - 1;
            var doubled = (i > 0 && structural[i - 1] == ' ')
                          || (i + 1 < structural.Length && structural[i + 1] == ' ');
            builder.Append(atEdge || doubled ? '·' : ' ');
        }

        return builder.ToString();
    }

    /// <summary>Display-only: analyzer-injected emphasis markers never reach a card.</summary>
    private static string StripMarkdownDecorations(string value) => value
        .Replace("**", string.Empty, StringComparison.Ordinal)
        .Replace("__", string.Empty, StringComparison.Ordinal)
        .Replace("~~", string.Empty, StringComparison.Ordinal);
}
