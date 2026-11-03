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
        // The panel sits over someone else's text field and must never take its caret: the
        // card the underline opens has always worked this way, and the panel did not, which is
        // why fixing from the indicator was the slow path — the field lost focus on the first
        // click and every write had to fight to get it back.
        NoActivateWindow.Attach(this);
        _ready = true;
        UpdateEmptyAndFooter();
        CompatibilityLogger.Technical("correction-popup-created", "instance=1");
    }

    public event Func<TextIssue, Task<bool>>? ApplyIssueRequested;
    public event Func<Task>? ApplyAllRequested;
    public event EventHandler? PanelHidden;
    public event EventHandler? OpenMainWindowRequested;
    public event Action<TextIssue>? AddToDictionaryRequested;
    public event Action<TextIssue>? IgnoreIssueRequested;

    /// <summary>Texts a fix from this panel has already replaced — any snapshot of them is stale.</summary>
    private readonly HashSet<string> _supersededTexts = new(StringComparer.Ordinal);

    public void ShowSnapshot(TextSnapshot snapshot)
    {
        // The monitor republishes its last snapshot when interaction state changes, and right
        // after a fix that snapshot still holds the text before it — with the fixed word in it.
        // Taking it would put the row the user just fixed back under their pointer.
        if (IsVisible && _supersededTexts.Contains(snapshot.Text))
        {
            CompatibilityLogger.Technical("panel-snapshot-skipped", "reason=superseded");
            return;
        }

        // While the field is being re-checked the monitor publishes the new text with no findings
        // yet. Emptying the list for those few hundred milliseconds made every row blink out and
        // back after each fix; the rows stay until the check has an answer.
        if (IsVisible && snapshot.Issues.Count == 0 && _allIssues.Count > 0
            && snapshot.IndicatorState is AnalysisIndicatorState.FastAnalyzing
                or AnalysisIndicatorState.DeepAnalyzing
                or AnalysisIndicatorState.Analyzing
                or AnalysisIndicatorState.Typing)
        {
            return;
        }

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
        if (AvoidBounds is { } avoid && !avoid.IsEmpty) field = Rect.Union(field, avoid);
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
        _supersededTexts.Clear();
        CompatibilityLogger.Technical("suggestions-panel-hidden", "instance=retained");
        PanelHidden?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Brings the rows in line with a snapshot without rebuilding the ones that did not change.
    /// </summary>
    /// <remarks>
    /// The panel now follows the field while it is open, so snapshots arrive every time the
    /// user types or a fix lands. Clearing and refilling the list on each one made every row
    /// flicker and re-created the button under the user's pointer mid-click.
    /// </remarks>
    private void ReconcileIssues(TextSnapshot snapshot)
    {
        // Rows are keyed by what the user sees — the words, the fix, the place — not by the
        // whole finding: the fast and the deep lane describe the same mistake with slightly
        // different explanations, and treating those as different rows re-created the row
        // (and its button) under the user's pointer on every lane switch.
        var wanted = snapshot.Issues
            .Select(CorrectionPresentation.NormalizeForApply)
            .ToList();
        var wantedKeys = wanted.Select(RowKey).ToHashSet();
        for (var i = _allIssues.Count - 1; i >= 0; i--)
        {
            if (!wantedKeys.Contains(RowKey(_allIssues[i].Issue))) _allIssues.RemoveAt(i);
        }

        for (var index = 0; index < wanted.Count; index++)
        {
            var key = RowKey(wanted[index]);
            var existing = -1;
            for (var j = 0; j < _allIssues.Count; j++)
            {
                if (RowKey(_allIssues[j].Issue) == key) { existing = j; break; }
            }

            if (existing < 0)
            {
                _allIssues.Insert(Math.Min(index, _allIssues.Count),
                    new IssueCard(wanted[index], snapshot.Text, snapshot.Target.SupportsDirectWrite));
            }
            else if (existing != index && index < _allIssues.Count)
            {
                _allIssues.Move(existing, index);
            }
        }
    }

    private static (int, int, string, string, IssueCategory) RowKey(TextIssue issue) =>
        (issue.Start, issue.Length, issue.Original, issue.Replacement ?? string.Empty, issue.Category);

    /// <summary>Rect (in DIPs) the panel must not cover — the indicator with its pen.</summary>
    public Rect? AvoidBounds { get; set; }

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
        var desired = _allIssues.Where(card => MatchesTab(card.Issue, _tabFilter)).ToList();
        for (var i = _visibleIssues.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(_visibleIssues[i])) _visibleIssues.RemoveAt(i);
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var at = _visibleIssues.IndexOf(desired[i]);
            if (at < 0) _visibleIssues.Insert(i, desired[i]);
            else if (at != i) _visibleIssues.Move(at, i);
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

    private readonly SemaphoreSlim _applyQueue = new(1, 1);

    /// <summary>
    /// Fixes one row without freezing the others.
    /// </summary>
    /// <remarks>
    /// Clicks queue: the user can press «Исправить» on three rows in a row and they are written
    /// one after another, each against the text the previous one left. The row disappears as
    /// soon as its write is verified, and the panel closes itself once nothing is left.
    /// </remarks>
    private async void ApplyIssueButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TextIssue issue } button || ApplyIssueRequested is null)
        {
            return;
        }

        button.IsEnabled = false;
        button.Content = Strings.Suggestions_Applying;
        var textBefore = _latestSnapshot?.Text;
        var succeeded = false;
        await _applyQueue.WaitAsync();
        try
        {
            succeeded = await ApplyIssueRequested.Invoke(issue);
        }
        catch (Exception ex)
        {
            CompatibilityLogger.Technical("panel-apply-failed", $"type={ex.GetType().Name}");
        }
        finally
        {
            _applyQueue.Release();
        }

        if (!succeeded)
        {
            button.IsEnabled = true;
            button.Content = Strings.Common_Fix;
            return;
        }

        if (textBefore is not null) _supersededTexts.Add(textBefore);
        // The row may have moved since the click (an earlier fix shifted the text); match by
        // what it says and take the nearest.
        var card = _allIssues
            .Where(c => c.Issue.Original == issue.Original && c.Issue.Replacement == issue.Replacement
                        && c.Issue.Category == issue.Category)
            .OrderBy(c => Math.Abs(c.Issue.Start - issue.Start))
            .FirstOrDefault();
        if (card is not null)
        {
            _allIssues.Remove(card);
            _visibleIssues.Remove(card);
        }

        CompatibilityLogger.Technical("panel-row-removed",
            $"found={(card is null ? 0 : 1)} remaining={_allIssues.Count} visible={_visibleIssues.Count} superseded={_supersededTexts.Count}");
        UpdateEmptyAndFooter();
        if (_allIssues.Count == 0)
        {
            await Task.Delay(700);
            if (_allIssues.Count == 0) Hide();
        }
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
