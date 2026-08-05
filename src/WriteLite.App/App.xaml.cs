using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Automation;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Ai;
using WriteLite.Services.Diagnostics;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Lexical;
using WriteLite.Language.Packs;
using WriteLite.Services.Settings;
using WriteLite.Services.Spelling;
using WriteLite.Views;
using MessageBox = System.Windows.MessageBox;

namespace WriteLite;

public partial class App : System.Windows.Application
{
    private TextFieldMonitor? _monitor;
    private BubbleWindow? _bubble;
    private SuggestionsWindow? _suggestions;
    private CorrectionPopupWindow? _correctionPopup;
    private LexicalPopupWindow? _lexicalPopup;
    private InlineErrorOverlayController? _inlineOverlay;
    private MainWindow? _mainWindow;
    private TrayIconService? _tray;
    private TextSnapshot? _latestSnapshot;
    private WriteLiteOrchestratingAnalyzer? _orchestrator;
    private HybridTextAnalysisService? _hybrid;
    private WriteLiteLanguageEngine? _languageEngine;
    private IAiTextProvider? _aiProvider;
    private LocalAiTextProvider? _localAiProvider;
    private OfflineLexicalKnowledgeService? _lexicalKnowledge;
    private ILexicalReplacementService? _lexicalReplacement;
    private DoubleClickWordObserver? _doubleClickObserver;
    private CancellationTokenSource? _lexicalLookupCts;
    private WriteLiteSettingsStore? _settingsStore;
    private WriteLiteAppSettings _settings = new();
    private WriteLiteAutostartService? _autostart;
    private WriteLiteDiagnosticsService? _diagnostics;
    private readonly AsyncOperationGate _correctionGate = new();
    private readonly CancellationTokenSource _applicationLifetimeCts = new();
    private readonly UiaCircuitBreaker _uiaCircuitBreaker = new();
    private CorrectionApplicationService? _correctionApplication;
    private UiResponsivenessWatchdog? _uiWatchdog;
    private bool _startupComplete;
    private bool _openMainWindowPending;
    private SingleInstanceService? _singleInstance;
    private bool _monitorPaused;
    private int _lastIndicatorGeneration = -1;
    private bool _servicesBound;
    private int _shutdownRequested;

    // async void is deliberate here: it is the one WPF-sanctioned place for it
    // (a top-level event-style override), the unhandled-exception hooks are
    // wired before the first await, and the alternative was measured, not
    // hypothetical — constructing the spell checker inline parked the
    // dispatcher for the whole Hunspell parse at every launch.
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        // Single-instance: secondary process must not start LT/Qwen/UIA/overlay.
        _singleInstance = SingleInstanceService.Acquire();
        if (!_singleInstance.IsPrimaryInstance)
        {
            CompatibilityLogger.State("secondary-instance-exit");
            Shutdown(WriteLiteExitCodes.SecondaryInstanceActivatedPrimary);
            return;
        }

        CompatibilityLogger.State("application-starting");
        _correctionApplication = new CorrectionApplicationService(_uiaCircuitBreaker);
        _uiWatchdog = new UiResponsivenessWatchdog(Dispatcher);
        _uiWatchdog.SetContextProvider(() =>
        {
            var popup = (_correctionPopup?.IsVisible == true ? 1 : 0) + (_lexicalPopup?.IsVisible == true ? 1 : 0) + (_suggestions?.IsVisible == true ? 1 : 0);
            var fast = _monitor?.FastAnalysisLatency.Count ?? 0;
            var deep = _monitor?.DeepAnalysisLatency.Count ?? 0;
            return $"popups={popup} fastSamples={fast} deepSamples={deep} circuit={_uiaCircuitBreaker.OpenCount}";
        });
        _uiWatchdog.Start();
        _singleInstance.StartActivationServer(OpenMainWindow, Dispatcher);

        // Wire local AI technical logs into compatibility.log (no user text).
        WriteLite.AI.Local.LocalAiDiagnostics.Log = (eventName, detail) =>
            CompatibilityLogger.Technical(eventName, detail);

        _settingsStore = new WriteLiteSettingsStore();
        _settings = _settingsStore.Load();
        CompatibilityLogger.Technical(
            "settings-loaded",
            $"localAi={(_settings.LocalAiEnabled ? 1 : 0)} preferQwen={(_settings.PreferQwen ? 1 : 0)} " +
            $"profile={_settings.LocalAiProfile} endpoint={_settings.QwenEndpoint} schemaVersion={_settings.SchemaVersion}");
        // Ensure Qwen defaults for older settings files.
        if (string.IsNullOrWhiteSpace(_settings.QwenEndpoint))
        {
            _settings.QwenEndpoint = "http://127.0.0.1:8742";
        }

        if (string.IsNullOrWhiteSpace(_settings.LocalAiProfile)
            || _settings.LocalAiProfile.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            // Prefer Standard so live Qwen is not stuck on Lite-only path.
            _settings.LocalAiProfile = "Standard";
        }

        _autostart = new WriteLiteAutostartService();

        // Keep setting and registry in sync (do not leave stale Run value when setting is off).
        try
        {
            if (_settings.StartWithWindows && _autostart.CanEnableForCurrentBinary)
            {
                _autostart.SetEnabled(true);
            }
            else if (!_settings.StartWithWindows)
            {
                // Do not wipe user registry unless the setting explicitly says disabled.
                // If binary cannot autostart (dotnet run), leave registry alone.
                if (_autostart.CanEnableForCurrentBinary)
                {
                    _autostart.SetEnabled(false);
                }
            }
        }
        catch
        {
            // non-fatal
        }

        // Hunspell parse (ru_RU.dic is 3.5 MB) belongs on a worker. Inline it
        // held the dispatcher ~3.7 s on this machine (watchdog-measured); the
        // single-instance guard, watchdog and activation server are already
        // wired above, so the app stays responsive during the await and the
        // rest of startup simply completes a few seconds later.
        var spellChecker = await Task.Run(() => new LocalSpellChecker());
        CompatibilityLogger.State(
            $"spelling-dictionaries-loaded ru={spellChecker.Stats.RussianWordCount} en={spellChecker.Stats.EnglishWordCount} ms={spellChecker.Stats.InitialLoadTime.TotalMilliseconds:F1}");

        var dictionary = UserDictionaryService.LoadDefault();
        var ignore = WriteLiteIgnoreService.LoadDefault();
        var engineOptions = new WriteLiteLanguageOptions
        {
            EnableEngine = _settings.ExtendedChecking,
            HostAddress = WriteLiteLanguageOptions.DefaultHostAddress,
            PreferJavaw = true,
            MaxTextLength = _settings.MaxTextLength
        };
        _languageEngine = new WriteLiteLanguageEngine(engineOptions);
        _orchestrator = new WriteLiteOrchestratingAnalyzer(
            new RuleBasedAnalyzer(),
            new SpellTextAnalyzer(spellChecker),
            _languageEngine,
            dictionary,
            ignore);
        _orchestrator.ApplySettings(_settings);

        // Hybrid: rules+spell+LanguageTool + local offline AI (Lite/Qwen).
        _localAiProvider = CreateLocalAiProvider(_settings);
        _aiProvider = _localAiProvider;
        var aiService = new AiTextAnalysisService(_aiProvider);
        _hybrid = new HybridTextAnalysisService(_orchestrator, aiService);
        _hybrid.ApplySettings(_settings);
        CompatibilityLogger.Technical(
            "hybrid-analysis-configured",
            $"localAi={(_settings.LocalAiEnabled ? 1 : 0)} configured={(_aiProvider.IsConfigured ? 1 : 0)} " +
            $"model={_localAiProvider.ModelVersion} " +
            $"qwenEndpoint={_localAiProvider.QwenEndpoint} qwenAvailable={(_localAiProvider.QwenAvailable ? 1 : 0)} " +
            $"profile={_settings.LocalAiProfile} preferQwen={(_settings.PreferQwen ? 1 : 0)}");
        if (_settings.LocalAiEnabled)
        {
            _ = WarmupLocalAiAndRefreshStatusAsync();
        }

        _diagnostics = new WriteLiteDiagnosticsService(
            () => _latestSnapshot,
            () => _languageEngine,
            () => _settings,
            () => _monitor,
            () => _uiWatchdog?.Snapshot);

        if (_settings.ExtendedChecking)
        {
            _languageEngine.StartInBackground();
        }
        else
        {
            CompatibilityLogger.State("language-engine-stopped");
        }

        _languageEngine.StatusChanged += OnLanguageEngineStatusChanged;

        // Fast rules/spell publish first; LanguageTool/Qwen publish a validated replacement snapshot later.
        var fastAnalyzer = new CompositeTextAnalyzer(
            new RuleBasedAnalyzer(),
            new SpellTextAnalyzer(spellChecker));
        _monitor = new TextFieldMonitor(
            Dispatcher,
            fastAnalyzer,
            _hybrid,
            isFullDictionaryLoaded: () => spellChecker.IsFullDictionaryLoaded);
        var delayMs = ResolveAnalysisDelayMs(_settings, _aiProvider);
        _monitor.SetAnalysisDelay(TimeSpan.FromMilliseconds(delayMs));
        _bubble = new BubbleWindow();
        _bubble.ApplyDisplaySettings(_settings.ShowIndicator, _settings.ShowGreenIndicatorWhenClean);
        IssueUnderlineTheme.MinimizeDuplication = _settings.MinimizeUnderlineDuplication;
        _monitor.SetShowUiOnlyWhileEditing(_settings.ShowUiOnlyWhileEditing);
        _monitor.SetIdleGrace(UiInteractionStateMachine.FromSecondsClamped(_settings.EditingIdleGraceSeconds));
        _suggestions = new SuggestionsWindow();
        _correctionPopup = new CorrectionPopupWindow();
        _lexicalPopup = new LexicalPopupWindow();
        _lexicalKnowledge = new OfflineLexicalKnowledgeService();
        // Bootstrap parses the bundled lexical packs from disk. Inline it was
        // the LAST measured startup stall (3.4 s, dump-confirmed at
        // LexicalPackLoader.LoadFromBytes under OnStartup). Off the dispatcher
        // like the spell checker above; the lexical card simply becomes
        // available a few seconds after launch.
        var lexicalBootstrap = _lexicalKnowledge;
        await Task.Run(() => lexicalBootstrap.LoadBootstrapFromDirectory());
        _ = WarmupLexicalKnowledgeAsync(_lexicalKnowledge);
        try
        {
            // Discover() hashes pack files; keep it off the dispatcher too.
            var packs = await Task.Run(() => new LanguagePackRegistry().Discover());
            CompatibilityLogger.Technical("language-packs", new LanguagePackRegistry().ToDiagnosticSummary(packs));
        }
        catch
        {
            // non-fatal diagnostics
        }
        _lexicalReplacement = new LexicalReplacementService();
        _inlineOverlay = new InlineErrorOverlayController(new InlineErrorOverlayWindow(), new TextPatternRangeGeometryProvider());
        _inlineOverlay.IssueClicked += OnInlineIssueClicked;
        _mainWindow = new MainWindow();
        _mainWindow.SetMinimizeToTray(_settings.MinimizeToTrayOnClose);
        _mainWindow.SetMonitorActive(true);
        _mainWindow.SetEngineStatus(ResolveEngineStatusText());
        BindMainWindowServices();

        _tray = new TrayIconService(
            openMainWindow: OpenMainWindow,
            pauseChanged: paused =>
            {
                if (_monitor is null) return;

                _monitorPaused = paused;
                _mainWindow?.SetMonitorActive(!paused);

                if (paused)
                {
                    CompatibilityLogger.State("application-paused");
                    _monitor.Stop();
                    _bubble?.Hide();
                    _suggestions?.Hide();
                }
                else
                {
                    CompatibilityLogger.State("application-resumed");
                    _monitor.Start();
                }
            },
            exitRequested: RequestShutdown);

        _monitor.SnapshotChanged += OnSnapshotChanged;
        _bubble.OpenRequested += OpenSuggestions;
        _bubble.ApplyAllRequested += async (_, _) => await ApplyAllAsync();
        _bubble.OpenMainWindowRequested += (_, _) => OpenMainWindow();
        _bubble.HideIndicatorRequested += (_, _) => CompatibilityLogger.State("indicator-hidden-by-user");
        _suggestions.ApplyIssueRequested += ApplyIssueAsync;
        _suggestions.ApplyAllRequested += ApplyAllAsync;
        _suggestions.PanelHidden += (_, _) =>
        {
            _monitor?.SetSuggestionsWindowOpen(false);
            _monitor?.SetPopupInteractionOpen(false);
        };
        _suggestions.OpenMainWindowRequested += (_, _) => OpenMainWindow();
        _suggestions.AddToDictionaryRequested += OnAddToDictionary;
        _suggestions.IgnoreIssueRequested += OnIgnoreIssue;
        _correctionPopup.ApplyRequested += async (_, issue) => await ApplyIssueAsync(issue);
        _correctionPopup.IgnoreRequested += (_, issue) => OnIgnoreIssue(issue);
        _correctionPopup.AddToDictionaryRequested += (_, issue) => OnAddToDictionary(issue);
        _correctionPopup.IsVisibleChanged += (_, _) =>
        {
            if (_correctionPopup is { IsVisible: false })
            {
                _monitor?.SetPopupInteractionOpen(false);
            }
        };
        _lexicalPopup.ReplaceRequested += OnLexicalReplaceRequested;
        _lexicalPopup.ClosedByUser += (_, _) => _monitor?.SetPopupInteractionOpen(false);
        _lexicalPopup.IsVisibleChanged += (_, _) =>
        {
            if (_lexicalPopup is { IsVisible: false })
            {
                _monitor?.SetPopupInteractionOpen(false);
            }
        };

        _doubleClickObserver = new DoubleClickWordObserver(
            Dispatcher,
            new WordRangeResolver(),
            () =>
            {
                try { return AutomationElement.FocusedElement; }
                catch { return null; }
            },
            () => _monitor?.GetTargetState() ?? (null, 0, 0, null));
        _doubleClickObserver.WordDoubleClicked += OnWordDoubleClicked;

        if (_settings.CheckingEnabled)
        {
            _monitor.Start();
            if (_settings.LexicalCardEnabled)
            {
                _doubleClickObserver.Start();
            }
        }

        CompatibilityLogger.State("application-running");
        _startupComplete = true;

        if (_openMainWindowPending || _settings.OpenMainWindowOnStart)
        {
            _openMainWindowPending = false;
            OpenMainWindow();
        }

        if (e.Args.Contains("--show-main-window", StringComparer.OrdinalIgnoreCase))
        {
            OpenMainWindow();
        }

        // Exercises the same orderly shutdown path as the tray command. This
        // switch is intentionally undocumented in the UI and is used only by
        // cross-process lifecycle verification.
        if (e.Args.Contains("--lifecycle-smoke", StringComparer.OrdinalIgnoreCase))
        {
            _ = Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await Task.Delay(1_500, _applicationLifetimeCts.Token);
                    RequestShutdown();
                }
                catch (OperationCanceledException)
                {
                    // A normal user exit won the race.
                }
            });
        }

        // Best-effort cleanup if the process is terminated without a clean WPF exit path.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                _doubleClickObserver?.Dispose();
                _lexicalLookupCts?.Cancel();
                (_lexicalKnowledge as IDisposable)?.Dispose();
                _languageEngine?.Dispose();
            }
            catch
            {
                // ignore
            }
        };
    }

    private void RequestShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0)
        {
            return;
        }

        CompatibilityLogger.State("exit-requested");
        _applicationLifetimeCts.Cancel();
        _hybrid?.CancelPending();

        if (Dispatcher.CheckAccess())
        {
            Shutdown(0);
        }
        else
        {
            _ = Dispatcher.BeginInvoke(() => Shutdown(0));
        }
    }

    private void BindMainWindowServices()
    {
        if (_mainWindow is null || _servicesBound || _autostart is null || _orchestrator is null
            || _diagnostics is null || _languageEngine is null)
        {
            return;
        }

        _mainWindow.BindServices(
            _settings,
            _autostart,
            _orchestrator.Dictionary,
            _orchestrator.Ignore,
            _diagnostics,
            RestartEngineAsync);

        _mainWindow.SettingsChanged += OnSettingsChanged;
        _mainWindow.DictionaryChanged += () => _ = _monitor?.RefreshOnceAfterSuppressionAsync();
        _mainWindow.ExceptionsChanged += () => _ = _monitor?.RefreshOnceAfterSuppressionAsync();
        _servicesBound = true;
    }

    private void OnSettingsChanged(WriteLiteAppSettings settings)
    {
        _settings = settings;
        try
        {
            _settingsStore?.Save(settings);
        }
        catch
        {
            CompatibilityLogger.State("settings-save-failed");
        }

        ApplyRuntimeSettings(settings);
    }

    private void ApplyRuntimeSettings(WriteLiteAppSettings settings)
    {
        _orchestrator?.ApplySettings(settings);
        _hybrid?.ApplySettings(settings);
        // Rebuild the local provider when its endpoint/profile changes.
        RebuildAiProvider(settings);
        _mainWindow?.SetMinimizeToTray(settings.MinimizeToTrayOnClose);
        _bubble?.ApplyDisplaySettings(settings.ShowIndicator, settings.ShowGreenIndicatorWhenClean);
        IssueUnderlineTheme.MinimizeDuplication = settings.MinimizeUnderlineDuplication;
        _monitor?.SetShowUiOnlyWhileEditing(settings.ShowUiOnlyWhileEditing);
        _monitor?.SetIdleGrace(UiInteractionStateMachine.FromSecondsClamped(settings.EditingIdleGraceSeconds));
        var delayMs = ResolveAnalysisDelayMs(settings, _aiProvider);
        _monitor?.SetAnalysisDelay(TimeSpan.FromMilliseconds(delayMs));
        _mainWindow?.SetEngineStatus(ResolveEngineStatusText());

        if (_languageEngine is not null)
        {
            // Fire-and-forget is intentional: settings UI must not block on engine stop/start.
            _ = ApplyExtendedCheckingAsync(settings.ExtendedChecking);
        }

        if (_monitor is not null)
        {
            if (settings.CheckingEnabled && !_monitorPaused)
            {
                // Start is idempotent when already running.
                _monitor.Start();
                if (settings.LexicalCardEnabled)
                {
                    _doubleClickObserver?.Start();
                }
                else
                {
                    _doubleClickObserver?.Stop();
                    _lexicalPopup?.Hide();
                }
            }
            else if (!settings.CheckingEnabled)
            {
                _monitor.Stop();
                _doubleClickObserver?.Stop();
                _bubble?.Hide();
                _suggestions?.Hide();
                _lexicalPopup?.Hide();
            }
        }

        if (!settings.ShowIndicator)
        {
            _bubble?.Hide();
        }

        CompatibilityLogger.State("settings-applied");
    }

    private async Task ApplyExtendedCheckingAsync(bool enabled)
    {
        if (_languageEngine is null)
        {
            return;
        }

        try
        {
            await _languageEngine.SetExtendedEnabledAsync(enabled).ConfigureAwait(true);
            _ = Dispatcher.BeginInvoke(() => _mainWindow?.SetEngineStatus(_languageEngine.UserFacingStatus));
        }
        catch
        {
            CompatibilityLogger.State("language-engine-fallback-active");
        }
    }

    private async Task<bool> RestartEngineAsync()
    {
        if (_languageEngine is null)
        {
            return false;
        }

        var ok = await _languageEngine.TryRestartAsync().ConfigureAwait(true);
        _mainWindow?.SetEngineStatus(_languageEngine.UserFacingStatus);
        return ok;
    }

    private void OnAddToDictionary(TextIssue issue)
    {
        var word = issue.Original.Trim();
        if (string.IsNullOrEmpty(word))
        {
            return;
        }

        _orchestrator?.Dictionary.Add(word);
        _orchestrator?.Ignore.IgnoreWord(word);
        CompatibilityLogger.Technical("language-engine-ignore", "kind=dictionary-add");
        _ = _monitor?.RefreshOnceAfterSuppressionAsync();
        _mainWindow?.RefreshDictionary();
        _correctionPopup?.Hide();
    }

    private void OnIgnoreIssue(TextIssue issue)
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
            if (_settings.ShowIndicator)
            {
                _bubble?.ShowSnapshot(snap);
            }
        }

        _mainWindow?.RefreshExceptions();
        _correctionPopup?.Hide();
    }

    private void OpenMainWindow()
    {
        // OnStartup now awaits the dictionary parse, so an activation signal
        // (user relaunches the exe) can arrive before services exist. Remember
        // the request and honour it when startup finishes instead of binding a
        // window to null services.
        if (!_startupComplete)
        {
            _openMainWindowPending = true;
            CompatibilityLogger.State("main-window-open-deferred reason=startup-in-progress");
            return;
        }

        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow();
            _mainWindow.SetMonitorActive(!_monitorPaused);
            _mainWindow.SetMinimizeToTray(_settings.MinimizeToTrayOnClose);
            _servicesBound = false;
            BindMainWindowServices();
        }

        CompatibilityLogger.State("main-window-open");
        _mainWindow.ShowFromTray();
    }

    private void OnSnapshotChanged(object? sender, TextSnapshot? snapshot)
    {
        _latestSnapshot = snapshot;

        // Do not leave an old dictionary result on screen while the user has
        // moved to another editable field or changed the text that was looked
        // up.  This also cancels its background lookup before it can update UI.
        if (_lexicalPopup is { IsVisible: true }
            && (snapshot is null || !_lexicalPopup.IsBoundTo(
                snapshot.Target.Identity.RuntimeId,
                snapshot.GenerationId,
                snapshot.TextVersion)))
        {
            _lexicalLookupCts?.Cancel();
            _lexicalPopup.Hide();
            CompatibilityLogger.Technical("lexical-popup-invalidated", "reason=target-generation-or-text-changed");
        }

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
                _correctionPopup?.Hide();
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

        if (_settings.ShowIndicator)
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

    private async Task UpdateInlineOverlayAsync(TextSnapshot snapshot)
    {
        var overlay = _inlineOverlay;
        if (overlay is null || Volatile.Read(ref _shutdownRequested) != 0)
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

    private void OpenSuggestions(object? sender, EventArgs e)
    {
        if (_latestSnapshot is null || _suggestions is null || !_latestSnapshot.ShowMainUi) return;

        _bubble?.ClearUserHide();
        _monitor?.SetSuggestionsWindowOpen(true);
        _monitor?.SetPopupInteractionOpen(true);
        _suggestions.ShowSnapshot(_latestSnapshot);
    }

    private void OnInlineIssueClicked(object? sender, InlineIssueClickedEventArgs args)
    {
        var snapshot = _latestSnapshot;
        if (snapshot is null || !snapshot.Target.IsEditable || !snapshot.ShowMainUi || !snapshot.Issues.Contains(args.Issue)
            || !TextCorrectionService.IsRangeValid(snapshot.Text, args.Issue.Start, args.Issue.Length)
            || (args.Issue.Length > 0 && !string.Equals(snapshot.Text.Substring(args.Issue.Start, args.Issue.Length), args.Issue.Original, StringComparison.Ordinal)))
        {
            CompatibilityLogger.Technical("inline-stale-result-rejected", "reason=issue-not-current");
            return;
        }

        _bubble?.ClearUserHide();
        _monitor?.SetPopupInteractionOpen(true);
        CompatibilityLogger.Technical("correction-popup-open-requested", $"rule={args.Issue.RuleId} generation={snapshot.GenerationId} request={snapshot.RequestId}");
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    _correctionPopup?.ShowForIssue(args.Issue, args.Anchor);
                    CompatibilityLogger.Technical("correction-popup-opened", $"rule={args.Issue.RuleId}");
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

    private void OnWordDoubleClicked(object? sender, WordDoubleClickedEventArgs args)
    {
        var snapshot = _latestSnapshot;
        var lexicalCardAvailable = _settings.LexicalCardEnabled
                                   && _lexicalKnowledge is not null
                                   && _lexicalPopup is not null;
        // Double-click is reserved for the dictionary card. A single click on
        // an underline opens the correction card. Only fall back to correction
        // here when the lexical card is explicitly unavailable.
        if (!lexicalCardAvailable
            && snapshot is not null
            && snapshot.Target.IsEditable
            && snapshot.ShowMainUi
            && snapshot.Target.Identity.RuntimeId == args.TargetId
            && snapshot.GenerationId == args.GenerationId
            && snapshot.TextVersion == args.TextVersion)
        {
            var issue = CorrectionInteractionResolver.FindIssueAtRange(
                snapshot.Issues, args.Range.Start, args.Range.Length);
            if (issue is not null
                && TextCorrectionService.IsRangeValid(snapshot.Text, issue.Start, issue.Length)
                && (issue.Length == 0 || string.Equals(
                    snapshot.Text.Substring(issue.Start, issue.Length), issue.Original, StringComparison.Ordinal)))
            {
                _lexicalLookupCts?.Cancel();
                _lexicalPopup?.Hide();
                CompatibilityLogger.Technical("double-click-correction-priority",
                    $"rule={issue.RuleId} generation={snapshot.GenerationId}");
                OnInlineIssueClicked(this, new InlineIssueClickedEventArgs(issue, args.Anchor));
                return;
            }
        }

        if (!lexicalCardAvailable || _lexicalKnowledge is null || _lexicalPopup is null)
            return;

        _monitor?.SetPopupInteractionOpen(true);
        _lexicalPopup.ShowLoading(
            args.Range,
            args.Anchor,
            args.RequestId,
            args.TargetId,
            args.GenerationId,
            args.TextVersion);

        _lexicalLookupCts?.Cancel();
        _lexicalLookupCts?.Dispose();
        _lexicalLookupCts = new CancellationTokenSource();
        var token = _lexicalLookupCts.Token;
        var requestId = args.RequestId;

        _ = Task.Run(async () =>
        {
            try
            {
                var request = new LexicalLookupRequest(
                    args.Range.Word,
                    args.Range.Sentence,
                    args.FullText,
                    args.Range.Start,
                    args.Range.Length,
                    LexicalLanguage.Russian,
                    requestId,
                    args.GenerationId,
                    args.TextVersion,
                    args.TargetId);

                var result = await _lexicalKnowledge.LookupAsync(request, token).ConfigureAwait(true);
                if (token.IsCancellationRequested) return;
                if (_lexicalPopup.CurrentRequestId > requestId)
                {
                    CompatibilityLogger.Technical("lexical-stale-result-rejected", $"request={requestId}");
                    return;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    _lexicalPopup.ShowResult(
                        result,
                        args.Range,
                        args.FullText,
                        args.TargetId,
                        args.GenerationId,
                        args.TextVersion,
                        args.SupportsDirectWrite,
                        args.Anchor,
                        requestId);
                });
            }
            catch (OperationCanceledException)
            {
                // newer lookup owns the UI
            }
            catch (Exception ex)
            {
                CompatibilityLogger.AccessError("lexical-lookup", null, ex);
                await Dispatcher.InvokeAsync(() =>
                    _lexicalPopup.ShowError("Не удалось загрузить словарные данные.", requestId));
            }
        }, token);
    }

    private async void OnLexicalReplaceRequested(object? sender, LexicalReplaceRequestedEventArgs args)
    {
        if (_lexicalReplacement is null) return;

        var snapshot = _latestSnapshot;
        if (snapshot is null)
        {
            CompatibilityLogger.Technical("lexical-replace-rejected", "reason=no-snapshot");
            return;
        }

        // Prefer live target identity from the active snapshot.
        if (!LexicalRequestValidator.IsCurrent(
                snapshot.Target.Identity.RuntimeId,
                snapshot.GenerationId,
                snapshot.TextVersion,
                args.TargetId,
                args.GenerationId,
                args.TextVersion))
        {
            CompatibilityLogger.Technical("lexical-replace-rejected", "reason=stale-target-generation-or-text");
            return;
        }

        var read = await snapshot.Target.TryReadTextAsync();
        var liveText = read.Succeeded ? read.Text : snapshot.Text;
        var supportsWrite = snapshot.Target.SupportsDirectWrite;

        var replaceRequest = new LexicalReplacementRequest(
            args.TargetId,
            args.GenerationId,
            args.TextVersion,
            liveText,
            args.Start,
            args.Length,
            args.OriginalWord,
            args.Replacement,
            supportsWrite,
            IsPassword: false,
            IsReadOnly: !supportsWrite);

        var outcome = _lexicalReplacement.TryReplace(replaceRequest);
        if (!outcome.Success || outcome.NewText is null)
        {
            CompatibilityLogger.Technical("lexical-replace-rejected", $"reason={outcome.FailureReason}");
            if (outcome.OfferCopyOnly)
            {
                try { System.Windows.Clipboard.SetText(args.Replacement); } catch { /* ignore */ }
                MessageBox.Show(
                    "Не удалось безопасно заменить слово. Вариант скопирован в буфер обмена.",
                    "WriteLite",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return;
        }

        await ReplaceTargetTextAsync(snapshot, _ => (true, outcome.NewText, outcome.CaretIndex));
        _lexicalPopup?.Hide();
    }

    private async Task ApplyIssueAsync(TextIssue issue)
    {
        CompatibilityLogger.State("apply-issue-requested");
        var snapshot = _latestSnapshot;
        if (snapshot is null || _monitor is null || _correctionApplication is null)
        {
            NotifyCorrectionFailure("Нет активного текстового поля.");
            return;
        }

        var normalized = CorrectionPresentation.NormalizeForApply(issue);
        var outcome = await _correctionApplication.ApplyAsync(
            normalized, snapshot, _monitor, _correctionGate, _applicationLifetimeCts.Token);

        await Dispatcher.InvokeAsync(() => HandleCorrectionOutcome(outcome, normalized.Replacement));
    }

    private async Task ApplyAllAsync()
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
        await Dispatcher.InvokeAsync(() => HandleCorrectionOutcome(outcome, null));
    }

    private void HandleCorrectionOutcome(CorrectionApplicationOutcome outcome, string? replacementForCopy)
    {
        if (outcome.Succeeded)
        {
            _correctionPopup?.Hide();
            if (_settings.HidePanelAfterSuccessfulApply)
            {
                _suggestions?.Hide();
            }

            CompatibilityLogger.Technical("correction-ui-success", $"path={outcome.WritePath ?? "n/a"}");
            return;
        }

        _correctionPopup?.ShowApplyFeedback(outcome.UserMessage, isError: true);
        if (outcome.OfferCopy && !string.IsNullOrEmpty(replacementForCopy))
        {
            try { System.Windows.Clipboard.SetText(replacementForCopy); }
            catch { /* ignore */ }
            MessageBox.Show(
                outcome.UserMessage + (outcome.OfferCopy ? "\n\nВариант скопирован в буфер обмена." : string.Empty),
                "WriteLite",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!string.IsNullOrWhiteSpace(outcome.UserMessage))
        {
            MessageBox.Show(
                outcome.UserMessage,
                "WriteLite",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void NotifyCorrectionFailure(string message)
    {
        CompatibilityLogger.Technical("correction-apply-failed", "reason=no-context");
        _correctionPopup?.ShowApplyFeedback(message, isError: true);
        MessageBox.Show(message, "WriteLite", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task ReplaceTargetTextAsync(
        TextSnapshot snapshot,
        Func<string, (bool CanApply, string NewText, int Caret)> compose)
    {
        // Lexical replace path still uses full-text compose.
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

            var composed = compose(read.Text);
            if (!composed.CanApply)
            {
                CompatibilityLogger.State("correction-failed");
                NotifyCorrectionFailure("Текст изменился. Обновите предложение.");
                return;
            }

            var result = await snapshot.Target.TryReplaceAllAsync(composed.NewText, composed.Caret);
            if (!result.Succeeded)
            {
                CompatibilityLogger.State("correction-failed");
                MessageBox.Show(
                    result.Error,
                    "WriteLite не смог применить правку",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            CompatibilityLogger.State("correction-completed");
            _correctionPopup?.Hide();
            _lexicalPopup?.Hide();
            if (_settings.HidePanelAfterSuccessfulApply)
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

    private string ResolveEngineStatusText()
    {
        var local = _languageEngine?.UserFacingStatus ?? "Используется базовая проверка WriteLite";

        // Technical backend: Qwen / LanguageTool / Lite fallback
        var backend = ResolveActiveBackendLabel();
        if (!string.IsNullOrEmpty(backend))
        {
            local += " · Backend: " + backend;
        }

        return local;
    }

    private string ResolveActiveBackendLabel()
    {
        if (_settings.LocalAiEnabled && _localAiProvider is { IsConfigured: true })
        {
            var last = _localAiProvider.LastBackend;
            if (string.Equals(last, "writelight-qwen", StringComparison.OrdinalIgnoreCase)
                || (_localAiProvider.QwenAvailable
                    && _settings.PreferQwen
                    && !_settings.LocalAiProfile.Equals("Lite", StringComparison.OrdinalIgnoreCase)))
            {
                var qwenTag = _localAiProvider.QwenAvailable ? "Qwen" : "Qwen (offline)";
                if (string.Equals(last, "writelight-qwen", StringComparison.OrdinalIgnoreCase))
                {
                    return qwenTag;
                }

                if (string.Equals(last, "lite-fallback", StringComparison.OrdinalIgnoreCase))
                {
                    return "Lite fallback";
                }

                if (string.Equals(last, "lite", StringComparison.OrdinalIgnoreCase))
                {
                    return _localAiProvider.QwenAvailable ? "Lite (Qwen idle)" : "Lite";
                }
            }

            if (string.Equals(last, "lite-fallback", StringComparison.OrdinalIgnoreCase))
            {
                return "Lite fallback";
            }

            if (string.Equals(last, "writelight-qwen", StringComparison.OrdinalIgnoreCase))
            {
                return "Qwen";
            }
        }

        if (_settings.ExtendedChecking
            && _languageEngine is not null
            && !string.IsNullOrWhiteSpace(_languageEngine.UserFacingStatus)
            && !_languageEngine.UserFacingStatus.Contains("базовая", StringComparison.OrdinalIgnoreCase))
        {
            return "LanguageTool";
        }

        return "Lite fallback";
    }

    private static int ResolveAnalysisDelayMs(WriteLiteAppSettings settings, IAiTextProvider? provider)
    {
        // Local Lite is fast — keep monitor debounce closer to normal typing delay.
        if (settings.LocalAiEnabled && (provider?.IsConfigured ?? false))
        {
            return Math.Max(settings.AnalysisDelayMs, settings.AiDebounceMs);
        }

        return settings.AnalysisDelayMs;
    }

    private static LocalAiTextProvider CreateLocalAiProvider(WriteLiteAppSettings settings)
    {
        var profile = ParseLocalProfile(settings.LocalAiProfile);
        var modelDir = Path.Combine(AppContext.BaseDirectory, "models", "writelight-qwen");
        var endpoint = string.IsNullOrWhiteSpace(settings.QwenEndpoint)
            ? "http://127.0.0.1:8742"
            : settings.QwenEndpoint.Trim().TrimEnd('/');

        CompatibilityLogger.Technical(
            "local-ai-provider-create",
            $"enabled={(settings.LocalAiEnabled ? 1 : 0)} profile={profile} preferQwen={(settings.PreferQwen ? 1 : 0)} " +
            $"endpoint={endpoint} modelDirExists={(Directory.Exists(modelDir) ? 1 : 0)}");

        return new LocalAiTextProvider(new LocalAiOptions
        {
            Enabled = settings.LocalAiEnabled,
            Profile = profile,
            ModelDirectory = modelDir,
            QwenEndpoint = endpoint,
            PreferQwen = settings.PreferQwen,
            TimeoutSeconds = 90,
            MaxInputChars = settings.AiMaxTextLength,
            // Allow AI on typical sentences; hybrid still has its own min length gate.
            MinInputChars = 1
        });
    }

    private static WriteLite.AI.Contracts.AiModelProfile ParseLocalProfile(string? value)
        => (value ?? "Auto").Trim().ToLowerInvariant() switch
        {
            "lite" => WriteLite.AI.Contracts.AiModelProfile.Lite,
            "standard" => WriteLite.AI.Contracts.AiModelProfile.Standard,
            "quality" => WriteLite.AI.Contracts.AiModelProfile.Quality,
            _ => WriteLite.AI.Contracts.AiModelProfile.Auto
        };

    private void RebuildAiProvider(WriteLiteAppSettings settings)
    {
        try
        {
            if (_aiProvider is IDisposable d)
            {
                d.Dispose();
            }
            else
            {
                _localAiProvider?.Dispose();
            }
        }
        catch
        {
            // ignore
        }

        _localAiProvider = CreateLocalAiProvider(settings);
        _aiProvider = _localAiProvider;
        _hybrid?.SetAiService(new AiTextAnalysisService(_aiProvider));
        _hybrid?.ApplySettings(settings);
        if (settings.LocalAiEnabled)
        {
            _ = WarmupLocalAiAndRefreshStatusAsync();
        }
    }

    private async Task WarmupLocalAiAndRefreshStatusAsync()
    {
        try
        {
            if (_localAiProvider is null)
            {
                return;
            }

            // Task.Run is load-bearing, not defensive. An async method runs
            // synchronously until its first real await, and this chain reaches
            // QwenModelBackend.RefreshAvailability — a fully synchronous method
            // that blocks on an HTTP health probe to 127.0.0.1:8742 — before any
            // await. Called directly from OnStartup, that executed the whole
            // probe sequence on the dispatcher: a dump taken inside the hang
            // shows OnStartup → WarmupAsync → ProbeHealth → GetResult parking
            // the UI thread ~10 s at every launch while llama-server was not up
            // yet. The worker thread eats that wait instead.
            await Task.Run(() => _localAiProvider.WarmupAsync()).ConfigureAwait(true);
            CompatibilityLogger.Technical(
                "local-ai-warmup-done",
                $"qwenAvailable={(_localAiProvider.QwenAvailable ? 1 : 0)} endpoint={_localAiProvider.QwenEndpoint}");
        }
        catch (Exception ex)
        {
            CompatibilityLogger.Technical("local-ai-warmup-failed", $"type={ex.GetType().Name}");
        }

        try
        {
            _ = Dispatcher.BeginInvoke(() => _mainWindow?.SetEngineStatus(ResolveEngineStatusText()));
        }
        catch
        {
            // ignore
        }
    }

    private async Task WarmupLexicalKnowledgeAsync(OfflineLexicalKnowledgeService service)
    {
        try
        {
            // The open dictionary contains tens of thousands of lemmas. Parsing it
            // off the dispatcher keeps tray startup and the first keystroke smooth.
            await Task.Run(
                () => service.LoadFromDirectory(),
                _applicationLifetimeCts.Token).ConfigureAwait(true);
            CompatibilityLogger.Technical(
                "lexical-warmup-done",
                $"loaded={(service.IsPackLoaded ? 1 : 0)} version={service.PackVersion ?? "none"}");
        }
        catch (OperationCanceledException)
        {
            // Normal application shutdown.
        }
        catch (Exception ex)
        {
            CompatibilityLogger.Technical("lexical-warmup-failed", $"type={ex.GetType().Name}");
        }
    }

    private void OnLanguageEngineStatusChanged(object? sender, EventArgs e)
    {
        // Engine shutdown and the WPF dispatcher can race.  Status updates are
        // optional UI work and must never turn a successful shutdown into a
        // dispatcher exception.
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            _ = Dispatcher.BeginInvoke(() => _mainWindow?.SetEngineStatus(ResolveEngineStatusText()));
        }
        catch (Exception exception)
        {
            CompatibilityLogger.AccessError("engine-status-dispatch", null, exception);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CompatibilityLogger.State("application-stopping");

        try
        {
            _uiWatchdog?.Stop();
            _uiWatchdog?.Dispose();
            _uiWatchdog = null;
        }
        catch
        {
            // best effort
        }

        try
        {
            _singleInstance?.Dispose();
            _singleInstance = null;
        }
        catch
        {
            // best effort
        }

        try
        {
            _applicationLifetimeCts.Cancel();
            _hybrid?.CancelPending();
        }
        catch
        {
            // best effort during application teardown
        }

        // Cancel all optional UI work before the UI objects are torn down.  In
        // particular, a delayed lexical lookup must not be able to marshal a
        // result back to a dispatcher that is already shutting down.
        try
        {
            _lexicalLookupCts?.Cancel();
            _lexicalLookupCts?.Dispose();
            _lexicalLookupCts = null;
        }
        catch
        {
            // best effort during application teardown
        }

        try
        {
            _doubleClickObserver?.Dispose();
            _doubleClickObserver = null;
        }
        catch
        {
            // best effort during application teardown
        }

        try
        {
            _inlineOverlay?.Dispose();
            _inlineOverlay = null;
        }
        catch
        {
            // best effort during application teardown
        }

        _monitor?.BeginShutdown();
        _monitor?.Dispose();
        try
        {
            if (_languageEngine is not null)
            {
                _languageEngine.StatusChanged -= OnLanguageEngineStatusChanged;
            }

            _languageEngine?.Dispose();
        }
        catch
        {
            // best effort
        }

        try
        {
            if (_aiProvider is IDisposable d)
            {
                d.Dispose();
            }
            else
            {
                _localAiProvider?.Dispose();
            }
        }
        catch
        {
            // ignore
        }

        _languageEngine = null;
        _orchestrator = null;
        _hybrid = null;
        _aiProvider = null;
        _localAiProvider = null;
        _tray?.Dispose();
        _bubble?.Close();
        _suggestions?.Close();
        _correctionPopup?.Close();
        _lexicalPopup?.Close();
        _mainWindow?.ForceClose();
        _mainWindow = null;

        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        _applicationLifetimeCts.Dispose();

        CompatibilityLogger.State("application-stopped");
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CompatibilityLogger.AccessError("dispatcher-unhandled", null, e.Exception);
        var disposition = ApplicationExceptionPolicy.Classify(
            e.Exception,
            Volatile.Read(ref _shutdownRequested) != 0);
        CompatibilityLogger.Technical("dispatcher-failure-policy", $"disposition={disposition}");
        // Only known transient automation/cancellation failures may continue. Unknown
        // dispatcher failures can leave WPF state corrupted and must follow the normal
        // fatal path instead of being silently swallowed.
        e.Handled = disposition == ApplicationFailureDisposition.Recoverable;
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CompatibilityLogger.AccessError("task-unobserved", null, e.Exception);
        e.SetObserved();
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            CompatibilityLogger.AccessError("appdomain-unhandled", null, exception);
        }
        else
        {
            CompatibilityLogger.State("appdomain-unhandled");
        }
    }
}
