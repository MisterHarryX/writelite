using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Views.Pages;
using WpfParagraph = System.Windows.Documents.Paragraph;

namespace WriteLite.Tests.Documents;

/// <summary>
/// When the findings panel stops saying it is working.
/// </summary>
/// <remarks>
/// <para>A pass runs in lanes. The deterministic ones answer in milliseconds; the model lane
/// after them can take tens of seconds and, measured on the shipping configuration, accepts
/// no findings at all. The panel used to keep the "checking" state up until the whole pass
/// finished.</para>
///
/// <para>On text with mistakes in it nobody saw this, because the findings filled the panel
/// and cleared the state on their way in. On <em>clean</em> text there was nothing to fill it
/// with, so a document with nothing wrong showed a spinner for the length of the model lane
/// and only then said it was fine — the case that most deserved an immediate answer was the
/// one that waited longest.</para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class EditorAnalysingStateTests
{
    /// <summary>
    /// Answers deterministically at once, then holds the model lane open indefinitely.
    /// </summary>
    /// <remarks>
    /// The shape of the shipping stack with the timings exaggerated so the window between
    /// "deterministic answer" and "pass finished" is wide enough to assert inside.
    /// </remarks>
    private sealed class SlowDeepAnalyzer : IStagedTextAnalyzer
    {
        private readonly IReadOnlyList<TextIssue> _findings;

        public SlowDeepAnalyzer(IReadOnlyList<TextIssue>? findings = null) => _findings = findings ?? [];

        public IReadOnlyList<TextIssue> Analyze(string text) => _findings;

        public Task<IReadOnlyList<TextIssue>> AnalyzeAsync(string text, CancellationToken cancellationToken = default)
            => AnalyzeStagedAsync(text, (_, _) => { }, cancellationToken);

        public async Task<IReadOnlyList<TextIssue>> AnalyzeStagedAsync(
            string text,
            Action<AnalysisLane, IReadOnlyList<TextIssue>> publish,
            CancellationToken cancellationToken = default)
        {
            publish(AnalysisLane.Deterministic, _findings);

            // Stands in for the model lane. Long enough that a panel still claiming to be
            // working when it ends would have been claiming it for an unacceptable time.
            await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            return _findings;
        }
    }

    [TestMethod]
    public void CleanTextStopsSayingCheckingAsSoonAsTheDeterministicLaneAnswers()
        => WpfTestHost.RunAsync(async () =>
        {
            using var workspace = new DocumentFixtures.Workspace();
            var page = NewPage(workspace);
            var window = Host(page);

            try
            {
                page.BindAnalyzer(new SlowDeepAnalyzer());
                page.Editor.Document = new FlowDocument(
                    new WpfParagraph(new Run("Совершенно обычное предложение без ошибок.")));

                // Well past the debounce and the deterministic lane, and nowhere near the
                // twenty seconds the model lane runs for.
                await WpfTestHost.PumpAsync(2_000);

                Assert.AreEqual(
                    Visibility.Collapsed,
                    Analysing(page).Visibility,
                    "the panel is still claiming to be checking after it has its answer");

                Assert.AreEqual(
                    Visibility.Visible,
                    Empty(page).Visibility,
                    "clean text must be told it is clean, not left under a spinner");
            }
            finally
            {
                window.Close();
                page.Dispose();
            }
        });

    [TestMethod]
    public void TextWithFindingsShowsThemRatherThanTheCheckingState()
        => WpfTestHost.RunAsync(async () =>
        {
            using var workspace = new DocumentFixtures.Workspace();
            var page = NewPage(workspace);
            var window = Host(page);

            try
            {
                const string sample = "Проблема связанна с обновлением системы.";
                page.BindAnalyzer(new SlowDeepAnalyzer(
                [
                    new TextIssue(
                        sample.IndexOf("связанна", StringComparison.Ordinal),
                        "связанна".Length,
                        "связанна",
                        "связана",
                        "Тест",
                        "Маркер.",
                        IssueCategory.Grammar,
                        IssueSeverity.Error,
                        CanApplyAutomatically: false,
                        RuleId: "test.analysing.marker",
                        LinguisticCategory: LinguisticIssueCategory.AgreementError)
                ]));

                page.Editor.Document = new FlowDocument(new WpfParagraph(new Run(sample)));
                await WpfTestHost.PumpAsync(2_000);

                Assert.AreEqual(Visibility.Collapsed, Analysing(page).Visibility);
                Assert.IsNotEmpty(page.Issues);
            }
            finally
            {
                window.Close();
                page.Dispose();
            }
        });

    // ── Harness ──────────────────────────────────────────────────────────────

    private static UIElement Analysing(EditorPage page) => (UIElement)page.FindName("AnalyzingState")!;

    private static UIElement Empty(EditorPage page) => (UIElement)page.FindName("EmptyState")!;

    private static EditorPage NewPage(DocumentFixtures.Workspace workspace)
    {
        var page = new EditorPage
        {
            Recovery = new Services.Documents.DocumentRecoveryService(
                System.IO.Path.Combine(workspace.Root, "recovery"))
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
}
