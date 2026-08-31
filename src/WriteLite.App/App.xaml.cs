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
    private TextFieldMonitor? _monitor;
    private BubbleWindow? _bubble;
    private SuggestionsWindow? _suggestions;
    private CorrectionPopupWindow? _correctionPopup;

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
    private readonly CorrectionCardCoordinator _cardCoordinator = new();

    /// <summary>Where the open card is anchored, so a refreshed result lands on the same word.</summary>
    private Rect _pinnedPopupAnchor;
    private LexicalPopupWindow? _lexicalPopup;
    private ShortcutRegistry _shortcuts = new();
    private GlobalHotkeyService? _hotkeys;
    private InlineErrorOverlayController? _inlineOverlay;
    private MainWindow? _mainWindow;
    private TrayIconService? _tray;
    private TextSnapshot? _latestSnapshot;
    private WriteLiteOrchestratingAnalyzer? _orchestrator;
    private HybridTextAnalysisService? _hybrid;

    /// <summary>
    /// The native deterministic lane, shared by the field monitor and the editor.
    /// </summary>
    /// <remarks>
    /// One instance for both surfaces: it holds a rule catalogue and a reference to the form
    /// index, and a second copy would parse the catalogue again to answer the same questions.
    /// </remarks>
    private CompositeTextAnalyzer? _fastAnalyzer;
    private WritingAssistanceService? _writingAssistance;
    private WritingAssistanceCoordinator? _writingCoordinator;
    private LocalAiRoutingPolicy? _routingPolicy;
    private WriteLiteLanguageEngine? _languageEngine;
    private IAiTextProvider? _aiProvider;
    private LocalAiTextProvider? _localAiProvider;
    private OfflineLexicalKnowledgeService? _lexicalKnowledge;
    private TranslationIndex? _translations;
    private AmbiencePlayer? _ambience;
    private NotesService? _notes;
    private ReadingLibraryService? _readingLibrary;
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

        // Built from the persisted overrides, so the editor, the shell and Windows itself
        // are all told about the same set of bindings from one place.
        _shortcuts = new ShortcutRegistry(_settings.Shortcuts);
        _hotkeys = new GlobalHotkeyService(Dispatcher);
        _hotkeys.Handle(ShortcutRegistry.OpenWriteLite, OpenMainWindow);
        _hotkeys.Apply(_shortcuts);

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
        _correctionPopup.CopyRequested += (_, issue) => CopyReplacementToClipboard(issue.Replacement);
        _correctionPopup.IsVisibleChanged += (_, _) =>
        {
            if (_correctionPopup is { IsVisible: false })
            {
                _cardCoordinator.Close();
                _monitor?.SetPopupInteractionOpen(false);
            }
        };
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
                catch { return null; }
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

    /// <summary>
    /// Takes a shortcut change to Windows, so a rebound global shortcut works immediately.
    /// </summary>
    /// <remarks>
    /// The registry object is the same instance the editor and the shell already hold, so
    /// application shortcuts need nothing here — they are read from it on every keypress.
    /// Only the system-wide ones are held by Windows and have to be told.
    /// </remarks>
    private void OnShortcutsChanged(ShortcutRegistry registry)
    {
        _shortcuts = registry;
        _hotkeys?.Apply(registry);
    }

    private void OnSettingsChanged(WriteLiteAppSettings settings)
    {
        _settings = settings;
        SaveSettingsQuietly(settings);
        ApplyRuntimeSettings(settings);
    }

    /// <summary>Persists settings without re-applying runtime behaviour.</summary>
    private void SaveSettingsQuietly(WriteLiteAppSettings settings)
    {
        try
        {
            _settingsStore?.Save(settings);
        }
        catch
        {
            CompatibilityLogger.State("settings-save-failed");
        }
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
        // §18: never a diff fragment, never punctuation, never part of a word.
        var word = DictionaryWordPolicy.ResolveWord(issue);
        if (word is null)
        {
            CompatibilityLogger.Technical("dictionary-add-rejected", $"rule={issue.RuleId} length={issue.Length}");
            _correctionPopup?.ShowApplyFeedback(
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
        _correctionPopup?.Dismiss();
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
        _correctionPopup?.Dismiss();
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

    private void OnSnapshotChanged(object? sender, TextSnapshot? snapshot)
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
            _lexicalLookupCts?.Cancel();
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
                _correctionPopup?.Dismiss();
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
        if (_correctionPopup is not { IsVisible: true } popup) return;

        // A write in flight owns the card until its outcome lands — including the text change
        // the write itself is about to cause.
        if (popup.Phase == CorrectionCardPhase.Applying) return;

        var action = _cardCoordinator.Observe(snapshot);
        switch (action.Kind)
        {
            case CorrectionCardActionKind.None:
                return;

            case CorrectionCardActionKind.Dismiss:
                CompatibilityLogger.Technical("correction-card-invalidated", "reason=not-rebindable-or-target-changed");
                popup.Dismiss();
                return;

            case CorrectionCardActionKind.Recheck:
                popup.BeginRecheck();
                return;

            case CorrectionCardActionKind.Render when action.Issue is { } rebound:
                // The token is taken from the same call that voided the old result, so the
                // new one cannot be overtaken by anything that started before it.
                var token = popup.BeginRecheck();
                CompatibilityLogger.Technical(
                    "correction-card-refreshed",
                    $"rule={rebound.RuleId} generation={snapshot?.GenerationId} token={token}");
                popup.ShowForIssue(rebound, _pinnedPopupAnchor, token);
                return;
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
        // §10: the card outlives the monitor's idea of "the current field". Whatever happens
        // to focus between now and the click on «Исправить», the correction belongs to this
        // target, this text and this range — so the apply path is given them rather than
        // whatever the monitor happens to be looking at by then.
        _cardCoordinator.Open(snapshot, args.Issue);
        _pinnedPopupAnchor = args.Anchor;
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

    /// <summary>
    /// Closes the dictionary card when the user clicks anywhere that is not the card.
    /// </summary>
    /// <remarks>
    /// The dismissal the product promises — "click somewhere else and it goes away" — stated
    /// as the thing it actually is, rather than derived from field-monitor state. Both clicks
    /// of a double-click arrive here: the first closes whatever card is open, and the second
    /// is what opens the next one, so double-clicking a second word replaces the card instead
    /// of leaving the old one over the new word.
    /// </remarks>
    private void OnPrimaryButtonPressed(object? sender, System.Windows.Point physicalScreenPoint)
    {
        if (_lexicalPopup is not { IsVisible: true } popup) return;
        if (popup.ContainsPhysicalPoint(physicalScreenPoint)) return;

        _lexicalLookupCts?.Cancel();
        popup.Hide();
        CompatibilityLogger.Technical("lexical-popup-dismissed", "reason=click-outside");
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
            args.TextVersion,
            args.FullText);

        _lexicalLookupCts?.Cancel();
        _lexicalLookupCts?.Dispose();
        _lexicalLookupCts = new CancellationTokenSource();
        var token = _lexicalLookupCts.Token;
        var requestId = args.RequestId;

        _ = Task.Run(async () =>
        {
            try
            {
                // The word's own script decides which pack answers. Russian was passed here
                // unconditionally, which the service happened to survive because it re-detects
                // the language itself — but it meant the request said one thing and the answer
                // another, and anything downstream that trusted the request was wrong about
                // every English word.
                var request = new LexicalLookupRequest(
                    args.Range.Word,
                    args.Range.Sentence,
                    args.FullText,
                    args.Range.Start,
                    args.Range.Length,
                    args.Range.Language,
                    requestId,
                    args.GenerationId,
                    args.TextVersion,
                    args.TargetId);

                var result = await _lexicalKnowledge.LookupAsync(request, token).ConfigureAwait(true);
                if (token.IsCancellationRequested) return;

                // Read from the same index the dictionary page and the editor's word panel
                // use, on this worker rather than the dispatcher: it is a SQLite query, and
                // this path runs while the user is typing in someone else's window.
                var translations = ResolveCardTranslations(result, args.Range);
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
                        requestId,
                        translations);
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

    /// <summary>
    /// Takes the user from the card over another application to the full dictionary article.
    /// </summary>
    /// <remarks>
    /// Routed through <c>MainWindow.OpenWordInDictionary</c>, which is the one destination the
    /// editor, the reader and the notes board already navigate to. The card is deliberately
    /// dismissed on the way: it is a summary of the article now being opened, and leaving it
    /// floating over the main window would be two views of one word on screen at once.
    /// </remarks>
    private void OnLexicalFullArticleRequested(object? sender, string word)
    {
        _lexicalLookupCts?.Cancel();
        _lexicalPopup?.Hide();

        OpenMainWindow();
        _mainWindow?.OpenWordInDictionary(word);
        CompatibilityLogger.Technical("lexical-card-open-full-article", "ok=1");
    }

    /// <summary>
    /// The cross-language glosses for a card's word, or nothing when none are installed.
    /// </summary>
    /// <remarks>
    /// The lemma is tried before the surface form because the index is keyed by lemma, and
    /// the language the lookup actually resolved to is preferred over the word's script —
    /// they agree except where the pack knows better.
    /// </remarks>
    private IReadOnlyList<string> ResolveCardTranslations(LexicalLookupResult result, WordRange range)
    {
        if (_translations is null) return [];

        var language = result.Language == LexicalLanguage.Unknown ? range.Language : result.Language;
        if (language == LexicalLanguage.Unknown) return [];

        try
        {
            var values = _translations.Translate(result.Lemma, language);
            if (values.Count == 0 && !string.Equals(result.Lemma, range.Word, StringComparison.OrdinalIgnoreCase))
            {
                values = _translations.Translate(range.Word, language);
            }

            return values;
        }
        catch (Exception exception)
        {
            // A damaged translation file costs the card its translation tab, not the card.
            CompatibilityLogger.Technical("lexical-translations-failed", $"type={exception.GetType().Name}");
            return [];
        }
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

        await ReplaceTargetTextAsync(snapshot, liveText => CanonicalCorrection.TryBind(
                liveText, args.Start, args.Length, args.OriginalWord, args.Replacement, out var correction)
                    == CorrectionBindingStatus.Bound && correction is not null
            ? correction
            : null);
        _lexicalPopup?.Hide();
    }

    /// <summary>Puts a replacement on the clipboard, at the user's explicit request.</summary>
    private void CopyReplacementToClipboard(string? replacement)
    {
        if (string.IsNullOrEmpty(replacement)) return;
        try
        {
            System.Windows.Clipboard.SetText(replacement);
            _correctionPopup?.ShowApplyFeedback(
                "Исправление скопировано в буфер обмена.", isError: false, offerCopy: false, offerRetry: false);
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical("clipboard-copy-failed", $"type={exception.GetType().Name}");
        }
    }

    private async Task ApplyIssueAsync(TextIssue issue)
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
        var token = _correctionPopup?.Token ?? 0;
        var normalized = CorrectionPresentation.NormalizeForApply(issue);
        var outcome = await _correctionApplication.ApplyAsync(
            normalized, snapshot, _monitor, _correctionGate, _applicationLifetimeCts.Token);

        await Dispatcher.InvokeAsync(() => HandleCorrectionOutcome(outcome, normalized.Replacement, token));
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
        await Dispatcher.InvokeAsync(() => HandleCorrectionOutcome(outcome, null, _correctionPopup?.Token ?? 0));
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
            _correctionPopup?.Dismiss();
            if (_settings.HidePanelAfterSuccessfulApply)
            {
                _suggestions?.Hide();
            }

            CompatibilityLogger.Technical("correction-ui-success", $"path={outcome.WritePath ?? "n/a"}");
            return;
        }

        CompatibilityLogger.Technical(
            "correction-ui-failed",
            $"status={outcome.Status} offerCopy={(outcome.OfferCopy ? 1 : 0)} offerRefresh={(outcome.OfferRefresh ? 1 : 0)}");

        if (IsStaleTextOutcome(outcome.Status) && _correctionPopup is { IsVisible: true } stalePopup)
        {
            if (!stalePopup.IsCurrent(cardToken)) return;

            CompatibilityLogger.Technical("correction-card-invalidated", $"reason=apply-{outcome.Status}");
            stalePopup.BeginRecheck();
            _ = _monitor?.RefreshOnceAfterSuppressionAsync();
            return;
        }

        var canCopy = outcome.OfferCopy && !string.IsNullOrEmpty(replacementForCopy);
        if (_correctionPopup is { IsVisible: true })
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
        if (_correctionPopup is { IsVisible: true })
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
    private async Task ReplaceTargetTextAsync(
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
                if (_correctionPopup is { IsVisible: true } popup)
                {
                    popup.BeginRecheck();
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
            _correctionPopup?.Dismiss();
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

    /// <summary>
    /// The backend tag shown beside the engine status, in WriteAI vocabulary.
    /// </summary>
    /// <remarks>
    /// The branching this replaced produced six different strings from three transport
    /// identifiers and two booleans, and four of the six said «Qwen» to the user.
    /// <see cref="WriteAiStatus.BackendLabel"/> is now the single place that decides.
    /// </remarks>
    private string ResolveActiveBackendLabel()
    {
        if (_settings.WriteAiEnabled && _localAiProvider is { IsConfigured: true })
        {
            var last = _localAiProvider.LastBackend;
            var available = _localAiProvider.WriteAiAvailable;
            if (!string.IsNullOrEmpty(last))
            {
                return WriteAiStatus.BackendLabel(last, available);
            }

            if (available && _settings.PreferWriteAi
                && !_settings.LocalAiProfile.Equals("Lite", StringComparison.OrdinalIgnoreCase))
            {
                return WriteAiStatus.ProductName;
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
            // Availability only. Starting the model server here would hold ~481 MB
            // resident for every session, including the many in which no Smart Action is
            // ever invoked — §34. It starts on the first request that needs it.
            await Task.Run(() => _localAiProvider.WarmupAsync(startBackend: false))
                .ConfigureAwait(true);
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
        _correctionPopup?.Close();
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
