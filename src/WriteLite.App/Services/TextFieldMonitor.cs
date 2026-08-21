using System.Windows.Automation;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows.Threading;
using WriteLite.Models;
using WriteLite.Services.LanguageEngine;

namespace WriteLite.Services;

public sealed class TextFieldMonitor : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan AnalysisDebounce = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan CorrectionSuppression = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan IdleTickInterval = TimeSpan.FromMilliseconds(500);

    private readonly Dispatcher _dispatcher;
    private readonly DebouncedTextAnalyzer _fastAnalysisRunner;
    private readonly DebouncedTextAnalyzer? _deepAnalysisRunner;
    private readonly ActiveTextTargetManager _targetManager = new();
    private readonly WriteLiteIssueMerger _issueMerger = new();
    private readonly CompositeTextTargetAdapter _adapter = new();
    private readonly UiInteractionStateMachine _uiState;
    private readonly UserInputActivityTracker _inputTracker = new();
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _idleTimer;
    private readonly int _ownProcessId = Environment.ProcessId;
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _uiaOperationGate = new(1, 1);
    private readonly Func<bool> _isFullDictionaryLoaded;
    private AutomationFocusChangedEventHandler? _focusHandler;
    private WinEventDelegate? _foregroundHandler;
    private IntPtr _foregroundHook;
    private AutomationElement? _lastLoggedElement;
    private CancellationTokenSource? _monitorCancellation;
    private DateTimeOffset _suppressedUntil;
    private string? _lastText;
    private string _lastFastText = string.Empty;
    private IReadOnlyList<TextIssue> _lastFastIssues = [];
    private long _textVersion;
    private bool _started;
    private bool _isRefreshing;
    private bool _forceNextAsUserEdit;
    private bool _showUiOnlyWhileEditing = true;
    private TextSnapshot? _lastPublishedSnapshot;
    private bool _lastPublishWasNull = true;
    private long _lastFocusSignalTick;
    private long _lastForegroundSignalTick;

    public TextFieldMonitor(
        Dispatcher dispatcher,
        ITextAnalyzer analyzer,
        ITextAnalyzer? deepAnalyzer = null,
        UiInteractionStateMachine? uiState = null,
        Func<bool>? isFullDictionaryLoaded = null)
    {
        _dispatcher = dispatcher;
        _uiState = uiState ?? new UiInteractionStateMachine();
        _isFullDictionaryLoaded = isFullDictionaryLoaded ?? (() => true);
        _fastAnalysisRunner = new DebouncedTextAnalyzer(analyzer, TimeSpan.FromMilliseconds(180));
        _deepAnalysisRunner = deepAnalyzer is null ? null : new DebouncedTextAnalyzer(deepAnalyzer, AnalysisDebounce);
        _pollTimer = new DispatcherTimer(
            PollInterval,
            DispatcherPriority.Background,
            (_, _) => RefreshNow(),
            dispatcher);
        _idleTimer = new DispatcherTimer(
            IdleTickInterval,
            DispatcherPriority.Background,
            (_, _) => OnIdleTick(),
            dispatcher);
    }

    /// <summary>
    /// When true (default), main UI appears only after confirmed text editing.
    /// When false, legacy behaviour: analysis UI may appear on editable focus.
    /// </summary>
    public void SetShowUiOnlyWhileEditing(bool enabled) => _showUiOnlyWhileEditing = enabled;

    /// <summary>Configure idle hide grace (clamped). Default 4 seconds.</summary>
    public void SetIdleGrace(TimeSpan idleGrace) => _uiState.SetIdleGrace(idleGrace);

    public (string? TargetId, int GenerationId, long TextVersion, string? LastText) GetTargetState()
    {
        lock (_stateGate)
        {
            return (
                _targetManager.CurrentTarget?.Identity.RuntimeId,
                _targetManager.GenerationId,
                _textVersion,
                _lastText);
        }
    }

    public event EventHandler<TextSnapshot?>? SnapshotChanged;

    public bool IsApplyingCorrection { get; private set; }
    public bool IsSuggestionsWindowOpen { get; private set; }
    public bool IsShuttingDown { get; private set; }
    public UiInteractionState InteractionState => _uiState.State;
    public bool ShouldShowMainUi => _uiState.ShouldShowMainUi;
    public bool ShouldKeepPopup => _uiState.ShouldKeepPopup;
    public long TextVersion => _textVersion;
    public TimeSpan MaxAnalysisDuration => _deepAnalysisRunner?.MaxDuration ?? _fastAnalysisRunner.MaxDuration;
    public TimeSpan LastAnalysisDuration => _deepAnalysisRunner?.LastDuration ?? _fastAnalysisRunner.LastDuration;
    public InputLatencySnapshot FastAnalysisLatency => _fastAnalysisRunner.DurationSnapshot;
    public InputLatencySnapshot DeepAnalysisLatency => _deepAnalysisRunner?.DurationSnapshot ?? default;
    public bool IsSuppressed => IsMonitoringSuppressed();

    public static bool IsWriteLiteProcess(int processId, int writeLiteProcessId)
    {
        return processId == writeLiteProcessId;
    }

    public static bool IsWriteLiteProcess(int processId, int writeLiteProcessId, string? processName)
        => processId == writeLiteProcessId
           || string.Equals(processName, "WriteLite", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetProcessName(int processId)
    {
        try { return Process.GetProcessById(processId).ProcessName; }
        catch { return null; }
    }

    /// <summary>Applies analysis debounce from settings without restarting monitoring.</summary>
    public void SetAnalysisDelay(TimeSpan delay)
    {
        if (_deepAnalysisRunner is null)
        {
            _fastAnalysisRunner.SetDebounce(Clamp(delay, 120, 220));
            return;
        }

        _fastAnalysisRunner.SetDebounce(TimeSpan.FromMilliseconds(180));
        _deepAnalysisRunner.SetDebounce(Clamp(delay, 700, 1200));
    }

    private static TimeSpan Clamp(TimeSpan value, double minimumMs, double maximumMs)
        => TimeSpan.FromMilliseconds(Math.Clamp(value.TotalMilliseconds, minimumMs, maximumMs));

    /// <summary>
    /// Begins monitoring. Returns as soon as WriteLite's own state is set up; the UI
    /// Automation subscription completes in the background.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the focus subscription is not awaited.</b>
    /// <c>Automation.AddAutomationFocusChangedEventHandler</c> is a synchronous cross-process
    /// COM call: it reaches into whatever currently has focus to attach a listener, and it
    /// returns when that application's automation provider answers. When the provider is
    /// wedged — a hung Electron window, a modal native dialog, an app mid-crash — it does not
    /// answer, and this call blocks for as long as the COM timeout allows.</para>
    ///
    /// <para>Called on the dispatcher during startup, as it was, that is a foreign process
    /// holding WriteLite's window closed. §36 of the Phase 7 brief asks for this to be fixed
    /// in the architecture rather than explained away as a test-environment artefact, and the
    /// fix is that the shell no longer waits for it: the timers, the foreground hook and the
    /// started flag are all local work and happen immediately, and the one call that talks to
    /// another process is moved off the dispatcher and bounded.</para>
    ///
    /// <para>Failure is isolated rather than fatal. Without the focus subscription WriteLite
    /// still tracks the active field through <see cref="SubscribeForegroundEvents"/> and the
    /// poll timer — it notices focus changes a fraction of a second later instead of
    /// immediately. That is a degraded monitor, not a broken editor.</para>
    /// </remarks>
    public void Start()
    {
        if (_started) return;

        _monitorCancellation = new CancellationTokenSource();

        // Local state first, so the shell is usable whatever the automation tree is doing.
        _pollTimer.Start();
        _idleTimer.Start();
        SubscribeForegroundEvents();
        _started = true;
        CompatibilityLogger.State("monitor-started");

        SubscribeFocusEventsInBackground();
        RefreshNow();
    }

    /// <summary>How long to wait for the focus subscription before giving up on this attempt.</summary>
    /// <remarks>
    /// Five seconds is far longer than the call takes against a healthy provider — it is
    /// single-digit milliseconds — and short enough that a wedged one is recorded rather than
    /// waited on indefinitely. Nothing is blocked meanwhile; the timeout exists so the log
    /// says which application was responsible.
    /// </remarks>
    private static readonly TimeSpan FocusSubscriptionTimeout = TimeSpan.FromSeconds(5);

    private void SubscribeFocusEventsInBackground()
    {
        var handler = new AutomationFocusChangedEventHandler(OnAutomationFocusChanged);

        _ = Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var subscription = Task.Run(() => Automation.AddAutomationFocusChangedEventHandler(handler));
                if (!subscription.Wait(FocusSubscriptionTimeout))
                {
                    // The call is still outstanding inside a COM apartment and cannot be
                    // cancelled. It is left to finish on its own; what matters is that
                    // nothing is waiting for it.
                    CompatibilityLogger.State(
                        $"focus-subscription-slow elapsedMs={sw.ElapsedMilliseconds}");
                    return;
                }

                if (!_started)
                {
                    // Stopped while the subscription was in flight. Undo it here rather than
                    // leaving a handler attached that Stop has already stopped looking for.
                    Automation.RemoveAutomationFocusChangedEventHandler(handler);
                    return;
                }

                _focusHandler = handler;
                CompatibilityLogger.State(
                    $"focus-subscription-ready elapsedMs={sw.ElapsedMilliseconds}");
            }
            catch (Exception exception)
            {
                CompatibilityLogger.AccessError("subscribe-focus-events", null, exception);
            }
        });
    }

    public void Stop()
    {
        if (!_started) return;

        // Unsubscribing is the same cross-process COM call as subscribing and blocks in the
        // same circumstances — on the way out that would be a hang closing to tray rather than
        // a hang starting up. Off the dispatcher for the same reason, and not waited on: the
        // process is either continuing without a monitor or exiting, and neither needs the
        // acknowledgement.
        var handler = _focusHandler;
        if (handler is not null)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    Automation.RemoveAutomationFocusChangedEventHandler(handler);
                }
                catch (Exception exception)
                {
                    CompatibilityLogger.AccessError("unsubscribe-focus-events", null, exception);
                }
            });
        }

        _pollTimer.Stop();
        _idleTimer.Stop();
        UnsubscribeForegroundEvents();
        _focusHandler = null;
        _fastAnalysisRunner.Cancel();
        _deepAnalysisRunner?.Cancel();
        _monitorCancellation?.Cancel();
        _monitorCancellation?.Dispose();
        _monitorCancellation = null;
        _targetManager.Clear();
        _lastText = null;
        _lastFastText = string.Empty;
        _lastFastIssues = [];
        _textVersion = 0;
        _forceNextAsUserEdit = false;
        _lastPublishedSnapshot = null;
        _uiState.OnTargetLost();
        _started = false;
        CompatibilityLogger.State("monitor-stopped");
        PublishSnapshot(null);
    }

    /// <summary>Notify that a correction/lexical popup is open so idle hide does not dismiss it.</summary>
    public void SetPopupInteractionOpen(bool isOpen)
    {
        var previous = _uiState.State;
        var previousKeepPopup = _uiState.ShouldKeepPopup;
        if (isOpen)
        {
            _uiState.OnPopupOpened();
        }
        else
        {
            _uiState.OnPopupClosed();
        }

        if (previous != _uiState.State || previousKeepPopup != _uiState.ShouldKeepPopup)
        {
            RepublishWithCurrentUiState();
        }
    }

    public void SetSuggestionsWindowOpen(bool isOpen)
    {
        IsSuggestionsWindowOpen = isOpen;
        if (isOpen)
        {
            CompatibilityLogger.State("monitoring-suppressed");
            _fastAnalysisRunner.Cancel();
            _deepAnalysisRunner?.Cancel();
        }
    }

    public void BeginApplyingCorrection()
    {
        IsApplyingCorrection = true;
        _forceNextAsUserEdit = true;
        SuppressFor(CorrectionSuppression);
        _fastAnalysisRunner.Cancel();
        _deepAnalysisRunner?.Cancel();
    }

    public void EndApplyingCorrection()
    {
        IsApplyingCorrection = false;
        _forceNextAsUserEdit = true;
        SuppressFor(CorrectionSuppression);
    }

    public void BeginShutdown()
    {
        IsShuttingDown = true;
        _fastAnalysisRunner.Cancel();
        _deepAnalysisRunner?.Cancel();
    }

    public async Task RefreshOnceAfterSuppressionAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(CorrectionSuppression, cancellationToken).ConfigureAwait(false);
        // Force a full re-read/re-analysis after apply (do not skip as "same text").
        lock (_stateGate)
        {
            _lastText = null;
            _lastFastText = string.Empty;
            _lastFastIssues = [];
            _forceNextAsUserEdit = true;
        }

        await RefreshNowAsync(force: true).ConfigureAwait(false);
    }

    private void OnIdleTick()
    {
        if (!_started) return;

        if (!_showUiOnlyWhileEditing) return;

        var previous = _uiState.State;
        var next = _uiState.OnIdleTick(DateTimeOffset.UtcNow);
        if (next == previous) return;

        CompatibilityLogger.Technical("ui-interaction-state", $"from={previous} to={next} reason=idle-tick");
        if (_lastPublishedSnapshot is not null)
        {
            PublishSnapshot(WithUiState(_lastPublishedSnapshot));
        }
    }

    private void SuppressFor(TimeSpan duration)
    {
        _suppressedUntil = DateTimeOffset.Now.Add(duration);
        CompatibilityLogger.State("monitoring-suppressed");
    }

    private void OnAutomationFocusChanged(object sender, AutomationFocusChangedEventArgs e)
    {
        if (!AcceptSignal(ref _lastFocusSignalTick, 90))
        {
            CompatibilityLogger.Technical("focus-event-coalesced", "windowMs=90");
            return;
        }

        CompatibilityLogger.State("focus-event-received");
        _ = RefreshNowAsync(sender as AutomationElement, force: false);
    }

    private void SubscribeForegroundEvents()
    {
        _foregroundHandler = OnForegroundWindowChanged;
        _foregroundHook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            _foregroundHandler,
            0,
            0,
            WineventOutOfContext | WineventSkipOwnProcess);

        if (_foregroundHook == IntPtr.Zero)
        {
            CompatibilityLogger.State("foreground-hook-unavailable");
        }
    }

    private void UnsubscribeForegroundEvents()
    {
        if (_foregroundHook == IntPtr.Zero) return;

        UnhookWinEvent(_foregroundHook);
        _foregroundHook = IntPtr.Zero;
        _foregroundHandler = null;
    }

    private void OnForegroundWindowChanged(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (!AcceptSignal(ref _lastForegroundSignalTick, 120))
        {
            CompatibilityLogger.Technical("foreground-event-coalesced", "windowMs=120");
            return;
        }

        CompatibilityLogger.State("foreground-window-changed");
        _ = RefreshNowAsync(force: false);
    }

    public void RefreshNow()
    {
        _ = RefreshNowAsync(force: false);
    }

    private async Task RefreshNowAsync(AutomationElement? knownElement = null, bool force = false)
    {
        if (!_started || _monitorCancellation is null) return;
        if (!force && IsMonitoringSuppressed())
        {
            CompatibilityLogger.State("monitoring-suppressed");
            return;
        }

        lock (_stateGate)
        {
            if (_isRefreshing) return;
            _isRefreshing = true;
        }

        try
        {
            var cancellationToken = _monitorCancellation.Token;
            var focusedElement = knownElement ?? await GetFocusedElementAsync(cancellationToken).ConfigureAwait(false);
            var element = await ResolveCandidateElementAsync(focusedElement, cancellationToken).ConfigureAwait(false);
            await ProcessElementAsync(element, force, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CompatibilityLogger.State("analysis-cancelled");
        }
        catch (TimeoutException)
        {
            // A transient or hung UI Automation provider must not erase the last
            // valid underline/indicator. The bounded worker remains single-flight.
            CompatibilityLogger.State("uia-timeout");
        }
        catch (ElementNotAvailableException)
        {
            // Expected, not exceptional: the element disappeared because the user
            // closed a window, navigated a page, or the host rebuilt its tree.
            // Routing it through the generic handler below cleared the target and
            // forced a full re-resolve, whose next attempt failed the same way —
            // logs showed this repeating on a ~0.4 s cadence. Keep the last known
            // target; the next successful refresh replaces it.
            CompatibilityLogger.State("uia-element-gone");
        }
        catch (Exception exception)
        {
            CompatibilityLogger.AccessError("refresh", null, exception);
            ClearTarget();
        }
        finally
        {
            lock (_stateGate)
            {
                _isRefreshing = false;
            }
        }
    }

    private bool IsMonitoringSuppressed()
    {
        return IsShuttingDown ||
               IsApplyingCorrection ||
               IsSuggestionsWindowOpen ||
               DateTimeOffset.Now < _suppressedUntil;
    }

    /// <summary>
    /// The control the keyboard is typing into.
    /// </summary>
    /// <remarks>
    /// Not simply <c>AutomationElement.FocusedElement</c>: a provider can claim focus it does
    /// not have, and on a machine with several messengers running one routinely does — see
    /// <see cref="FocusedControlResolver"/>. Following the claim makes WriteLite analyse a
    /// field the user is not typing in, and decorate nothing they can see.
    /// </remarks>
    private Task<AutomationElement?> GetFocusedElementAsync(CancellationToken cancellationToken)
        => RunBoundedUiaAsync(
            () => FocusedControlResolver.Resolve().Element,
            TimeSpan.FromMilliseconds(700),
            cancellationToken);

    private async Task ProcessElementAsync(AutomationElement? element, bool force, CancellationToken cancellationToken)
    {
        if (element is null)
        {
            ClearTarget();
            return;
        }

        // Sample keyboard activity on the dispatcher path (poll/focus) — never logs key content.
        try
        {
            _inputTracker.SampleKeyboardState();
        }
        catch
        {
            // Non-fatal: text-delta path remains authoritative.
        }

        ElementReadResult result;
        try
        {
            result = await ReadElementAsync(element, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException)
        {
            CompatibilityLogger.AccessError("process-element", null, exception);
            ClearTarget();
            return;
        }

        if (result.Ignore)
        {
            if (!result.IsOwnProcess)
            {
                // Immediate hide when leaving for unsupported / read-only / password / static text.
                HideForUnsupportedTarget();
            }

            return;
        }

        var targetChanged = _targetManager.Update(element, result.Target!);
        var targetId = result.Target!.Identity.RuntimeId;
        if (targetChanged)
        {
            _fastAnalysisRunner.Cancel();
            _deepAnalysisRunner?.Cancel();
            _lastText = null;
            _textVersion = 0;
            _lastPublishedSnapshot = null;
            var previous = _uiState.State;
            _uiState.OnEditableFocused(targetId, _textVersion);
            CompatibilityLogger.Technical("ui-interaction-state", $"from={previous} to={_uiState.State} reason=target-activated");
            PublishSnapshot(null);
            CompatibilityLogger.State("target-activated");
        }

        var generationId = _targetManager.GenerationId;
        var textUnchanged = string.Equals(_lastText, result.Text, StringComparison.Ordinal);

        // First observation of this field (or after target switch): establish baseline without showing UI.
        // Focus / scroll / caret / selection alone must not open the WriteLite chrome.
        if (_lastText is null)
        {
            _lastText = result.Text;
            _lastFastText = result.Text;
            _lastFastIssues = [];
            _textVersion++;
            var previous = _uiState.State;
            _uiState.OnEditableFocused(targetId, _textVersion);

            if (_forceNextAsUserEdit)
            {
                _forceNextAsUserEdit = false;
                _uiState.OnCorrectionApplied(targetId, _textVersion);
                CompatibilityLogger.Technical("ui-interaction-state", $"from={previous} to={_uiState.State} reason=correction-baseline");
                // Fall through to analysis — apply is confirmed user-linked editing.
            }
            else if (!_showUiOnlyWhileEditing)
            {
                // Legacy opt-out: treat first focus as an editing session so analysis UI can appear.
                _uiState.OnTextChanged(targetId, _textVersion, TextChangeSource.UserInput);
                CompatibilityLogger.Technical("ui-interaction-state", $"from={previous} to={_uiState.State} reason=legacy-show-on-focus");
            }
            else
            {
                CompatibilityLogger.Technical("ui-interaction-state", $"from={previous} to={_uiState.State} reason=focus-baseline");
                CompatibilityLogger.Technical("ui-main-hidden", "reason=editable-focused-no-edit");
                return;
            }
        }
        else if (!force && !targetChanged && textUnchanged)
        {
            return;
        }
        else if (!textUnchanged || _forceNextAsUserEdit)
        {
            _lastText = result.Text;
            _textVersion++;
            var previous = _uiState.State;
            var source = _forceNextAsUserEdit
                ? TextChangeSource.CorrectionApply
                : _inputTracker.WasRecentlyActive()
                    ? TextChangeSource.UserInput
                    : TextChangeSource.AutomationTextChange;
            _forceNextAsUserEdit = false;
            _uiState.OnTextChanged(targetId, _textVersion, source);
            CompatibilityLogger.Technical(
                "ui-interaction-state",
                $"from={previous} to={_uiState.State} reason=text-changed source={source} version={_textVersion}");
        }
        else if (force && textUnchanged)
        {
            // Forced refresh of same text (e.g. dictionary change) — only re-analyze if UI already active.
            if (!_uiState.ShouldShowMainUi)
            {
                return;
            }
        }

        // Analysis runs only when the user is in an editing session (or after WriteLite apply).
        if (!_uiState.ShouldShowMainUi && !_forceNextAsUserEdit)
        {
            CompatibilityLogger.Technical("analysis-skipped", "reason=not-editing");
            return;
        }

        CompatibilityLogger.Operation("read", result.ProcessId, true, result.Target!.AdapterName);
        CompatibilityLogger.State("analysis-scheduled");

        var snapshotTarget = result.Target;
        var snapshotText = result.Text;
        PublishSnapshot(new TextSnapshot(
            snapshotTarget!, snapshotText, [], DateTimeOffset.Now, generationId,
            IndicatorState: AnalysisIndicatorState.FastAnalyzing,
            IsFullDictionaryLoaded: true,
            ShowMainUi: _uiState.ShouldShowMainUi,
            InteractionState: _uiState.State,
            TextVersion: _textVersion));
        CompatibilityLogger.Technical("fast-analysis-started", $"generation={generationId}");

        var dirtyPlan = DirtyIssueAnalysis.Plan(_lastFastText, snapshotText, fullDocument: force);
        CompatibilityLogger.Technical("fast-analysis-range",
            $"start={dirtyPlan.CurrentRange.Start} length={dirtyPlan.CurrentRange.Length} textLength={snapshotText.Length}");
        var analysis = await _fastAnalysisRunner.StartAsync(dirtyPlan.Segment, cancellationToken).ConfigureAwait(false);
        if (analysis is null || analysis.IsStale) return;
        if (!_targetManager.BelongsToCurrent(generationId, snapshotTarget!)) return;

        var fastIssues = _issueMerger.Merge(
            DirtyIssueAnalysis.Merge(snapshotText, _lastFastIssues, analysis.Issues, dirtyPlan)).Issues;
        _lastFastText = snapshotText;
        _lastFastIssues = fastIssues;

        var supportsWrite = snapshotTarget!.SupportsDirectWrite;
        var fullDict = _isFullDictionaryLoaded();
        var indicator = AnalysisIndicatorResolver.Resolve(
            snapshotText,
            fastIssues,
            supportsWrite,
            fullDict);

        var applicable = fastIssues.Count(i => i.CanApplyAutomatically && !string.IsNullOrEmpty(i.Replacement));
        PublishSnapshot(new TextSnapshot(
            snapshotTarget,
            snapshotText,
            fastIssues,
            DateTimeOffset.Now,
            generationId,
            analysis.RequestId,
            IsFullDictionaryLoaded: fullDict,
            IndicatorState: indicator,
            ApplicableIssueCount: applicable,
            StatusMessage: AnalysisIndicatorResolver.ResolveTooltip(indicator),
            ShowMainUi: _uiState.ShouldShowMainUi,
            InteractionState: _uiState.State,
            TextVersion: _textVersion));
        CompatibilityLogger.Technical("fast-analysis-completed", $"generation={generationId} issueCount={fastIssues.Count}");

        if (_deepAnalysisRunner is not null)
        {
            _ = RunDeepAnalysisAsync(snapshotTarget, snapshotText, generationId, fastIssues, cancellationToken);
        }
    }

    private async Task RunDeepAnalysisAsync(
        AutomationTextTarget target,
        string text,
        int generationId,
        IReadOnlyList<TextIssue> fastIssues,
        CancellationToken cancellationToken)
    {
        try
        {
            CompatibilityLogger.Technical("deep-analysis-started", $"generation={generationId}");
            var analysis = await _deepAnalysisRunner!.StartAsync(text, cancellationToken).ConfigureAwait(false);
            if (analysis is null || analysis.IsStale
                || !_targetManager.BelongsToCurrent(generationId, target)
                || !string.Equals(_lastText, text, StringComparison.Ordinal))
            {
                CompatibilityLogger.Technical("stale-result-rejected", $"stage=deep generation={generationId}");
                return;
            }

            // Deep is supplementary: it must never erase a fast spelling/rule hit.
            var mergedIssues = _issueMerger.Merge(fastIssues, [], analysis.Issues).Issues
                .Where(i => i.Start >= 0 && i.Start + i.Length <= text.Length
                            && string.Equals(text.Substring(i.Start, i.Length), i.Original, StringComparison.Ordinal))
                .ToList();
            var fullDict = _isFullDictionaryLoaded();
            var indicator = AnalysisIndicatorResolver.Resolve(text, mergedIssues, target.SupportsDirectWrite, fullDict);
            var applicable = mergedIssues.Count(i => i.CanApplyAutomatically && !string.IsNullOrEmpty(i.Replacement));
            PublishSnapshot(new TextSnapshot(target, text, mergedIssues, DateTimeOffset.Now, generationId,
                analysis.RequestId, fullDict, indicator, applicable, AnalysisIndicatorResolver.ResolveTooltip(indicator),
                ShowMainUi: _uiState.ShouldShowMainUi,
                InteractionState: _uiState.State,
                TextVersion: _textVersion));
            CompatibilityLogger.Technical("snapshot-merged", $"generation={generationId} fastCount={fastIssues.Count} deepCount={analysis.Issues.Count} mergedCount={mergedIssues.Count}");
            CompatibilityLogger.Technical("deep-analysis-completed", $"generation={generationId} issueCount={mergedIssues.Count}");
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke owns the next deep snapshot.
        }
    }

    private async Task<AutomationElement?> ResolveCandidateElementAsync(AutomationElement? element, CancellationToken cancellationToken)
    {
        if (element is null) return null;

        return await RunBoundedUiaAsync(() =>
        {
            var current = element;
            for (var depth = 0; depth < 8 && current is not null; depth++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var processId = current.Current.ProcessId;
                    if (IsWriteLiteProcess(processId, _ownProcessId, TryGetProcessName(processId)))
                    {
                        return current;
                    }

                    var isEnabled = current.Current.IsEnabled;
                    var isOffscreen = current.Current.IsOffscreen;
                    var isPassword = current.Current.IsPassword;
                    var supportsValue = current.TryGetCurrentPattern(ValuePattern.Pattern, out _);
                    var supportsText = current.TryGetCurrentPattern(TextPattern.Pattern, out _);

                    if (EditableTextTargetPolicy.IsEditableTextTarget(current) && _adapter.Select(current) is not null)
                    {
                        CompatibilityLogger.State("candidate-element-found");
                        return current;
                    }

                    current = TreeWalker.ControlViewWalker.GetParent(current);
                }
                catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException)
                {
                    CompatibilityLogger.AccessError("candidate-parent-search", null, exception);
                    return null;
                }
            }

            CompatibilityLogger.State("candidate-rejected_no-supported-parent");
            return null;
        }, TimeSpan.FromMilliseconds(700), cancellationToken).ConfigureAwait(false);
    }

    private async Task<ElementReadResult> ReadElementAsync(AutomationElement element, CancellationToken cancellationToken)
    {
        return await RunBoundedUiaAsync(async () =>
        {
            var processId = element.Current.ProcessId;
            if (IsWriteLiteProcess(processId, _ownProcessId, TryGetProcessName(processId)))
            {
                return ElementReadResult.OwnProcess(processId);
            }

            var isEnabled = element.Current.IsEnabled;
            var isOffscreen = element.Current.IsOffscreen;
            var isPassword = element.Current.IsPassword;
            var controlType = element.Current.LocalizedControlType ?? string.Empty;
            var supportsValue = element.TryGetCurrentPattern(ValuePattern.Pattern, out _);
            var supportsText = element.TryGetCurrentPattern(TextPattern.Pattern, out _);
            var adapter = _adapter.Select(element);

            LogElementOnce(
                element,
                processId,
                controlType,
                supportsValue,
                supportsText,
                isPassword,
                isEnabled,
                isOffscreen);

            if (!EditableTextTargetPolicy.IsEditableTextTarget(element) || adapter is null)
            {
                var reason = !isEnabled
                    ? "disabled"
                    : isOffscreen
                        ? "offscreen"
                        : isPassword
                            ? "password"
                            : "not-editable";
                CompatibilityLogger.State("candidate-rejected_" + reason);
                return ElementReadResult.Ignored(processId);
            }

            var target = new AutomationTextTarget(element, adapter);
            if (target.Bounds.IsEmpty || target.Bounds.Width < 20 || target.Bounds.Height < 12)
            {
                CompatibilityLogger.State("candidate-rejected_invalid-bounds");
                return ElementReadResult.Ignored(processId);
            }

            var read = await target.TryReadTextAsync().ConfigureAwait(false);
            if (!read.Succeeded)
            {
                CompatibilityLogger.Operation("read", target.ProcessId, false, "pattern-read-failed");
                return ElementReadResult.Ignored(processId);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return ElementReadResult.Read(processId, target, read.Text);
        }, TimeSpan.FromMilliseconds(1200), cancellationToken).ConfigureAwait(false);
    }

    private void LogElementOnce(
        AutomationElement element,
        int processId,
        string controlType,
        bool supportsValue,
        bool supportsText,
        bool isPassword,
        bool isEnabled,
        bool isOffscreen)
    {
        try
        {
            if (_lastLoggedElement is not null && Automation.Compare(_lastLoggedElement, element)) return;
        }
        catch (Exception exception)
        {
            CompatibilityLogger.AccessError("compare-elements", processId, exception);
        }

        _lastLoggedElement = element;
        CompatibilityLogger.Element(
            processId,
            controlType,
            supportsValue,
            supportsText,
            isPassword,
            isEnabled,
            isOffscreen);
    }

    private void ClearTarget()
    {
        if (_targetManager.CurrentTarget is not null)
        {
            CompatibilityLogger.State("target-lost");
        }

        _fastAnalysisRunner.Cancel();
        _deepAnalysisRunner?.Cancel();
        _targetManager.Clear();
        _lastText = null;
        _textVersion = 0;
        _lastPublishedSnapshot = null;
        var previous = _uiState.State;
        _uiState.OnTargetLost();
        if (previous != _uiState.State)
        {
            CompatibilityLogger.Technical("ui-interaction-state", $"from={previous} to={_uiState.State} reason=target-lost");
        }

        PublishSnapshot(null);
    }

    private void HideForUnsupportedTarget()
    {
        if (_targetManager.CurrentTarget is not null)
        {
            CompatibilityLogger.State("target-lost");
        }

        _fastAnalysisRunner.Cancel();
        _deepAnalysisRunner?.Cancel();
        _targetManager.Clear();
        _lastText = null;
        _textVersion = 0;
        _lastPublishedSnapshot = null;
        var previous = _uiState.State;
        _uiState.OnUnsupportedOrReadOnlyTarget();
        if (previous != _uiState.State)
        {
            CompatibilityLogger.Technical("ui-interaction-state", $"from={previous} to={_uiState.State} reason=unsupported-or-readonly");
        }

        // Hide main UI immediately; keep popup if user is interacting with it.
        if (_uiState.ShouldKeepPopup)
        {
            PublishSnapshot(null);
        }
        else
        {
            PublishSnapshot(null);
        }
    }

    private void PublishSnapshot(TextSnapshot? snapshot)
    {
        // Deep analysis can complete after the app has begun closing.  Never queue
        // new UI work onto a dispatcher that is already being dismantled.
        if (IsShuttingDown || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (snapshot is not null)
        {
            snapshot = WithUiState(snapshot);
            _lastPublishedSnapshot = snapshot;
            _lastPublishWasNull = false;
        }
        else
        {
            if (_lastPublishWasNull)
            {
                return;
            }

            _lastPublishedSnapshot = null;
            _lastPublishWasNull = true;
        }

        var capture = snapshot;
        try
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (!IsShuttingDown)
                {
                    SnapshotChanged?.Invoke(this, capture);
                }
            });
        }
        catch (InvalidOperationException)
        {
            // Dispatcher shutdown won the race. This is an expected exit path.
            CompatibilityLogger.Technical("snapshot-publish-skipped", "reason=dispatcher-shutdown");
        }
    }

    private void RepublishWithCurrentUiState()
    {
        if (_lastPublishedSnapshot is null) return;
        PublishSnapshot(WithUiState(_lastPublishedSnapshot));
    }

    private TextSnapshot WithUiState(TextSnapshot snapshot)
        => snapshot with
        {
            ShowMainUi = _uiState.ShouldShowMainUi,
            InteractionState = _uiState.State,
            TextVersion = _textVersion,
            IndicatorState = _uiState.ShouldShowMainUi
                ? snapshot.IndicatorState
                : AnalysisIndicatorState.Hidden
        };

    public void Dispose()
    {
        BeginShutdown();
        Stop();
    }

    private async Task<T> RunBoundedUiaAsync<T>(
        Func<T> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!_uiaOperationGate.Wait(0))
        {
            throw new TimeoutException("A previous UI Automation call is still running.");
        }

        var ownsLease = 1;
        var work = Task.Run(() =>
        {
            try { return operation(); }
            finally { ReleaseUiaLease(ref ownsLease); }
        }, CancellationToken.None);
        try
        {
            return await work.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The provider may never return. Release this monitor lease so a
            // different focused provider is not globally deadlocked.
            ReleaseUiaLease(ref ownsLease);
            throw;
        }
    }

    private async Task<T> RunBoundedUiaAsync<T>(
        Func<Task<T>> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!_uiaOperationGate.Wait(0))
        {
            throw new TimeoutException("A previous UI Automation call is still running.");
        }

        var ownsLease = 1;
        var work = Task.Run(async () =>
        {
            try { return await operation().ConfigureAwait(false); }
            finally { ReleaseUiaLease(ref ownsLease); }
        }, CancellationToken.None);
        try
        {
            return await work.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReleaseUiaLease(ref ownsLease);
            throw;
        }
    }

    private void ReleaseUiaLease(ref int ownsLease)
    {
        if (Interlocked.Exchange(ref ownsLease, 0) == 1)
        {
            _uiaOperationGate.Release();
        }
    }

    private static bool AcceptSignal(ref long lastTick, int minimumIntervalMs)
    {
        var now = Environment.TickCount64;
        var previous = Interlocked.Read(ref lastTick);
        if (previous != 0 && now - previous < minimumIntervalMs)
        {
            return false;
        }

        Interlocked.Exchange(ref lastTick, now);
        return true;
    }

    private sealed record ElementReadResult(
        int ProcessId,
        AutomationTextTarget? Target,
        string Text,
        bool Ignore,
        bool IsOwnProcess)
    {
        public static ElementReadResult OwnProcess(int processId) =>
            new(processId, null, string.Empty, Ignore: true, IsOwnProcess: true);

        public static ElementReadResult Ignored(int processId) =>
            new(processId, null, string.Empty, Ignore: true, IsOwnProcess: false);

        public static ElementReadResult Read(int processId, AutomationTextTarget target, string text) =>
            new(processId, target, text, Ignore: false, IsOwnProcess: false);
    }

    private delegate void WinEventDelegate(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr eventHookAssembly,
        WinEventDelegate eventHookCallback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr eventHook);
}
