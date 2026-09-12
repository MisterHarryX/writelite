using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WriteLite.Resources;
using WriteLite.Services;
using WriteLite.Services.Ai;
using Brush = System.Windows.Media.Brush;
using Clipboard = System.Windows.Clipboard;

namespace WriteLite.Views;

/// <summary>What the user chose to do with a suggestion.</summary>
public enum AiPreviewOutcome
{
    Cancelled,
    Replace,
    InsertBelow
}

/// <summary>
/// Shows an AI suggestion before it touches the document.
/// </summary>
/// <remarks>
/// The product rule is that AI never silently destroys text, and this window is
/// where that rule is enforced: the editor's rewrite path has no way to change the
/// document except through an outcome returned from here.
///
/// The suggestion is rendered as a word-level diff rather than as plain text. A
/// person accepting a rewrite needs to see what actually changed, and for a
/// grammar fix that can be a single letter in the middle of a paragraph.
/// </remarks>
public partial class AiPreviewWindow : Window
{
    private readonly Func<CancellationToken, Task<RewriteResult>> _generate;
    private CancellationTokenSource? _generation;
    private RewriteResult? _result;

    public AiPreviewWindow(string operationLabel, Func<CancellationToken, Task<RewriteResult>> generate)
    {
        InitializeComponent();
        _generate = generate;
        OperationText.Text = operationLabel;
        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) => CancelGeneration();
    }

    /// <summary>The accepted suggestion, or null when nothing was accepted.</summary>
    public string? AcceptedText { get; private set; }

    public AiPreviewOutcome Outcome { get; private set; } = AiPreviewOutcome.Cancelled;

    // ── Generation ───────────────────────────────────────────────────────────

    private async Task RunAsync()
    {
        CancelGeneration();
        var generation = new CancellationTokenSource();
        _generation = generation;
        var token = generation.Token;

        SetBusy(true);
        SetNotice(null);

        try
        {
            var result = await _generate(token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            _result = result;
            Render(result);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_generation, generation))
            {
                SetNotice(Strings.AiPreview_CancelledNotice);
                DisableAcceptance();
            }
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical("ai-preview-failed", exception);
            if (ReferenceEquals(_generation, generation))
            {
                SetNotice(exception is InvalidOperationException
                    ? exception.Message
                    : Strings.AiPreview_ProcessingFailed);
                DisableAcceptance();
            }
        }
        finally
        {
            // Only the operation that still owns the window may finish its state.
            // A cancelled predecessor must not clear the busy state of a retry.
            if (ReferenceEquals(_generation, generation))
            {
                _generation = null;
                SetBusy(false);
            }

            generation.Dispose();
        }
    }

    private void Render(RewriteResult result)
    {
        OriginalText.Text = result.Original;

        SuggestionText.Inlines.Clear();

        // An advisory answer — an explanation or a translation — is not a rewrite of
        // the original, so diffing it against the original would mark every word as
        // changed and tell the reader nothing.
        if (result.IsAdvisory)
        {
            SuggestionText.Inlines.Add(new Run(result.Suggestion));
        }
        else
        {
            foreach (var segment in RewriteDiff.Compute(result.Original, result.Suggestion))
            {
                SuggestionText.Inlines.Add(BuildRun(segment));
            }
        }

        BackendText.Text = result.Backend switch
        {
            "writelite-qwen" => Strings.AiPreview_BackendQwen,
            "offline-rules" => Strings.AiPreview_BackendOfflineRules,
            _ => Strings.AiPreview_BackendDefault
        };

        if (result.IsNoOp)
        {
            SetNotice(Strings.AiPreview_NoOpNotice);
        }
        else if (result.Backend == "offline-rules"
                 && result.Operation is not (RewriteOperation.Grammar
                     or RewriteOperation.Shorten
                     or RewriteOperation.Simplify
                     or RewriteOperation.ImproveStyle))
        {
            // Saying so beats inventing a rewrite the rules cannot actually perform.
            SetNotice(WriteLite.Services.Ai.WriteAiStatus.DescribeWithHint(
                WriteLite.Services.Ai.WriteAiState.Unavailable));
        }

        // Insertion only makes sense for text that stands on its own; replacing a
        // selection with an explanation of itself would be a bug, not a feature.
        ReplaceButton.IsEnabled = !result.IsAdvisory;
        InsertButton.IsEnabled = true;
        Motion.Reveal(SuggestionText, offset: 6);
    }

    private Run BuildRun(DiffSegment segment)
    {
        var run = new Run(segment.Text);

        switch (segment.Kind)
        {
            case DiffKind.Added:
                run.Background = (Brush)FindResource("WlDiffAddedBg");
                run.Foreground = (Brush)FindResource("WlText");
                break;

            case DiffKind.Removed:
                run.Background = (Brush)FindResource("WlDiffRemovedBg");
                run.Foreground = (Brush)FindResource("WlDiffRemovedFg");
                run.TextDecorations = TextDecorations.Strikethrough;
                break;
        }

        return run;
    }

    private void SetBusy(bool busy)
    {
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.IsEnabled = !busy;
        ReplaceButton.IsEnabled = !busy && _result is { IsAdvisory: false };
        InsertButton.IsEnabled = !busy && _result is not null;
    }

    private void SetNotice(string? message)
    {
        NoticeText.Text = message ?? string.Empty;
        NoticePanel.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void DisableAcceptance()
    {
        ReplaceButton.IsEnabled = false;
        InsertButton.IsEnabled = false;
    }

    private void CancelGeneration()
    {
        _generation?.Cancel();
        _generation = null;
        SetBusy(false);
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    private void StopGeneration_Click(object sender, RoutedEventArgs e)
    {
        CancelGeneration();
        SetNotice(Strings.AiPreview_CancelledNotice);
        DisableAcceptance();
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await RunAsync();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(_result.Suggestion);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process owns the clipboard; nothing here is worth an error dialog.
            SetNotice(Strings.AiPreview_CopyFailedNotice);
        }
    }

    private void Replace_Click(object sender, RoutedEventArgs e) => Accept(AiPreviewOutcome.Replace);

    private void Insert_Click(object sender, RoutedEventArgs e) => Accept(AiPreviewOutcome.InsertBelow);

    private void Accept(AiPreviewOutcome outcome)
    {
        if (_result is null)
        {
            return;
        }

        AcceptedText = _result.Suggestion;
        Outcome = outcome;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelGeneration();
        Outcome = AiPreviewOutcome.Cancelled;
        AcceptedText = null;
        DialogResult = false;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
