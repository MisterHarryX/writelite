using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WriteLite.Language.Russian;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Rules;
using WriteLite.Views;
using WriteLite.Views.Pages;

namespace WriteLite.Tests.Language;

/// <summary>
/// The rules reference, and the rule note a correction card shows from it.
/// </summary>
/// <remarks>
/// The point of the note is that a finding says why it is an error — the rule, an example,
/// and a way to read more — rather than only what to change. These tests hold the two ends of
/// that together: every rule the checker reports has an entry, and the card shows it.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class RuleReferenceTests
{
    [TestMethod]
    public void Every_rule_the_analyzers_report_has_an_entry()
    {
        var reference = RuleReference.Default;
        var source = Path.Combine(FindRepoRoot(), "src", "WriteLite.App", "Services");
        var reported = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "RuleId:\\s*\"(ru\\.[^\"]+)\"").Select(m => m.Groups[1].Value))
            .Distinct()
            .ToArray();

        // Internal plumbing ids that never reach a card as a rule of Russian.
        var exempt = new HashSet<string>
        {
            "ru.context.real-word", "ru.punctuation.model-comma", "ru.writing.continuation",
            "ru.style.repetition-across-sentences", "ru.style.double-comparative", "ru.spelling.particle-to",
        };

        var missing = reported.Where(id => !exempt.Contains(id) && reference.Find(id) is null).ToArray();
        Assert.IsEmpty(missing, "rules without a reference entry: " + string.Join(", ", missing));
    }

    [TestMethod]
    public void Entries_carry_an_explanation_and_a_source()
    {
        foreach (var entry in RuleReference.Default.Sections.SelectMany(s => s.Entries))
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(entry.Explanation), entry.RuleId);
            Assert.IsFalse(string.IsNullOrWhiteSpace(entry.Source), entry.RuleId);
        }
    }

    [TestMethod]
    public void Search_finds_by_any_word_of_the_rule()
    {
        var hits = RuleReference.Default.Search("косвенн").SelectMany(s => s.Entries).Select(e => e.RuleId).ToArray();
        CollectionAssert.Contains(hits, "ru.punctuation.indirect-question");

        var byExample = RuleReference.Default.Search("почему это ошибка").SelectMany(s => s.Entries).Select(e => e.RuleId).ToArray();
        CollectionAssert.Contains(byExample, "ru.punctuation.indirect-question", "examples are searchable too");
    }

    [TestMethod]
    public void Correction_card_shows_the_rule_behind_the_finding() => WpfTestHost.Run(() =>
    {
        WpfTestHost.EnsureThemeApplied();
        var analyzer = new RuleBasedAnalyzer(RuleCatalog.LoadDefault(), RussianFormIndex.Load());
        var issue = analyzer.Analyze("Нужно указывать почему это ошибка.")
            .Single(i => i.RuleId == "ru.punctuation.indirect-question");

        var card = new CorrectionPopupWindow { Opacity = 0 };
        try
        {
            card.ShowForIssue(issue, new Rect(300, 300, 60, 18));
            card.UpdateLayout();

            var texts = Texts(card);
            Assert.IsTrue(texts.Any(t => t.EndsWith("Запятая перед косвенным вопросом", StringComparison.Ordinal)), string.Join(" | ", texts));
            Assert.IsTrue(texts.Any(t => t.Contains("Нужно указывать, почему это ошибка.", StringComparison.Ordinal)),
                "the example pair is shown: " + string.Join(" | ", texts));

            string? opened = null;
            card.RuleReferenceRequested += (_, id) => opened = id;
            var link = FindAll<Button>(card).Single(b => Equals(b.Content, WriteLite.Resources.Strings.Corr_RuleOpenReference));
            link.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.AreEqual("ru.punctuation.indirect-question", opened);

            Dump(card, "correction-card-rule.png");
        }
        finally
        {
            card.Close();
        }
    });

    [TestMethod]
    public void Rules_page_opens_the_requested_rule() => WpfTestHost.Run(() =>
    {
        WpfTestHost.EnsureThemeApplied();
        var page = new RulesPage();
        var host = new Window { Content = page, Width = 900, Height = 700, Opacity = 0, ShowActivated = false };
        try
        {
            host.Show();
            Assert.IsTrue(page.ShowRule("ru.grammar.agreement"));
            Assert.IsTrue(page.IsOpen("ru.grammar.agreement"));
            Assert.IsFalse(page.IsOpen("ru.punctuation.gerund-phrase"));
            Assert.IsFalse(page.ShowRule("ru.no-such-rule"));
            host.UpdateLayout();
            Dump(host, "rules-page.png");
        }
        finally
        {
            host.Close();
        }
    });

    private static string[] Texts(DependencyObject root)
        => FindAll<TextBlock>(root)
            .Where(t => t.IsVisible)
            .Select(t => string.Concat(t.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)) is { Length: > 0 } runs ? runs : t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToArray();

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is T match) yield return match;
            var count = node is Visual ? VisualTreeHelper.GetChildrenCount(node) : 0;
            for (var i = 0; i < count; i++) stack.Push(VisualTreeHelper.GetChild(node, i));
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WriteLite.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static void Dump(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("WL_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(directory) || window.Content is not FrameworkElement content) return;

        Directory.CreateDirectory(directory);
        var width = content.ActualWidth;
        var height = content.ActualHeight;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, width, height));
        }

        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }
}
