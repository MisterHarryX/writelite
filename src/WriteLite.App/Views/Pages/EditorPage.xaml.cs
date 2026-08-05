using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Spelling;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace WriteLite.Views.Pages;

public partial class EditorPage : UserControl, IDisposable
{
    /// <summary>
    /// The editor's analyzer, constructed once per process on a worker thread.
    ///
    /// Constructing it inline used to freeze the app: LocalSpellChecker parses
    /// the Hunspell dictionaries (ru_RU.dic is 3.5 MB) in its constructor, and
    /// EditorPage is a field initializer of MainWindow — so opening the main
    /// window parked the dispatcher for however long the parse took. On a
    /// machine under disk pressure that exceeded Windows' 5-second ghost-window
    /// threshold and the OS recorded AppHangB1 ("WriteLite не отвечает").
    ///
    /// Static on purpose: a second EditorPage must reuse the parsed lexicons,
    /// not spend another parse.
    /// </summary>
    private static readonly Lazy<Task<ITextAnalyzer>> SharedAnalyzer = new(
        static () => Task.Run(static () => (ITextAnalyzer)new CompositeTextAnalyzer(
            new RuleBasedAnalyzer(),
            new SpellTextAnalyzer(new LocalSpellChecker()))),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly IssueRenderingPipeline _pipeline = new();
    private readonly HashSet<string> _ignored = new(StringComparer.Ordinal);
    private readonly InputLatencyMetrics _inputLatency = new();
    private CancellationTokenSource? _analysisCancellation;
    private int _textVersion;
    private bool _internalEdit;
    private string _lastAnalyzedText = string.Empty;
    private IReadOnlyList<TextIssue> _lastAnalyzedIssues = [];

    public EditorPage()
    {
        InitializeComponent();
        // Warm the analyzer without waiting for it: the page (and MainWindow,
        // whose field initializer constructs this page) must render instantly.
        _ = SharedAnalyzer.Value;
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => RefreshPresentation();
    }

    public ObservableCollection<EditorSuggestion> Issues { get; } = [];

    private void EditorTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_internalEdit) return;
        QueueAnalysis(TimeSpan.FromMilliseconds(180), fullDocument: false);
    }

    private void QueueAnalysis(TimeSpan delay, bool fullDocument)
    {
        InputSignalScheduler.Signal(
            ref _analysisCancellation,
            ref _textVersion,
            _inputLatency,
            (version, token) => _ = RunScheduledAnalysisAsync(version, delay, fullDocument, token));
    }

    private Task ScheduleAnalysisAsync(TimeSpan delay)
    {
        QueueAnalysis(delay, fullDocument: true);
        return Task.CompletedTask;
    }

    private async Task RunScheduledAnalysisAsync(int version, TimeSpan delay, bool fullDocument, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var text = EditorTextBox.Text;
            var previousText = _lastAnalyzedText;
            var previousIssues = _lastAnalyzedIssues;

            if (string.IsNullOrWhiteSpace(text))
            {
                Issues.Clear();
                _lastAnalyzedText = string.Empty;
                _lastAnalyzedIssues = [];
                UpdateCounters(0);
                RefreshPresentation();
                return;
            }

            // First analysis after process start may wait for the dictionary
            // parse; the await keeps that wait off the dispatcher.
            var analyzer = await SharedAnalyzer.Value;
            cancellationToken.ThrowIfCancellationRequested();

            var result = await Task.Run(
                () => AnalyzeChangedRange(analyzer, previousText, text, previousIssues, fullDocument, cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (version != _textVersion || !string.Equals(text, EditorTextBox.Text, StringComparison.Ordinal)) return;

            _lastAnalyzedText = text;
            _lastAnalyzedIssues = result.Issues;
            ApplyDiff(result.Issues);
            UpdateCounters(result.WordCount);
            var latency = _inputLatency.Snapshot();
            if (latency.Count > 0 && latency.Count % 64 == 0)
            {
                CompatibilityLogger.Technical("editor-input-latency",
                    $"samples={latency.Count} p50={latency.P50Milliseconds:F2} p95={latency.P95Milliseconds:F2} max={latency.MaxMilliseconds:F2}");
            }
            RefreshPresentation();
        }
        catch (OperationCanceledException)
        {
            // A newer text version owns the UI now.
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical("editor-analysis-failed", $"type={exception.GetType().Name}");
            Issues.Clear();
            RefreshPresentation();
        }
    }

    private AnalysisBatch AnalyzeChangedRange(
        ITextAnalyzer analyzer,
        string previousText,
        string text,
        IReadOnlyList<TextIssue> previousIssues,
        bool fullDocument,
        CancellationToken cancellationToken)
    {
        var plan = DirtyIssueAnalysis.Plan(previousText, text, fullDocument);
        var analyzed = _pipeline.Filter(plan.Segment, analyzer.Analyze(plan.Segment))
            .Where(issue => !_ignored.Contains(IgnoreKey(issue)))
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var issues = DirtyIssueAnalysis.Merge(text, previousIssues, analyzed, plan);
        return new AnalysisBatch(issues, CountWords(text));
    }

    private void ApplyDiff(IReadOnlyList<TextIssue> next)
    {
        var desired = next.Select(issue => new EditorSuggestion(issue)).ToArray();
        var desiredKeys = desired.Select(item => item.Identity).ToHashSet(StringComparer.Ordinal);
        for (var i = Issues.Count - 1; i >= 0; i--)
        {
            if (!desiredKeys.Contains(Issues[i].Identity)) Issues.RemoveAt(i);
        }

        for (var index = 0; index < desired.Length; index++)
        {
            var existingIndex = -1;
            for (var current = index; current < Issues.Count; current++)
            {
                if (Issues[current].Identity == desired[index].Identity) { existingIndex = current; break; }
            }
            if (existingIndex < 0) Issues.Insert(Math.Min(index, Issues.Count), desired[index]);
            else if (existingIndex != index) Issues.Move(existingIndex, index);
        }
    }

    private void IssuesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IssuesList.SelectedItem is not EditorSuggestion suggestion) return;
        var issue = suggestion.Issue;
        if (!IsCurrent(issue)) return;
        EditorTextBox.Focus();
        EditorTextBox.Select(issue.Start, issue.Length);
    }

    private async void ApplyIssue_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EditorSuggestion suggestion) return;
        if (!TryApply(suggestion.Issue)) return;
        await ScheduleAnalysisAsync(TimeSpan.FromMilliseconds(80));
    }

    private void IgnoreIssue_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EditorSuggestion suggestion) return;
        _ignored.Add(IgnoreKey(suggestion.Issue));
        Issues.Remove(suggestion);
        RefreshPresentation();
    }

    private async void ApplySafe_Click(object sender, RoutedEventArgs e)
    {
        var safe = Issues.Select(item => item.Issue)
            .Where(issue => issue.CanApplyAutomatically && issue.Confidence >= 0.85 && issue.Replacement is not null)
            .OrderByDescending(issue => issue.Start)
            .ToArray();
        foreach (var issue in safe) TryApply(issue);
        await ScheduleAnalysisAsync(TimeSpan.FromMilliseconds(80));
    }

    private bool TryApply(TextIssue issue)
    {
        issue = Services.CorrectionPresentation.NormalizeForApply(issue);
        if (!IsCurrent(issue) || issue.Replacement is null) return false;
        var text = EditorTextBox.Text;
        var resolved = Services.CorrectionApplicationService.ResolveRange(text, issue);
        if (!resolved.Ok) return false;
        _internalEdit = true;
        try
        {
            EditorTextBox.Text = text.Remove(resolved.Start, resolved.Length).Insert(resolved.Start, issue.Replacement);
            EditorTextBox.CaretIndex = issue.Start + issue.Replacement.Length;
            _textVersion++;
            return true;
        }
        finally
        {
            _internalEdit = false;
        }
    }

    private bool IsCurrent(TextIssue issue)
    {
        var text = EditorTextBox.Text;
        return issue.Start >= 0 && issue.Length >= 0 && issue.Start <= text.Length - issue.Length
            && string.Equals(text.Substring(issue.Start, issue.Length), issue.Original, StringComparison.Ordinal);
    }

    private async void Recheck_Click(object sender, RoutedEventArgs e) =>
        await ScheduleAnalysisAsync(TimeSpan.Zero);

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _ignored.Clear();
        EditorTextBox.Clear();
        EditorTextBox.Focus();
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        e.Handled = true;
        await ScheduleAnalysisAsync(TimeSpan.Zero);
    }

    private static int CountWords(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private void UpdateCounters(int words)
    {
        WordCountText.Text = words switch { 1 => "1 слово", >= 2 and <= 4 => $"{words} слова", _ => $"{words} слов" };
    }

    private void RefreshPresentation()
    {
        var count = Issues.Count;
        IssueBadge.Text = count.ToString();
        IssueCountText.Text = count == 0 ? "Замечаний нет" : $"Найдено: {count}";
        EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        IssuesList.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ApplySafeButton.IsEnabled = Issues.Any(item => item.Issue.CanApplyAutomatically && item.Issue.Confidence >= 0.85 && item.Issue.Replacement is not null);
    }

    private static string IgnoreKey(TextIssue issue) => $"{issue.RuleId}:{issue.Start}:{issue.Length}:{issue.Original}";

    public void Dispose()
    {
        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _analysisCancellation = null;
    }
}

internal readonly record struct AnalysisBatch(IReadOnlyList<TextIssue> Issues, int WordCount);

public sealed class EditorSuggestion
{
    public EditorSuggestion(TextIssue issue) => Issue = issue;
    public TextIssue Issue { get; }
    public string Identity => $"{Issue.RuleId}:{Issue.Start}:{Issue.Length}:{Issue.Original}:{Issue.Replacement}";
    public string CategoryLabel => Issue.Category switch
    {
        IssueCategory.Orthography => "Орфография",
        IssueCategory.Grammar => "Грамматика",
        IssueCategory.Punctuation => "Пунктуация",
        IssueCategory.Style => "Стиль",
        _ => "Читаемость"
    };
    public string ChangeLabel => string.IsNullOrEmpty(Issue.Replacement)
        ? Issue.Original
        : $"{Issue.Original} → {Issue.Replacement}";
    public string Explanation => Issue.Explanation;
    public string SourceLabel => Issue.RuleId.StartsWith("ru.spelling", StringComparison.Ordinal) || Issue.RuleId.StartsWith("en.spelling", StringComparison.Ordinal)
        ? "Словарь"
        : "Правила";
    public Brush CategoryBrush => Issue.Category switch
    {
        IssueCategory.Orthography => ThemeResource.Brush("WlCatSpelling", Brushes.IndianRed),
        IssueCategory.Grammar => ThemeResource.Brush("WlCatGrammar", Brushes.Goldenrod),
        IssueCategory.Punctuation => ThemeResource.Brush("WlCatPunctuation", Brushes.DarkOrange),
        _ => ThemeResource.Brush("WlCatStyle", Brushes.SlateBlue)
    };
}
