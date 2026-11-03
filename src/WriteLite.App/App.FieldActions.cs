using WriteLite.Services;
using WriteLite.Services.Ai;
using WriteLite.Services.FieldActions;
using WriteLite.Services.Translation;
using WriteLite.Views;

namespace WriteLite;

/// <summary>
/// The indicator pen's actions on another application's text: translate, shorten, improve.
/// </summary>
/// <remarks>
/// The scope (selection, sentence or whole field) is read when the pen is pressed — before the
/// menu opens and before anything could move the caret — and every later step works on that
/// pinned fragment. Running an action never edits the field; «Заменить» does, through the same
/// verified write path the correction card uses.
/// </remarks>
public partial class App
{
    private TextActionWindow? _textActionWindow;
    private FieldTextActionService? _fieldActions;
    private MarianTranslator? _translator;
    private Task<FieldTextScope?>? _pendingScope;
    private FieldTextScope? _actionScope;
    private CancellationTokenSource? _actionCts;

    /// <summary>The offline translator, shared with the editor.</summary>
    internal MarianTranslator Translator => _translator ??= new MarianTranslator();

    private bool _fieldActionsSubscribed;

    private void InitializeFieldActions(IEditorAiService? rewriter)
    {
        _fieldActions = new FieldTextActionService(rewriter, Translator);

        // The language engine explains English mistakes in English; cards show them in Russian.
        var translator = Translator;
        WriteLite.Services.LanguageEngine.WriteLiteIssueMapper.MessageTranslator = message =>
            translator.Supports("en", "ru")
                ? translator.TranslateAsync(message, "en", "ru").GetAwaiter().GetResult()
                : null;
        if (_bubble is null || _fieldActionsSubscribed) return;
        _fieldActionsSubscribed = true;

        _bubble.PenPressed += (_, _) =>
        {
            var snapshot = _corrections?.LatestSnapshot;
            _pendingScope = snapshot is null ? null : FieldTextScope.CaptureAsync(snapshot);
        };
        _bubble.TextActionRequested += action => _ = RunFieldActionAsync(action, newScope: true);
    }

    private TextActionWindow EnsureTextActionWindow()
    {
        if (_textActionWindow is not null) return _textActionWindow;
        var window = new TextActionWindow();
        window.ActionChosen += action => _ = RunFieldActionAsync(action, newScope: false);
        window.RetryRequested += () => _ = RunFieldActionAsync(window.CurrentAction, newScope: false);
        window.ReplaceRequested += replacement => _ = ReplaceFieldTextAsync(replacement);
        window.Closed2 += () =>
        {
            _actionCts?.Cancel();
            _monitor?.SetPopupInteractionOpen(false);
        };
        _textActionWindow = window;
        return window;
    }

    private async Task RunFieldActionAsync(FieldTextAction action, bool newScope)
    {
        if (_fieldActions is null) return;
        var window = EnsureTextActionWindow();

        if (newScope || _actionScope is null)
        {
            FieldTextScope? scope = null;
            try
            {
                scope = _pendingScope is not null ? await _pendingScope : null;
            }
            catch (Exception ex)
            {
                CompatibilityLogger.Technical("field-action-scope-failed", $"type={ex.GetType().Name}");
            }

            if (scope is null && _corrections?.LatestSnapshot is { } snapshot)
            {
                scope = FieldTextScope.FromSelection(snapshot, null);
            }

            _pendingScope = null;
            if (scope is null || scope.Length == 0) return;
            _actionScope = scope;
        }

        var current = _actionScope!;
        _actionCts?.Cancel();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        _actionCts = cts;
        _monitor?.SetPopupInteractionOpen(true);
        window.ShowBusy(action, current, current.Snapshot.Target.Bounds);
        CompatibilityLogger.Technical("field-action-started", $"action={action} scope={current.Kind} length={current.Length}");

        try
        {
            var text = current.Snapshot.Text;
            var before = text[..current.Start];
            var after = text[(current.Start + current.Length)..];
            var result = await _fieldActions.RunAsync(
                action,
                current.Text,
                before.Length > 300 ? before[^300..] : before,
                after.Length > 200 ? after[..200] : after,
                cts.Token);
            if (cts.IsCancellationRequested) return;
            window.ShowResult(result);
            CompatibilityLogger.Technical("field-action-ready", $"action={action} backend={result.Backend} noop={(result.IsNoOp ? 1 : 0)}");
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_actionCts, cts)) window.ShowError("Не дождались ответа модели. Попробуйте ещё раз.");
        }
        catch (Exception ex)
        {
            CompatibilityLogger.Technical("field-action-failed", $"action={action} type={ex.GetType().Name}");
            window.ShowError(ex is InvalidOperationException ? ex.Message : "Не получилось подготовить вариант.");
        }
    }

    private async Task ReplaceFieldTextAsync(string replacement)
    {
        if (_actionScope is not { } scope || _textActionWindow is not { } window) return;
        window.SetApplying(true);
        _monitor?.BeginApplyingCorrection();
        (bool Succeeded, string Message) outcome;
        try
        {
            outcome = await FieldTextActionService.ApplyAsync(scope, replacement, CancellationToken.None);
        }
        catch (Exception ex)
        {
            CompatibilityLogger.Technical("field-action-apply-failed", $"type={ex.GetType().Name}");
            outcome = (false, "Не удалось изменить текст в этом поле.");
        }
        finally
        {
            _monitor?.EndApplyingCorrection();
        }

        window.SetApplying(false);
        if (outcome.Succeeded)
        {
            CompatibilityLogger.Technical("field-action-applied", $"length={replacement.Length}");
            _actionScope = null;
            window.Hide();
            _monitor?.SetPopupInteractionOpen(false);
            if (_monitor is not null) await _monitor.RefreshOnceAfterSuppressionAsync();
            return;
        }

        window.ShowFeedback(outcome.Message, isError: true);
    }
}
