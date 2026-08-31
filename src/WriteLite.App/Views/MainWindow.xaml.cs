using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using WriteLite.Controls;
using WriteLite.Services;
using WriteLite.Services.Ai;
using WriteLite.Services.Audio;
using WriteLite.Services.Diagnostics;
using WriteLite.Services.Lexical;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Notes;
using WriteLite.Services.Reading;
using WriteLite.Services.Settings;
using WriteLite.Services.Shell;
using WriteLite.Services.Spelling;
using WriteLite.Views.Pages;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using Point = System.Windows.Point;
using TranslateTransform = System.Windows.Media.TranslateTransform;

namespace WriteLite.Views;

public partial class MainWindow : Window
{
    /// <summary>Below this width the rail drops to icons so the content keeps its measure.</summary>
    private const double CompactRailThreshold = 1000;

    private readonly HomePage _homePage = new();
    private readonly SettingsPage _settingsPage = new();
    private readonly DictionaryPage _dictionaryPage = new();
    private readonly WordManagerPage _wordManagerPage = new();
    private readonly DiagnosticsPage _diagnosticsPage = new();
    private readonly EditorPage _editorPage = new();
    private readonly ConverterPage _converterPage = new();
    private readonly LocalEnginePage _localEnginePage = new();
    private readonly NotesPage _notesPage = new();
    private readonly ReadingPage _readingPage = new();
    private readonly PlaceholderPage _aboutPage = new();
    private readonly PlaceholderPage _appsPage = new();
    private readonly ShellWindowBehavior _windowBehavior;
    private bool _minimizeToTray = true;
    private bool _isCompactRail;
    private AmbiencePlayer? _ambiencePlayer;

    public MainWindow()
    {
        InitializeComponent();
        PageHost.Content = _homePage;
        Closing += OnClosing;
        StateChanged += (_, _) => UpdateMaximizeAffordance();

        // Owns both the maximised size and fullscreen: they are the same question
        // asked of WM_GETMINMAXINFO, so one object answers it.
        _windowBehavior = new ShellWindowBehavior(this);
        _windowBehavior.FullScreenChanged += OnFullScreenChanged;

        // The two shell keys are listened for differently on purpose.
        //
        // F11 belongs to the window whatever has focus, so it is taken on the way down:
        // bubbling only reaches the window if nothing claimed the key first, and the
        // editor, the reader's canvas and every text field on the settings page are all
        // in a position to claim it.
        //
        // Escape is the opposite. It belongs to whatever is in front of it, and getting
        // here at all is the proof that no popup, dialog or text field wanted it.
        PreviewKeyDown += OnWindowShortcut;
        KeyDown += OnEscape;

        // Both new sections hand a selected word to the dictionary through the one
        // shared flow, rather than each carrying its own half-working lookup.
        _notesPage.WordNavigationRequested += OpenWordInDictionary;
        _readingPage.WordNavigationRequested += OpenWordInDictionary;
        _editorPage.WordNavigationRequested += OpenWordInDictionary;

        // NavHome raises Checked during InitializeComponent, before PageHost exists, so
        // the first page never goes through Nav_Checked and would otherwise open wherever
        // the initial focus pass scrolled it.
        Loaded += (_, _) =>
        {
            ScrollPageToTop();

            // The indicator cannot be placed until the rail has been measured; before
            // that every item reports a height of zero. It starts invisible so it is
            // never seen sitting at the top of the rail before it finds its row.
            MoveNavIndicator(NavHome, animate: false);
        };

        _aboutPage.Configure(
            index: "09",
            title: "О программе",
            body: "WriteLite — локальный помощник проверки русского текста.\n\n" +
                  "«Грамотный текст — там, где вы печатаете.»\n\n" +
                  "Проверка выполняется локально на этом компьютере. " +
                  "WriteLite использует сторонние компоненты с открытым исходным кодом — " +
                  "сведения в файле THIRD_PARTY_NOTICES.",
            actionLabel: "Открыть THIRD_PARTY_NOTICES",
            action: OpenThirdPartyNotices);

        _appsPage.Configure(
            index: "06",
            title: "Совместимость",
            body: "WriteLite работает в активных редактируемых полях Windows, которые предоставляют " +
                  "совместимые UI Automation patterns.\n\n" +
                  "Статический текст, поля только для чтения и поля паролей игнорируются полностью — " +
                  "они не читаются и не анализируются.\n\n" +
                  "Некоторые многострочные, браузерные и нестандартные редакторы отдают текст для чтения, " +
                  "но не позволяют прямую замену: в таких полях WriteLite показывает замечания без кнопки исправления.");

        _homePage.NavigationRequested += OpenSection;
        _localEnginePage.NavigationRequested += OpenSection;
        _wordManagerPage.NavigationRequested += OpenSection;
        _editorPage.NavigationRequested += OpenSection;

        // The two document surfaces hand work to each other: "Convert" in the
        // editor's File menu opens the converter with the current file already
        // chosen, and a converted file can be opened straight back into the editor.
        _converterPage.OpenInEditorRequested += path =>
        {
            NavPanel.IsChecked = true;
            _ = _editorPage.OpenFromShellAsync(path);
        };
    }

    /// <summary>The section on screen, for a crash record. Never throws and never null.</summary>
    public string CurrentSection =>
        NavStack?.Children.OfType<NavItem>().FirstOrDefault(item => item.IsChecked == true)?.Label
        ?? "none";

    public event Action<WriteLiteAppSettings>? SettingsChanged;

    /// <summary>Raised when a shortcut binding changes, so the shell can re-register it.</summary>
    public event Action<ShortcutRegistry>? ShortcutsChanged;

    public event Action? DictionaryChanged;

    public event Action? ExceptionsChanged;

    public void SetMonitorActive(bool active)
    {
        _homePage.SetMonitorActive(active);
        EngineDot.Fill = (System.Windows.Media.Brush)FindResource(active ? "WlSuccess" : "WlTextMuted");
        Controls.Type.SetTracked(EngineStateText, active ? "ЛОКАЛЬНАЯ ПРОВЕРКА" : "ПРОВЕРКА НА ПАУЗЕ");
    }

    public void SetEngineStatus(string status)
    {
        _homePage.SetEngineStatus(status);
        _localEnginePage.SetEngineStatus(status);
    }

    public void SetMinimizeToTray(bool value) => _minimizeToTray = value;

    /// <summary>
    /// Hands the rail's ambience player its tracks and remembered state.
    /// </summary>
    /// <remarks>
    /// Scanning for tracks is file-system work, so it happens off the dispatcher; the
    /// bar stays hidden until there is something to play, which is also what happens
    /// when the audio folder is missing entirely.
    /// </remarks>
    public void BindAmbience(AmbiencePlayer player, WriteLiteAppSettings settings)
    {
        _ambiencePlayer = player;

        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Run(() => player.LoadTracks());
            Ambience.Bind(player, settings.AmbienceVolume, settings.AmbienceEnabled, settings.AmbienceLoop);
        }, System.Windows.Threading.DispatcherPriority.Background);

        Ambience.StateChanged += (volume, playing, loop) =>
        {
            settings.AmbienceVolume = volume;
            settings.AmbienceEnabled = playing;
            settings.AmbienceLoop = loop;
            AmbienceStateChanged?.Invoke(settings);
        };
    }

    /// <summary>Raised when the player's volume or playback state should be persisted.</summary>
    public event Action<WriteLiteAppSettings>? AmbienceStateChanged;

    public void BindServices(
        WriteLiteAppSettings settings,
        IWriteLiteAutostartService autostart,
        UserDictionaryService dictionary,
        WriteLiteIgnoreService ignore,
        WriteLiteDiagnosticsService diagnostics,
        Func<Task<bool>> restartEngine)
    {
        _settingsPage.Bind(settings, autostart);
        _settingsPage.SettingsChanged += s => SettingsChanged?.Invoke(s);
        _settingsPage.ShortcutsChanged += registry => ShortcutsChanged?.Invoke(registry);
        _wordManagerPage.Bind(dictionary, ignore);
        _wordManagerPage.DictionaryChanged += () => DictionaryChanged?.Invoke();
        _wordManagerPage.ExceptionsChanged += () => ExceptionsChanged?.Invoke();
        _diagnosticsPage.Bind(diagnostics, restartEngine);
        _localEnginePage.Bind(settings);
    }

    /// <summary>
    /// Gives the editor its checking stack, its local AI and its dictionary services.
    /// </summary>
    /// <remarks>
    /// Passed in rather than constructed by the page: all three are process-wide, already
    /// warmed at startup, and expensive to build a second time. <paramref name="analyzer"/>
    /// is the one that decides whether the editor checks documents with what WriteLite has
    /// or with a fallback — see <see cref="Pages.EditorPage.BindAnalyzer"/>.
    /// </remarks>
    public void BindEditorServices(
        ITextAnalyzer? analyzer,
        IEditorAiService? editorAi,
        LexicalLookupService? lexical,
        WriteLite.Services.Ai.WritingAssistanceService? writing = null,
        ITextAnalyzer? fastAnalyzer = null)
    {
        _editorPage.BindAnalyzer(analyzer, fastAnalyzer);
        _editorPage.BindAi(editorAi, lexical);
        _editorPage.BindWritingAssistance(writing);

        // Recovery is offered once the shell is on screen, not when the page is
        // constructed: a modal question must never be a side effect of laying out
        // a control.
        //
        // Binding happens inside App.OnStartup, so asking here synchronously parked
        // the dispatcher on a MessageBox before the tray, the monitor and the
        // shutdown path existed — a leftover snapshot made the whole application
        // unstartable, with the prompt behind a window that was never shown.
        // Waiting for the window keeps the prompt for the user who opens the editor
        // and keeps it out of a tray-only session entirely.
        if (IsVisible)
        {
            QueueRecoveryOffer();
        }
        else
        {
            IsVisibleChanged += OnShellVisibleForRecovery;
        }
    }

    private void OnShellVisibleForRecovery(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            return;
        }

        IsVisibleChanged -= OnShellVisibleForRecovery;
        QueueRecoveryOffer();
    }

    /// <summary>Asks about orphaned work after the shell has finished laying out.</summary>
    private void QueueRecoveryOffer()
        => _ = Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            () => _editorPage.CheckForRecoverableWork());

    /// <summary>Gives the notes board and the reading library their local stores.</summary>
    /// <remarks>
    /// Both are process-wide and both own an autosave timer, so the shell creates them
    /// once and hands them over rather than letting a page construct its own — two
    /// services writing the same file is how a board loses a card.
    /// </remarks>
    public void BindWorkspaces(NotesService notes, ReadingLibraryService reading, StudyCardDraftService? cardDrafts)
    {
        _notesPage.Bind(notes);
        _readingPage.Bind(reading, cardDrafts);
    }

    /// <summary>Optional: enables word lookup on the dictionary page.</summary>
    /// <param name="translations">
    /// Absent when the translation index is missing, in which case the dictionary
    /// simply shows no English column rather than failing.
    /// </param>
    public void BindLexical(ILexicalKnowledgeService lexical, TranslationIndex? translations = null) =>
        _dictionaryPage.Bind(lexical, translations);

    public void RefreshDictionary() => _wordManagerPage.Reload();

    public void RefreshExceptions() => _wordManagerPage.Reload();

    public void RefreshDiagnostics() => _diagnosticsPage.Refresh();

    public void ShowFromTray()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;

        // Land focus on the rail rather than on the window. Focusing the window makes WPF
        // hand focus to the first focusable element inside the page, whose BringIntoView
        // then scrolls the page away from its own heading.
        var selected = NavStack.Children.OfType<NavItem>().FirstOrDefault(item => item.IsChecked == true);
        if (selected is not null)
        {
            selected.Focus();
        }
        else
        {
            Focus();
        }
    }

    public void ForceClose()
    {
        // The reader holds an unsaved scroll position; giving it the chance to write
        // it is the difference between "continue where you left off" and "continue
        // where you were a minute ago".
        _readingPage.Shutdown();
        _editorPage.Dispose();
        _windowBehavior.Dispose();
        Closing -= OnClosing;
        Close();
    }

    // ── Word navigation ──────────────────────────────────────────────────────

    /// <summary>
    /// Opens a word's article in the dictionary, from wherever it was selected.
    /// </summary>
    /// <remarks>
    /// The single destination for every «Открыть в словаре» in the product. The editor,
    /// the reader and the notes board all normalise a selection through
    /// <see cref="WordNavigation"/> and call this; nothing else navigates to the
    /// dictionary, so there is one behaviour to get right and one place to fix it.
    ///
    /// The word is looked up after the rail has switched, because <c>Nav_Checked</c>
    /// scrolls the incoming page to its own heading and would otherwise scroll the
    /// article away the moment it rendered.
    /// </remarks>
    public void OpenWordInDictionary(string word)
    {
        var normalized = WordNavigation.Normalize(word);
        if (normalized is null)
        {
            return;
        }

        NavDictionary.IsChecked = true;

        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() => _ = _dictionaryPage.ShowWordAsync(normalized)));
    }

    // ── Fullscreen ───────────────────────────────────────────────────────────

    /// <summary>True while the window owns the whole display.</summary>
    public bool IsFullScreen => _windowBehavior.IsFullScreen;

    public void ToggleFullScreen() => _windowBehavior.ToggleFullScreen();

    /// <summary>
    /// The shortcuts that belong to the window rather than to whichever page is on screen.
    /// </summary>
    /// <remarks>
    /// Reader page navigation is handled here, not in the reader, because a shortcut for
    /// turning the page has to work while the reader has focus anywhere inside it — in the
    /// text, in the marks panel, on a toolbar button — and the window is the one element
    /// every one of those is inside. It is offered only while a book is actually open, so
    /// the same keys stay free everywhere else.
    /// </remarks>
    private void OnWindowShortcut(object sender, KeyEventArgs e)
    {
        var shortcut = _shortcuts.Match(e.Key, Keyboard.Modifiers, ShortcutScope.Application);

        switch (shortcut)
        {
            case ShortcutRegistry.FullScreen:
                e.Handled = true;
                _windowBehavior.ToggleFullScreen();
                break;

            case ShortcutRegistry.PreviousPage when _readingPage.IsReading:
                e.Handled = true;
                _readingPage.GoToPreviousPage();
                break;

            case ShortcutRegistry.NextPage when _readingPage.IsReading:
                e.Handled = true;
                _readingPage.GoToNextPage();
                break;
        }
    }

    /// <summary>
    /// The shortcuts this window obeys. Replaced when the settings page changes them.
    /// </summary>
    private ShortcutRegistry _shortcuts = new();

    /// <summary>Gives the shell and its pages the shortcut bindings the reader configured.</summary>
    public void BindShortcuts(ShortcutRegistry shortcuts)
    {
        _shortcuts = shortcuts;
        _editorPage.BindShortcuts(shortcuts);
        _settingsPage.BindShortcuts(shortcuts);
    }

    private void OnEscape(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            // Closing an open book is a smaller step than leaving fullscreen, so the
            // reader gets first refusal.
            case Key.Escape when _readingPage.IsReading:
                e.Handled = true;
                _readingPage.TryCloseReader();
                break;

            case Key.Escape when _windowBehavior.IsFullScreen:
                e.Handled = true;
                _windowBehavior.ExitFullScreen();
                break;
        }
    }

    /// <summary>
    /// Hides the title bar in fullscreen and gives its space back to the page.
    /// </summary>
    /// <remarks>
    /// The caption height goes to zero along with it: a 44 px strip of window-drag
    /// area over the top of a full-screen reader would swallow clicks meant for the
    /// text underneath.
    /// </remarks>
    private void OnFullScreenChanged(bool fullScreen)
    {
        TitleBar.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;
        TitleBarRow.Height = fullScreen ? new GridLength(0) : new GridLength(44);

        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        if (chrome is not null)
        {
            chrome.CaptionHeight = fullScreen ? 0 : 44;
            chrome.ResizeBorderThickness = new Thickness(fullScreen ? 0 : 6);
        }

        // RootShell's own border and radius are left to its style: fullscreen is a
        // maximised window, so the trigger that already flattens the frame when
        // maximised covers this case too. Setting them here would win over the style
        // for good and leave the window square after the first exit.
    }

    private static void OpenThirdPartyNotices()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ThirdParty", "THIRD_PARTY_NOTICES.txt");
        if (!File.Exists(path))
        {
            MessageBox.Show("Файл THIRD_PARTY_NOTICES.txt не найден.", "WriteLite");
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_minimizeToTray)
        {
            e.Cancel = true;
            Hide();
        }
    }

    /// <summary>Lets a page hand navigation back to the rail, keeping selection in sync.</summary>
    private void OpenSection(string section)
    {
        var target = section switch
        {
            "editor" => NavPanel,
            "converter" => NavConverter,
            "notes" => NavNotes,
            "reading" => NavReading,
            "dictionary" => NavDictionary,
            "words" => NavWords,
            "engine" => NavLocalAi,
            "settings" => NavSettings,
            "diagnostics" => NavDiagnostics,
            "apps" => NavApps,
            "about" => NavAbout,
            _ => NavHome
        };

        target.IsChecked = true;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not NavItem item || PageHost is null)
        {
            return;
        }

        // Leaving the reader flushes where it had got to, so the position survives
        // however the session ends after this point.
        if (!ReferenceEquals(item, NavReading))
        {
            _readingPage.Suspend();
        }

        // Pages that read from disk or from a service refresh as they come into view,
        // so a change made elsewhere (tray menu, another page) is never shown stale.
        object page;
        if (ReferenceEquals(item, NavHome))
        {
            page = _homePage;
        }
        else if (ReferenceEquals(item, NavPanel))
        {
            page = _editorPage;
        }
        else if (ReferenceEquals(item, NavConverter))
        {
            // Arriving from the editor's File menu, the converter starts on the
            // file that is already open rather than on an empty form.
            _converterPage.PrepareFor(_editorPage.CurrentDocumentPath);
            _converterPage.Reload();
            page = _converterPage;
        }
        else if (ReferenceEquals(item, NavNotes))
        {
            _notesPage.Reload();
            page = _notesPage;
        }
        else if (ReferenceEquals(item, NavReading))
        {
            _readingPage.Reload();
            page = _readingPage;
        }
        else if (ReferenceEquals(item, NavDictionary))
        {
            _dictionaryPage.Reload();
            page = _dictionaryPage;
        }
        else if (ReferenceEquals(item, NavWords))
        {
            _wordManagerPage.Reload();
            page = _wordManagerPage;
        }
        else if (ReferenceEquals(item, NavLocalAi))
        {
            page = _localEnginePage;
        }
        else if (ReferenceEquals(item, NavApps))
        {
            page = _appsPage;
        }
        else if (ReferenceEquals(item, NavSettings))
        {
            page = _settingsPage;
        }
        else if (ReferenceEquals(item, NavDiagnostics))
        {
            _diagnosticsPage.Refresh();
            page = _diagnosticsPage;
        }
        else
        {
            page = _aboutPage;
        }

        // The page is swapped immediately and only the arrival animates. Waiting out
        // an exit fade before the new page starts would make every navigation feel
        // slower than the instant swap this replaces.
        Motion.TransitionContent(PageHost, page);
        MoveNavIndicator(item, animate: true);

        Controls.Type.SetTracked(PageNameText, item.Label.ToUpperInvariant());
        ScrollPageToTop();
    }

    /// <summary>
    /// Slides the rail's single selection indicator onto <paramref name="item"/>.
    /// </summary>
    /// <remarks>
    /// One indicator that travels, rather than one per item switching colour: the
    /// movement is what tells the eye where the selection went. It also means the
    /// rail has exactly one orange element at any moment, which is the point of a
    /// single-accent palette.
    /// </remarks>
    private void MoveNavIndicator(NavItem item, bool animate)
    {
        if (NavIndicator is null || NavStack is null || item.ActualHeight <= 0)
        {
            return;
        }

        // Inset top and bottom so the indicator reads as a mark beside the row rather
        // than as a full-height rule.
        const double Inset = 7;

        var top = item.TranslatePoint(new Point(0, 0), NavStack).Y + Inset;
        var height = Math.Max(2, item.ActualHeight - (Inset * 2));

        if (animate && NavIndicator.Opacity > 0)
        {
            Motion.SlideTo(NavIndicatorShift, top);
            Motion.ResizeTo(NavIndicator, height);
        }
        else
        {
            // First placement: appear where the selection already is instead of
            // sliding in from the top of the rail.
            NavIndicatorShift.BeginAnimation(TranslateTransform.YProperty, null);
            NavIndicator.BeginAnimation(HeightProperty, null);
            NavIndicatorShift.Y = top;
            NavIndicator.Height = height;
        }

        NavIndicator.Opacity = 1;
    }

    /// <summary>
    /// A page always opens at its own heading. Without this, returning to a page shows it
    /// wherever it was last scrolled, which reads as a rendering glitch rather than as
    /// remembered state.
    /// </summary>
    private void ScrollPageToTop()
    {
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() => FindScrollViewer(PageHost)?.ScrollToHome()));
    }

    private static System.Windows.Controls.ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.ScrollViewer viewer)
            {
                return viewer;
            }

            if (FindScrollViewer(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged)
        {
            return;
        }

        var compact = e.NewSize.Width < CompactRailThreshold;
        if (compact == _isCompactRail)
        {
            return;
        }

        _isCompactRail = compact;
        RailColumn.Width = new GridLength(compact ? 60 : 216);
        Rail.Padding = new Thickness(compact ? 6 : 10, 8, compact ? 6 : 10, 12);

        var railVisibility = compact ? Visibility.Collapsed : Visibility.Visible;
        GroupWorkspace.Visibility = railVisibility;
        GroupSystem.Visibility = railVisibility;
        ExitButton.Visibility = railVisibility;
        RailFootnote.Visibility = railVisibility;

        // A 60 px rail cannot hold a transport, a title and a volume slider. Playback
        // is untouched — the sound keeps going, only the controls step aside.
        Ambience.Visibility = railVisibility;

        foreach (var child in NavStack.Children)
        {
            if (child is NavItem item)
            {
                item.IsCompact = compact;
            }
        }

        // Collapsing the group labels moves every row below them, so the indicator
        // has to be re-placed once the rail has been laid out again. Without the
        // animation: this is a resize, not a selection change.
        var selected = NavStack.Children.OfType<NavItem>().FirstOrDefault(nav => nav.IsChecked == true);
        if (selected is not null)
        {
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() => MoveNavIndicator(selected, animate: false)));
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateMaximizeAffordance()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeIcon.Data = (System.Windows.Media.Geometry)FindResource(maximized ? "WlIconRestore" : "WlIconMaximize");
        MaximizeButton.ToolTip = maximized ? "Восстановить" : "Развернуть";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Exit_Click(object sender, RoutedEventArgs e) => System.Windows.Application.Current.Shutdown(0);
}
