using System.Runtime.InteropServices;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.FieldActions;
using WriteLite.Services.Writing;
using WriteLite.Views;

namespace WriteLite;

/// <summary>
/// Word prediction in other applications: the editor's «Мой стиль» completion, shown at the caret
/// of whatever field the user is typing in, accepted with Tab.
/// </summary>
/// <remarks>
/// <para>The flow per snapshot of the watched field: read the caret (UI Automation, 350 ms budget),
/// ask the personal model what word is being typed, and show the completion in a chip right of the
/// caret. While the chip is up a temporary keyboard hook watches for Tab; any other key hides it at
/// once — the chip is stale the moment the user types on.</para>
///
/// <para>Accepting types the completion into the field as ordinary characters, after checking that
/// the field's application still owns the foreground. Typing rather than writing the value keeps
/// the caret after the word and the application's own undo, in Notepad, browsers and messengers
/// alike. When the personal model has nothing, the local model's continuation of the sentence is
/// offered instead (only at the end of the text).</para>
/// </remarks>
public partial class App
{
    private PredictionChipWindow? _predictionChip;
    private TemporaryKeyboardWatch? _predictionKeys;
    private CancellationTokenSource? _predictionCts;
    private (int ProcessId, string Completion, string Source)? _pendingPrediction;
    private (string Text, int Caret, System.Windows.Rect Rect, AutomationTextTarget Target)? _lastCaret;

    private bool FieldPredictionEnabled =>
        _settings.FieldPredictionEnabled && _settings.CheckingEnabled;

    private bool _fieldPredictionInitialized;

    private void InitializeFieldPrediction()
    {
        // BindMainWindowServices runs again when the main window is rebuilt; subscribe once.
        if (_monitor is null || _fieldPredictionInitialized) return;
        _fieldPredictionInitialized = true;
        _monitor.SnapshotChanged += (_, snapshot) => OnPredictionSnapshot(snapshot);
        if (_writingCoordinator is not null)
        {
            _writingCoordinator.SuggestionReady += (_, issue) => Dispatcher.BeginInvoke(() => OnContinuationReady(issue));
        }
    }

    private void OnPredictionSnapshot(TextSnapshot? snapshot)
    {
        _predictionCts?.Cancel();
        if (!FieldPredictionEnabled || snapshot is null || !snapshot.ShowMainUi || !snapshot.Target.IsEditable
            || string.IsNullOrWhiteSpace(snapshot.Text))
        {
            HidePrediction();
            return;
        }

        var cts = new CancellationTokenSource();
        _predictionCts = cts;
        _ = PredictAsync(snapshot, cts.Token);
    }

    private async Task PredictAsync(TextSnapshot snapshot, CancellationToken token)
    {
        try
        {
            var caret = await CaretLocator.TryLocateAsync(snapshot.Target.Element, snapshot.Text, token);
            if (token.IsCancellationRequested) return;
            if (caret is null)
            {
                HidePrediction();
                return;
            }

            _lastCaret = (snapshot.Text, caret.Value.Offset, caret.Value.ScreenRect, snapshot.Target);
            var before = snapshot.Text[..caret.Value.Offset];
            var prediction = _personalStyle?.Predict(before);
            if (prediction is null || prediction.Completion.Length == 0)
            {
                HidePrediction();
                return;
            }

            ShowPrediction(snapshot.Target.ProcessId, prediction.Completion, caret.Value.ScreenRect, "personal");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            CompatibilityLogger.Technical("field-prediction-failed", $"type={ex.GetType().Name}");
        }
    }

    /// <summary>The local model's continuation, used when the personal model had no word.</summary>
    private void OnContinuationReady(TextIssue issue)
    {
        if (!FieldPredictionEnabled || _pendingPrediction is not null) return;
        if (_lastCaret is not { } caret || issue.Length != 0 || string.IsNullOrWhiteSpace(issue.Replacement)) return;
        if (caret.Caret != caret.Text.Length || issue.Start != caret.Text.Length) return;
        // A sentence continuation belongs between words, never in the middle of one.
        if (caret.Text.Length == 0 || !char.IsWhiteSpace(caret.Text[^1])) return;
        var continuation = issue.Replacement!;
        if (continuation.Length > 48)
        {
            var cut = continuation.LastIndexOf(' ', 48);
            continuation = cut > 8 ? continuation[..cut] : continuation[..48];
        }

        if (continuation.Any(char.IsControl)) return;
        ShowPrediction(caret.Target.ProcessId, continuation, caret.Rect, "model");
    }

    private void ShowPrediction(int processId, string completion, System.Windows.Rect caretRect, string source)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _predictionChip ??= new PredictionChipWindow();
            if (_predictionKeys is null)
            {
                _predictionKeys = new TemporaryKeyboardWatch();
                _predictionKeys.AcceptPressed += () => Dispatcher.BeginInvoke(AcceptPrediction);
                _predictionKeys.OtherKeyPressed += () => Dispatcher.BeginInvoke(HidePrediction);
            }

            _pendingPrediction = (processId, completion, source);
            _predictionChip.ShowAt(completion, caretRect);
            _predictionKeys.Start();
            CompatibilityLogger.Technical("field-prediction-shown", $"source={source} length={completion.Length}");
        });
    }

    private void HidePrediction()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(HidePrediction);
            return;
        }

        _pendingPrediction = null;
        _predictionKeys?.Stop();
        _predictionChip?.Hide();
    }

    private void AcceptPrediction()
    {
        if (_pendingPrediction is not { } pending)
        {
            HidePrediction();
            return;
        }

        HidePrediction();

        // The keystrokes go to whoever owns the keyboard; make sure that is still the field.
        _ = GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundPid);
        if (foregroundPid != (uint)pending.ProcessId)
        {
            CompatibilityLogger.Technical("field-prediction-refused", "reason=foreground-moved");
            return;
        }

        if (KeyboardInput.TypeText(pending.Completion))
        {
            if (pending.Source == "personal") _personalStyle?.RecordPredictionAccepted();
            CompatibilityLogger.Technical("field-prediction-accepted", $"source={pending.Source} length={pending.Completion.Length}");
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
