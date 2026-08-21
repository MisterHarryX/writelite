using System.Windows;
using System.Windows.Documents;
using WriteLite.Documents.Model;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Documents;
using WriteLite.Views.Pages;
using WpfParagraph = System.Windows.Documents.Paragraph;

namespace WriteLite.Tests.Documents;

/// <summary>
/// Which analyzer the editor actually checks documents with.
/// </summary>
/// <remarks>
/// <para>These exist because of a shipped defect with no symptom other than silence. The page
/// built a private rules-and-spelling analyzer in a static field and used it for every check,
/// and the shell — which had already constructed the full stack for the system-wide field
/// monitor — handed the page only its rewriting service and its dictionary packs. A document
/// full of Russian errors was therefore checked by a fraction of WriteLite and reached the
/// sidebar nearly clean, while the status bar said «ЛОКАЛЬНАЯ ПРОВЕРКА» and the model server
/// sat healthy and unasked on loopback.</para>
///
/// <para>Nothing about that was visible: the fallback analyzer is a real analyzer and returns
/// real findings, just far fewer of them, and no log line said which one had run. So the
/// assertions here are about wiring rather than about linguistics — that the analyzer the
/// shell binds is the analyzer that runs, on a typed document and on a restored one alike.
/// A fake analyzer is deliberately used: a test that asserted on Russian findings would pass
/// for the wrong reason the moment the fallback happened to find the same thing.</para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class EditorAnalysisWiringTests
{
    private const string Sample =
        "Согласно нового плана мы должны закончить работу в течении недели. "
        + "Проблема связанна с обновлением системы, потому-что мы не успели.";

    /// <summary>
    /// An analyzer that could not possibly be the page's own, and says what it was asked.
    /// </summary>
    private sealed class RecordingAnalyzer : ITextAnalyzer
    {
        private readonly List<string> _texts = [];

        /// <summary>The word every finding is about, chosen so no real rule produces it.</summary>
        public const string Marker = "связанна";

        public const string MarkerRuleId = "test.wiring.marker";

        public IReadOnlyList<string> AnalyzedTexts
        {
            get
            {
                lock (_texts)
                {
                    return _texts.ToArray();
                }
            }
        }

        public IReadOnlyList<TextIssue> Analyze(string text)
        {
            lock (_texts)
            {
                _texts.Add(text);
            }

            var start = text.IndexOf(Marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return [];
            }

            return
            [
                new TextIssue(
                    start,
                    Marker.Length,
                    Marker,
                    "связана",
                    "Тест",
                    "Маркер проводки анализатора.",
                    IssueCategory.Grammar,
                    IssueSeverity.Error,
                    CanApplyAutomatically: false,
                    RuleId: MarkerRuleId,
                    LinguisticCategory: LinguisticIssueCategory.AgreementError)
            ];
        }

        public Task<IReadOnlyList<TextIssue>> AnalyzeAsync(
            string text,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Analyze(text));
    }

    private static EditorPage NewPage(DocumentFixtures.Workspace workspace)
    {
        var page = new EditorPage
        {
            Recovery = new DocumentRecoveryService(Path.Combine(workspace.Root, "recovery"))
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

    private static WlDocument SampleDocument()
    {
        var document = WlDocument.Empty();
        document.CurrentSection().Blocks.Add(DocumentParagraph.FromText(Sample));
        return document;
    }

    private static bool HasMarker(EditorPage page) =>
        page.Issues.Any(item => item.Issue.RuleId == RecordingAnalyzer.MarkerRuleId);

    [TestMethod]
    public void A_bound_analyzer_is_the_one_that_checks_a_typed_document() => WpfTestHost.RunAsync(async () =>
    {
        using var workspace = new DocumentFixtures.Workspace();
        var analyzer = new RecordingAnalyzer();
        var page = NewPage(workspace);
        var window = Host(page);

        try
        {
            page.BindAnalyzer(analyzer);
            page.Editor.Document = new FlowDocument(new WpfParagraph(new Run(Sample)));
            await WpfTestHost.PumpAsync(1_500);

            Assert.IsNotEmpty(
                analyzer.AnalyzedTexts,
                "The bound analyzer was never asked; the page checked with its own fallback.");
            Assert.IsTrue(
                analyzer.AnalyzedTexts.Any(text => text.Contains(RecordingAnalyzer.Marker, StringComparison.Ordinal)),
                "The bound analyzer was called, but never with the document's text.");
            Assert.IsTrue(HasMarker(page), "The bound analyzer's findings did not reach the sidebar.");
        }
        finally
        {
            window.Close();
            page.Dispose();
        }
    });

    /// <summary>
    /// A restored document must raise the same analysis flow as any other.
    /// </summary>
    /// <remarks>
    /// Recovery reaches the editing surface by a different route from an ordinary open — the
    /// snapshot is parsed, adopted under the original file's identity and marked dirty — and
    /// it is the route a user is least likely to notice going quiet, because a document that
    /// has just been rescued is one they are already relieved to see at all.
    /// </remarks>
    [TestMethod]
    public void A_restored_document_is_checked_by_the_bound_analyzer() => WpfTestHost.RunAsync(async () =>
    {
        using var workspace = new DocumentFixtures.Workspace();
        var recoveryDirectory = Path.Combine(workspace.Root, "recovery");

        // Written by one service instance and found by another: that difference in session
        // id is what makes the snapshot an orphan, which is what makes it offered back.
        var previousSession = new DocumentRecoveryService(recoveryDirectory);
        await previousSession.SaveSnapshotAsync(SampleDocument(), originalPath: null);

        var analyzer = new RecordingAnalyzer();
        var page = NewPage(workspace);
        var window = Host(page);

        try
        {
            page.BindAnalyzer(analyzer);
            await WpfTestHost.PumpAsync(100);

            page.CheckForRecoverableWork();
            await WpfTestHost.PumpAsync(2_500);

            Assert.Contains(
                Sample,
                DocumentTextIndex.Build(page.Editor.Document).Text,
                "The snapshot did not reach the editing surface at all.");
            Assert.IsTrue(
                analyzer.AnalyzedTexts.Any(text => text.Contains(RecordingAnalyzer.Marker, StringComparison.Ordinal)),
                "A restored document did not update the checker's snapshot.");
            Assert.IsTrue(HasMarker(page), "A restored document produced no issues in the sidebar.");
        }
        finally
        {
            window.Close();
            page.Dispose();
        }
    });

    /// <summary>
    /// Binding after a document is already open must re-check what is on screen.
    /// </summary>
    /// <remarks>
    /// The shell binds during startup, which can be after the page has already run a check
    /// against the fallback. Without a re-check the first document of a session would keep
    /// the fallback's answer until the next keystroke — which for a document someone opened
    /// to read rather than to edit is forever.
    /// </remarks>
    [TestMethod]
    public void Binding_an_analyzer_rechecks_the_open_document() => WpfTestHost.RunAsync(async () =>
    {
        using var workspace = new DocumentFixtures.Workspace();
        var analyzer = new RecordingAnalyzer();
        var page = NewPage(workspace);
        var window = Host(page);

        try
        {
            page.Editor.Document = new FlowDocument(new WpfParagraph(new Run(Sample)));
            await WpfTestHost.PumpAsync(1_500);
            Assert.IsFalse(HasMarker(page), "The analyzer was not bound yet, so its marker cannot be present.");

            page.BindAnalyzer(analyzer);
            await WpfTestHost.PumpAsync(1_500);

            Assert.IsTrue(HasMarker(page), "Binding an analyzer did not re-check the document already on screen.");
        }
        finally
        {
            window.Close();
            page.Dispose();
        }
    });

    /// <summary>
    /// Автоисправление governs applying corrections, never finding them.
    /// </summary>
    /// <remarks>
    /// Worth pinning down because the two are one word apart in the UI and the failure is
    /// invisible: if the toggle gated detection, turning it off would empty the sidebar and
    /// look exactly like a clean document.
    /// </remarks>
    [TestMethod]
    public void Turning_off_autocorrection_does_not_turn_off_detection() => WpfTestHost.RunAsync(async () =>
    {
        using var workspace = new DocumentFixtures.Workspace();
        var analyzer = new RecordingAnalyzer();
        var page = NewPage(workspace);
        var window = Host(page);

        try
        {
            page.AutoCorrectToggle.IsChecked = false;
            page.BindAnalyzer(analyzer);
            page.Editor.Document = new FlowDocument(new WpfParagraph(new Run(Sample)));
            await WpfTestHost.PumpAsync(1_500);

            Assert.IsTrue(HasMarker(page), "Disabling automatic correction also disabled detection.");

            // And the text is untouched: nothing was applied on the writer's behalf.
            Assert.Contains(
                RecordingAnalyzer.Marker,
                DocumentTextIndex.Build(page.Editor.Document).Text,
                "A correction was applied even though automatic correction was off.");
        }
        finally
        {
            window.Close();
            page.Dispose();
        }
    });
}
