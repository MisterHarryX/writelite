using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WriteLite.Services.Ai;
using WriteLite.Services;
using WriteLite.Services.Documents;

namespace WriteLite.Views.Pages;

/// <summary>
/// Inline continuation — ghost text — in the editor.
/// </summary>
/// <remarks>
/// <para><b>Why the suggestion is drawn as an adorner and not inserted into the document.</b>
/// §35 requires that nothing is ever inserted without an explicit acceptance, and the cheapest
/// way to guarantee that is for the suggested text never to exist in the document at all. An
/// adorner paints over the editor; the document is untouched until Tab is pressed, so there is
/// no state in which a crash, a save or an autosave could capture text the user did not type.
/// Inserting greyed-out text and removing it later is the alternative, and it makes undo,
/// autosave and the analysis pipeline all responsible for knowing about a fiction.</para>
///
/// <para><b>Three things dismiss it, and typing is one of them.</b> A keystroke, a caret move
/// and Escape all cancel the request in flight and clear whatever is painted. That is why the
/// suggestion carries the caret offset and the document generation it was prepared for: an
/// answer that arrives after the user has moved on fails <see cref="WritingSuggestion.AppliesTo"/>
/// and is dropped rather than painted at a position that no longer means anything.</para>
///
/// <para><b>The trigger is a long pause, not a keystroke.</b> §35 asks for suggestions not to
/// follow every character; the delay here is deliberately several times the analysis debounce,
/// so error checking has already happened and settled before anything is asked of the model.
/// Correctness feedback is never behind a completion request.</para>
/// </remarks>
public sealed partial class EditorPage
{
    private WritingAssistanceService? _writing;
    private GhostTextAdorner? _ghost;
    private CancellationTokenSource? _writingCts;

    /// <summary>How long the user must pause before a continuation is prepared.</summary>
    /// <remarks>
    /// 900 ms, against 220 ms for analysis. Ordering the two this way is §52: a hard error
    /// must never wait behind an optional suggestion, and at this spacing the deterministic
    /// findings have been on screen for most of a second before the model is asked anything.
    /// </remarks>
    private static readonly TimeSpan WritingDelay = TimeSpan.FromMilliseconds(900);

    /// <summary>Attaches the writing-assistance layer. Absent service leaves the editor unchanged.</summary>
    public void BindWritingAssistance(WritingAssistanceService? writing)
    {
        _writing = writing;
        if (_writing is null) return;

        _writing.SuggestionChanged += (_, suggestion) => Dispatcher.BeginInvoke(() => PaintGhost(suggestion));
    }

    /// <summary>True when a continuation is painted and can be accepted.</summary>
    public bool HasWritingSuggestion => _ghost is { HasText: true };

    /// <summary>Cancels any pending request and clears the painted suggestion.</summary>
    private void DismissWritingSuggestion()
    {
        _writingCts?.Cancel();
        _writingCts?.Dispose();
        _writingCts = null;
        _writing?.Dismiss();
        PaintGhost(WritingSuggestion.None);
    }

    /// <summary>Schedules a continuation for the current caret, after a pause.</summary>
    private void QueueWritingSuggestion()
    {
        if (_writing is not { IsAvailable: true }) return;

        _writingCts?.Cancel();
        _writingCts?.Dispose();
        _writingCts = new CancellationTokenSource();
        var token = _writingCts.Token;

        _ = RunWritingSuggestionAsync(token);
    }

    private async Task RunWritingSuggestionAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(WritingDelay, token);
            token.ThrowIfCancellationRequested();

            // The index touches TextPointer, so the snapshot is taken on the dispatcher and
            // everything after it works on a flat string and two integers.
            var generation = _documentGeneration;
            var index = DocumentTextIndex.Build(Editor.Document, generation);
            var caret = index.OffsetOf(Editor.CaretPosition);
            var text = index.Text;
            if (caret < 0) return;

            await _writing!.RequestAsync(text, caret, generation, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by the next keystroke. The normal path.
        }
        catch (Exception ex)
        {
            CompatibilityLogger.Technical("editor-writing-assist-failed", $"type={ex.GetType().Name}");
        }
    }

    /// <summary>Paints or clears the ghost text, only where it still describes the document.</summary>
    private void PaintGhost(WritingSuggestion suggestion)
    {
        var layer = AdornerLayer.GetAdornerLayer(Editor);
        if (layer is null) return;

        if (_ghost is null)
        {
            _ghost = new GhostTextAdorner(Editor);
            layer.Add(_ghost);
        }

        if (!suggestion.HasText)
        {
            _ghost.Clear();
            return;
        }

        var index = _index;
        if (index is null || index.IsStale(_documentGeneration))
        {
            _ghost.Clear();
            return;
        }

        var caret = index.OffsetOf(Editor.CaretPosition);
        if (!suggestion.AppliesTo(caret, _documentGeneration))
        {
            _ghost.Clear();
            return;
        }

        _ghost.Show(suggestion.Text, Editor.CaretPosition);
    }

    /// <summary>Inserts the painted continuation at the caret. Returns false when there is none.</summary>
    /// <remarks>
    /// The insertion goes through the normal typing path, so it is one undo unit and the
    /// analysis pipeline sees it as an ordinary edit — a suggestion the user accepted is the
    /// user's text and is checked like the rest of it.
    /// </remarks>
    private bool AcceptWritingSuggestion()
    {
        if (_ghost is not { HasText: true } ghost) return false;

        var text = ghost.Text;
        DismissWritingSuggestion();

        BeginUndoBatch();
        try
        {
            Editor.CaretPosition.InsertTextInRun(text);
            Editor.CaretPosition = Editor.CaretPosition.GetPositionAtOffset(text.Length) ?? Editor.CaretPosition;
        }
        finally
        {
            EndUndoBatch();
        }

        return true;
    }
}

/// <summary>
/// Draws a greyed-out continuation after the caret without touching the document.
/// </summary>
/// <remarks>
/// The rectangle comes from the caret's own <c>GetCharacterRect</c>, so the ghost sits on the
/// text baseline at whatever font and zoom the editor is using, and it moves with the text
/// rather than with the window. Hit testing is off: the ghost is not something to click, and a
/// transparent adorner that swallowed clicks would break selection in the editor underneath it.
/// </remarks>
internal sealed class GhostTextAdorner : Adorner
{
    private readonly System.Windows.Controls.RichTextBox _editor;
    private FormattedText? _formatted;
    private System.Windows.Point _origin;

    public GhostTextAdorner(System.Windows.Controls.RichTextBox editor)
        : base(editor)
    {
        _editor = editor;
        IsHitTestVisible = false;
    }

    public string Text { get; private set; } = string.Empty;

    public bool HasText => Text.Length > 0;

    public void Show(string text, TextPointer caret)
    {
        Text = text ?? string.Empty;
        if (Text.Length == 0)
        {
            _formatted = null;
            InvalidateVisual();
            return;
        }

        var rect = caret.GetCharacterRect(LogicalDirection.Forward);
        _origin = new System.Windows.Point(rect.Right, rect.Top);

        var typeface = new Typeface(
            _editor.FontFamily, _editor.FontStyle, _editor.FontWeight, _editor.FontStretch);

        _formatted = new FormattedText(
            Text,
            System.Globalization.CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            typeface,
            _editor.FontSize,
            new SolidColorBrush(System.Windows.Media.Color.FromArgb(120, 128, 128, 128)),
            VisualTreeHelper.GetDpi(_editor).PixelsPerDip);

        InvalidateVisual();
    }

    public void Clear()
    {
        if (Text.Length == 0) return;
        Text = string.Empty;
        _formatted = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_formatted is null) return;
        drawingContext.DrawText(_formatted, _origin);
    }
}
