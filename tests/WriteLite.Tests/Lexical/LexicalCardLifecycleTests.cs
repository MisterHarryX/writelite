using System.Windows;
using WriteLite.Services.Lexical;
using WriteLite.Views;

namespace WriteLite.Tests.Lexical;

/// <summary>
/// When the dictionary card stays on screen and when it goes away.
/// </summary>
/// <remarks>
/// The reported defect these cover: double-clicking a word in Discord opened the card and
/// then something closed it on its own, or the second click of the double-click did. Both
/// came from treating the field monitor's snapshot stream as a statement about the user's
/// intent. It is not one — it republishes on state changes and publishes nothing at all
/// while switching fields — so the card is now invalidated only by facts about the field
/// (<see cref="LexicalPopupWindow.IsStaleFor"/>) and dismissed only by a click the user
/// actually made.
/// </remarks>
[TestClass]
public sealed class LexicalCardLifecycleTests
{
    private const string FieldId = "42.31337.7";
    private const string OtherFieldId = "42.31337.8";
    private const string Text = "Мы обсудили этот вопрос вчера вечером.";

    [TestMethod]
    public void CardSurvivesASnapshotThatTracksNoField()
    {
        // The monitor publishes null while it is between fields and whenever the tracked
        // window loses activation. Showing the card and then hiding it a frame later, with
        // the pointer still over the word, is the visible bug this prevents.
        WithOpenCard(card => Assert.IsFalse(card.IsStaleFor(null, null)));
    }

    [TestMethod]
    public void CardSurvivesARepublishOfTheSameFieldWithUnchangedText()
    {
        WithOpenCard(card => Assert.IsFalse(card.IsStaleFor(FieldId, Text)));
    }

    [TestMethod]
    public void CardGoesAwayWhenTheFieldItDescribesIsEdited()
    {
        WithOpenCard(card => Assert.IsTrue(card.IsStaleFor(FieldId, Text + " Ещё одно предложение.")));
    }

    [TestMethod]
    public void CardGoesAwayWhenADifferentFieldIsTracked()
    {
        WithOpenCard(card => Assert.IsTrue(card.IsStaleFor(OtherFieldId, "совсем другой текст")));
    }

    [TestMethod]
    public void AClickOnTheCardIsNotAClickOutsideIt()
    {
        WithOpenCard(card =>
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(card);
            var inside = new Point(
                (card.Left + (card.Width / 2)) * dpi.DpiScaleX,
                (card.Top + 10) * dpi.DpiScaleY);

            Assert.IsTrue(
                card.ContainsPhysicalPoint(inside),
                "A press on the card must not dismiss it, or none of its buttons can be used.");
        });
    }

    [TestMethod]
    public void AClickWellAwayFromTheCardIsOutsideIt()
    {
        WithOpenCard(card =>
        {
            var far = new Point(card.Left - 4000, card.Top - 4000);
            Assert.IsFalse(card.ContainsPhysicalPoint(far));
        });
    }

    [TestMethod]
    public void AHiddenCardContainsNothing()
    {
        WithOpenCard(card =>
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(card);
            var inside = new Point(
                (card.Left + (card.Width / 2)) * dpi.DpiScaleX,
                (card.Top + 10) * dpi.DpiScaleY);

            card.Hide();

            // Otherwise a press anywhere would be treated as landing on a card that is not
            // on screen, and the correction card underneath it would never see the click.
            Assert.IsFalse(card.ContainsPhysicalPoint(inside));
        });
    }

    private static void WithOpenCard(Action<LexicalPopupWindow> assert)
    {
        WpfTestHost.Run(() =>
        {
            // Inside Run: the theme, the Application and the window must all belong to the
            // one UI thread, or the brushes the card is built from are owned elsewhere.
            WpfTestHost.EnsureThemeApplied();
            var card = new LexicalPopupWindow();
            try
            {
                card.ShowLoading(
                    new WordRange(
                        Text.IndexOf("вопрос", StringComparison.Ordinal),
                        "вопрос".Length,
                        "вопрос",
                        Text,
                        LexicalLanguage.Russian),
                    new Rect(400, 400, 60, 18),
                    requestId: 1,
                    targetId: FieldId,
                    generationId: 3,
                    textVersion: 11,
                    fullText: Text);

                assert(card);
            }
            finally
            {
                card.Close();
            }
        });
    }
}
