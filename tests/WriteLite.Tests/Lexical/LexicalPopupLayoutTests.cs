using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WriteLite.Services.Lexical;
using WriteLite.Views;

namespace WriteLite.Tests.Lexical;

/// <summary>
/// The word card, laid out against the real theme with every section filled.
/// </summary>
/// <remarks>
/// Reported from the shipping build: with five sections present the tab strip showed «Сло»,
/// «Синони», «Определен», «Пример», «Рс» — every label cut short. Nothing in the data was
/// wrong, so no lookup test could see it; only laying the card out does.
/// Set <c>WL_RENDER_DIR</c> to also write the card as a PNG for inspection.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class LexicalPopupLayoutTests
{
    [TestMethod]
    public void Every_section_label_is_shown_in_full() => WpfTestHost.Run(() =>
    {
        WpfTestHost.EnsureThemeApplied();
        var popup = Show(FullResult());
        try
        {
            Dump(popup, "lexical-card-full.png");
            var headers = FindAll<TabItem>(popup)
                .Where(tab => tab.Visibility == Visibility.Visible)
                .Select(tab => (Label: tab.Header as string ?? "", Presenter: FindAll<ContentPresenter>(tab).FirstOrDefault()))
                .ToArray();

            Assert.IsTrue(headers.Length >= 5, $"expected every filled section to have a tab, got {headers.Length}");
            foreach (var (label, presenter) in headers)
            {
                Assert.IsNotNull(presenter, $"tab «{label}» has no header presenter");
                presenter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Assert.IsTrue(
                    presenter.ActualWidth + 0.5 >= presenter.DesiredSize.Width,
                    $"tab «{label}» is clipped: {presenter.ActualWidth:F1} of {presenter.DesiredSize.Width:F1} px shown");
            }
        }
        finally
        {
            popup.Close();
        }
    });

    [TestMethod]
    public void Word_section_states_each_fact_once() => WpfTestHost.Run(() =>
    {
        WpfTestHost.EnsureThemeApplied();
        var popup = Show(FullResult());
        try
        {
            var texts = FindAll<TextBlock>(popup)
                .Where(t => t.IsVisible)
                .Select(t => t.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToArray();

            Assert.IsFalse(texts.Any(t => t.Contains('?')), "a guess is not a fact: " + string.Join(" | ", texts.Where(t => t.Contains('?'))));
            Assert.AreEqual(1, texts.Count(t => t.Contains("совершенный", StringComparison.Ordinal)),
                "aspect must be stated once: " + string.Join(" | ", texts));
        }
        finally
        {
            popup.Close();
        }
    });

    private static LexicalPopupWindow Show(LexicalLookupResult result)
    {
        var popup = new LexicalPopupWindow { Opacity = 0, ShowActivated = false };
        var range = new WordRange(0, result.Word.Length, result.Word, Sentence, LexicalLanguage.Russian);
        popup.ShowResult(
            result, range, Sentence, "test-field", 1, 1, supportsWrite: true,
            new Rect(200, 200, 40, 18), requestId: 1,
            translations: ["to take up", "to get busy with"]);
        popup.UpdateLayout();
        return popup;
    }

    private const string Sentence = "Во-вторых, мы должны заняться расширением словаря.";

    private static LexicalLookupResult FullResult() => new(
        "заняться",
        "заняться",
        LexicalLanguage.Russian,
        LexicalPartOfSpeech.Verb,
        [
            new LexicalSuggestion("взяться", "взяться", LexicalPartOfSpeech.Verb, null, 0.9, true),
            new LexicalSuggestion("приступить", "приступить", LexicalPartOfSpeech.Verb, null, 0.8, true),
        ],
        [
            new LexicalDefinition("Начать делать что-либо, посвятить чему-либо своё время.", LexicalPartOfSpeech.Verb, null, SourceId: "test"),
            new LexicalDefinition("Начать учиться у кого-либо или с кем-либо.", LexicalPartOfSpeech.Verb, null, SourceId: "test"),
        ],
        [new LexicalExample("Пора заняться делом.", null, "заняться")],
        RequestId: 1,
        Morphology: new MorphologicalFeatures(Aspect: "совершенный", Transitivity: "непереходный, возвратный"),
        Syntax: new SyntacticAnalysis(SyntacticRole.Predicate, "мы", null, "Часть составного сказуемого.", []),
        Antonyms: [],
        Relations: []);

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is T match) yield return match;

            var count = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(node) : 0;
            for (var i = 0; i < count; i++) stack.Push(VisualTreeHelper.GetChild(node, i));

            // Unselected tabs are not in the visual tree, but their headers are what is measured.
            if (count == 0)
            {
                foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
                {
                    stack.Push(child);
                }
            }
        }
    }

    private static void Dump(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("WL_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(directory) || window.Content is not FrameworkElement content) return;

        Directory.CreateDirectory(directory);
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var opaque = new DrawingVisual();
        using (var dc = opaque.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }

        bitmap.Render(opaque);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }
}
