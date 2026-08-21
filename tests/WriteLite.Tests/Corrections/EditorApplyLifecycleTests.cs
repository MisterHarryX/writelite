using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Documents;
using WriteLite.Tests.Documents;
using WriteLite.Views.Pages;
using WpfParagraph = System.Windows.Documents.Paragraph;

namespace WriteLite.Tests.Corrections;

/// <summary>
/// The apply path, driven through the real editor page.
/// </summary>
/// <remarks>
/// <para>These pin the behaviour the sprint exists for: a click on a prepared correction is a
/// text replacement and nothing else. The measured shape before this work was a click that
/// mutated the document and then waited for a full re-analysis — 24–53 s on the shipping
/// stack — before the sidebar reflected any of it, which a user reads as the application
/// hanging and then handing the correction back.</para>
///
/// <para>Not parallelised: one shared STA dispatcher, like every other UI test here.</para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class EditorApplyLifecycleTests
{
    private const string Sentence = "Согласно нового плана мы начали работу.";

    private static TextIssue GovernmentIssue() => new(
        Start: 9,
        Length: 6,
        Original: "нового",
        Replacement: "новому",
        Title: "government",
        Explanation: "dative required",
        Category: IssueCategory.Grammar,
        Severity: IssueSeverity.Error,
        CanApplyAutomatically: true,
        RuleId: "ru.case.government.soglasno",
        Confidence: 0.97);

    private static EditorPage NewPage(string root)
    {
        var page = new EditorPage
        {
            Recovery = new DocumentRecoveryService(Path.Combine(root, "recovery"))
        };
        page.ReportError = (_, _) => { };
        page.AskConfirmation = (_, _) => true;
        return page;
    }

    private static Window Host(EditorPage page)
    {
        WpfTestHost.EnsureThemeApplied();
        var window = new Window
        {
            Width = 1280,
            Height = 800,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000,
            Content = page
        };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static void SetText(EditorPage page, string text)
    {
        page.Editor.Document.Blocks.Clear();
        page.Editor.Document.Blocks.Add(new WpfParagraph(new Run(text)));
    }

    private static string PlainText(EditorPage page)
        => DocumentTextIndex.Build(page.Editor.Document).Text;

    // ── §35 Apply is a replacement and nothing else ──────────────────────────

    [TestMethod]
    public void Applying_a_prepared_correction_never_reaches_the_deep_lane()
        => WpfTestHost.RunAsync(async () =>
        {
            using var workspace = new DocumentFixtures.Workspace();
            var page = NewPage(workspace.Root);
            var window = Host(page);

            // A deep lane that never completes: if apply waited on it, the test would hang
            // rather than fail, which is the honest expression of the old behaviour.
            var analyzer = new StagedSpyAnalyzer(
                deterministic: _ => [GovernmentIssue()],
                blockDeepLane: true);

            try
            {
                SetText(page, Sentence);
                page.BindAnalyzer(analyzer);
                page.PublishForTest(AnalysisLane.Deterministic, [GovernmentIssue()], Sentence);
                await WpfTestHost.PumpAsync();

                Assert.HasCount(1, page.Issues, "the prepared correction should be on the card");
                var deepBefore = analyzer.DeepLaneCalls;
                var analysesBefore = analyzer.AnalysisCalls;

                var stopwatch = Stopwatch.StartNew();
                var applied = page.ApplyPrepared(page.Issues[0]);
                stopwatch.Stop();

                Assert.IsTrue(applied, "a prepared correction whose original still matches must apply");
                Assert.AreEqual(deepBefore, analyzer.DeepLaneCalls, "apply must not enter the deep lane");
                Assert.AreEqual(
                    analysesBefore,
                    analyzer.AnalysisCalls,
                    "apply must not start an analysis of its own; revalidation is scheduled, not awaited");

                Assert.Contains("Согласно новому плана", PlainText(page));
                Assert.IsEmpty(page.Issues, "the applied correction must leave the card immediately");
                Assert.IsEmpty(page.AllIssues, "and must leave the issue set immediately");

                Assert.IsLessThan(
                    250,
                    stopwatch.Elapsed.TotalMilliseconds,
                    $"apply took {stopwatch.Elapsed.TotalMilliseconds:F1} ms on the dispatcher");
            }
            finally
            {
                analyzer.Release();
                window.Close();
                page.Dispose();
            }
        });

    // ── §36 An applied correction does not come back ─────────────────────────

    [TestMethod]
    public void A_stale_lane_result_cannot_resurrect_an_applied_correction()
        => WpfTestHost.RunAsync(async () =>
        {
            using var workspace = new DocumentFixtures.Workspace();
            var page = NewPage(workspace.Root);
            var window = Host(page);

            // A quiet analyzer, so nothing but this test publishes into the page.
            var analyzer = new StagedSpyAnalyzer(_ => [], blockDeepLane: true);

            try
            {
                SetText(page, Sentence);
                page.BindAnalyzer(analyzer);
                await WpfTestHost.PumpAsync(250);

                page.PublishForTest(AnalysisLane.Deterministic, [GovernmentIssue()], Sentence);
                await WpfTestHost.PumpAsync();

                var revisionAtAnalysis = page.Revision;
                Assert.HasCount(1, page.Issues);

                Assert.IsTrue(page.ApplyPrepared(page.Issues[0]));
                await WpfTestHost.PumpAsync();
                Assert.IsEmpty(page.Issues);

                // The deep lane, started before the apply, finishes now and offers the
                // correction the user has already accepted. Both the deterministic and the
                // model shape of that arrival are tested: the guard is the revision, not the
                // lane it came from.
                page.PublishStaleForTest(
                    AnalysisLane.Deterministic,
                    [GovernmentIssue()],
                    Sentence,
                    revisionAtAnalysis.Version,
                    revisionAtAnalysis.Generation);
                page.PublishStaleForTest(
                    AnalysisLane.Deep,
                    [GovernmentIssue()],
                    Sentence,
                    revisionAtAnalysis.Version,
                    revisionAtAnalysis.Generation);
                await WpfTestHost.PumpAsync();

                Assert.IsEmpty(page.Issues, "a result computed against the pre-apply revision must be discarded");
                Assert.Contains("Согласно новому плана", PlainText(page));
            }
            finally
            {
                analyzer.Release();
                window.Close();
                page.Dispose();
            }
        });

    [TestMethod]
    public void The_ledger_suppresses_an_accepted_correction_recomputed_at_the_same_revision()
    {
        var ledger = new AppliedCorrectionLedger();
        var issue = GovernmentIssue();

        Assert.IsFalse(ledger.IsConsumed(issue, revision: 4));
        ledger.Record(issue, revision: 4);

        Assert.IsTrue(
            ledger.IsConsumed(issue, revision: 4),
            "a lane that started at the applying revision must not offer it back");
        Assert.IsFalse(
            ledger.IsConsumed(issue, revision: 5),
            "a lane that started after the apply is describing text the user has seen since");

        // Once the original text is gone from the document, so is the entry: retyping the
        // same mistake later deserves the same correction.
        ledger.Prune("другой текст");
        Assert.AreEqual(0, ledger.Count);
    }

    // ── §37 The safe batch is one transaction ────────────────────────────────

    [TestMethod]
    public void Fixing_everything_safe_applies_once_and_undoes_once()
        => WpfTestHost.RunAsync(async () =>
        {
            using var workspace = new DocumentFixtures.Workspace();
            var page = NewPage(workspace.Root);
            var window = Host(page);

            const string text = "Согласно нового плана, вопреки новых целей, мы начали.";
            var issues = new[]
            {
                GovernmentIssue(),
                new TextIssue(
                    31, 5, "новых", "новым", "government", "dative required",
                    IssueCategory.Grammar, IssueSeverity.Error, true, "ru.case.government.vopreki",
                    Confidence: 0.97),
            };

            var analyzer = new StagedSpyAnalyzer(_ => issues, blockDeepLane: true);

            try
            {
                SetText(page, text);
                page.BindAnalyzer(analyzer);
                page.PublishForTest(AnalysisLane.Deterministic, issues, text);
                await WpfTestHost.PumpAsync();

                Assert.HasCount(2, page.Issues);
                var analysesBefore = analyzer.AnalysisCalls;

                var applied = page.ApplyAllSafePrepared();

                Assert.AreEqual(2, applied);
                Assert.AreEqual(
                    analysesBefore,
                    analyzer.AnalysisCalls,
                    "the batch must not analyse between its own edits");

                var after = PlainText(page);
                Assert.Contains("Согласно новому плана", after);
                Assert.Contains("вопреки новым целей", after);
                Assert.IsEmpty(page.Issues, "every consumed correction leaves the card with the batch");

                // One undo unit for the whole batch.
                Assert.IsTrue(page.Editor.CanUndo);
                page.Editor.Undo();
                var undone = PlainText(page);
                Assert.Contains("Согласно нового плана", undone);
                Assert.Contains("вопреки новых целей", undone);
            }
            finally
            {
                analyzer.Release();
                window.Close();
                page.Dispose();
            }
        });

    // ── §39 Apply latency, measured rather than asserted anecdotally ─────────

    [TestMethod]
    public void Apply_latency_stays_inside_an_interactive_budget()
        => WpfTestHost.RunAsync(async () =>
        {
            using var workspace = new DocumentFixtures.Workspace();
            var page = NewPage(workspace.Root);
            var window = Host(page);

            try
            {
                // A document long enough that a full re-analysis would be visible if one
                // happened: the same shape as the 480-word sample the sprint measured.
                var paragraph = string.Join(" ", Enumerable.Repeat(Sentence, 40));
                for (var iteration = 0; iteration < 30; iteration++)
                {
                    SetText(page, paragraph);
                    page.PublishForTest(AnalysisLane.Deterministic, [GovernmentIssue()], paragraph);
                    await WpfTestHost.PumpAsync();
                    if (page.Issues.Count == 0) continue;
                    page.ApplyPrepared(page.Issues[0]);
                }

                var latency = page.ApplyLatency;
                Assert.IsGreaterThan(0, latency.Count, "no apply was measured");

                Console.WriteLine(
                    $"editor apply: n={latency.Count} p50={latency.P50Milliseconds:F2} ms "
                    + $"p95={latency.P95Milliseconds:F2} ms max={latency.MaxMilliseconds:F2} ms");

                Assert.IsLessThan(
                    50,
                    latency.P50Milliseconds,
                    $"p50 apply latency was {latency.P50Milliseconds:F2} ms");
                Assert.IsLessThan(
                    150,
                    latency.P95Milliseconds,
                    $"p95 apply latency was {latency.P95Milliseconds:F2} ms");
            }
            finally
            {
                window.Close();
                page.Dispose();
            }
        });
}
