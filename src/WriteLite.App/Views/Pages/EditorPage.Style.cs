using System.Windows.Documents;
using System.Windows.Threading;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Personal;

namespace WriteLite.Views.Pages;

/// <summary>
/// «Мой стиль письма» in the editor: the word being typed, predicted from the user's own
/// writing, and learning from what they type.
/// </summary>
/// <remarks>
/// <para><b>Same surface as the continuation, different clock.</b> A predicted word is drawn by
/// the same ghost-text adorner as the model's continuation and accepted with the same Tab, so
/// there is one gesture to learn. But it is ready the moment a key is released — it is a
/// lookup in a table, not a model call — while the continuation waits for a long pause. When
/// both exist the predicted word wins: it answers the question the user is in the middle of.</para>
///
/// <para><b>What the editor learns from.</b> Only sentences the user has just typed: the
/// finished sentences before the caret, after a keystroke-triggered check. Opening a document
/// teaches nothing, because a document someone opened is not necessarily one they wrote — the
/// «Мой стиль» page has an explicit import for the ones that are.</para>
/// </remarks>
public sealed partial class EditorPage
{
    /// <summary>How far back from the caret typed text is offered for learning.</summary>
    private const int LearningWindowChars = 1500;

    private PersonalStyleService? _personalStyle;
    private WordPrediction? _prediction;
    private TextPointer? _predictionAnchor;
    private bool _predictionQueued;

    /// <summary>Attaches the personal style library. Absent, the editor behaves as before.</summary>
    public void BindPersonalStyle(PersonalStyleService? style) => _personalStyle = style;

    /// <summary>The word currently predicted, or null. Tests.</summary>
    internal WordPrediction? CurrentPrediction => _prediction;

    /// <summary>Schedules a prediction for the caret once the keystroke has been laid out.</summary>
    /// <remarks>
    /// Deferred to render priority so the caret rectangle the ghost is placed against already
    /// describes the text with the new character in it; computed synchronously it would sit one
    /// character to the left.
    /// </remarks>
    private void QueuePersonalPrediction()
    {
        if (_personalStyle is not { IsPredictionEnabled: true } || _predictionQueued) return;

        _predictionQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, UpdatePersonalPrediction);
    }

    /// <summary>Computes and paints the prediction for the caret, now. Dispatcher thread.</summary>
    internal void UpdatePersonalPrediction()
    {
        _predictionQueued = false;
        ClearPersonalPrediction();

        if (_personalStyle is not { IsPredictionEnabled: true }) return;
        if (!Editor.Selection.IsEmpty) return;

        var caret = Editor.CaretPosition;

        // Inside a word the user is editing an existing word, not typing a new one.
        var after = caret.GetTextInRun(LogicalDirection.Forward);
        if (after.Length > 0 && char.IsLetterOrDigit(after[0])) return;

        if (caret.Paragraph is not { } paragraph) return;
        var before = new TextRange(paragraph.ContentStart, caret).Text;

        WordPrediction? prediction;
        try
        {
            prediction = _personalStyle.Predict(before);
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical("editor-prediction-failed", exception);
            return;
        }

        if (prediction is null || prediction.Completion.Length == 0) return;

        if (EnsureGhostAdorner() is not { } ghost) return;

        // A continuation from the model, if one was painted, gives way to the word.
        _writingCts?.Cancel();
        _prediction = prediction;
        _predictionAnchor = caret;
        ghost.Show(prediction.Completion, caret);
    }

    /// <summary>Drops the predicted word, if any, and its ghost.</summary>
    private void ClearPersonalPrediction()
    {
        if (_prediction is null) return;

        _prediction = null;
        _predictionAnchor = null;
        _ghost?.Clear();
    }

    /// <summary>A caret moved by the mouse or the arrows no longer has the word in front of it.</summary>
    private void InvalidatePredictionOnCaretMove()
    {
        if (_prediction is null || _predictionAnchor is null) return;

        try
        {
            if (Editor.CaretPosition.CompareTo(_predictionAnchor) == 0 && Editor.Selection.IsEmpty) return;
        }
        catch (ArgumentException)
        {
            // The anchor belongs to a document that has since been replaced.
        }

        ClearPersonalPrediction();
    }

    private GhostTextAdorner? EnsureGhostAdorner()
    {
        if (_ghost is not null) return _ghost;

        var layer = AdornerLayer.GetAdornerLayer(Editor);
        if (layer is null) return null;

        _ghost = new GhostTextAdorner(Editor);
        layer.Add(_ghost);
        return _ghost;
    }

    /// <summary>
    /// Offers the sentences just typed before the caret to the library.
    /// </summary>
    /// <param name="text">The analysed document text.</param>
    /// <param name="caret">Caret offset in <paramref name="text"/> when the check started.</param>
    private void ObserveTypedText(string text, int caret)
    {
        if (_personalStyle is not { IsLearningEnabled: true } style) return;
        if (caret <= 0 || caret > text.Length) return;

        var start = Math.Max(0, caret - LearningWindowChars);
        if (start > 0)
        {
            // Never begin in the middle of a sentence: a fragment would be learned as if it
            // started one.
            var boundary = text.IndexOfAny(['.', '!', '?', '…', '\n'], start);
            if (boundary < 0 || boundary >= caret) return;
            start = boundary + 1;
        }

        style.Observe(text[start..caret], StyleLearningSource.Editor, includeTrailingSentence: false);
    }

    /// <summary>Teaches the library from a correction applied in the editor.</summary>
    private void LearnFromAppliedCorrection(TextIssue issue) => _personalStyle?.RecordAcceptedCorrection(issue);
}
