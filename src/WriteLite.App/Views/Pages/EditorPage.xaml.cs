using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WriteLite.Controls;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Ai;
using WriteLite.Services.Diagnostics;
using WriteLite.Services.Documents;
using WriteLite.Services.Lexical;
using WriteLite.Services.Spelling;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

using WriteLite.Services.Settings;

namespace WriteLite.Views.Pages;

/// <summary>
/// The document workspace: open, edit, format, analyse, save.
/// </summary>
/// <remarks>
/// The page is split across four files by concern rather than kept as one:
/// this one owns the editing surface and the analysis loop,
/// <c>EditorPage.Documents.cs</c> owns files, <c>EditorPage.Formatting.cs</c> owns
/// the toolbar and <c>EditorPage.Ai.cs</c> owns the word panel and the AI actions.
///
/// ── Why the text index exists ───────────────────────────────────────────────
///
/// The editor moved from a <c>TextBox</c> to a <c>RichTextBox</c>, but the entire
/// language stack — spelling, grammar, punctuation, the ignore list, the correction
/// applier — speaks in <c>(start, length)</c> over a flat string. Rather than
/// rewrite that layer for <see cref="TextPointer"/>, every analysis pass projects
/// the document to a flat string and keeps a
/// <see cref="Services.Documents.DocumentTextIndex"/> to map the answers back.
///
/// ── Why nothing blocks ──────────────────────────────────────────────────────
///
/// Only two things happen on the dispatcher: building the index, and applying the
/// result. Parsing, analysis, import, export and conversion all run on worker
/// threads with a cancellation token, and every one of them is cancelled when a
/// newer request arrives.
/// </remarks>
public partial class EditorPage : UserControl, IDisposable
{
    /// <summary>
    /// The analyzer a page uses when the shell has not given it one.
    /// </summary>
    /// <remarks>
    /// <para><b>A fallback, not the product's checker.</b> It is rules and spelling over a
    /// private <see cref="LocalSpellChecker"/> and nothing else: no form index, so the
    /// case-government and vocative rules stand down; no language engine; no local model. It
    /// exists so that an <see cref="EditorPage"/> hosted without a shell — a render smoke
    /// test, a preview host — still underlines something rather than throwing. The real
    /// editor is given the application's analyzer through <see cref="BindAnalyzer"/>, and
    /// until Phase 7.6 it was not, which is why a document full of errors could reach the
    /// sidebar as a clean document.</para>
    ///
    /// <para>Constructed on a worker thread and only on first use. Inline it froze the app:
    /// LocalSpellChecker parses the Hunspell dictionaries (ru_RU.dic is 3.5 MB) in its
    /// constructor, and EditorPage is a field initializer of MainWindow — so opening the main
    /// window parked the dispatcher for however long the parse took. On a machine under disk
    /// pressure that exceeded Windows' 5-second ghost-window threshold and the OS recorded
    /// AppHangB1 ("WriteLite не отвечает"). It is no longer warmed from the constructor
    /// either: the shell binds its analyzer moments later, and warming this one anyway spent
    /// a second 3.5 MB parse and a second form index on a stack nothing would consult.</para>
    ///
    /// <para>Static on purpose: a second unbound EditorPage must reuse the parsed lexicons,
    /// not spend another parse.</para>
    /// </remarks>
    private static readonly Lazy<Task<ITextAnalyzer>> SharedAnalyzer = new(
        // Contextual refinement off for the same reason as the shell's fast analyzer:
        // this one backs the editor's own as-you-type pass.
        static () => Task.Run(static () => (ITextAnalyzer)new CompositeTextAnalyzer(
            new RuleBasedAnalyzer(),
            new SpellTextAnalyzer(new LocalSpellChecker()) { ContextualRefinementEnabled = false })),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Below this width the side panel becomes a toggle instead of a column.</summary>
    private const double PanelCollapseThreshold = 1080;

    /// <summary>Below this width the toolbar sheds its least-used groups.</summary>
    private const double CompactToolbarThreshold = 860;

    /// <summary>Auto-correction only touches issues WriteLite is near-certain about.</summary>
    private const double AutoCorrectConfidence = 0.95;

    private const double SafeApplyConfidence = 0.85;

    /// <summary>
    /// Live analysis stops above this many characters.
    /// </summary>
    /// <remarks>
    /// Re-analysing a 300-page import on every keystroke would be pointless work:
    /// the panel can only show so much, and the index rebuild alone would start to
    /// show up as input latency. Above the ceiling analysis runs on demand, which
    /// the status bar says plainly.
    /// </remarks>
    private const int LiveAnalysisCharacterCeiling = 120_000;

    private readonly IssueRenderingPipeline _pipeline = new();
    private readonly HashSet<string> _ignored = new(StringComparer.Ordinal);
    private readonly InputLatencyMetrics _inputLatency = new();

    /// <summary>Corrections already accepted, so a slow lane cannot offer one back.</summary>
    private readonly AppliedCorrectionLedger _applied = new();

    /// <summary>Apply-path timings, reported as p50/p95 rather than as anecdotes.</summary>
    private readonly InputLatencyMetrics _applyLatency = new();

    /// <summary>
    /// The application's analyzer, once the shell has handed it over.
    /// </summary>
    /// <remarks>
    /// Null until <see cref="BindAnalyzer"/> is called, and null forever in a host that has no
    /// shell. Every check reads it fresh rather than caching a resolved analyzer, so a page
    /// that runs a check during startup picks up the real stack on its next pass instead of
    /// being stuck with the fallback for the lifetime of the process.
    /// </remarks>
    private ITextAnalyzer? _analyzer;

    /// <summary>
    /// The native deterministic lane: rules and spelling over the shared form index.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="_analyzer"/> because the two answer on different timescales.
    /// Measured on a 480-word document with the shipping stack: this lane produces 19 findings
    /// in 100–290 ms, the full stack produces 22–24 in 1.2–2.7 s, and the model lane pushes
    /// the single task that carried all of them to 53 s. Binding only the full stack meant the
    /// sidebar was empty for the whole 53 s. Null in a host that has not bound one, in which
    /// case the full analyzer publishes alone exactly as it used to.
    /// </remarks>
    private ITextAnalyzer? _fastAnalyzer;

    /// <summary>Whether the document on screen came back from a recovery snapshot.</summary>
    private bool _restoredDocument;

    private DocumentIssueAdorner? _underlines;
    private CancellationTokenSource? _analysisCancellation;
    private DocumentTextIndex? _index;
    private int _textVersion;
    private int _documentGeneration;
    private bool _internalEdit;
    private bool _panelCollapsed;
    private bool _panelForcedOpen;
    private string _lastAnalyzedText = string.Empty;
    private IReadOnlyList<TextIssue> _lastAnalyzedIssues = [];
    private IReadOnlyList<TextIssue> _allIssues = [];
    private IssueCategory? _filter;

    public EditorPage()
    {
        InitializeComponent();

        IssuesList.ItemsSource = Issues;
        InitialiseFormattingControls();
        InitialiseDocumentState();

        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += OnLoaded;
    }

    public ObservableCollection<EditorSuggestion> Issues { get; } = [];

    /// <summary>Measured click→applied latency for the apply path. Diagnostics and tests.</summary>
    internal InputLatencySnapshot ApplyLatency => _applyLatency.Snapshot();

    /// <summary>Every finding currently held, filtered or not. Tests and the underline layer.</summary>
    internal IReadOnlyList<TextIssue> AllIssues => _allIssues;

    /// <summary>Publishes a lane's result as if an analyzer had produced it. Tests only.</summary>
    /// <remarks>
    /// Builds the index first, because that is the order the real loop uses: the index is
    /// built on the dispatcher and every offset an analyzer returns is interpreted against it.
    /// A publication without one would be a state the product never reaches.
    /// </remarks>
    internal void PublishForTest(AnalysisLane lane, IReadOnlyList<TextIssue> issues, string text)
    {
        _index = DocumentTextIndex.Build(Editor.Document, _documentGeneration);
        PublishLaneCore(lane, issues, _textVersion, _documentGeneration, text, 0);
    }

    /// <summary>
    /// Publishes a result computed against an older revision. Tests only: this is the shape
    /// of a slow lane finishing after the user has already applied something.
    /// </summary>
    internal void PublishStaleForTest(
        AnalysisLane lane, IReadOnlyList<TextIssue> issues, string text, int version, int generation)
        => PublishLaneCore(lane, issues, version, generation, text, 0);

    /// <summary>The revision counters a stale-result test needs to capture before applying.</summary>
    internal (int Version, int Generation) Revision => (_textVersion, _documentGeneration);

    /// <summary>Raised when the page wants the shell to open another section.</summary>
    public event Action<string>? NavigationRequested;

    /// <summary>
    /// Raised when a word selected in the editor should open in the dictionary.
    /// </summary>
    /// <remarks>
    /// The page does not navigate itself: the shell owns the rail, and routing every
    /// lookup through <c>MainWindow.OpenWordInDictionary</c> is what keeps the editor,
    /// the reader and the notes board on one flow instead of three.
    /// </remarks>
    public event Action<string>? WordNavigationRequested;

    public void Dispose()
    {
        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _analysisCancellation = null;
        DisposeDocumentState();
        GC.SuppressFinalize(this);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The adorner layer only exists once the page is in a visual tree.
        if (_underlines is null && AdornerLayer.GetAdornerLayer(Editor) is { } layer)
        {
            _underlines = new DocumentIssueAdorner(Editor, ResolveIssueRange);
            layer.Add(_underlines);
        }

        EnsureEmptyDocument();
        RefreshPresentation();
        UpdateFormattingState();
    }

    // ── Typography ───────────────────────────────────────────────────────────

    /// <summary>
    /// The editor's own defaults, resolved from the theme rather than hard-coded.
    /// </summary>
    /// <remarks>
    /// Read on demand instead of cached: a page constructed before the theme is
    /// merged would otherwise capture WPF's fallbacks permanently, which is exactly
    /// what happens in the render smoke tests.
    /// </remarks>
    private EditorTypography Typography => new(
        TryFindResource("WlFont") as FontFamily ?? new FontFamily("Segoe UI"),
        12,
        TryFindResource("WlText") as Brush,
        TryFindResource("WlLine") as Brush ?? Brushes.Gray,
        TryFindResource("WlBrand") as Brush ?? Brushes.OrangeRed);

    // ── Analysis ─────────────────────────────────────────────────────────────

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePlaceholder();

        if (_internalEdit)
        {
            return;
        }

        // Every edit invalidates every position the previous index handed out.
        _documentGeneration++;
        MarkDirty();
        HideUndo();
        QueueAnalysis(TimeSpan.FromMilliseconds(220), fullDocument: false);

        // Continued typing supersedes any prepared continuation, then schedules the next one
        // behind a much longer pause. Errors are never waiting on this.
        DismissWritingSuggestion();
        QueueWritingSuggestion();
    }

    /// <summary>
    /// The shortcuts this page obeys. Replaced when the settings page changes them.
    /// </summary>
    /// <remarks>
    /// Defaults until the shell binds the configured set, so a page constructed in a test or
    /// before startup finishes still responds to Ctrl+S rather than to nothing.
    /// </remarks>
    private ShortcutRegistry _shortcuts = new();

    /// <summary>Gives the editor the shortcut bindings the reader configured.</summary>
    public void BindShortcuts(ShortcutRegistry shortcuts) => _shortcuts = shortcuts;

    private void UpdatePlaceholder()
    {
        var empty = Editor.Document.Blocks.Count <= 1
                    && new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).IsEmpty;
        CanvasPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
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

    private async Task RunScheduledAnalysisAsync(
        int version,
        TimeSpan delay,
        bool fullDocument,
        CancellationToken cancellationToken)
    {
        using var scope = CheckPipelineTracing.Begin(out var trace);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await Task.Delay(delay, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // Index construction touches TextPointer, so it belongs on the dispatcher
            // and nowhere else. Everything after this line works on a flat string.
            var generation = _documentGeneration;
            var index = DocumentTextIndex.Build(Editor.Document, generation);
            _index = index;
            var text = index.Text;

            if (trace is not null)
            {
                trace.DocumentId = _workspace.CurrentPath ?? _workspace.DisplayName;
                trace.DocumentKind = _workspace.CurrentFormat.ToString();
                trace.IsRestoredDocument = _restoredDocument;
                trace.EditorTextLength = text.Length;
            }

            UpdateCounters(text);

            if (string.IsNullOrWhiteSpace(text))
            {
                if (trace is not null)
                {
                    trace.CheckAbortReason = "empty-document";
                }

                _allIssues = [];
                Issues.Clear();
                _lastAnalyzedText = string.Empty;
                _lastAnalyzedIssues = [];
                RefreshPresentation();
                return;
            }

            if (text.Length > LiveAnalysisCharacterCeiling && !fullDocument)
            {
                if (trace is not null)
                {
                    trace.CheckAbortReason = "above-live-analysis-ceiling";
                }

                Controls.Type.SetTracked(StatusText, "БОЛЬШОЙ ДОКУМЕНТ · CTRL+ENTER");
                return;
            }

            var previousText = _lastAnalyzedText;
            var previousIssues = _lastAnalyzedIssues;

            _answerSettled = false;
            SetAnalyzing(true);
            _applied.Prune(text);

            // The shell's analyzer when there is one, otherwise the page's own. First
            // analysis after process start may wait for the dictionary parse; the await
            // keeps that wait off the dispatcher.
            var analyzer = _analyzer ?? await SharedAnalyzer.Value;
            cancellationToken.ThrowIfCancellationRequested();

            // The fast lane, when the shell bound one, answers first and alone. It is the
            // native deterministic engine and nothing else — no language engine, no ONNX
            // session, no model — so it lands in about the time one debounce interval costs
            // and the reader has underlines before the wider stack has started merging.
            var fastLane = _fastAnalyzer;
            if (fastLane is not null && !ReferenceEquals(fastLane, analyzer))
            {
                var fast = await AnalyzeChangedRangeAsync(
                    fastLane, previousText, text, previousIssues, fullDocument, trace: null, cancellationToken);
                PublishLane(AnalysisLane.Fast, fast.Issues, version, generation, text, stopwatch);
            }

            // §50: the deep lane spends its inference budget outward from the caret, so the
            // sentence the reader is in is answered before a distant page. The field monitor
            // leaves this at zero, where its single field's only sentence already is.
            if (analyzer is Services.Ai.HybridTextAnalysisService hybrid)
            {
                var caretOffset = index.OffsetOf(Editor.CaretPosition);
                if (caretOffset >= 0) hybrid.SetPriorityOffset(caretOffset);
            }

            if (trace is not null)
            {
                trace.CheckStarted = true;
                trace.AnalyzerKind = analyzer.GetType().Name
                                     + (_analyzer is null ? " (page fallback)" : " (shell)");
            }

            var result = await AnalyzeChangedRangeAsync(
                analyzer, previousText, text, previousIssues, fullDocument, trace, cancellationToken,
                onLane: (lane, issues) => PublishLane(lane, issues, version, generation, text, stopwatch));

            cancellationToken.ThrowIfCancellationRequested();

            // A keystroke landed while the worker was running: its answers describe
            // a document that no longer exists.
            if (version != _textVersion || generation != _documentGeneration)
            {
                if (trace is not null)
                {
                    trace.CheckAbortReason = "superseded-by-newer-edit";
                }

                return;
            }

            PublishLane(AnalysisLane.Deep, result.Issues, version, generation, text, stopwatch);

            if (trace is not null)
            {
                trace.DeliveredToViewModel = _allIssues.Count;
                trace.RenderedInSidebar = Issues.Count;
            }

            var latency = _inputLatency.Snapshot();
            if (latency.Count > 0 && latency.Count % 64 == 0)
            {
                CompatibilityLogger.Technical("editor-input-latency",
                    $"samples={latency.Count} p50={latency.P50Milliseconds:F2} p95={latency.P95Milliseconds:F2} p99={latency.P99Milliseconds:F2} max={latency.MaxMilliseconds:F2}");
            }

            RefreshPresentation();
            TryAutoCorrect();
        }
        catch (OperationCanceledException)
        {
            // A newer text version owns the UI now.
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical("editor-analysis-failed", $"type={exception.GetType().Name}");
            if (trace is not null)
            {
                trace.CheckAbortReason = $"exception:{exception.GetType().Name}";
            }

            _allIssues = [];
            Issues.Clear();
            RefreshPresentation();
        }
        finally
        {
            SetAnalyzing(false);
        }
    }

    /// <summary>
    /// Runs the analyzer over the changed region and merges the answer back.
    /// </summary>
    /// <remarks>
    /// Awaited rather than run through the synchronous <see cref="ITextAnalyzer.Analyze"/>
    /// overload. The shell's analyzer is the hybrid stack, whose synchronous entry point is
    /// <c>AnalyzeAsync().GetAwaiter().GetResult()</c> — blocking a pool thread for the whole
    /// LanguageTool round trip and the model inference behind it. The work itself already
    /// runs off the dispatcher because every layer below is genuinely asynchronous.
    /// </remarks>
    private async Task<AnalysisBatch> AnalyzeChangedRangeAsync(
        ITextAnalyzer analyzer,
        string previousText,
        string text,
        IReadOnlyList<TextIssue> previousIssues,
        bool fullDocument,
        CheckPipelineTrace? trace,
        CancellationToken cancellationToken,
        Action<AnalysisLane, IReadOnlyList<TextIssue>>? onLane = null)
    {
        var plan = DirtyIssueAnalysis.Plan(previousText, text, fullDocument);

        if (trace is not null)
        {
            trace.AnalyzedSegmentLength = plan.Segment.Length;
        }

        // ConfigureAwait(false) so the filtering and merging below stay off the dispatcher,
        // as they did when this ran inside Task.Run. The caller's own await has no
        // ConfigureAwait, so the UI update that follows still resumes on the dispatcher.
        IReadOnlyList<TextIssue> raw;
        if (onLane is not null && analyzer is IStagedTextAnalyzer staged)
        {
            // Each intermediate lane is finished through the same segment mapping and merge
            // as the final one, so what the sidebar shows early is the same shape as what it
            // ends up with — not a preview in different coordinates.
            raw = await staged
                .AnalyzeStagedAsync(
                    plan.Segment,
                    (lane, issues) => onLane(lane, FinishSegment(text, previousIssues, issues, plan)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            raw = await analyzer.AnalyzeAsync(plan.Segment, cancellationToken).ConfigureAwait(false);
        }

        var analyzed = _pipeline.Filter(plan.Segment, raw)
            .Where(issue => !_ignored.Contains(IgnoreKey(issue)))
            .ToArray();

        if (trace is not null)
        {
            // The editor's own filter is the last deduplication before the view model, so
            // this is the honest place to record the post-deduplication count whichever
            // analyzer ran.
            trace.MergedAfterDeduplication = analyzed.Length;

            if (!trace.DeterministicReported)
            {
                // An analyzer with no AI stage: everything it found is deterministic.
                trace.DeterministicFindings = raw.Count;
            }

            if (raw.Count != analyzed.Length)
            {
                trace.Note($"editor-pipeline-dropped: {raw.Count - analyzed.Length} of {raw.Count}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var issues = DirtyIssueAnalysis.Merge(text, previousIssues, analyzed, plan);
        return new AnalysisBatch(issues, CountWords(text));
    }

    /// <summary>
    /// Takes a lane's segment-local findings through the same filtering and merge the final
    /// answer goes through, so an early publication is a smaller answer and not a different one.
    /// </summary>
    private IReadOnlyList<TextIssue> FinishSegment(
        string text,
        IReadOnlyList<TextIssue> previousIssues,
        IReadOnlyList<TextIssue> segmentIssues,
        DirtyAnalysisPlan plan)
    {
        var analyzed = _pipeline.Filter(plan.Segment, segmentIssues)
            .Where(issue => !_ignored.Contains(IgnoreKey(issue)))
            .ToArray();
        return DirtyIssueAnalysis.Merge(text, previousIssues, analyzed, plan);
    }

    /// <summary>
    /// Puts one lane's answer on screen, if the document it describes is still the document.
    /// </summary>
    /// <remarks>
    /// <para>Every publication is guarded by both counters. <c>_textVersion</c> moves on every
    /// scheduled analysis and <c>_documentGeneration</c> on every mutation, so a lane that
    /// started before an edit — a model call that takes 50 s, say — cannot write its answer
    /// over a newer one. This is the mechanism that makes a stale AI result harmless rather
    /// than merely unlikely.</para>
    ///
    /// <para>The ledger is consulted after the revision guard, not instead of it: it catches
    /// the narrower case of a lane that recomputes a correction the user has already accepted
    /// from a segment that still matches.</para>
    /// </remarks>
    private void PublishLane(
        AnalysisLane lane,
        IReadOnlyList<TextIssue> issues,
        int version,
        int generation,
        string text,
        System.Diagnostics.Stopwatch stopwatch)
    {
        if (!Dispatcher.CheckAccess())
        {
            var elapsed = stopwatch.ElapsedMilliseconds;
            Dispatcher.BeginInvoke(() => PublishLaneCore(lane, issues, version, generation, text, elapsed));
            return;
        }

        PublishLaneCore(lane, issues, version, generation, text, stopwatch.ElapsedMilliseconds);
    }

    private void PublishLaneCore(
        AnalysisLane lane,
        IReadOnlyList<TextIssue> issues,
        int version,
        int generation,
        string text,
        long elapsedMs)
    {
        if (version != _textVersion || generation != _documentGeneration)
        {
            CompatibilityLogger.Technical(
                "editor-lane-dropped-stale",
                $"lane={lane} version={version} current={_textVersion} generation={generation} currentGeneration={_documentGeneration}");
            return;
        }

        IReadOnlyList<TextIssue> live = IssueSetRebase.ValidAgainst(issues, text)
            .Where(issue => !_applied.IsConsumed(issue, generation))
            .ToArray();

        _lastAnalyzedText = text;
        _lastAnalyzedIssues = live;
        _allIssues = live;
        ApplyDiff(Filtered(live));
        RefreshPresentation();

        if (lane is AnalysisLane.Fast or AnalysisLane.Deterministic)
        {
            _answerSettled = true;
            SetAnalyzing(false);
        }

        CompatibilityLogger.Technical(
            "editor-lane-published",
            $"lane={lane} count={live.Count} elapsedMs={elapsedMs}");
    }

    /// <summary>Maps an issue's flat offsets to a live document range.</summary>
    private TextRange? ResolveIssueRange(TextIssue issue)
    {
        var index = _index;
        if (index is null || index.IsStale(_documentGeneration))
        {
            return null;
        }

        return index.RangeFor(issue.Start, issue.Length);
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

    private IReadOnlyList<TextIssue> Filtered(IReadOnlyList<TextIssue> issues)
    {
        if (_filter is null)
        {
            return issues;
        }

        // Readability shares the Style filter: the distinction is analyzer-internal
        // and would read as two near-identical tabs to a writer.
        return issues
            .Where(issue => issue.Category == _filter
                            || (_filter == IssueCategory.Style && issue.Category == IssueCategory.Readability))
            .ToArray();
    }

    // ── Interaction ──────────────────────────────────────────────────────────

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (IssuesList is null) return;

        _filter = sender switch
        {
            _ when ReferenceEquals(sender, FilterOrthography) => IssueCategory.Orthography,
            _ when ReferenceEquals(sender, FilterGrammar) => IssueCategory.Grammar,
            _ when ReferenceEquals(sender, FilterPunctuation) => IssueCategory.Punctuation,
            _ when ReferenceEquals(sender, FilterStyle) => IssueCategory.Style,
            _ => null
        };

        ApplyDiff(Filtered(_allIssues));
        RefreshPresentation();
    }

    private void PanelTab_Checked(object sender, RoutedEventArgs e)
    {
        if (IssuesTab is null || WordTab is null)
        {
            return;
        }

        var showWord = ReferenceEquals(sender, TabWord);
        IssuesTab.Visibility = showWord ? Visibility.Collapsed : Visibility.Visible;
        WordTab.Visibility = showWord ? Visibility.Visible : Visibility.Collapsed;

        if (showWord)
        {
            Motion.Reveal(WordTab, offset: 6);
        }
    }

    private void IssuesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IssuesList.SelectedItem is not EditorSuggestion suggestion) return;

        var issue = suggestion.Issue;
        _underlines?.SetFocused(issue);

        if (ResolveIssueRange(issue) is not { } range)
        {
            return;
        }

        Editor.Focus();
        Editor.Selection.Select(range.Start, range.End);
        range.Start.Paragraph?.BringIntoView();
    }

    /// <summary>Manual mode: clicking an underlined word reveals its correction card.</summary>
    private void Editor_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Issues.Count == 0)
        {
            return;
        }

        var index = _index;
        if (index is null || index.IsStale(_documentGeneration))
        {
            return;
        }

        var position = Editor.GetPositionFromPoint(e.GetPosition(Editor), snapToText: true);
        if (position is null)
        {
            return;
        }

        var offset = index.OffsetOf(position);
        if (offset < 0)
        {
            return;
        }

        var hit = Issues.FirstOrDefault(item =>
            offset >= item.Issue.Start && offset <= item.Issue.Start + Math.Max(item.Issue.Length, 1));

        if (hit is null)
        {
            return;
        }

        IssuesList.SelectedItem = hit;
        IssuesList.ScrollIntoView(hit);
    }

    /// <summary>
    /// Applies one prepared correction, then returns. Nothing here consults an analyzer.
    /// </summary>
    /// <remarks>
    /// <para><b>The shape this replaces.</b> The handler used to mutate the document and then
    /// schedule a full-document re-analysis, and the sidebar was rebuilt only when that
    /// finished. On the shipping stack that was 24–53 s, and for all of it the card the user
    /// had just applied was still on screen and still enabled — which reads as the correction
    /// having come back, and clicking it again silently did nothing because the original no
    /// longer matched.</para>
    ///
    /// <para><b>The shape now.</b> Validate, mutate, invalidate the consumed issue, rebase the
    /// rest, redraw, return. The revalidation that follows is a background correctness check
    /// over the affected region only; it owns none of the interaction.</para>
    /// </remarks>
    private void ApplyIssue_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EditorSuggestion suggestion) return;
        ApplyPrepared(suggestion);
    }

    /// <summary>The apply path itself, without the click. Shared with the tests that pin it.</summary>
    internal bool ApplyPrepared(EditorSuggestion suggestion)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        if (!TryApply(suggestion.Issue))
        {
            // The span moved or its text changed since the card was drawn. Drop the card
            // rather than leaving a control that cannot do anything.
            Issues.Remove(suggestion);
            _allIssues = _allIssues.Where(issue => issue != suggestion.Issue).ToArray();
            RefreshPresentation();
            return false;
        }

        RefreshAfterApply();
        stopwatch.Stop();
        _applyLatency.Record(stopwatch.Elapsed);
        CompatibilityLogger.Technical(
            "editor-apply-latency",
            $"origin={TextEditOrigin.CorrectionApply} elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F2} remaining={_allIssues.Count}");

        ScheduleRevalidation(TextEditOrigin.CorrectionApply);
        return true;
    }

    private void IgnoreIssue_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EditorSuggestion suggestion) return;
        _ignored.Add(IgnoreKey(suggestion.Issue));
        Issues.Remove(suggestion);
        _allIssues = _allIssues.Where(issue => !_ignored.Contains(IgnoreKey(issue))).ToArray();
        RefreshPresentation();
    }

    private void ApplySafe_Click(object sender, RoutedEventArgs e) => ApplyAllSafePrepared();

    /// <summary>The "fix everything safe" transaction, without the click.</summary>
    internal int ApplyAllSafePrepared()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var applied = ApplySafeCorrections(SafeApplyConfidence);
        stopwatch.Stop();

        if (applied == 0) return 0;

        _applyLatency.Record(stopwatch.Elapsed);
        CompatibilityLogger.Technical(
            "editor-apply-latency",
            $"origin={TextEditOrigin.SafeBatchApply} applied={applied} elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F2} remaining={_allIssues.Count}");

        // One batch, one revalidation. Not one per correction.
        ScheduleRevalidation(TextEditOrigin.SafeBatchApply);
        return applied;
    }

    /// <summary>
    /// Schedules the background correctness pass that follows an apply.
    /// </summary>
    /// <remarks>
    /// <para>Incremental rather than full: the previous text and issue set were updated
    /// transactionally by the apply itself, so <see cref="DirtyIssueAnalysis"/> can compute the
    /// changed region exactly and the analyzer sees the affected sentences instead of the whole
    /// document. On the 480-word sample that is a few hundred characters rather than 3 516.</para>
    ///
    /// <para>The delay is longer than a keystroke's because nothing is waiting for it. The UI
    /// is already correct when this is scheduled; this pass exists to catch the case where an
    /// edit changed a sentence boundary and a neighbouring finding needs revising.</para>
    /// </remarks>
    private void ScheduleRevalidation(TextEditOrigin origin)
    {
        var fullDocument = origin is TextEditOrigin.Undo
            or TextEditOrigin.SmartAction
            or TextEditOrigin.ProgrammaticRestore;

        QueueAnalysis(TimeSpan.FromMilliseconds(fullDocument ? 80 : 250), fullDocument);
    }

    /// <summary>
    /// Automatic correction, in the spirit of a good predictive-text system: it only
    /// touches near-certain corrections, never rewrites the word being typed, and always
    /// leaves an undo within reach.
    /// </summary>
    private void TryAutoCorrect()
    {
        if (AutoCorrectToggle.IsChecked != true || _allIssues.Count == 0)
        {
            return;
        }

        var index = _index;
        if (index is null || index.IsStale(_documentGeneration))
        {
            return;
        }

        var caret = index.OffsetOf(Editor.CaretPosition);

        var applied = ApplySafeCorrections(
            AutoCorrectConfidence,
            issue => caret < 0 || caret < issue.Start || caret > issue.Start + issue.Length);

        if (applied == 0)
        {
            return;
        }

        UndoAutoButton.Visibility = Visibility.Visible;
        Controls.Type.SetTracked(StatusText, applied == 1 ? "ИСПРАВЛЕНО 1" : $"ИСПРАВЛЕНО {applied}");
        QueueAnalysis(TimeSpan.FromMilliseconds(120), fullDocument: true);
    }

    private int ApplySafeCorrections(double minimumConfidence, Func<TextIssue, bool>? extraFilter = null)
    {
        var safe = _allIssues
            .Where(issue => issue.CanApplyAutomatically
                            && issue.Confidence >= minimumConfidence
                            && issue.Replacement is not null
                            && (extraFilter is null || extraFilter(issue)))
            // Applied back to front so an earlier replacement cannot shift the
            // offsets of the ones still to come.
            .OrderByDescending(issue => issue.Start)
            .ToArray();

        if (safe.Length == 0)
        {
            return 0;
        }

        var applied = 0;

        // One undo unit for the whole batch: pressing Ctrl+Z after "fix everything
        // safe" should undo that action, not the last of forty replacements.
        BeginUndoBatch();
        try
        {
            foreach (var issue in safe)
            {
                if (TryApply(issue, rebuildIndex: false))
                {
                    applied++;
                }
            }
        }
        finally
        {
            EndUndoBatch();
        }

        if (applied > 0)
        {
            _documentGeneration++;
            RefreshAfterApply();
        }

        return applied;
    }

    private void UndoAuto_Click(object sender, RoutedEventArgs e)
    {
        if (Editor.CanUndo)
        {
            Editor.Undo();
        }

        HideUndo();
        QueueAnalysis(TimeSpan.FromMilliseconds(80), fullDocument: true);
    }

    private void HideUndo()
    {
        if (UndoAutoButton.Visibility == Visibility.Collapsed) return;
        UndoAutoButton.Visibility = Visibility.Collapsed;
        Controls.Type.SetTracked(StatusText, "ЛОКАЛЬНАЯ ПРОВЕРКА");
    }

    private void AutoCorrect_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (AutoCorrectToggle.IsChecked == true)
        {
            TryAutoCorrect();
        }
    }

    /// <summary>
    /// Replaces the span an issue names with its suggestion.
    /// </summary>
    /// <remarks>
    /// The replacement inherits the formatting already at the start of the span, so
    /// fixing a typo inside a bold sentence leaves the sentence bold. Writing the
    /// text through the range and then re-applying the captured formatting is the
    /// only way to get that: WPF resets a range's properties when its text is set.
    /// </remarks>
    private bool TryApply(TextIssue issue, bool rebuildIndex = true)
    {
        issue = CorrectionPresentation.NormalizeForApply(issue);
        if (issue.Replacement is null)
        {
            return false;
        }

        var index = _index;
        if (index is null || index.IsStale(_documentGeneration))
        {
            return false;
        }

        if (index.RangeFor(issue.Start, issue.Length) is not { } range)
        {
            return false;
        }

        // Guard against a stale offset landing on different text than the issue
        // described; replacing the wrong span is worse than skipping a correction.
        if (!string.Equals(range.Text, issue.Original, StringComparison.Ordinal))
        {
            return false;
        }

        var captured = CaptureFormatting(range.Start);

        _internalEdit = true;
        try
        {
            range.Text = issue.Replacement;
            ApplyCaptured(new TextRange(range.Start, range.End), captured);
            Editor.CaretPosition = range.End;
            _textVersion++;

            if (rebuildIndex)
            {
                _documentGeneration++;
            }

            // ── The issue is consumed here, not when the next analysis says so ──────────
            //
            // Everything below is arithmetic over spans the caller already knows: the issue
            // that was applied is dropped, the ones after it move by the delta, and the ones
            // overlapping it are dropped because their text has changed. That keeps the
            // sidebar, the underlines and the safe-apply button correct without asking any
            // analyzer anything, which is what makes the click feel like it did something.
            CommitApplied(issue);

            MarkDirty();
            return true;
        }
        catch (ArgumentException)
        {
            // The pointers no longer belong to this document.
            return false;
        }
        finally
        {
            _internalEdit = false;
        }
    }

    /// <summary>
    /// Records an applied correction and brings every in-memory issue set up to date with it.
    /// </summary>
    private void CommitApplied(TextIssue issue)
    {
        var replacementLength = issue.Replacement?.Length ?? 0;

        _applied.Record(issue, _documentGeneration);

        _allIssues = IssueSetRebase.AfterReplacement(
            _allIssues.Where(candidate => candidate != issue).ToArray(),
            issue.Start,
            issue.Length,
            replacementLength);

        // The analysed baseline moves with the document. Leaving it behind is what let an
        // incremental pass afterwards plan its dirty range against text that no longer
        // existed and retain findings the apply had already resolved.
        if (_lastAnalyzedText.Length >= issue.Start + issue.Length)
        {
            _lastAnalyzedText = _lastAnalyzedText
                .Remove(issue.Start, issue.Length)
                .Insert(issue.Start, issue.Replacement ?? string.Empty);
            _lastAnalyzedIssues = IssueSetRebase.AfterReplacement(
                _lastAnalyzedIssues.Where(candidate => candidate != issue).ToArray(),
                issue.Start,
                issue.Length,
                replacementLength);
        }
    }

    /// <summary>Drops applied issues from the sidebar and redraws. Dispatcher-thread only.</summary>
    /// <remarks>
    /// The index is rebuilt here rather than left for the revalidation pass. Without it every
    /// remaining underline disappears the instant one correction is applied — <see
    /// cref="ResolveIssueRange"/> refuses a stale index, correctly — and comes back a few
    /// hundred milliseconds later, which looks like the document being re-checked from scratch
    /// after every click. Rebuilding costs a walk of the document's text pointers; on the
    /// 480-word sample that is under a millisecond, and it is the only dispatcher work an
    /// apply does beyond the mutation itself.
    /// </remarks>
    private void RefreshAfterApply()
    {
        _index = DocumentTextIndex.Build(Editor.Document, _documentGeneration);
        ApplyDiff(Filtered(_allIssues));
        RefreshPresentation();
        UpdateCounters(_index.Text);
    }

    private async void Recheck_Click(object sender, RoutedEventArgs e) =>
        await ScheduleAnalysisAsync(TimeSpan.Zero);

    /// <summary>
    /// Turns a keypress in the editor into whatever it is currently bound to.
    /// </summary>
    /// <remarks>
    /// <para>The bindings used to be a <c>switch</c> on <see cref="Key"/> here, which is why
    /// nothing in the product could list them and nothing could tell anyone what Ctrl+H does.
    /// They now come from <see cref="ShortcutRegistry"/>, so the settings page shows the same
    /// set this method obeys, and changing one there changes it here with nothing to keep in
    /// step.</para>
    ///
    /// <para>Tab and Escape stay written out rather than looked up. Neither is a command:
    /// each has a meaning only when something is on screen to act on — a prepared
    /// continuation, an open find bar — and falls through to its ordinary behaviour when
    /// there is not. That condition is the binding, and a registry entry could not express
    /// it.</para>
    /// </remarks>
    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;

        if (modifiers == ModifierKeys.None)
        {
            // Tab accepts a prepared continuation and does nothing else; when none is painted
            // Tab keeps its ordinary meaning, so the binding costs the user nothing.
            if (e.Key == Key.Tab && HasWritingSuggestion)
            {
                e.Handled = AcceptWritingSuggestion();
                if (e.Handled) return;
            }

            if (e.Key == Key.Escape)
            {
                if (HasWritingSuggestion)
                {
                    e.Handled = true;
                    DismissWritingSuggestion();
                    return;
                }

                if (FindBar.Visibility == Visibility.Visible)
                {
                    e.Handled = true;
                    CloseFind();
                }

                return;
            }
        }

        var shortcut = _shortcuts.Match(e.Key, modifiers, ShortcutScope.Application);
        if (shortcut is null)
        {
            return;
        }

        // A caret move by any other means also invalidates a prepared continuation.
        DismissWritingSuggestion();

        switch (shortcut)
        {
            case ShortcutRegistry.CheckNow:
                e.Handled = true;
                await ScheduleAnalysisAsync(TimeSpan.Zero);
                break;

            case ShortcutRegistry.NewDocument:
                e.Handled = true;
                NewDocument();
                break;

            case ShortcutRegistry.OpenDocument:
                e.Handled = true;
                await OpenDocumentAsync();
                break;

            case ShortcutRegistry.SaveDocument:
                e.Handled = true;
                await SaveAsync();
                break;

            case ShortcutRegistry.SaveDocumentAs:
                e.Handled = true;
                await SaveAsAsync();
                break;

            case ShortcutRegistry.Bold:
                e.Handled = true;
                ToggleBold();
                break;

            case ShortcutRegistry.Italic:
                e.Handled = true;
                ToggleItalic();
                break;

            case ShortcutRegistry.Underline:
                e.Handled = true;
                ToggleUnderline();
                break;

            case ShortcutRegistry.Find:
                e.Handled = true;
                OpenFind(replace: false);
                break;

            case ShortcutRegistry.Replace:
                e.Handled = true;
                OpenFind(replace: true);
                break;
        }
    }

    // ── Presentation ─────────────────────────────────────────────────────────

    private static int CountWords(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private void UpdateCounters(string text)
    {
        var words = CountWords(text);
        Controls.Type.SetTracked(WordCountText, RussianPlural.Words(words).ToUpperInvariant());
        Controls.Type.SetTracked(CharCountText, $"{text.Length} ЗНАКОВ");

        // Page count is the document's own page breaks plus one, not an estimate:
        // claiming "12 стр." for a continuous document would be an invented number.
        var breaks = CountPageBreaks();
        Controls.Type.SetTracked(PageCountText, breaks == 0 ? "1 СТР." : $"{breaks + 1} СТР.");
    }

    private int CountPageBreaks() =>
        Editor.Document.Blocks
            .OfType<System.Windows.Documents.Paragraph>()
            .Count(paragraph => (paragraph.Tag as string) == FlowDocumentBridge.PageBreakTag);

    /// <summary>
    /// Shows or clears the "checking" state in the findings panel.
    /// </summary>
    /// <remarks>
    /// <para>Analysis never blocks the canvas; only the empty slot in the panel changes.
    /// What it must also not do is keep claiming to be working after it has an answer.</para>
    ///
    /// <para>A pass runs in lanes: the deterministic ones answer in milliseconds, and the
    /// model lane afterwards can take tens of seconds. When the deterministic lanes find
    /// something the panel fills and the state clears itself, so the problem was invisible on
    /// text with mistakes in it. On <em>clean</em> text there is nothing to fill the panel
    /// with, and the state used to stay up for the whole model lane — a document with nothing
    /// wrong with it showed a spinner for as long as it took the model to also find nothing,
    /// and then said so. That is the worst case reading it the wrong way round.</para>
    ///
    /// <para>So the state is cleared by the first deterministic answer, whatever that answer
    /// is, and <see cref="_answerSettled"/> keeps a later lane from putting it back.</para>
    /// </remarks>
    private void SetAnalyzing(bool analyzing)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetAnalyzing(analyzing));
            return;
        }

        var show = analyzing && !_answerSettled && Issues.Count == 0;
        AnalyzingState.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (show)
        {
            EmptyState.Visibility = Visibility.Collapsed;
        }
        else
        {
            RefreshPresentation();
        }
    }

    /// <summary>
    /// True once a lane that does not need the model has answered for the current text.
    /// </summary>
    /// <remarks>
    /// Cleared at the start of every pass, set by the first deterministic lane to publish.
    /// It is what separates "still working" from "still working, but you already have the
    /// answer that matters".
    /// </remarks>
    private bool _answerSettled;

    private void RefreshPresentation()
    {
        var count = Issues.Count;
        var total = _allIssues.Count;

        IssueBadge.Text = count.ToString();
        IssueCountText.Text = total switch
        {
            0 => "Замечаний нет",
            _ when _filter is null => $"Найдено: {total}",
            _ => $"Показано: {count} из {total}"
        };

        var showEmpty = count == 0 && AnalyzingState.Visibility != Visibility.Visible;
        EmptyState.Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;
        IssuesList.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Distinguish "nothing wrong" from "nothing in this filter".
        if (showEmpty)
        {
            var filteredOut = total > 0;
            EmptyTitle.Text = filteredOut ? "В этой категории пусто" : "Замечаний не найдено";
            EmptyHint.Text = filteredOut
                ? "Снимите фильтр, чтобы увидеть остальные замечания."
                : "Текст выглядит хорошо.";
        }

        ApplySafeButton.IsEnabled = _allIssues.Any(issue =>
            issue.CanApplyAutomatically && issue.Confidence >= SafeApplyConfidence && issue.Replacement is not null);

        _underlines?.SetIssues(_allIssues);
    }

    private static string IgnoreKey(TextIssue issue) => $"{issue.RuleId}:{issue.Start}:{issue.Length}:{issue.Original}";

    // ── Responsive layout ────────────────────────────────────────────────────

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;

        var shouldCollapse = e.NewSize.Width < PanelCollapseThreshold;
        if (shouldCollapse != _panelCollapsed)
        {
            _panelCollapsed = shouldCollapse;
            _panelForcedOpen = false;
            TogglePanelButton.Visibility = shouldCollapse ? Visibility.Visible : Visibility.Collapsed;
            ApplyPanelVisibility();
        }

        // The toolbar sheds groups before it starts scrolling: a scrolling toolbar
        // hides controls behind an interaction, which is worse than showing fewer.
        var compact = e.NewSize.Width < CompactToolbarThreshold;
        var groupVisibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ListGroup.Visibility = groupVisibility;
        ListGroupRule.Visibility = groupVisibility;
        HistoryGroup.Visibility = groupVisibility;
        HistoryGroupRule.Visibility = groupVisibility;
    }

    private void TogglePanel_Click(object sender, RoutedEventArgs e)
    {
        _panelForcedOpen = !_panelForcedOpen;
        ApplyPanelVisibility();
    }

    private void ApplyPanelVisibility()
    {
        var visible = !_panelCollapsed || _panelForcedOpen;
        SuggestionsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PanelColumn.Width = visible ? new GridLength(360) : new GridLength(0);
    }
}

internal readonly record struct AnalysisBatch(IReadOnlyList<TextIssue> Issues, int WordCount);

public sealed class EditorSuggestion
{
    public EditorSuggestion(TextIssue issue) => Issue = CorrectionPresentation.NormalizeForApply(issue);

    public TextIssue Issue { get; }

    public string Identity => $"{Issue.RuleId}:{Issue.Start}:{Issue.Length}:{Issue.Original}:{Issue.Replacement}";

    public string CategoryLabel => CorrectionCardText.CategoryLabel(Issue.Category);

    public string CategoryLabelUpper => CorrectionCardText.CategoryLabelUpper(Issue.Category);

    public string SourceLabelUpper => CorrectionCardText.SourceLabel(Issue);

    public string OriginalDisplay => CorrectionCardText.OriginalDisplay(Issue);

    public string ReplacementDisplay => CorrectionCardText.ReplacementDisplay(Issue);

    public Visibility ReplacementVisibility =>
        string.IsNullOrEmpty(Issue.Replacement) ? Visibility.Collapsed : Visibility.Visible;

    public bool CanApply => Issue.Replacement is not null;

    public string Explanation => Issue.Explanation;

    public Brush CategoryBrush => CorrectionCardText.CategoryBrushes(Issue.Category).Foreground;
}
