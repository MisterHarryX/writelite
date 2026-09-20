using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Automation;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Rules;
using WriteLite.Services.Ai;
using WriteLite.Services.Audio;
using WriteLite.Services.Diagnostics;
using WriteLite.Services.Grammar;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Lexical;
using WriteLite.Services.Notes;
using WriteLite.Services.Reading;
using WriteLite.Language.Core;
using WriteLite.Language.Packs;
using WriteLite.Services.Settings;
using WriteLite.Services.Spelling;
using WriteLite.Views;
using MessageBox = System.Windows.MessageBox;

namespace WriteLite;

public partial class App : System.Windows.Application
{

    private ShortcutRegistry _shortcuts = new();

    private GlobalHotkeyService? _hotkeys;

    private MainWindow? _mainWindow;

    private TrayIconService? _tray;

    private AmbiencePlayer? _ambience;

    private NotesService? _notes;

    private ReadingLibraryService? _readingLibrary;

    private WriteLiteAppSettings _settings = new();

    private WriteLiteAutostartService? _autostart;

    private WriteLiteDiagnosticsService? _diagnostics;

    private readonly CancellationTokenSource _applicationLifetimeCts = new();

    private UiResponsivenessWatchdog? _uiWatchdog;

    private bool _startupComplete;

    private bool _openMainWindowPending;

    private SingleInstanceService? _singleInstance;

    private bool _monitorPaused;

    private TextFieldMonitor? _monitor;

    private readonly UiaCircuitBreaker _uiaCircuitBreaker = new();

    private CorrectionUiCoordinator? _corrections;

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
        var correctionApplication = new CorrectionApplicationService(_uiaCircuitBreaker);
        _uiWatchdog = new UiResponsivenessWatchdog(Dispatcher);
        _uiWatchdog.SetContextProvider(() =>
        {
            var popup = (_corrections is { IsCorrectionPopupVisible: true } ? 1 : 0) + (_lexicalPopup?.IsVisible == true ? 1 : 0) + (_suggestions?.IsVisible == true ? 1 : 0);
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
        // Asked before Load, which cannot distinguish "no file" from "file full of defaults".
        var hadPersistedSettings = _settingsStore.HasPersistedSettings;
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

        // Built from the persisted overrides, so the editor, the shell and Windows itself
        // are all told about the same set of bindings from one place.
        _shortcuts = new ShortcutRegistry(_settings.Shortcuts);
        _hotkeys = new GlobalHotkeyService(Dispatcher);
        _hotkeys.Handle(ShortcutRegistry.OpenWriteLite, OpenMainWindow);
        _hotkeys.Apply(_shortcuts);

        _autostart = new WriteLiteAutostartService();

        // Keep setting and registry in sync. On a first launch the installer's Run value
        // is the only expressed intent and is adopted; afterwards the stored preference
        // wins. See AutostartReconciler for why the direction is not fixed.
        try
        {
            if (AutostartReconciler.Reconcile(_settings, _autostart, hadPersistedSettings))
            {
                _settingsStore.Save(_settings);
                CompatibilityLogger.Technical(
                    "autostart-adopted-from-registry",
                    $"startWithWindows={(_settings.StartWithWindows ? 1 : 0)}");
            }
        }
        catch (Exception exception)
        {
            // Autostart is a convenience; a registry or disk refusal must not stop startup.
            CompatibilityLogger.Technical("autostart-sync-failed", $"type={exception.GetType().Name}");
        }

        // Hunspell parse (ru_RU.dic is 3.5 MB) belongs on a worker. Inline it
        // held the dispatcher ~3.7 s on this machine (watchdog-measured); the
        // single-instance guard, watchdog and activation server are already
        // wired above, so the app stays responsive during the await and the
        // rest of startup simply completes a few seconds later.
        // The rule pack is independent of the dictionaries and is the same kind of work —
        // read files, build immutable structures — so it runs alongside them instead of
        // afterwards. Building it cost 462 ms cold on the dispatcher (tools/startlat), and
        // it was built twice; it is now cached inside RuleCatalog and started here, where
        // the wait is already being paid for the far slower Hunspell parse.
        var ruleCatalogTask = Task.Run(() =>
        {
            var catalog = RuleCatalog.LoadDefault();

            // The patterns are compiled but emit their code on first match, which put 176 ms
            // on the first sentence anyone typed. Paid here instead, where the wait is
            // already happening. See RuleCatalog.Warm.
            catalog.Warm();
            return catalog;
        });
        var spellChecker = await Task.Run(() => new LocalSpellChecker());
        var ruleCatalog = await ruleCatalogTask;
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
        var lexicalSignals = LexicalSignalService.TryCreate(spellChecker, dictionary);
        _languageEngine = new WriteLiteLanguageEngine(engineOptions);
        _orchestrator = new WriteLiteOrchestratingAnalyzer(
            new RuleBasedAnalyzer(ruleCatalog, spellChecker.RussianFormIndex),
            new SpellTextAnalyzer(spellChecker),
            _languageEngine,
            dictionary,
            ignore,
            isKnownWord: word => spellChecker.CheckWord(word, SpellingLanguage.Russian).IsKnown,
            lexicalSignals: lexicalSignals,
            // Loaded on first orchestrated analysis, not now: 400 ms of ONNX session
            // construction has no business on the path to the first window, and nothing
            // consults this layer until at least one debounce interval after typing starts.
            punctuationModelFactory: () => PunctuationModelAnalyzer.TryLoad());
        _orchestrator.ApplySettings(_settings);

        // Hybrid: rules+spell+LanguageTool + local offline AI (Lite/Qwen).
        _localAiProvider = CreateLocalAiProvider(_settings);
        _aiProvider = _localAiProvider;
        var aiService = new AiTextAnalysisService(_aiProvider);

        // The measured routing policy, not the null one. With no policy the hybrid service
        // consults the model on every sentence and merges everything it returns as a peer of
        // dictionary-backed findings — the "unrestricted Qwen" configuration the Phase 3
        // benchmark measured at precision 0.945 → 0.713 with 5.3× the false positives and no
        // gain in corrections made. The policy is also what applies SemanticEditGuard and the
        // register protections to model output; without it they were never on this path.
        _routingPolicy = new LocalAiRoutingPolicy(
            lexicalSignals: lexicalSignals,
            // §29: the same form index the agreement rules use, reading the sentence a
            // model correction would leave behind before it is ever shown.
            morphologyGuard: MorphologicalAcceptanceGuard.TryCreate(spellChecker.RussianFormIndex));
        _hybrid = new HybridTextAnalysisService(_orchestrator, aiService, _routingPolicy);
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
            () => _corrections?.LatestSnapshot,
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
        // The contextual reranker is deliberately off on this path: it is a neural model,
        // this analyzer runs against every keystroke burst, and its findings arrive a
        // moment later on the orchestrated path anyway. Keeping it here would put an ONNX
        // session on the typing hot path to publish the same result twice.
        var fastAnalyzer = new CompositeTextAnalyzer(
            new RuleBasedAnalyzer(ruleCatalog, spellChecker.RussianFormIndex),
            new SpellTextAnalyzer(spellChecker) { ContextualRefinementEnabled = false });
        _fastAnalyzer = fastAnalyzer;
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
        // Opening the translation index is a file handle, not a parse, so it can stay
        // inline; absence is normal and simply removes the dictionary's English column.
        _translations = TranslationIndex.TryOpen();
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

        _corrections = new CorrectionUiCoordinator(
            Dispatcher,
            correctionApplication,
            () => _settings,
            _monitor,
            () => _lexicalLookupCts,
            _bubble,
            _suggestions,
            _inlineOverlay,
            _lexicalPopup,
            _orchestrator,
            _writingCoordinator,
            _mainWindow,
            _applicationLifetimeCts,
            () => Volatile.Read(ref _shutdownRequested) != 0);

        _monitor.SnapshotChanged += (_, snapshot) => _corrections?.HandleSnapshotChanged(snapshot);
        _bubble.OpenRequested += OpenSuggestions;
        _bubble.ApplyAllRequested += async (_, _) =>
        {
            if (_corrections is { } corrections) await corrections.ApplyAllAsync();
        };
        _bubble.OpenMainWindowRequested += (_, _) => OpenMainWindow();
        _bubble.HideIndicatorRequested += (_, _) => CompatibilityLogger.State("indicator-hidden-by-user");
        _suggestions.ApplyIssueRequested += issue => _corrections?.ApplyIssueAsync(issue) ?? Task.CompletedTask;
        _suggestions.ApplyAllRequested += () => _corrections?.ApplyAllAsync() ?? Task.CompletedTask;
        _suggestions.PanelHidden += (_, _) =>
        {
            _monitor?.SetSuggestionsWindowOpen(false);
            _monitor?.SetPopupInteractionOpen(false);
        };
        _suggestions.OpenMainWindowRequested += (_, _) => OpenMainWindow();
        _suggestions.AddToDictionaryRequested += issue => _corrections?.AddToDictionary(issue);
        _suggestions.IgnoreIssueRequested += issue => _corrections?.IgnoreIssue(issue);
        _lexicalPopup.ReplaceRequested += OnLexicalReplaceRequested;
        _lexicalPopup.FullArticleRequested += OnLexicalFullArticleRequested;
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
                catch { return null; } // UIA throws when busy or the element is torn down; a miss is fine
            },
            () => _monitor?.GetTargetState() ?? (null, 0, 0, null));
        _doubleClickObserver.WordDoubleClicked += OnWordDoubleClicked;
        _doubleClickObserver.PrimaryButtonPressed += OnPrimaryButtonPressed;

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
                    await Task.Delay(WriteLiteDefaults.Application.LifecycleSmokeShutdownDelay, _applicationLifetimeCts.Token);
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
                _translations?.Dispose();
                _ambience?.Dispose();
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

        // Word lookup on the dictionary page reuses the already-warmed offline pack.
        if (_lexicalKnowledge is not null)
        {
            _mainWindow.BindLexical(_lexicalKnowledge, _translations);
        }

        // The editor's AI actions and word panel run on exactly the services that
        // already exist in this process: the loopback model the corrections pipeline
        // uses, and the lexical packs the dictionary page reads. Nothing new is
        // started, and nothing here can reach the network.
        var editorAi = _localAiProvider is null ? null : new TextRewriteService(_localAiProvider.Backend);

        // The editor checks with the same stack the field monitor uses: rules and spelling
        // over the shared form index, the language engine, the user dictionary and ignore
        // list, the punctuation model, and WriteAI under its routing policy. It used to
        // check with a private rules+spelling analyzer instead, which is why «Согласно
        // нового плана» could reach the sidebar unremarked — the rule that catches it needs
        // the form index the page never had, and the model was never asked at all.
        // Writing assistance runs on the same loopback model as the corrections pipeline and
        // the Smart Actions — one runtime in the process, asked a different question. Absent a
        // model the editor simply never shows a continuation.
        _writingAssistance = new WritingAssistanceService(_localAiProvider?.Backend);
        _writingCoordinator = new WritingAssistanceCoordinator(_writingAssistance);

        _mainWindow.BindEditorServices(
            _hybrid,
            editorAi,
            _lexicalKnowledge is null ? null : new LexicalLookupService(_lexicalKnowledge, _translations),
            _writingAssistance,
            // §11: the editor gets the same two lanes the field monitor has had. Without this
            // it bound the hybrid alone, whose task completes only when the model does — 53 s
            // on a 480-word document, for findings the deterministic layers had in 1.2 s.
            _fastAnalyzer);

        // Notes and reading projects. Both keep their own file under the WriteLite
        // application-data folder and both autosave, so they are created once here and
        // handed to the pages rather than constructed per view — two services writing
        // the same file is how a board loses a card.
        //
        // Card drafting reuses the editor's rewrite service. There is one local model in
        // WriteLite and the reader asks it a different question; it does not start a
        // second runtime, and it stays optional when no model is bound.
        _notes = new NotesService();
        _readingLibrary = new ReadingLibraryService();
        _mainWindow.BindWorkspaces(_notes, _readingLibrary, new StudyCardDraftService(editorAi));

        _mainWindow.SettingsChanged += OnSettingsChanged;
        _mainWindow.ShortcutsChanged += OnShortcutsChanged;
        _mainWindow.DictionaryChanged += () => _ = _monitor?.RefreshOnceAfterSuppressionAsync();
        _mainWindow.ExceptionsChanged += () => _ = _monitor?.RefreshOnceAfterSuppressionAsync();

        // The ambience player. Volume and whether it was playing are persisted, but
        // through the plain save path only — nothing about audio touches the analyzer
        // or the monitor, so it deliberately does not go through ApplyRuntimeSettings.
        _ambience = new AmbiencePlayer();
        _mainWindow.BindAmbience(_ambience, _settings);
        _mainWindow.AmbienceStateChanged += SaveSettingsQuietly;

        _servicesBound = true;
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
            _mainWindow.BindShortcuts(_shortcuts);
            _mainWindow.SetMonitorActive(!_monitorPaused);
            _mainWindow.SetMinimizeToTray(_settings.MinimizeToTrayOnClose);
            _servicesBound = false;
            BindMainWindowServices();
        }

        CompatibilityLogger.State("main-window-open");
        _mainWindow.ShowFromTray();
    }


    protected override void OnExit(ExitEventArgs e)
    {
        CompatibilityLogger.State("application-stopping");

        // First, before anything else is torn down: both workspaces coalesce their
        // writes, so an edit made in the last half-second is still only in memory.
        // Flushing here is what makes "it was there when I closed it" true.
        try
        {
            _notes?.Dispose();
            _notes = null;
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical("notes-flush-failed", $"type={exception.GetType().Name}");
        }

        try
        {
            _readingLibrary?.Dispose();
            _readingLibrary = null;
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical("reading-flush-failed", $"type={exception.GetType().Name}");
        }

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

        // Silence the ambience before anything slower: leaving audio playing through
        // a multi-second teardown is the one part of shutdown a user can hear.
        try
        {
            _ambience?.Dispose();
        }
        catch
        {
            // best effort
        }

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
        _corrections?.ClosePopup();
        _hotkeys?.Dispose();
        _hotkeys = null;
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
        var disposition = ApplicationExceptionPolicy.Classify(
            e.Exception,
            Volatile.Read(ref _shutdownRequested) != 0);

        // Logged with the page it happened on and the stack that produced it: an
        // unhandled exception is a defect in WriteLite, and a line that only names the
        // exception type cannot be acted on. The disposition is part of the same record
        // so the log says both what happened and what was decided about it.
        CompatibilityLogger.Unhandled(
            "dispatcher",
            e.Exception,
            $"page={CurrentPageName()} disposition={disposition}");

        // Only known transient automation/cancellation failures may continue. Unknown
        // dispatcher failures can leave WPF state corrupted and must follow the normal
        // fatal path instead of being silently swallowed.
        e.Handled = disposition == ApplicationFailureDisposition.Recoverable;
    }


    /// <summary>Which section was on screen, for the crash record. Never throws.</summary>
    private string CurrentPageName()
    {
        try
        {
            return _mainWindow?.CurrentSection ?? "none";
        }
        catch (Exception)
        {
            // Best effort for the crash record; never take the process down over it.
            return "unknown";
        }
    }


    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Every inner failure, not just the first: an unobserved AggregateException from
        // a Task.WhenAll can carry several, and the one that matters is rarely first.
        foreach (var inner in e.Exception.Flatten().InnerExceptions)
        {
            CompatibilityLogger.Unhandled("background-task", inner);
        }

        e.SetObserved();
    }


    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            CompatibilityLogger.Unhandled(
                "appdomain",
                exception,
                $"terminating={(e.IsTerminating ? 1 : 0)}");
        }
        else
        {
            CompatibilityLogger.State("appdomain-unhandled-nonexception");
        }
    }

}
