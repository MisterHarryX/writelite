using System.Windows.Automation;
using System.Windows.Automation.Text;
using WriteLite.Models;

namespace WriteLite.Services.FieldActions;

/// <summary>What part of a field an action works on.</summary>
public enum FieldScopeKind
{
    /// <summary>What the user selected.</summary>
    Selection,

    /// <summary>The sentence the caret is in.</summary>
    Sentence,

    /// <summary>The whole field (short fields, or when the caret cannot be read).</summary>
    All,
}

/// <summary>
/// A fragment of the text in another application's field, pinned to the text it was read from.
/// </summary>
/// <remarks>
/// The fragment is an offset range plus the exact text it covered, so the eventual write is a
/// <see cref="Language.Core.CanonicalCorrection"/> like any correction: it binds only if the
/// field still says what the user saw, and the verified write path re-reads it afterwards.
/// </remarks>
public sealed record FieldTextScope(
    TextSnapshot Snapshot,
    int Start,
    int Length,
    FieldScopeKind Kind)
{
    public string Text => Snapshot.Text.Substring(Start, Length);

    public string KindLabel => Kind switch
    {
        FieldScopeKind.Selection => "Выделенный фрагмент",
        FieldScopeKind.Sentence => "Текущее предложение",
        _ => "Весь текст",
    };

    /// <summary>
    /// Reads the selection (or the caret) from the field and decides what to work on.
    /// </summary>
    /// <remarks>
    /// UI Automation is called on a worker with a short timeout: a hung target application must
    /// cost the user a missing scope, never a frozen indicator. Whatever cannot be read falls
    /// back to the whole field — shortening or translating an entire short message is what the
    /// user wants in a chat box anyway.
    /// </remarks>
    public static async Task<FieldTextScope?> CaptureAsync(TextSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.Text)) return null;
        var text = snapshot.Text;

        (int Start, int Length)? selection = null;
        try
        {
            var read = Task.Run(() => ReadSelection(snapshot.Target.Element, text), cancellationToken);
            var finished = await Task.WhenAny(read, Task.Delay(600, cancellationToken)).ConfigureAwait(false);
            if (finished == read) selection = await read.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CompatibilityLogger.Technical("field-scope-selection-failed", $"type={ex.GetType().Name}");
        }

        return FromSelection(snapshot, selection);
    }

    /// <summary>The decision, separated from UI Automation so it can be tested.</summary>
    public static FieldTextScope FromSelection(TextSnapshot snapshot, (int Start, int Length)? selection)
    {
        var text = snapshot.Text;
        if (selection is { } sel && sel.Start >= 0 && sel.Start + sel.Length <= text.Length)
        {
            if (sel.Length > 0 && !string.IsNullOrWhiteSpace(text.Substring(sel.Start, sel.Length)))
            {
                var (s, l) = TrimRange(text, sel.Start, sel.Length);
                return new FieldTextScope(snapshot, s, l, FieldScopeKind.Selection);
            }

            // A caret inside a long text: the sentence around it.
            if (text.Length > 280)
            {
                var (s, l) = SentenceAround(text, sel.Start);
                if (l > 0) return new FieldTextScope(snapshot, s, l, FieldScopeKind.Sentence);
            }
        }

        var (start, length) = TrimRange(text, 0, text.Length);
        return new FieldTextScope(snapshot, start, length, FieldScopeKind.All);
    }

    private static (int Start, int Length)? ReadSelection(AutomationElement element, string text)
    {
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var obj) || obj is not TextPattern pattern)
        {
            return null;
        }

        var ranges = pattern.GetSelection();
        if (ranges is null || ranges.Length == 0) return null;
        var range = ranges[0];
        var selected = range.GetText(-1) ?? string.Empty;

        // Offset of the selection start: the length of the document range before it.
        var before = pattern.DocumentRange.Clone();
        before.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
        var prefix = before.GetText(-1) ?? string.Empty;
        var start = prefix.Length;

        // Controls disagree about \r\n; trust the text when the two readings disagree.
        if (selected.Length > 0)
        {
            if (start + selected.Length > text.Length
                || !string.Equals(text.Substring(start, selected.Length), selected, StringComparison.Ordinal))
            {
                var found = text.IndexOf(selected, StringComparison.Ordinal);
                if (found < 0) return null;
                if (text.IndexOf(selected, found + 1, StringComparison.Ordinal) >= 0)
                {
                    // Ambiguous: pick the occurrence nearest the reported offset.
                    var best = found;
                    for (var i = found; i >= 0; i = text.IndexOf(selected, i + 1, StringComparison.Ordinal))
                    {
                        if (Math.Abs(i - start) < Math.Abs(best - start)) best = i;
                    }

                    found = best;
                }

                start = found;
            }
        }

        return (Math.Clamp(start, 0, text.Length), selected.Length);
    }

    private static (int Start, int Length) TrimRange(string text, int start, int length)
    {
        var end = start + length;
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        return (start, end - start);
    }

    /// <summary>The sentence containing <paramref name="caret"/>, terminal mark included.</summary>
    public static (int Start, int Length) SentenceAround(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var start = caret;
        while (start > 0)
        {
            var c = text[start - 1];
            if (c is '\n' or '\r') break;
            if (c is '.' or '!' or '?' or '…' && start < text.Length && char.IsWhiteSpace(text[start])) break;
            start--;
        }

        var end = caret;
        while (end < text.Length)
        {
            var c = text[end];
            end++;
            if (c is '\n' or '\r') { end--; break; }
            if (c is '.' or '!' or '?' or '…' && (end >= text.Length || char.IsWhiteSpace(text[end]))) break;
        }

        return TrimRange(text, start, end - start);
    }
}
