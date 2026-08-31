using WriteLite.Models;

namespace WriteLite.Services;

/// <summary>What an open correction card should do about the snapshot that just arrived.</summary>
public enum CorrectionCardActionKind
{
    /// <summary>Nothing changed that the card is about.</summary>
    None,

    /// <summary>The text changed and the new analysis has not finished. Show progress.</summary>
    Recheck,

    /// <summary>The text changed and the finding has been re-bound. Draw the new one.</summary>
    Render,

    /// <summary>The card can no longer describe anything the user is looking at. Close it.</summary>
    Dismiss,
}

public readonly record struct CorrectionCardAction(CorrectionCardActionKind Kind, TextIssue? Issue = null)
{
    public static readonly CorrectionCardAction None = new(CorrectionCardActionKind.None);
    public static readonly CorrectionCardAction Recheck = new(CorrectionCardActionKind.Recheck);
    public static readonly CorrectionCardAction Dismiss = new(CorrectionCardActionKind.Dismiss);

    public static CorrectionCardAction Render(TextIssue issue) => new(CorrectionCardActionKind.Render, issue);
}

/// <summary>
/// What the monitor has just said about the field, reduced to what the card cares about.
/// </summary>
/// <remarks>
/// A <see cref="TextSnapshot"/> carries a live <c>AutomationElement</c>, so a decision
/// expressed in terms of one can only be exercised with a real out-of-process control in
/// front of it. The decisions below are the part most worth pinning, so they are stated
/// against this instead; <see cref="ExternalFieldCorrectionCardTests"/> then confirms the
/// same coordinator behaves against a real field.
/// </remarks>
public readonly record struct CorrectionCardContext(
    string TargetId,
    string Text,
    IReadOnlyList<TextIssue> Issues,
    bool AnalysisInFlight)
{
    public static CorrectionCardContext From(TextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new CorrectionCardContext(
            snapshot.Target.Identity.RuntimeId,
            snapshot.Text,
            snapshot.Issues,
            CorrectionCardCoordinator.IsAnalysisInFlight(snapshot));
    }
}

/// <summary>
/// Decides what happens to an open correction card as the monitor publishes snapshots.
/// </summary>
/// <remarks>
/// <para><b>The defect this fixes.</b> Nothing invalidated the correction card when the text
/// underneath it changed. The card kept its finished result, its explanation and its apply
/// action, all computed from text the user had since edited; pressing the action failed the
/// range check inside the write path, and the failure came back to the card as «Текст
/// изменился. Обновите предложение.» with a «Повторить» button, laid on top of the old result
/// that was still on screen and still wrong. Retrying could not help — nothing was going to
/// bring the old text back.</para>
///
/// <para>A text change is not a processing failure, so it is not reported as one. The card
/// either follows the word into the new analysis or closes. Separated from the application
/// shell because this is the part with the interesting decisions in it, and because a
/// decision that can only be exercised by starting the whole tray application is a decision
/// that does not get tested.</para>
///
/// <para>It holds the card's own context — <see cref="PinnedSnapshot"/> — for as long as the
/// card is up. <c>_latestSnapshot</c> follows the focused field and goes null the moment
/// focus leaves one, which is routine while a card is open and is not evidence of anything.</para>
/// </remarks>
public sealed class CorrectionCardCoordinator
{
    private TextSnapshot? _pinnedSnapshot;
    private CorrectionCardContext _pinned;
    private TextIssue? _issue;

    /// <summary>The snapshot the open card was built from, or null when no card is open.</summary>
    public TextSnapshot? PinnedSnapshot => _pinnedSnapshot;

    /// <summary>The text the open card was computed against.</summary>
    public string PinnedText => _pinned.Text ?? string.Empty;

    /// <summary>The finding the open card is about, as the newest analysis stated it.</summary>
    public TextIssue? PinnedIssue => _issue;

    public bool IsOpen => _issue is not null;

    public void Open(TextSnapshot snapshot, TextIssue issue)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _pinnedSnapshot = snapshot;
        Open(CorrectionCardContext.From(snapshot), issue);
    }

    public void Open(CorrectionCardContext context, TextIssue issue)
    {
        _pinned = context;
        _issue = issue ?? throw new ArgumentNullException(nameof(issue));
    }

    public void Close()
    {
        _pinnedSnapshot = null;
        _pinned = default;
        _issue = null;
    }

    /// <summary>
    /// What the card should do about <paramref name="snapshot"/>.
    /// </summary>
    /// <remarks>
    /// A null snapshot is deliberately inert. The monitor drops its target whenever keyboard
    /// focus leaves an editable field, which happens routinely while a card is open —
    /// including because of WriteLite’s own popup — and treating that as staleness is what
    /// made a perfectly valid correction report «Текст изменился».
    /// </remarks>
    public CorrectionCardAction Observe(TextSnapshot? snapshot)
    {
        if (snapshot is null) return CorrectionCardAction.None;

        var action = Observe(CorrectionCardContext.From(snapshot));
        if (action.Kind == CorrectionCardActionKind.Render) _pinnedSnapshot = snapshot;
        return action;
    }

    /// <summary>
    /// What the card should do about the field as it now stands.
    /// </summary>
    /// <remarks>
    /// Returning <see cref="CorrectionCardActionKind.Render"/> also advances the pinned
    /// context, so the next observation is compared against the text the card is now showing
    /// rather than the text it was opened on.
    /// </remarks>
    public CorrectionCardAction Observe(CorrectionCardContext context)
    {
        if (_issue is not { } issue) return CorrectionCardAction.None;

        if (!string.Equals(context.TargetId, _pinned.TargetId, StringComparison.Ordinal))
        {
            return CorrectionCardAction.Dismiss;
        }

        if (string.Equals(context.Text, _pinned.Text, StringComparison.Ordinal)) return CorrectionCardAction.None;

        // Keep the pinned text where it is until a finished pass arrives, so the span still
        // maps forward from the text the card was actually computed against.
        if (context.AnalysisInFlight) return CorrectionCardAction.Recheck;

        if (CorrectionCardRebinder.Rebind(_pinned.Text, issue, context.Text, context.Issues) is not { } rebound)
        {
            return CorrectionCardAction.Dismiss;
        }

        _pinned = context;
        _issue = rebound;
        return CorrectionCardAction.Render(rebound);
    }

    /// <summary>
    /// Whether this publish is the monitor announcing an edit rather than reporting on it.
    /// </summary>
    /// <remarks>
    /// <para>An in-flight publish carries an empty issue set, and so does a finished pass over
    /// clean text — the two are indistinguishable by their findings, and telling them apart is
    /// what decides whether the card waits or closes.</para>
    ///
    /// <para><see cref="TextSnapshot.RequestId"/> is the reliable half: the analyzer numbers
    /// its requests from one, and the announcement publish carries the default zero. The
    /// indicator state is checked as well because it is the field that says so in words, but
    /// it is not sufficient on its own — the monitor rewrites it to
    /// <see cref="AnalysisIndicatorState.Hidden"/> whenever the main UI is suppressed, which
    /// would make an announcement look like a finished result.</para>
    /// </remarks>
    public static bool IsAnalysisInFlight(TextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.RequestId == 0
            || snapshot.IndicatorState is AnalysisIndicatorState.FastAnalyzing
                or AnalysisIndicatorState.DeepAnalyzing
                or AnalysisIndicatorState.Analyzing
                or AnalysisIndicatorState.Typing;
    }
}
