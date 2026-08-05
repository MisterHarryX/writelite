using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WriteLite.Models;
using WriteLite.Services;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using RadioButton = System.Windows.Controls.RadioButton;
using Size = System.Windows.Size;

namespace WriteLite.Views;

public partial class SuggestionsWindow : Window
{
    private readonly ObservableCollection<IssueCard> _allIssues = [];
    private readonly ObservableCollection<IssueCard> _visibleIssues = [];
    private bool _isBusy;
    private bool _ready;
    private string _tabFilter = "All";
    private Storyboard? _panelSpin;
    private TextSnapshot? _latestSnapshot;

    public SuggestionsWindow()
    {
        InitializeComponent();
        AppIconLoader.ApplyTo(this);
        IssuesList.ItemsSource = _visibleIssues;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                Hide();
            }
        };
        SetupSpin();
        _ready = true;
        UpdateEmptyAndFooter();
        CompatibilityLogger.Technical("correction-popup-created", "instance=1");
    }

    public event Func<TextIssue, Task>? ApplyIssueRequested;
    public event Func<Task>? ApplyAllRequested;
    public event EventHandler? PanelHidden;
    public event EventHandler? OpenMainWindowRequested;
    public event Action<TextIssue>? AddToDictionaryRequested;
    public event Action<TextIssue>? IgnoreIssueRequested;

    public void ShowSnapshot(TextSnapshot snapshot)
    {
        _latestSnapshot = snapshot;
        ReconcileIssues(snapshot);
        CompatibilityLogger.Technical("correction-popup-reused", $"generation={snapshot.GenerationId}");

        UpdateHeader(snapshot);
        UpdateTabCounts(snapshot);
        UpdateBanners(snapshot);
        ApplyFilter();
        UpdateEmptyAndFooter();

        var dpi = VisualTreeHelper.GetDpi(this);
        var field = OverlayPlacementService.Scale(snapshot.Target.Bounds, dpi.DpiScaleX, dpi.DpiScaleY);
        var placement = OverlayPlacementService.PlacePanel(
            field,
            SystemParameters.WorkArea,
            new Size(Width, Height));

        // Do not reposition an already open panel while the user is choosing a fix.
        if (!IsVisible)
        {
            Left = placement.Location.X;
            Top = placement.Location.Y;
        }
        MaxHeight = Math.Min(560, placement.Size.Height);

        if (!IsVisible)
        {
            Show();
            CompatibilityLogger.Technical("correction-popup-opened", "instance=reused");
        }
    }

    public new void Hide()
    {
        if (!IsVisible)
        {
            return;
        }

        base.Hide();
        CompatibilityLogger.Technical("suggestions-panel-hidden", "instance=retained");
        PanelHidden?.Invoke(this, EventArgs.Empty);
    }

    private void ReconcileIssues(TextSnapshot snapshot)
    {
        var wanted = snapshot.Issues.ToHashSet();
        for (var i = _allIssues.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(_allIssues[i].Issue)) _allIssues.RemoveAt(i);
        }
        foreach (var issue in snapshot.Issues)
        {
            if (!_allIssues.Any(card => card.Issue == issue))
            {
                _allIssues.Add(new IssueCard(issue, snapshot.Text, snapshot.Target.SupportsDirectWrite));
            }
        }
    }

    private void UpdateHeader(TextSnapshot snapshot)
    {
        var count = snapshot.Issues.Count;
        SummaryText.Text = count switch
        {
            0 when snapshot.IndicatorState is AnalysisIndicatorState.Analyzing => "Анализ текста…",
            0 when !snapshot.IsFullDictionaryLoaded => "Словарь загружается",
            0 when !snapshot.Target.SupportsDirectWrite => "Только для чтения",
            0 => "Нет замечаний",
            1 => "1 замечание",
            _ => $"{count} замечаний"
        };
    }

    private void UpdateTabCounts(TextSnapshot snapshot)
    {
        var issues = snapshot.Issues;
        TabAll.Content = $"Все {issues.Count}";
        TabOrthography.Content = $"Орфография {issues.Count(issue => issue.Category == IssueCategory.Orthography)}";
        TabPunctuation.Content = $"Пунктуация {issues.Count(issue => issue.Category == IssueCategory.Punctuation)}";
        TabGrammar.Content = $"Грамматика {issues.Count(issue => issue.Category == IssueCategory.Grammar)}";
        TabStyle.Content = $"Стиль {issues.Count(issue => issue.Category == IssueCategory.Style)}";
    }

    private void UpdateBanners(TextSnapshot snapshot)
    {
        ReadonlyBanner.Visibility = snapshot.Target.SupportsDirectWrite
            ? Visibility.Collapsed
            : Visibility.Visible;

        DictBanner.Visibility = !snapshot.IsFullDictionaryLoaded && snapshot.Issues.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyFilter()
    {
        _visibleIssues.Clear();
        foreach (var card in _allIssues)
        {
            if (MatchesTab(card.Issue, _tabFilter))
            {
                _visibleIssues.Add(card);
            }
        }
    }

    private static bool MatchesTab(TextIssue issue, string tab)
    {
        return tab switch
        {
            "All" => true,
            "Orthography" => issue.Category == IssueCategory.Orthography,
            "Punctuation" => issue.Category == IssueCategory.Punctuation,
            "Grammar" => issue.Category == IssueCategory.Grammar,
            "Style" => issue.Category == IssueCategory.Style,
            _ => true
        };
    }

    private void UpdateEmptyAndFooter()
    {
        var hasVisible = _visibleIssues.Count > 0;
        var hasAny = _allIssues.Count > 0;

        EmptyState.Visibility = !hasVisible && !_isBusy ? Visibility.Visible : Visibility.Collapsed;
        IssuesScroller.Visibility = hasVisible || ReadonlyBanner.Visibility == Visibility.Visible || DictBanner.Visibility == Visibility.Visible
            ? Visibility.Visible
            : (hasAny ? Visibility.Visible : Visibility.Collapsed);

        if (!hasVisible && !_isBusy)
        {
            IssuesScroller.Visibility = ReadonlyBanner.Visibility == Visibility.Visible || DictBanner.Visibility == Visibility.Visible
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        AnalyzingState.Visibility = _isBusy ? Visibility.Visible : Visibility.Collapsed;
        if (_isBusy)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            IssuesScroller.Visibility = Visibility.Collapsed;
            _panelSpin?.Begin();
        }
        else
        {
            _panelSpin?.Stop();
        }

        var safeCount = _allIssues.Count(c => c.CanApply);
        FooterBar.Visibility = hasAny ? Visibility.Visible : Visibility.Collapsed;
        SafeCountText.Text = safeCount > 0
            ? $"{safeCount} можно исправить автоматически"
            : "Нет безопасных автоправок";
        ApplyAllButton.IsEnabled = safeCount > 0 && !_isBusy;
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        // IsChecked is set during InitializeComponent before named fields are ready.
        if (!_ready || sender is not RadioButton { IsChecked: true } tab)
        {
            return;
        }

        _tabFilter = tab.Name switch
        {
            nameof(TabOrthography) => "Orthography",
            nameof(TabPunctuation) => "Punctuation",
            nameof(TabGrammar) => "Grammar",
            nameof(TabStyle) => "Style",
            _ => "All"
        };

        ApplyFilter();
        UpdateEmptyAndFooter();
    }

    private void AddToDictionary_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TextIssue issue })
        {
            AddToDictionaryRequested?.Invoke(issue);
        }
    }

    private void IgnoreIssue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TextIssue issue })
        {
            IgnoreIssueRequested?.Invoke(issue);
        }
    }

    private async void ApplyIssueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not Button { Tag: TextIssue issue } || ApplyIssueRequested is null)
        {
            return;
        }

        await RunBusyAsync(() => ApplyIssueRequested.Invoke(issue));
    }

    private async void ApplyAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || ApplyAllRequested is null) return;

        await RunBusyAsync(() => ApplyAllRequested.Invoke());
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // UI Spec: settings marked "Скоро". Open main window if requested by product shell.
        OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunBusyAsync(Func<Task> operation)
    {
        _isBusy = true;
        IssuesList.IsEnabled = false;
        ApplyAllButton.IsEnabled = false;
        ProgressIndicator.Visibility = Visibility.Visible;
        UpdateEmptyAndFooter();

        try
        {
            await operation();
        }
        finally
        {
            ProgressIndicator.Visibility = Visibility.Collapsed;
            IssuesList.IsEnabled = true;
            _isBusy = false;
            UpdateEmptyAndFooter();
        }
    }

    private void SetupSpin()
    {
        var animation = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        _panelSpin = new Storyboard();
        Storyboard.SetTarget(animation, AnalyzingState.Children[0]);
        Storyboard.SetTargetProperty(animation, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
        _panelSpin.Children.Add(animation);
    }

    private sealed class IssueCard
    {
        public IssueCard(TextIssue issue, string currentText, bool supportsDirectWrite)
        {
            var normalized = CorrectionPresentation.NormalizeForApply(issue);
            Issue = normalized;
            CanApply = TextCorrectionService.CanApplyManual(normalized, currentText, supportsDirectWrite)
                       || TextCorrectionService.CanApply(normalized, currentText, supportsDirectWrite);
            if (CorrectionPresentation.IsTerminalPunctuationInsert(normalized) || normalized.Length == 0)
            {
                OriginalDisplay = "—";
                CorrectedDisplay = CorrectionPresentation.FormatChipLabel(normalized);
            }
            else
            {
                OriginalDisplay = VisualizeWhitespace(Shorten(StripMarkdownDecorations(normalized.Original), 80));
                CorrectedDisplay = VisualizeWhitespace(Shorten(StripMarkdownDecorations(normalized.Replacement ?? string.Empty), 80));
            }

            BriefLabel = string.IsNullOrWhiteSpace(normalized.Title) ? CategoryLabel : normalized.Title;
            (CategoryBrush, CategoryBackground) = ResolveCategoryBrushes(normalized.Category);
        }

        public TextIssue Issue { get; }
        public bool CanApply { get; }
        public Visibility DictionaryVisibility =>
            Issue.Category == IssueCategory.Orthography ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ReplacementVisibility =>
            !string.IsNullOrEmpty(Issue.Replacement) ? Visibility.Visible : Visibility.Collapsed;
        public string OriginalDisplay { get; }
        public string CorrectedDisplay { get; }
        public string BriefLabel { get; }
        public Brush CategoryBrush { get; }
        public Brush CategoryBackground { get; }

        public string CategoryLabel => Issue.Category switch
        {
            IssueCategory.Orthography => "ОРФОГРАФИЯ",
            IssueCategory.Grammar => "ГРАММАТИКА",
            IssueCategory.Punctuation => "ПУНКТУАЦИЯ",
            IssueCategory.Style => "СТИЛЬ",
            IssueCategory.Readability => "ОФОРМЛЕНИЕ",
            _ => "ЗАМЕЧАНИЕ"
        };

        private static (Brush Foreground, Brush Background) ResolveCategoryBrushes(IssueCategory category)
        {
            return category switch
            {
                IssueCategory.Orthography => (ThemeResource.Brush("WlCatSpelling"), ThemeResource.Brush("WlCatSpellingBg")),
                IssueCategory.Punctuation => (ThemeResource.Brush("WlCatPunctuation"), ThemeResource.Brush("WlCatPunctuationBg")),
                IssueCategory.Grammar => (ThemeResource.Brush("WlCatGrammar"), ThemeResource.Brush("WlCatGrammarBg")),
                IssueCategory.Style => (ThemeResource.Brush("WlCatStyle"), ThemeResource.Brush("WlCatStyleBg")),
                IssueCategory.Readability => (ThemeResource.Brush("WlCatFormatting"), ThemeResource.Brush("WlCatFormattingBg")),
                _ => (ThemeResource.Brush("WlTextSecondary"), ThemeResource.Brush("WlRaised"))
            };
        }

        private static string Shorten(string value, int maxLength)
        {
            var normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
            return normalized.Length <= maxLength
                ? normalized
                : normalized[..maxLength] + "...";
        }

        private static string VisualizeWhitespace(string value)
        {
            return value
                .Replace("\r\n", "↵")
                .Replace('\n', '↵')
                .Replace('\r', '↵')
                .Replace('\t', '→')
                .Replace(' ', '·');
        }

        private static string StripMarkdownDecorations(string value)
        {
            // Display-only: never show analyzer-injected stars in cards.
            return value
                .Replace("**", "", StringComparison.Ordinal)
                .Replace("__", "", StringComparison.Ordinal)
                .Replace("~~", "", StringComparison.Ordinal);
        }
    }
}
