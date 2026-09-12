using System.Windows;
using System.Windows.Threading;
using WriteLite.Language.Core;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Ai;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Settings;
using WriteLite.Views;

namespace WriteLite;

/// <summary>
/// Owns the correction UI surface — the click-to-correct card, the suggestions panel,
/// feedback, the inline overlay, and the interactions that feed them the write path.
/// </summary>
/// <remarks>
/// <para>Level B extraction: the correction cluster that lived in <c>App.Corrections.cs</c>
/// now lives in its own class. The write path itself stays in
/// <see cref="CorrectionApplicationService"/>; this class decides what the user sees and
/// hands application decisions to that service. It is constructed once on startup, after
/// the monitor, the windows and the analyzers exist, and is driven by the application shell
/// through the few public entry points below.</para>
///
/// <para>Shared state lives outside and is read through providers: <c>_settings</c> is
/// replaced wholesale on every settings change, <c>_lexicalLookupCts</c> is swapped by the
/// lexical popup path, and the shutdown flag is App-owned — so they are injected as
/// functions, exactly as the diagnostics service consumes them.</para>
/// </remarks>
public sealed class CorrectionUiCoordinator
{
    private readonly Dispatcher _dispatcher;
    private readonly TextFieldMonitor? _monitor;
    private readonly Func<WriteLiteAppSettings> _settingsProvider;
    private readonly Func<CancellationTokenSource?> _lexicalLookupCancellation;
    private readonly BubbleWindow? _bubble;
    private readonly SuggestionsWindow? _suggestions;
    private readonly InlineErrorOverlayController? _inlineOverlay;
    private readonly LexicalPopupWindow? _lexicalPopup;
    private readonly WriteLiteOrchestratingAnalyzer? _orchestrator;
    private readonly WritingAssistanceCoordinator? _writingCoordinator;
    private readonly MainWindow? _mainWindow;
    private readonly CancellationTokenSource _applicationLifetimeCts;
    private readonly Func<bool> _isShutdownRequested;

    private readonly CorrectionCardCoordinator _cardCoordinator = new();
    private readonly AsyncOperationGate _correctionGate = new();

    /// <summary>Where the open card is anchored, so a refreshed result lands on the same word.</summary>
    private Rect _pinnedPopupAnchor;

    /// <summary>
    /// The snapshot the open correction card was built from.
    /// </summary>
    /// <remarks>
    /// <see cref="_latestSnapshot"/> follows the focused field and is set to null the moment
    /// focus leaves one — which is routine while a card is open, and which made pressing the
    /// card's button report «Нет активного текстового поля» for a correction that was on
    /// screen and perfectly valid. This holds the card's own context for as long as the card
    /// is up. It is not a shortcut past staleness: the apply path still re-reads the target
    /// and refuses if the text has moved on.
    /// </remarks>
    private TextSnapshot? _latestSnapshot;

    private int _lastIndicatorGeneration = -1;

    private readonly CorrectionApplicationService? _correctionApplication;

    private readonly CorrectionPopupWindow _correctionPopup;

    public CorrectionUiCoordinator(
        Dispatcher dispatcher,
        CorrectionApplicationService? correctionApplication,
        Func<WriteLiteAppSettings> settingsProvider,
        TextFieldMonitor? monitor,
        Func<CancellationTokenSource?> lexicalLookupCancellation,
        BubbleWindow? bubble,
        SuggestionsWindow? suggestions,
        InlineErrorOverlayController? inlineOverlay,
        LexicalPopupWindow? lexicalPopup,
        WriteLiteOrchestratingAnalyzer? orchestrator,
        WritingAssistanceCoordinator? writingCoordinator,
        MainWindow? mainWindow,
        CancellationTokenSource applicationLifetimeCts,
        Func<bool> isShutdownRequested)
    {
        _dispatcher = dispatcher;
        _correctionApplication = correctionApplication;
        _settingsProvider = settingsProvider;
        _monitor = monitor;
        _lexicalLookupCancellation = lexicalLookupCancellation;
        _bubble = bubble;
        _suggestions = suggestions;
        _inlineOverlay = inlineOverlay;
        _lexicalPopup = lexicalPopup;
        _orchestrator = orchestrator;
        _writingCoordinator = writingCoordinator;
        _mainWindow = mainWindow;
        _applicationLifetimeCts = applicationLifetimeCts;
        _isShutdownRequested = isShutdownRequested;

        _correctionPopup = new CorrectionPopupWindow();
        _correctionPopup.ApplyRequested += async (_, issue) => await ApplyIssueAsync(issue);
        _correctionPopup.IgnoreRequested += (_, issue) => IgnoreIssue(issue);
        _correctionPopup.AddToDictionaryRequested += (_, issue) => AddToDictionary(issue);
        _correctionPopup.CopyRequested += (_, issue) => CopyReplacementToClipboard(issue.Replacement);
        _correctionPopup.IsVisibleChanged += (_, _) =>
        {
            if (!_correctionPopup.IsVisible)
            {
                _cardCoordinator.Close();
                _monitor?.SetPopupInteractionOpen(false);
            }
        };
    }

    private WriteLiteAppSettings Settings => _settingsProvider();

    /// <summary>The snapshot the corrections surface was last given, for the application shell.</summary>
    public TextSnapshot? LatestSnapshot => _latestSnapshot;

    /// <summary>Whether the click-to-correct card is up (for the responsiveness watchdog).</summary>
    public bool IsCorrectionPopupVisible => _correctionPopup.IsVisible;

    /// <summary>Closes the correction card at application shutdown.</summary>
    public void ClosePopup() => _correctionPopup.Close();

    /// <summary>
    /// Opens the click-to-correct card for an inline issue the user clicked.
    /// </summary>
    /// <remarks>
    /// §10: the card outlives the monitor's idea of "the current field". Whatever happens
    /// to focus between now and the click on «Исправить», the correction belongs to this
    /// target, this text and this range — so the apply path is given them rather than
    /// whatever the monitor happens to be looking at by then.
    /// </remarks>
    public void OpenCorrectionCard(TextSnapshot snapshot, TextIssue issue, Rect anchor)
    {
        _bubble?.ClearUserHide();
        _monitor?.SetPopupInteractionOpen(true);
        _cardCoordinator.Open(snapshot, issue);
        _pinnedPopupAnchor = anchor;
        CompatibilityLogger.Technical("correction-popup-open-requested", $"rule={issue.RuleId} generation={snapshot.GenerationId} request={snapshot.RequestId}");
        try
        {
            _dispatcher.BeginInvoke(() =>
            {
                try
                {
                    _correctionPopup.ShowForIssue(issue, anchor);
                    CompatibilityLogger.Technical("correction-popup-opened", $"rule={issue.RuleId}");
                }
                catch (Exception exception)
                {
                    CompatibilityLogger.AccessError("correction-popup", snapshot.Target.ProcessId, exception);
                    CompatibilityLogger.Technical("correction-popup-failed", "reason=show-exception");
                }
            });
        }
        catch (Exception exception)
        {
            CompatibilityLogger.AccessError("correction-popup-dispatch", snapshot.Target.ProcessId, exception);
            CompatibilityLogger.Technical("correction-popup-failed", "reason=dispatch-exception");
        }
    }

    /// <summary>
    /// Publishes a fresh snapshot to every correction-adjacent surface.
    /// Raised by the field monitor's <c>SnapshotChanged</c> event.
    /// </summary>
    public void HandleSnapshotChanged(TextSnapshot? snapshot)
    {
        _latestSnapshot = snapshot;

        // §39/§81: the field path runs the same writing-assistance service the editor runs,
        // through the same bounded context and the same never-insert-without-acceptance rule.
        // The text has already been captured off the UIA callback by the monitor, so nothing
        // here touches the target application.
        QueueFieldContinuation(snapshot);

        // Do not leave an old dictionary result on screen once the user has moved to a
        // different field, or edited the text the card was read from. This also cancels its
        // background lookup before it can update the UI. What does *not* invalidate a card is
        // the monitor merely republishing, or reporting that it is tracking nothing — see
        // LexicalPopupWindow.IsStaleFor.
        if (_lexicalPopup is { IsVisible: true }
            && _lexicalPopup.IsStaleFor(snapshot?.Target.Identity.RuntimeId, snapshot?.Text))
        {
            _lexicalLookupCancellation()?.Cancel();
            _lexicalPopup.Hide();
            CompatibilityLogger.Technical("lexical-popup-invalidated", "reason=target-or-text-changed");
        }

        RefreshCorrectionCard(snapshot);

        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.Text))
        {
            CompatibilityLogger.State("overlay-hidden");
            _lastIndicatorGeneration = -1;
            _bubble?.ClearUserHide();
            _bubble?.Hide();
            _suggestions?.Hide();
            _inlineOverlay?.Hide();
            // Never force-close a popup the user is interacting with.
            if (_monitor?.ShouldKeepPopup != true)
            {
                _correctionPopup.Dismiss();
            }

            return;
        }

        // Clear "hide indicator" only when the active target generation changes.
        if (snapshot.GenerationId != _lastIndicatorGeneration)
        {
            _lastIndicatorGeneration = snapshot.GenerationId;
            _bubble?.ClearUserHide();
        }

        // Main chrome (indicator + underlines + suggestions) only after confirmed editing.
        var showMain = snapshot.ShowMainUi && snapshot.Target.IsEditable;
        if (!showMain)
        {
            CompatibilityLogger.Technical(
                "ui-main-hidden",
                $"state={snapshot.InteractionState} generation={snapshot.GenerationId}");
            _bubble?.Hide();
            _inlineOverlay?.Hide();
            if (_suggestions?.IsVisible == true && !snapshot.ShowMainUi)
            {
                _suggestions.Hide();
                _monitor?.SetSuggestionsWindowOpen(false);
            }

            if (_monitor?.ShouldKeepPopup != true)
            {
                // Keep correction popup only while explicitly in popup interaction.
            }

            return;
        }

        CompatibilityLogger.State(snapshot.Issues.Count == 0 ? "overlay-shown-clean" : "overlay-shown");

        if (Settings.ShowIndicator)
        {
            _bubble?.ShowSnapshot(snapshot);
        }
        else
        {
            _bubble?.Hide();
        }

        _ = UpdateInlineOverlayAsync(snapshot);

        if (_suggestions?.IsVisible == true)
        {
            _monitor?.SetSuggestionsWindowOpen(true);
            _suggestions.ShowSnapshot(snapshot);
        }
        else if (snapshot.Issues.Count == 0)
        {
            _suggestions?.Hide();
        }
    }

    /// <summary>
    /// Prepares a continuation for the field the user is typing in.
    /// </summary>
    /// <remarks>
    /// Fire-and-forget with supersession: the coordinator cancels the previous request, and a
    /// completion whose snapshot has been replaced is dropped before it is ever offered. The
    /// suggestion arrives as an ordinary insertion issue, so the suggestions panel, the apply
    /// path and the write telemetry need to know nothing about completions.
    /// </remarks>
    private void QueueFieldContinuation(TextSnapshot? snapshot)
    {
        if (_writingCoordinator is not { IsAvailable: true } coordinator) return;

        if (snapshot is null
            || string.IsNullOrWhiteSpace(snapshot.Text)
            || !snapshot.Target.IsEditable
            || !snapshot.ShowMainUi)
        {
            coordinator.Dismiss();
            return;
        }

        var caret = snapshot.Text.Length;
        _ = Task.Run(async () =>
        {
            try
            {
                await coordinator.RequestAsync(snapshot.Text, caret).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CompatibilityLogger.Technical("field-continuation-failed", $"type={ex.GetType().Name}");
            }
        });
    }

    /// <summary>Puts a word into the user dictionary and dismisses the card.</summary>
    public void AddToDictionary(TextIssue issue)
    {
        // §18: never a diff fragment, never punctuation, never part of a word.
        var word = DictionaryWordPolicy.ResolveWord(issue);
        if (word is null)
        {
            CompatibilityLogger.Technical("dictionary-add-rejected", $"rule={issue.RuleId} length={issue.Length}");
            _correctionPopup.ShowApplyFeedback(
                "Это не отдельное слово — в словарь можно добавить только слово целиком.",
                isError: true,
                offerCopy: false,
                offerRetry: false);
            return;
        }

        _orchestrator?.Dictionary.Add(word);
        _orchestrator?.Ignore.IgnoreWord(word);
        CompatibilityLogger.Technical("language-engine-ignore", "kind=dictionary-add");
        _ = _monitor?.RefreshOnceAfterSuppressionAsync();
        _mainWindow?.RefreshDictionary();
        _correctionPopup.Dismiss();
    }

    /// <summary>Ignores the finding and its rule, and refreshes every surface showing it.</summary>
    public void IgnoreIssue(TextIssue issue)
    {
        _orchestrator?.Ignore.IgnoreIssueUntilTextChanges(issue);
        _orchestrator?.Ignore.IgnoreRule(issue.RuleId);
        CompatibilityLogger.Technical("language-engine-ignore", "kind=issue-and-rule");
        if (_latestSnapshot is not null)
        {
            var filtered = _latestSnapshot.Issues.Where(i => i != issue && !_orchestrator!.Ignore.IsIgnored(i)).ToList();
            var snap = _latestSnapshot with { Issues = filtered };
            _latestSnapshot = snap;
            _suggestions?.ShowSnapshot(snap);
            if (Settings.ShowIndicator)
            {
                _bubble?.ShowSnapshot(snap);
            }
        }

        _mainWindow?.RefreshExceptions();
        _correctionPopup.Dismiss();
    }

    /// <summary>
    /// Keeps the open correction card pointing at text that still exists.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect this fixes.</b> Nothing invalidated the correction card when the
    /// text underneath it changed. The card kept its finished result, its explanation and its
    /// apply action, all computed from text the user had since edited; pressing the action
    /// failed the range check inside the write path, and the failure came back to the card as
    /// «Текст изменился. Обновите предложение.» with a «Повторить» button — laid on top of the
    /// old result, which was still on screen and still wrong. Retrying could not help, because
    /// nothing was going to make the old text come back.</para>
    ///
    /// <para>A text change is not a processing failure and is not reported as one. The old
    /// result is voided the moment the change is observed — <see cref="CorrectionPopupWindow.BeginRecheck"/>
    /// removes the apply action synchronously, so there is no window in which the user can
    /// press an action belonging to text that is gone — and the card then shows what the next
    /// analysis says about the same word. It rides the monitor's ordinary fast lane, so no new
    /// analysis pass and no extra debounce is introduced here.</para>
    ///
    /// <para>Where the word cannot be identified in the new text, the card closes.
    /// <see cref="CorrectionCardRebinder"/> will not guess: a card describing something the
    /// user can no longer see is the thing being removed, and a wrong guess would put it
    /// straight back.</para>
    /// </remarks>
    private void RefreshCorrectionCard(TextSnapshot? snapshot)
    {
        if (!_correctionPopup.IsVisible) return;

        // A write in flight owns the card until its outcome lands — including the text change
        // the write itself is about to cause.
        if (_correctionPopup.Phase == CorrectionCardPhase.Applying) return;

        var action = _cardCoordinator.Observe(snapshot);
        switch (action.Kind)
        {
            case CorrectionCardActionKind.None:
                return;

            case CorrectionCardActionKind.Dismiss:
                CompatibilityLogger.Technical("correction-card-invalidated", "reason=not-rebindable-or-target-changed");
                _correctionPopup.Dismiss();
                return;

            case CorrectionCardActionKind.Recheck:
                _correctionPopup.BeginRecheck();
                return;

            case CorrectionCardActionKind.Render when action.Issue is { } rebound:
                // The token is taken from the same call that voided the old result, so the
                // new one cannot be overtaken by anything that started before it.
                var token = _correctionPopup.BeginRecheck();
                CompatibilityLogger.Technical(
                    "correction-card-refreshed",
                    $"rule={rebound.RuleId} generation={snapshot?.GenerationId} token={token}");
                _correctionPopup.ShowForIssue(rebound, _pinnedPopupAnchor, token);
                return;
        }
    }

    private async Task UpdateInlineOverlayAsync(TextSnapshot snapshot)
    {
        var overlay = _inlineOverlay;
        if (overlay is null || _isShutdownRequested())
        {
            return;
        }

        try
        {
            await overlay.UpdateAsync(snapshot).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // A newer snapshot or normal shutdown superseded the geometry request.
        }
        catch (Exception exception)
        {
            // Overlay rendering is optional. It must not be able to terminate the
            // monitor or the host application when UIA geometry becomes unavailable.
            CompatibilityLogger.AccessError("inline-overlay-update", snapshot.Target.ProcessId, exception);
        }
    }

    /// <summary>Puts a replacement on the clipboard, at the user's explicit request.</summary>
    private void CopyReplacementToClipboard(string? replacement)
    {
        if (string.IsNullOrEmpty(replacement)) return;
        try
        {
            System.Windows.Clipboard.SetText(replacement);
            _correctionPopup.ShowApplyFeedback(
                "Исправление скопировано в буфер обмена.", isError: false, offerCopy: false, offerRetry: false);
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical("clipboard-copy-failed", $"type={exception.GetType().Name}");
        }
    }

    /// <summary>Applies a single correction through the verified write path.</summary>
    public async Task ApplyIssueAsync(TextIssue issue)
    {
        CompatibilityLogger.State("apply-issue-requested");
        // The card's own snapshot first: it is the one the user is looking at.
        var snapshot = _cardCoordinator.PinnedSnapshot ?? _latestSnapshot;
        if (snapshot is null || _monitor is null || _correctionApplication is null)
        {
            NotifyCorrectionFailure("Нет активного текстового поля.");
            return;
        }

        // The card moved into its applying phase before raising this, so its token is the
        // one this write belongs to. If anything supersedes it while the write is in flight,
        // the outcome is dropped rather than drawn over newer text — §5.
        var token = _correctionPopup.Token;
        var normalized = CorrectionPresentation.NormalizeForApply(issue);
        var outcome = await _correctionApplication.ApplyAsync(
            normalized, snapshot, _monitor, _correctionGate, _applicationLifetimeCts.Token);

        await _dispatcher.InvokeAsync(() => HandleCorrectionOutcome(outcome, normalized.Replacement, token));
    }

    /// <summary>Applies all safe corrections in the current snapshot in one verified edit.</summary>
    public async Task ApplyAllAsync()
    {
        CompatibilityLogger.State("apply-all-requested");
        var snapshot = _latestSnapshot;
        if (snapshot is null || _monitor is null || _correctionApplication is null)
        {
            NotifyCorrectionFailure("Нет активного текстового поля.");
            return;
        }

        var outcome = await _correctionApplication.ApplyAllSafeAsync(
            snapshot, _monitor, _correctionGate, _applicationLifetimeCts.Token);
        await _dispatcher.InvokeAsync(() => HandleCorrectionOutcome(outcome, null, _correctionPopup.Token));
    }

    /// <summary>
    /// Puts the result of a correction where the user is already looking.
    /// </summary>
    /// <remarks>
    /// <para>§12: a correction that did not apply is an ordinary outcome, not an error
    /// condition, and it used to be reported with a modal <c>MessageBox</c> — which takes the
    /// keyboard away from the field being corrected, hides the card that names the word, and
    /// has to be dismissed before anything else can happen. Everything short of "there is no
    /// card to put this on" now goes on the card, with the recovery actions the outcome
    /// actually supports.</para>
    ///
    /// <para>The clipboard is written only when the card is offering a copy, and only after
    /// the user asks for it — a failed correction is not a reason to overwrite what they had
    /// copied.</para>
    ///
    /// <para><b>Except staleness.</b> «Текст изменился» is not a failure the user can retry
    /// their way out of, and offering «Повторить» for it is what produced the dead card in the
    /// reported defect. The write path can still see a change our snapshot stream has not
    /// published yet, and where it does the card refreshes — the same response as any other
    /// text change — instead of reporting an error about it.</para>
    /// </remarks>
    private void HandleCorrectionOutcome(
        CorrectionApplicationOutcome outcome,
        string? replacementForCopy,
        long cardToken)
    {
        if (outcome.Succeeded)
        {
            _correctionPopup.Dismiss();
            if (Settings.HidePanelAfterSuccessfulApply)
            {
                _suggestions?.Hide();
            }

            CompatibilityLogger.Technical("correction-ui-success", $"path={outcome.WritePath ?? "n/a"}");
            return;
        }

        CompatibilityLogger.Technical(
            "correction-ui-failed",
            $"status={outcome.Status} offerCopy={(outcome.OfferCopy ? 1 : 0)} offerRefresh={(outcome.OfferRefresh ? 1 : 0)}");

        if (IsStaleTextOutcome(outcome.Status) && _correctionPopup.IsVisible)
        {
            if (!_correctionPopup.IsCurrent(cardToken)) return;

            CompatibilityLogger.Technical("correction-card-invalidated", $"reason=apply-{outcome.Status}");
            _correctionPopup.BeginRecheck();
            _ = _monitor?.RefreshOnceAfterSuppressionAsync();
            return;
        }

        var canCopy = outcome.OfferCopy && !string.IsNullOrEmpty(replacementForCopy);
        if (_correctionPopup.IsVisible)
        {
            _correctionPopup.ShowApplyFeedback(
                outcome.UserMessage,
                isError: true,
                offerCopy: canCopy,
                offerRetry: outcome.OfferRefresh,
                token: cardToken);
            return;
        }

        // No card on screen — the panel path, or a popup the user has already closed.
        _suggestions?.ShowApplyFeedback(outcome.UserMessage, canCopy ? replacementForCopy : null);
    }

    /// <summary>Outcomes that mean "the text moved on", not "the write failed".</summary>
    private static bool IsStaleTextOutcome(CorrectionApplicationStatus status) => status
        is CorrectionApplicationStatus.CorrectionStale
        or CorrectionApplicationStatus.OriginalMismatch
        or CorrectionApplicationStatus.AmbiguousRelocation;

    private void NotifyCorrectionFailure(string message)
    {
        CompatibilityLogger.Technical("correction-apply-failed", "reason=no-context");
        if (_correctionPopup.IsVisible)
        {
            _correctionPopup.ShowApplyFeedback(message, isError: true, offerCopy: false, offerRetry: false);
            return;
        }

        _suggestions?.ShowApplyFeedback(message, replacementForCopy: null);
    }

    /// <summary>
    /// Applies a lexical (dictionary card) replacement through the same verified write path
    /// as a correction card.
    /// </summary>
    /// <remarks>
    /// This used to compose a whole new value and push it with <c>ValuePattern.SetValue</c>,
    /// which is a second write path with none of the range validation, none of the strategy
    /// fallback and none of the read-back the correction path has. A word swap from the
    /// dictionary popup is the same kind of edit as a correction and now takes the same route.
    /// </remarks>
    public async Task ReplaceTargetTextAsync(
        TextSnapshot snapshot,
        Func<string, CanonicalCorrection?> bind)
    {
        if (!_correctionGate.TryEnter())
        {
            NotifyCorrectionFailure("Предыдущая правка ещё выполняется.");
            return;
        }

        _monitor?.BeginApplyingCorrection();
        CompatibilityLogger.State("correction-started");

        try
        {
            var read = await snapshot.Target.TryReadTextAsync();
            if (!read.Succeeded)
            {
                CompatibilityLogger.State("correction-failed");
                NotifyCorrectionFailure("Поле ввода временно недоступно.");
                return;
            }

            var correction = bind(read.Text);
            if (correction is null)
            {
                // The word moved between reading the card and pressing it. §4: a text change
                // is refreshed, not reported — and the dictionary card is about a word that
                // may no longer be there, so it closes rather than asserting anything.
                CompatibilityLogger.State("correction-failed");
                CompatibilityLogger.Technical("correction-card-invalidated", "reason=lexical-bind-stale");
                _lexicalPopup?.Hide();
                if (_correctionPopup.IsVisible)
                {
                    _correctionPopup.BeginRecheck();
                    _ = _monitor?.RefreshOnceAfterSuppressionAsync();
                }

                return;
            }

            var write = await snapshot.Target.TryApplyCorrectionAsync(correction, read.Text);
            if (!write.Succeeded)
            {
                CompatibilityLogger.State("correction-failed");
                NotifyCorrectionFailure("WriteLite не удалось изменить текст в этом поле.");
                return;
            }

            CompatibilityLogger.State("correction-completed");
            _correctionPopup.Dismiss();
            _lexicalPopup?.Hide();
            if (Settings.HidePanelAfterSuccessfulApply)
            {
                _suggestions?.Hide();
            }

            if (_monitor is not null)
            {
                await _monitor.RefreshOnceAfterSuppressionAsync();
            }
        }
        finally
        {
            _monitor?.EndApplyingCorrection();
            _correctionGate.Exit();
        }
    }
}