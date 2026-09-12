using WriteLite.Models;
using WriteLite.Resources;

namespace WriteLite.Services;

/// <summary>
/// The mutually exclusive conditions a correction card can be in.
/// </summary>
/// <remarks>
/// <para>There is deliberately no <c>Stale</c> phase. "The text this card describes has
/// changed" is not a condition a card may rest in — a card that refers to text which no
/// longer exists is worse than no card at all, and the «Текст изменился. Обновите
/// предложение.» dead end is exactly what this type exists to make unreachable. A text
/// change drives the card to <see cref="Checking"/> (the normal path — the new text is
/// re-analysed) or to <see cref="Hidden"/> (when the finding cannot be re-bound).</para>
///
/// <para><see cref="Failed"/> is reserved for a genuine processing failure — a field that
/// refused the write, a provider that was busy. Only that phase offers «Повторить».</para>
/// </remarks>
public enum CorrectionCardPhase
{
    /// <summary>No card. Nothing is rendered and no action belongs to anything.</summary>
    Hidden,

    /// <summary>An analysis is running for this word. Progress only — never a stale result.</summary>
    Checking,

    /// <summary>The user pressed the primary action and the write is in flight.</summary>
    Applying,

    /// <summary>A finished analysis with a replacement that genuinely differs from the original.</summary>
    Result,

    /// <summary>A finished analysis with no replacement worth offering. Informational, never an Apply.</summary>
    NoSuggestion,

    /// <summary>An attempted correction that the field refused. The only phase with «Повторить».</summary>
    Failed,
}

/// <summary>
/// Everything a correction card renders, derived from one phase.
/// </summary>
/// <remarks>
/// <para><b>The defect this replaces.</b> The card used to be a set of independent widgets —
/// a change row, an explanation, a status line, a recovery row, a suggestion chip — each
/// mutated by whichever code path ran last. <c>ShowForIssue</c> filled some of them and
/// <c>ShowApplyFeedback</c> filled others, with no phase between them, so a failure message
/// was laid <em>on top of</em> a finished result rather than replacing it. That is how one
/// card came to show a completed dictionary explanation, an orange apply action, a red
/// «Текст изменился» warning and a «Повторить» button at the same time: four incompatible
/// states, each true of a different moment, all on screen at once.</para>
///
/// <para>Every visibility flag below is computed from <see cref="Phase"/>. Nothing sets them
/// individually, so no sequence of calls can produce a card that claims two things. The
/// window that renders this holds no state of its own beyond the last instance it was
/// given.</para>
/// </remarks>
public sealed record CorrectionCardState
{
    private CorrectionCardState()
    {
    }

    public static CorrectionCardState Hidden { get; } = new();

    public CorrectionCardPhase Phase { get; private init; } = CorrectionCardPhase.Hidden;

    /// <summary>The finding this card belongs to, already normalised for apply.</summary>
    public TextIssue? Issue { get; private init; }

    public IssueCategory Category { get; private init; } = IssueCategory.Orthography;

    public string CategoryLabel { get; private init; } = string.Empty;

    /// <summary>The struck-through "before" fragment. Only meaningful in a change row.</summary>
    public string OriginalDisplay { get; private init; } = string.Empty;

    /// <summary>The accented "after" fragment. Only meaningful in a change row.</summary>
    public string ReplacementDisplay { get; private init; } = string.Empty;

    /// <summary>The one-line title of an informational card — «Слово не найдено в словаре».</summary>
    public string Headline { get; private init; } = string.Empty;

    public string Explanation { get; private init; } = string.Empty;

    /// <summary>A processing failure, in the user's words. Empty in every other phase.</summary>
    public string StatusMessage { get; private init; } = string.Empty;

    /// <summary>The label on the one primary action. Never a progress word — §7.</summary>
    public string PrimaryActionLabel { get; private init; } = string.Empty;

    public string ProgressLabel { get; private init; } = string.Empty;

    /// <summary>
    /// Set in one place — the branch of <see cref="ForIssue"/> that decided there is a real
    /// correction — so no later transition can give a finding with nothing to show a change row.
    /// </summary>
    private bool HasChange { get; init; }

    private bool OffersCopy { get; init; }

    private bool OffersRetry { get; init; }

    /// <summary>The token «В словарь» would add, or null when this finding is not one word.</summary>
    public string? DictionaryWord { get; private init; }

    /// <summary>A progress bar and a caption, and nothing that needs a finished result.</summary>
    public bool ShowProgress => Phase is CorrectionCardPhase.Checking or CorrectionCardPhase.Applying;

    /// <summary>
    /// «original → replacement». Drawn only where a replacement genuinely differs, so
    /// «Проверяем → Проверяем» cannot be rendered — see <see cref="ForIssue"/>.
    /// </summary>
    /// <remarks>
    /// Kept while a write is in flight: the user pressed «Исправить» on a specific change and
    /// taking it off the card mid-apply loses the only statement of what is being written.
    /// It is gone in <see cref="CorrectionCardPhase.Checking"/>, where the analysis that
    /// would justify it has not finished.
    /// </remarks>
    public bool ShowChange => HasChange
        && Phase is CorrectionCardPhase.Result
            or CorrectionCardPhase.Applying
            or CorrectionCardPhase.Failed;

    public bool ShowHeadline => Phase == CorrectionCardPhase.NoSuggestion;

    public bool ShowExplanation =>
        Phase is CorrectionCardPhase.Result or CorrectionCardPhase.NoSuggestion or CorrectionCardPhase.Failed
        && Explanation.Length > 0;

    /// <summary>The large accent action. Exists only where there is something to apply.</summary>
    public bool ShowPrimaryAction => Phase == CorrectionCardPhase.Result;

    public bool ShowStatus => Phase == CorrectionCardPhase.Failed && StatusMessage.Length > 0;

    public bool ShowCopy => Phase == CorrectionCardPhase.Failed && OffersCopy;

    /// <summary>«Повторить» belongs to a failed write, never to a text change — §4.</summary>
    public bool ShowRetry => Phase == CorrectionCardPhase.Failed && OffersRetry;

    public bool ShowDictionary =>
        Phase is CorrectionCardPhase.Result or CorrectionCardPhase.NoSuggestion or CorrectionCardPhase.Failed
        && Category == IssueCategory.Orthography
        && DictionaryWord is not null;

    public bool ShowIgnore => Phase is CorrectionCardPhase.Result
        or CorrectionCardPhase.NoSuggestion
        or CorrectionCardPhase.Failed;

    public bool IsVisible => Phase != CorrectionCardPhase.Hidden;

    /// <summary>
    /// The card for a finished analysis: a correction where there is one, an informational
    /// card where there is not.
    /// </summary>
    /// <remarks>
    /// <para>The split is decided on what the user would <em>see</em>, not on what the
    /// finding stores. <see cref="CorrectionCardText.IsNoOpForDisplay"/> also catches the
    /// cases where two different strings render as one — emphasis markers the analyzer
    /// injected, a difference past the truncation cap, line breaks that both draw as ↵ —
    /// because a card reading «Проверяем → Проверяем» with an apply action under it is a lie
    /// about the text whether or not the underlying strings happen to differ.</para>
    /// </remarks>
    public static CorrectionCardState ForIssue(TextIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        var normalized = CorrectionPresentation.NormalizeForApply(issue);
        var common = Common(normalized);

        if (CorrectionCardText.IsNoOpForDisplay(normalized))
        {
            return common with
            {
                Phase = CorrectionCardPhase.NoSuggestion,
                Headline = NoSuggestionHeadline(normalized),
                Explanation = NoSuggestionExplanation(normalized),
            };
        }

        return common with
        {
            Phase = CorrectionCardPhase.Result,
            HasChange = true,
            OriginalDisplay = CorrectionCardText.OriginalDisplay(normalized),
            ReplacementDisplay = CorrectionCardText.ReplacementDisplay(normalized),
            Explanation = normalized.Explanation ?? string.Empty,
            PrimaryActionLabel = PrimaryLabel(normalized),
        };
    }

    /// <summary>An analysis is in flight for the word this card is about.</summary>
    public static CorrectionCardState Checking(TextIssue? issue) => (issue is null
        ? new CorrectionCardState()
        : Common(CorrectionPresentation.NormalizeForApply(issue))) with
    {
        Phase = CorrectionCardPhase.Checking,
        ProgressLabel = Strings.Suggestions_Checking,
    };

    /// <summary>The user pressed the primary action; the write has not come back yet.</summary>
    public static CorrectionCardState Applying(TextIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return ForIssue(issue) with
        {
            Phase = CorrectionCardPhase.Applying,
            ProgressLabel = Strings.Corr_Applying,
        };
    }

    /// <summary>
    /// The correction was attempted and the field refused it.
    /// </summary>
    /// <remarks>
    /// Reachable only from an apply outcome, so «Повторить» can appear only where the write
    /// pipeline itself said a second attempt is worth making. A text change does not come
    /// through here — see <see cref="Recheck"/>.
    /// </remarks>
    public static CorrectionCardState Failed(TextIssue issue, string message, bool offerCopy, bool offerRetry)
    {
        ArgumentNullException.ThrowIfNull(issue);
        var result = ForIssue(issue);
        return result with
        {
            Phase = CorrectionCardPhase.Failed,
            PrimaryActionLabel = string.Empty,
            StatusMessage = message ?? string.Empty,
            OffersCopy = offerCopy && !string.IsNullOrEmpty(result.Issue?.Replacement),
            OffersRetry = offerRetry,
        };
    }

    /// <summary>
    /// The transition for "the text underneath this card changed".
    /// </summary>
    /// <remarks>
    /// It cannot produce a card that keeps the old result's actions: the phase becomes
    /// <see cref="CorrectionCardPhase.Checking"/> in the same frame the change is observed,
    /// so the apply action, the explanation and any failure row belonging to the previous
    /// text are gone before the user can reach them. What replaces them is decided by the
    /// next analysis, not here.
    /// </remarks>
    public CorrectionCardState Recheck() =>
        Phase == CorrectionCardPhase.Hidden ? this : Checking(Issue);

    private static CorrectionCardState Common(TextIssue issue) => new()
    {
        Issue = issue,
        Category = issue.Category,
        CategoryLabel = CorrectionCardText.CategoryLabelUpper(issue.Category),
        DictionaryWord = DictionaryWordPolicy.ResolveWord(issue),
    };

    /// <summary>
    /// One concrete action, never a state of the application — §7.
    /// </summary>
    /// <remarks>
    /// The chip used to be labelled with the replacement itself, which reads as an action
    /// only when the replacement happens to look like one. For the word «Проверяем» that
    /// produced an orange button saying «Проверяем», indistinguishable from a progress
    /// label. An insertion keeps its own phrasing — «Добавить запятую» is already a verb,
    /// and it is the one case with no "before" word for the change row to show.
    /// </remarks>
    private static string PrimaryLabel(TextIssue issue) =>
        CorrectionCardText.IsInsertion(issue)
            ? CorrectionPresentation.FormatChipLabel(issue)
            : Strings.Common_Fix;

    private static string NoSuggestionHeadline(TextIssue issue) =>
        issue.Category == IssueCategory.Orthography
            ? Strings.Corr_WordNotFound
            : issue.Title;

    /// <summary>
    /// Says which word the card is about.
    /// </summary>
    /// <remarks>
    /// An informational card offers «В словарь», and the user has to know what that button
    /// would add before pressing it. The change row that normally names the word is gone —
    /// there is no change — so the sentence has to carry it.
    /// </remarks>
    private static string NoSuggestionExplanation(TextIssue issue)
    {
        if (issue.Category != IssueCategory.Orthography) return issue.Explanation ?? string.Empty;

        return DictionaryWordPolicy.ResolveWord(issue) is { } word
            ? string.Format(Strings.Corr_NoConfidentFixForWord, word)
            : Strings.Corr_NoConfidentFix;
    }
}
