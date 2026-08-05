using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WriteLite.Services.Diagnostics;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Settings;
using WriteLite.Services.Spelling;
using WriteLite.Views.Pages;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace WriteLite.Views;

public partial class MainWindow : Window
{
    private readonly HomePage _homePage = new();
    private readonly SettingsPage _settingsPage = new();
    private readonly DictionaryPage _dictionaryPage = new();
    private readonly ExceptionsPage _exceptionsPage = new();
    private readonly DiagnosticsPage _diagnosticsPage = new();
    private readonly EditorPage _editorPage = new();
    private readonly PlaceholderPage _aboutPage = new();
    private readonly PlaceholderPage _localAiPage = new();
    private readonly PlaceholderPage _appsPage = new();
    private Button? _activeNav;
    private bool _minimizeToTray = true;

    public MainWindow()
    {
        InitializeComponent();
        _activeNav = NavHome;
        PageHost.Content = _homePage;
        Closing += OnClosing;
        _aboutPage.Configure(
            "О программе",
            "WriteLite — локальный помощник проверки русского текста.\n\n" +
            "«Грамотный текст — там, где вы печатаете.»\n\n" +
            "Проверка выполняется локально на этом компьютере. " +
            "WriteLite использует сторонние компоненты с открытым исходным кодом — " +
            "сведения в файле THIRD_PARTY_NOTICES.",
            actionLabel: "Открыть THIRD_PARTY_NOTICES",
            action: OpenThirdPartyNotices);
        _localAiPage.Configure(
            "Локальный AI",
            "Локальная модель используется как дополнительный слой после быстрых правил. Недоступность модели не отключает базовую проверку и не закрывает приложение.");
        _appsPage.Configure(
            "Поддерживаемые приложения",
            "WriteLite работает в активных редактируемых полях Windows, которые предоставляют совместимые UI Automation patterns. Статический текст, read-only и password-поля игнорируются.");
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

    public event Action<WriteLiteAppSettings>? SettingsChanged;
    public event Action? DictionaryChanged;
    public event Action? ExceptionsChanged;

    public void SetMonitorActive(bool active) => _homePage.SetMonitorActive(active);

    public void SetEngineStatus(string status) => _homePage.SetEngineStatus(status);

    public void SetMinimizeToTray(bool value) => _minimizeToTray = value;

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
        _dictionaryPage.Bind(dictionary);
        _dictionaryPage.DictionaryChanged += () => DictionaryChanged?.Invoke();
        _exceptionsPage.Bind(ignore);
        _exceptionsPage.ExceptionsChanged += () => ExceptionsChanged?.Invoke();
        _diagnosticsPage.Bind(diagnostics, restartEngine);
    }

    public void RefreshDictionary() => _dictionaryPage.Reload();

    public void RefreshExceptions() => _exceptionsPage.Reload();

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
        Focus();
    }

    public void ForceClose()
    {
        _editorPage.Dispose();
        Closing -= OnClosing;
        Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_minimizeToTray)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        SetActiveNav(button);

        if (ReferenceEquals(button, NavHome))
        {
            PageHost.Content = _homePage;
            return;
        }

        if (ReferenceEquals(button, NavSettings))
        {
            PageHost.Content = _settingsPage;
            return;
        }

        if (ReferenceEquals(button, NavDictionary))
        {
            _dictionaryPage.Reload();
            PageHost.Content = _dictionaryPage;
            return;
        }

        if (ReferenceEquals(button, NavDiagnostics))
        {
            _diagnosticsPage.Refresh();
            PageHost.Content = _diagnosticsPage;
            return;
        }

        // Use "Индикатор" nav slot for Exceptions page (closest fit in existing nav).
        if (ReferenceEquals(button, NavIndicator))
        {
            _exceptionsPage.Reload();
            PageHost.Content = _exceptionsPage;
            return;
        }

        if (ReferenceEquals(button, NavPanel))
        {
            PageHost.Content = _editorPage;
            return;
        }

        if (ReferenceEquals(button, NavLocalAi))
        {
            PageHost.Content = _localAiPage;
            return;
        }

        if (ReferenceEquals(button, NavApps))
        {
            PageHost.Content = _appsPage;
            return;
        }

        if (ReferenceEquals(button, NavAbout))
        {
            PageHost.Content = _aboutPage;
            return;
        }

        PageHost.Content = _aboutPage;
    }

    private void SetActiveNav(Button button)
    {
        if (_activeNav is not null)
        {
            _activeNav.Tag = null;
        }

        button.Tag = "Active";
        _activeNav = button;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Exit_Click(object sender, RoutedEventArgs e) => System.Windows.Application.Current.Shutdown(0);
}
