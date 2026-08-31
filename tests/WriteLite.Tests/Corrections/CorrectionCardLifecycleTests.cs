using System.Windows;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Views;

namespace WriteLite.Tests.Corrections;

/// <summary>
/// The card as the user sees it: what is on screen after each transition, and which result
/// wins when several are in flight.
/// </summary>
/// <remarks>
/// <see cref="CorrectionCardStateTests"/> holds the phases apart in the abstract. These run
/// the real window, because the reported defect was not a wrong state — it was a window that
/// drew several states at once regardless of what any state object said.
/// </remarks>
[TestClass]
public sealed class CorrectionCardLifecycleTests
{
    private static readonly Rect Anchor = new(100, 100, 60, 18);

    /// <summary>
    /// The screenshot, reconstructed step by step, asserted not to reassemble.
    /// </summary>
    /// <remarks>
    /// The sequence that produced it: a card opens on a spelling finding, the text changes
    /// underneath it, the user presses the apply action anyway, and the write path answers
    /// «Текст изменился. Обновите предложение.» with a retry offer. Before the fix each step
    /// added controls to the card and removed none, ending with the explanation, the change
    /// row, the orange action, the red warning and «Повторить» all visible. Now the first
    /// step ends the result, so the later ones have nothing to pile onto.
    /// </remarks>
    [TestMethod]
    public void TheReportedCardCannotBeReassembled()
    {
        WpfTestHost.Run(() =>
        {
            WpfTestHost.EnsureThemeApplied();
            var popup = new CorrectionPopupWindow();
            popup.ShowForIssue(CorrectionPopupFallbackTests.Spelling("роботает", "работает"), Anchor);
            popup.UpdateLayout();
            Assert.Contains("Исправить", CorrectionPopupFallbackTests.ButtonsOf(popup));

            // The text underneath the card changed.
            popup.BeginRecheck();
            popup.UpdateLayout();

            var buttons = CorrectionPopupFallbackTests.ButtonsOf(popup);
            var texts = CorrectionPopupFallbackTests.TextOf(popup);
            Assert.IsFalse(buttons.Contains("Исправить"), "the old result's action is gone");
            Assert.IsFalse(buttons.Contains("Повторить"), "a text change is not a failure to retry");
            Assert.IsFalse(buttons.Contains("В словарь"));
            Assert.IsFalse(buttons.Contains("Игнорировать"));
            Assert.IsFalse(texts.Any(t => t.Contains("Текст изменился", StringComparison.Ordinal)));
            Assert.IsFalse(texts.Any(t => t.Contains("роботает", StringComparison.Ordinal)),
                "no stale result behind the progress state");
            Assert.Contains("Проверяем текст…", texts);

            popup.Dismiss();
            popup.Close();
        });
    }

    /// <summary>§2: original == suggestion is not a correction, in the real card.</summary>
    [TestMethod]
    public void AnIdenticalSuggestionDrawsNoArrowAndNoApplyAction()
    {
        WpfTestHost.Run(() =>
        {
            WpfTestHost.EnsureThemeApplied();
            var popup = new CorrectionPopupWindow();
            popup.ShowForIssue(CorrectionPopupFallbackTests.Spelling("Проверяем", "Проверяем"), Anchor);
            popup.UpdateLayout();

            var texts = CorrectionPopupFallbackTests.TextOf(popup);
            var buttons = CorrectionPopupFallbackTests.ButtonsOf(popup);

            Assert.IsFalse(texts.Contains("→"), "no replacement diff where there is no replacement");
            Assert.IsFalse(buttons.Contains("Исправить"), "no apply action without an alternative to apply");
            Assert.AreEqual(1, texts.Count(t => t.Contains("Проверяем", StringComparison.Ordinal)),
                "the word is named once, not drawn as «Проверяем → Проверяем»");

            popup.Close();
        });
    }

    /// <summary>§3: a word with no confident replacement gets an informational card.</summary>
    [TestMethod]
    public void AWordWithNoConfidentReplacementGetsAnInformationalCard()
    {
        WpfTestHost.Run(() =>
        {
            WpfTestHost.EnsureThemeApplied();
            var popup = new CorrectionPopupWindow();
            popup.ShowForIssue(CorrectionPopupFallbackTests.Spelling("Проверяем", "Проверяем"), Anchor);
            popup.UpdateLayout();

            var texts = CorrectionPopupFallbackTests.TextOf(popup);
            var buttons = CorrectionPopupFallbackTests.ButtonsOf(popup);

            // The category eyebrow is letter-spaced into per-glyph runs, so its automation
            // name is the only place the word survives as one string.
            Assert.Contains("ОРФОГРАФИЯ", AutomationNamesOf(popup));
            Assert.Contains("Слово не найдено в словаре", texts);
            Assert.Contains("WriteLite не удалось подобрать уверенное исправление для «Проверяем».", texts);
            Assert.Contains("В словарь", buttons);
            Assert.Contains("Игнорировать", buttons);
            Assert.IsFalse(buttons.Contains("Исправить"));

            popup.Close();
        });
    }

    /// <summary>§9: A starts, the text becomes B, A finishes — A is discarded.</summary>
    [TestMethod]
    public void AResultThatArrivesAfterTheTextChangedIsDiscarded()
    {
        WpfTestHost.Run(() =>
        {
            WpfTestHost.EnsureThemeApplied();
            var popup = new CorrectionPopupWindow();
            var inFlight = popup.BeginRecheckFrom(CorrectionPopupFallbackTests.Spelling("Сечас", "Сейчас"), Anchor);

            // The text changes again while the first refresh is still running.
            popup.BeginRecheck();

            // The first analysis comes back.
            popup.ShowForIssue(CorrectionPopupFallbackTests.Spelling("Сечас", "Сейчас"), Anchor, inFlight);
            popup.UpdateLayout();

            Assert.AreEqual(CorrectionCardPhase.Checking, popup.Phase, "the superseded result was not drawn");
            Assert.IsFalse(CorrectionPopupFallbackTests.TextOf(popup)
                .Any(t => t.Contains("Сейчас", StringComparison.Ordinal)));

            popup.Dismiss();
            popup.Close();
        });
    }

    /// <summary>
    /// §9: four selections finishing out of order. Only the newest may appear.
    /// </summary>
    [TestMethod]
    public void FastConsecutiveSelectionsOnlyEverRenderTheNewest()
    {
        WpfTestHost.Run(() =>
        {
            WpfTestHost.EnsureThemeApplied();
            var popup = new CorrectionPopupWindow();

            // A, B, C, D start in order; each captures the token it owns.
            var a = popup.BeginRecheckFrom(CorrectionPopupFallbackTests.Spelling("аа", "ааа"), Anchor);
            var b = popup.BeginRecheck();
            var c = popup.BeginRecheck();
            var d = popup.BeginRecheck();

            // They finish C, A, D, B.
            popup.ShowForIssue(CorrectionPopupFallbackTests.Spelling("сс", "ссс"), Anchor, c);
            popup.ShowForIssue(CorrectionPopupFallbackTests.Spelling("аа", "ааа"), Anchor, a);
            popup.ShowForIssue(CorrectionPopupFallbackTests.Spelling("дд", "ддд"), Anchor, d);
            popup.ShowForIssue(CorrectionPopupFallbackTests.Spelling("бб", "ббб"), Anchor, b);
            popup.UpdateLayout();

            var texts = CorrectionPopupFallbackTests.TextOf(popup);
            Assert.Contains("ддд", texts);
            foreach (var superseded in new[] { "ааа", "ббб", "ссс" })
            {
                Assert.IsFalse(texts.Any(t => t.Contains(superseded, StringComparison.Ordinal)),
                    $"{superseded} was superseded before it finished");
            }

            popup.Close();
        });
    }

    /// <summary>§5: a write outcome cannot repaint a card that has already moved on.</summary>
    [TestMethod]
    public void AWriteOutcomeForSupersededTextIsDiscarded()
    {
        WpfTestHost.Run(() =>
        {
            WpfTestHost.EnsureThemeApplied();
            var popup = new CorrectionPopupWindow();
            popup.ShowForIssue(CorrectionPopupFallbackTests.Spelling("роботает", "работает"), Anchor);
            var duringApply = popup.Token;

            // The text changed before the write came back.
            popup.BeginRecheck();
            popup.ShowApplyFeedback("WriteLite не удалось изменить текст в этом поле.",
                isError: true, offerCopy: true, offerRetry: true, token: duringApply);
            popup.UpdateLayout();

            Assert.AreEqual(CorrectionCardPhase.Checking, popup.Phase);
            Assert.IsFalse(CorrectionPopupFallbackTests.ButtonsOf(popup).Contains("Повторить"));

            popup.Dismiss();
            popup.Close();
        });
    }

    /// <summary>A genuine write failure is still reported, and is the only source of retry.</summary>
    [TestMethod]
    public void AGenuineWriteFailureStillOffersRetryAndDropsTheApplyAction()
    {
        WpfTestHost.Run(() =>
        {
            WpfTestHost.EnsureThemeApplied();
            var popup = new CorrectionPopupWindow();
            popup.ShowForIssue(CorrectionPopupFallbackTests.Spelling("роботает", "работает"), Anchor);
            popup.ShowApplyFeedback("WriteLite не удалось изменить текст в этом поле.",
                isError: true, offerCopy: true, offerRetry: true);
            popup.UpdateLayout();

            var buttons = CorrectionPopupFallbackTests.ButtonsOf(popup);
            Assert.Contains("Повторить", buttons);
            Assert.Contains("Скопировать", buttons);
            Assert.IsFalse(buttons.Contains("Исправить"), "the failure replaces the apply action");
            Assert.Contains("WriteLite не удалось изменить текст в этом поле.",
                CorrectionPopupFallbackTests.TextOf(popup));

            popup.Close();
        });
    }

    /// <summary>A refresh that no analysis ever answers must not leave a card spinning.</summary>
    [TestMethod]
    public void ARefreshThatIsNeverAnsweredClosesTheCard()
    {
        WpfTestHost.RunAsync(async () =>
        {
            WpfTestHost.EnsureThemeApplied();
            var popup = new CorrectionPopupWindow();
            popup.ShowForIssue(CorrectionPopupFallbackTests.Spelling("роботает", "работает"), Anchor);
            popup.BeginRecheck();
            Assert.AreEqual(CorrectionCardPhase.Checking, popup.Phase);

            await WpfTestHost.PumpAsync(
                (int)CorrectionPopupWindow.RecheckDeadline.TotalMilliseconds + 600);

            Assert.AreEqual(CorrectionCardPhase.Hidden, popup.Phase);
            Assert.IsFalse(popup.IsVisible);

            popup.Close();
        });
    }

    private static List<string> AutomationNamesOf(DependencyObject root) =>
        CorrectionPopupFallbackTests.Descendants(root)
            .OfType<System.Windows.Controls.TextBlock>()
            .Select(System.Windows.Automation.AutomationProperties.GetName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
}

internal static class CorrectionPopupTestExtensions
{
    /// <summary>Opens a card and immediately puts it into a refresh, returning that token.</summary>
    internal static long BeginRecheckFrom(this CorrectionPopupWindow popup, TextIssue issue, Rect anchor)
    {
        popup.ShowForIssue(issue, anchor);
        return popup.BeginRecheck();
    }
}
