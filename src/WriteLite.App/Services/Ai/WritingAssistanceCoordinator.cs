using WriteLite.Models;

namespace WriteLite.Services.Ai;

/// <summary>
/// Drives writing assistance for the field-monitoring path, on the same service the editor uses.
/// </summary>
/// <remarks>
/// <para><b>Why the external path gets an issue rather than ghost text.</b> Painting into
/// another application's text box is not something WriteLite can do safely — it would mean
/// drawing over a window it does not own, at coordinates it cannot keep in step with that
/// application's own layout. §67 asks instead for a small unobtrusive surface and an explicit
/// acceptance, and WriteLite already has one: the suggestions panel, which knows how to show a
/// span, explain it, and apply it through the write path that respects password fields,
/// integrity levels and the UIA circuit breaker.</para>
///
/// <para>So a continuation is expressed as an <see cref="TextIssue"/> with zero length at the
/// caret — an insertion. Everything downstream then treats it as what it is: a suggestion the
/// user may apply, that never auto-applies, that disappears when the text changes, and that
/// goes through exactly one write path. Nothing about the external surface needed to learn what
/// a completion is.</para>
///
/// <para><b>Timing is the caller's, cancellation is this type's.</b> The monitor already
/// debounces; what it cannot do is know that the previous completion is now meaningless, so
/// every request supersedes the last and a snapshot whose text has moved on is dropped before
/// it can be shown.</para>
/// </remarks>
public sealed class WritingAssistanceCoordinator
{
    private readonly WritingAssistanceService _writing;
    private int _generation;

    public WritingAssistanceCoordinator(WritingAssistanceService writing)
        => _writing = writing ?? throw new ArgumentNullException(nameof(writing));

    public bool IsAvailable => _writing.IsAvailable;

    /// <summary>Raised when a continuation is ready for the text it was prepared against.</summary>
    public event EventHandler<TextIssue>? SuggestionReady;

    /// <summary>Drops anything prepared or in flight.</summary>
    public void Dismiss()
    {
        Interlocked.Increment(ref _generation);
        _writing.Dismiss();
    }

    /// <summary>
    /// Prepares a continuation for a field snapshot, superseding any earlier request.
    /// </summary>
    /// <returns>The suggestion issue, or null.</returns>
    public async Task<TextIssue?> RequestAsync(
        string text,
        int caret,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || string.IsNullOrEmpty(text)) return null;

        var generation = Interlocked.Increment(ref _generation);
        var suggestion = await _writing
            .RequestAsync(text, caret, generation, cancellationToken)
            .ConfigureAwait(false);

        if (!suggestion.HasText) return null;
        if (generation != Volatile.Read(ref _generation)) return null;

        var issue = ToIssue(suggestion);
        SuggestionReady?.Invoke(this, issue);
        return issue;
    }

    /// <summary>
    /// A continuation as a zero-length insertion at the caret.
    /// </summary>
    /// <remarks>
    /// Style class and no automatic application, deliberately: §68 separates detection from
    /// suggestion from auto-apply, and a generated continuation is the furthest thing from an
    /// edit safe to make on the user's behalf. The severity and category also make it render
    /// as a suggestion rather than as an error, which is the §38 requirement that stylistic
    /// offers look different from mistakes.
    /// </remarks>
    public static TextIssue ToIssue(WritingSuggestion suggestion) => new(
        suggestion.Anchor,
        0,
        string.Empty,
        suggestion.Text,
        "Продолжение",
        "Возможное продолжение фразы. Предложение необязательное — примените его только если оно подходит.",
        IssueCategory.Style,
        IssueSeverity.Suggestion,
        CanApplyAutomatically: false,
        RuleId: "ru.writing.continuation",
        LinguisticCategory: LinguisticIssueCategory.LongSentence,
        Confidence: 0.5);
}
