using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WriteLite.Models;
using WriteLite.Resources;
using WriteLite.Services;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using RadioButton = System.Windows.Controls.RadioButton;
using Size = System.Windows.Size;

namespace WriteLite.Views;

public partial class SuggestionsWindow : Window
{
    private string? _pendingCopy;

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
            0 when snapshot.IndicatorState is AnalysisIndicatorState.Analyzing => Strings.Suggestions_HeaderAnalyzing,
            0 when !snapshot.IsFullDictionaryLoaded => Strings.Suggestions_HeaderDictionaryLoading,
            0 when !snapshot.Target.SupportsDirectWrite => Strings.Suggestions_HeaderReadOnly,
            0 => Strings.Suggestions_HeaderNoIssues,
            _ => RussianPlural.Issues(count)
        };
    }

    private void UpdateTabCounts(TextSnapshot snapshot)
    {
        var counts = IssueCountSummary.From(snapshot.Issues);
        TabAll.Content = string.Format(Strings.Filter_All_Count, counts.All);
        TabOrthography.Content = string.Format(Strings.Filter_Orthography_Count, counts.Orthography);
        TabPunctuation.Content = string.Format(Strings.Filter_Punctuation_Count, counts.Punctuation);
        TabGrammar.Content = string.Format(Strings.Filter_Grammar_Count, counts.Grammar);
        TabStyle.Content = string.Format(Strings.Filter_Style_Count, counts.Style);
    }

    /// <summary>
    /// Reports a correction that did not apply, without a modal dialog.
    /// </summary>
    /// <remarks>
    /// The panel's counterpart to the card's own failure row — §12. Used when the click came
    /// from the panel, or from a popup the user has since closed, so that a failure is always
    /// visible somewhere the user is already looking rather than in a window that steals the
    /// keyboard from the field being corrected.
    /// </remarks>
    public void ShowApplyFeedback(string message, string? replacementForCopy)
    {
        if (Dispatcher.InvokeIfNeeded(() => ShowApplyFeedback(message, replacementForCopy))) return;

        _pendingCopy = replacementForCopy;
        ApplyFeedbackText.Text = message;
        ApplyFeedbackCopy.Visibility = string.IsNullOrEmpty(replacementForCopy)
            ? Visibility.Collapsed
            : Visibility.Visible;
        ApplyFeedbackBanner.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
        IssuesScroller.Visibility = Visibility.Visible;
    }

    private void ApplyFeedbackCopy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_pendingCopy)) return;
        try
        {
            System.Windows.Clipboard.SetText(_pendingCopy);
            ApplyFeedbackText.Text = Strings.Suggestions_CopiedToClipboard;
            ApplyFeedbackCopy.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical("clipboard-copy-failed", exception);
        }
    }

    private void UpdateBanners(TextSnapshot snapshot)
    {
        // A fresh snapshot means the field moved on; a stale failure notice must not outlive it.
        ApplyFeedbackBanner.Visibility = Visibility.Collapsed;

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
            "Style" => issue.Category is IssueCategory.Style or IssueCategory.Readability,
            _ => true
        };
    }

    private void UpdateEmptyAndFooter()
    {
        var hasVisible = _visibleIssues.Count > 0;
        var hasAny = _allIssues.Count > 0;

        EmptyState.Visibility = !hasVisible && !_isBusy ? Visibility.Visible : Visibility.Collapsed;
        IssuesScroller.Visibility = hasVisible || ReadonlyBanner.Visibility == Visibility.Visible || DictBanner.Visibility == Visibility.Visible
                                    || ApplyFeedbackBanner.Visibility == Visibility.Visible
            ? Visibility.Visible
            : (hasAny ? Visibility.Visible : Visibility.Collapsed);

        if (!hasVisible && !_isBusy)
        {
            IssuesScroller.Visibility = ReadonlyBanner.Visibility == Visibility.Visible || DictBanner.Visibility == Visibility.Visible
                                        || ApplyFeedbackBanner.Visibility == Visibility.Visible
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
            ? string.Format(Strings.Suggestions_SafeCount, safeCount)
            : Strings.Suggestions_NoSafeAutoFixes;
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
        var animation = new DoubleAnimation(0, 360, WriteLiteDefaults.Motion.SpinnerRotationDuration)
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

            // The same test the popup card uses. A finding whose two sides render as one
            // fragment — emphasis markers stripped, a difference past the truncation cap —
            // has nothing to apply, however different the underlying strings are, and
            // «Проверяем → Проверяем» over an enabled «Исправить» is a promise the button
            // cannot keep. Both surfaces show the same findings, so both ask the same
            // question of them.
            HasChange = !CorrectionCardText.IsNoOpForDisplay(normalized);
            CanApply = HasChange
                       && (TextCorrectionService.CanApplyManual(normalized, currentText, supportsDirectWrite)
                           || TextCorrectionService.CanApply(normalized, currentText, supportsDirectWrite));

            // Shared with the in-app editor panel so the same issue reads identically
            // in both places.
            OriginalDisplay = CorrectionCardText.OriginalDisplay(normalized);
            CorrectedDisplay = CorrectionCardText.ReplacementDisplay(normalized);
            BriefLabel = string.IsNullOrWhiteSpace(normalized.Title)
                ? CorrectionCardText.CategoryLabel(normalized.Category)
                : normalized.Title;
            (CategoryBrush, CategoryBackground) = CorrectionCardText.CategoryBrushes(normalized.Category);
        }

        public TextIssue Issue { get; }
        public bool CanApply { get; }

        /// <summary>Whether this finding has a replacement the card can honestly draw.</summary>
        public bool HasChange { get; }

        public Visibility DictionaryVisibility =>
            Issue.Category == IssueCategory.Orthography ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ReplacementVisibility =>
            HasChange ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// §3: no apply action where there is no alternative to apply. A disabled one is not
        /// enough — a large primary button still says a correction is available here.
        /// </summary>
        public Visibility ApplyVisibility =>
            HasChange ? Visibility.Visible : Visibility.Collapsed;
        public string OriginalDisplay { get; }
        public string CorrectedDisplay { get; }
        public string BriefLabel { get; }
        public Brush CategoryBrush { get; }
        public Brush CategoryBackground { get; }

        public string CategoryLabel => CorrectionCardText.CategoryLabelUpper(Issue.Category);
    }
}
