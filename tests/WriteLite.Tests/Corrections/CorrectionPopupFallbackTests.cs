using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WriteLite.Models;
using WriteLite.Views;

namespace WriteLite.Tests.Corrections;

/// <summary>
/// §12 and §17: what the card shows, and what it offers when the correction did not apply.
/// </summary>
[TestClass]
public sealed class CorrectionPopupFallbackTests
{
    [TestMethod]
    public void TheCardDrawsTheCorrectionAndNothingElse()
    {
        WpfTestHost.Run(() =>
        {
            // Inside Run: the theme, the Application and the window must all belong to the
            // one UI thread, or the brushes the card is built from are owned elsewhere.
            WpfTestHost.EnsureThemeApplied();
            var popup = new CorrectionPopupWindow();
            popup.ShowForIssue(Spelling("роботает", "работает"), new Rect(100, 100, 60, 18));

            var texts = TextOf(popup);
            Assert.Contains("роботает", texts);
            Assert.Contains("работает", texts);
            Assert.IsFalse(texts.Any(t => t.Contains('·')), "no split glyph on a word correction");

            // The fragments the card draws still spell both sides — §4.
            var reassembled = TextOf(popup);
            Assert.Contains("роботает", reassembled);

            popup.Close();
        });
    }

    [TestMethod]
    public void AMultiWordReplacementIsDrawnAsWords()
    {
        WpfTestHost.Run(() =>
        {
            // Inside Run: the theme, the Application and the window must all belong to the
            // one UI thread, or the brushes the card is built from are owned elsewhere.
            WpfTestHost.EnsureThemeApplied();
            var popup = new CorrectionPopupWindow();
            popup.ShowForIssue(Spelling("вообщем", "в общем"), new Rect(100, 100, 60, 18));

            var texts = TextOf(popup);
            Assert.Contains("в общем", texts);
            Assert.IsFalse(texts.Any(t => t.Contains('·')));

            popup.Close();
        });
    }

    [TestMethod]
    public void AFailedCorrectionOffersCopyAndRetryOnTheCard()
    {
        // §12: no modal dialog for an ordinary failure, and the recovery actions follow the
        // reason rather than the failure.
        WpfTestHost.Run(() =>
        {
            // Inside Run: the theme, the Application and the window must all belong to the
            // one UI thread, or the brushes the card is built from are owned elsewhere.
            WpfTestHost.EnsureThemeApplied();
            var popup = new CorrectionPopupWindow();
            popup.ShowForIssue(Spelling("роботает", "работает"), new Rect(100, 100, 60, 18));

            popup.ShowApplyFeedback(
                "WriteLite не удалось изменить текст в этом поле.",
                isError: true,
                offerCopy: true,
                offerRetry: true);
            popup.UpdateLayout();

            var buttons = ButtonsOf(popup);
            Assert.Contains("Скопировать", buttons);
            Assert.Contains("Повторить", buttons);
            Assert.Contains("WriteLite не удалось изменить текст в этом поле.", TextOf(popup));

            popup.Close();
        });
    }

    [TestMethod]
    public void ATrulyReadOnlyFieldOffersOnlyCopy()
    {
        WpfTestHost.Run(() =>
        {
            // Inside Run: the theme, the Application and the window must all belong to the
            // one UI thread, or the brushes the card is built from are owned elsewhere.
            WpfTestHost.EnsureThemeApplied();
            var popup = new CorrectionPopupWindow();
            popup.ShowForIssue(Spelling("роботает", "работает"), new Rect(100, 100, 60, 18));

            var outcome = Services.CorrectionApplicationOutcome.FromStatus(
                Services.CorrectionApplicationStatus.ReadOnly);
            popup.ShowApplyFeedback(outcome.UserMessage, true, outcome.OfferCopy, outcome.OfferRefresh);
            popup.UpdateLayout();

            var buttons = ButtonsOf(popup);
            Assert.Contains("Скопировать", buttons);
            Assert.IsFalse(buttons.Contains("Повторить"), "a read-only field will not become writable on retry");
            Assert.IsTrue(outcome.UserMessage.Contains("только для чтения", StringComparison.Ordinal));

            popup.Close();
        });
    }

    [TestMethod]
    public void ShowingAFreshIssueClearsTheFailureRow()
    {
        WpfTestHost.Run(() =>
        {
            // Inside Run: the theme, the Application and the window must all belong to the
            // one UI thread, or the brushes the card is built from are owned elsewhere.
            WpfTestHost.EnsureThemeApplied();
            var popup = new CorrectionPopupWindow();
            popup.ShowForIssue(Spelling("роботает", "работает"), new Rect(100, 100, 60, 18));
            popup.ShowApplyFeedback("не удалось", true, offerCopy: true, offerRetry: true);
            popup.UpdateLayout();
            Assert.Contains("Скопировать", ButtonsOf(popup));

            popup.ShowForIssue(Spelling("Сечас", "Сейчас"), new Rect(100, 100, 60, 18));
            popup.UpdateLayout();
            Assert.IsFalse(ButtonsOf(popup).Contains("Скопировать"));

            popup.Close();
        });
    }

    private static TextIssue Spelling(string original, string replacement) => new(
        0, original.Length, original, replacement,
        "Орфография", "Слово не найдено в русском орфографическом словаре.",
        IssueCategory.Orthography, IssueSeverity.Warning,
        CanApplyAutomatically: false, RuleId: "ru.spelling.typo");

    private static List<string> TextOf(DependencyObject root) =>
        Descendants(root).OfType<TextBlock>()
            .Select(t => t.Text.Length > 0 ? t.Text : string.Concat(t.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

    private static List<string> ButtonsOf(DependencyObject root) =>
        Descendants(root).OfType<Button>()
            .Select(b => b.Content as string ?? string.Empty)
            .Where(t => t.Length > 0)
            .ToList();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
