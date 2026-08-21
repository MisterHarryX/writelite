using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WriteLite.Controls;
using WriteLite.Services.Lexical;
using WriteLite.Views.Pages;

namespace WriteLite.Tests;

/// <summary>
/// Drives the dictionary page against the real lexical services.
/// </summary>
/// <remarks>
/// The page's whole point is that a reader can walk from one word to the next, so the
/// things worth guarding are the ones that break that walk: chips that are not
/// actually clickable, translations that do not appear, a history that cannot be
/// retraced. All of it is exercised in-process against the installed packs rather
/// than by driving the running application, which would mean putting synthetic input
/// on a live desktop.
///
/// Not parallelised: every UI test marshals onto the one shared dispatcher, and
/// pumping it from inside one test would otherwise run another test's body nested
/// inside this one.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class DictionaryPageTests
{
    private static OfflineLexicalKnowledgeService? CreateLexical()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "resources", "lexical");
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var service = new OfflineLexicalKnowledgeService();
        service.LoadFromDirectory(directory);
        return service.IsPackLoaded ? service : null;
    }

    /// <summary>
    /// Builds a page against the real theme and hands it to the test.
    /// </summary>
    /// <remarks>
    /// No host window. These assertions are about what the page builds — which
    /// blocks it shows, which chips it creates, where history points — and none of
    /// that needs compositing. Showing a window per test on the single shared
    /// Application is what made this class flaky; PageRenderSmokeTests already
    /// covers the "does it lay out at all" question.
    /// </remarks>
    private static void WithPage(Action<DictionaryPage> body) => WpfTestHost.Run(() =>
    {
        WpfTestHost.EnsureThemeApplied();

        var page = new DictionaryPage();
        page.Measure(new Size(1280, 800));
        page.Arrange(new Rect(0, 0, 1280, 800));

        body(page);
    });

    /// <summary>
    /// Pumps the dispatcher until <paramref name="until"/> holds or time runs out.
    /// </summary>
    /// <remarks>
    /// The page's lookup is asynchronous and its continuations are posted back to the
    /// dispatcher. These tests run on a thread whose dispatcher has no message loop,
    /// and <c>Dispatcher.Invoke</c> from that same thread runs the callback inline
    /// without ever draining the queue — so the continuations would sit there and the
    /// article would never appear. Pushing a frame is what actually processes them.
    /// </remarks>
    private static bool Pump(Func<bool> until, int milliseconds = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (until())
            {
                return true;
            }

            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);

            Thread.Sleep(15);
        }

        return until();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static WordChip[] Chips(DictionaryPage page) => Descendants<WordChip>(page).ToArray();

    /// <summary>Opens a word through the page's own public entry point.</summary>
    private static void Search(DictionaryPage page, string word) => _ = page.ShowWordAsync(word);

    private static void Click(DictionaryPage page, string name)
    {
        var button = (Button)page.FindName(name)!;
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    [TestMethod]
    public void Lookup_renders_an_article_with_clickable_synonyms()
    {
        var lexical = CreateLexical();
        if (lexical is null)
        {
            Assert.Inconclusive("No lexical pack in this build output.");
        }

        WithPage(page =>
        {
            page.Bind(lexical!, TranslationIndex.TryOpen());
            Search(page, "красивый");

            var article = (FrameworkElement)page.FindName("Article")!;
            Assert.IsTrue(
                Pump(() => article.Visibility == Visibility.Visible),
                "The article never became visible for «красивый».");

            var word = (System.Windows.Controls.TextBlock)page.FindName("WordText")!;
            Assert.AreEqual("красивый", word.Text);

            // Chips are the navigation surface: if they are not focusable buttons the
            // page is mouse-only, which is the failure this guards.
            var chips = Chips(page);
            Assert.IsNotEmpty(chips, "The article rendered no related-word chips.");
            Assert.IsTrue(
                chips.Any(chip => chip.HasArticle && chip.Focusable),
                "No chip is both navigable and reachable from the keyboard.");
        });
    }

    [TestMethod]
    public void Russian_word_offers_clickable_English_translations()
    {
        var lexical = CreateLexical();
        var translations = TranslationIndex.TryOpen();
        if (lexical is null || translations is null)
        {
            Assert.Inconclusive("No lexical pack or translation index in this build output.");
        }

        WithPage(page =>
        {
            page.Bind(lexical!, translations);
            Search(page, "красивый");

            var block = (FrameworkElement)page.FindName("TranslationsBlock")!;
            Assert.IsTrue(
                Pump(() => block.Visibility == Visibility.Visible),
                "No English translations were shown for «красивый».");

            var english = Chips(page)
                .Where(chip => chip.Language == LexicalLanguage.English)
                .Select(chip => chip.Word)
                .ToArray();

            CollectionAssert.Contains(english, "beautiful");
        });
    }

    /// <summary>
    /// The direction that closes the loop: an English word has to lead back to Russian,
    /// otherwise the trail is one-way and the "graph" is a dead end.
    /// </summary>
    [TestMethod]
    public void English_word_offers_clickable_Russian_translations()
    {
        var lexical = CreateLexical();
        var translations = TranslationIndex.TryOpen();
        if (lexical is null || translations is null)
        {
            Assert.Inconclusive("No lexical pack or translation index in this build output.");
        }

        WithPage(page =>
        {
            page.Bind(lexical!, translations);
            Search(page, "beautiful");

            var block = (FrameworkElement)page.FindName("TranslationsBlock")!;
            Assert.IsTrue(
                Pump(() => block.Visibility == Visibility.Visible),
                "No Russian translations were shown for \"beautiful\".");

            var russian = Chips(page)
                .Where(chip => chip.Language == LexicalLanguage.Russian)
                .Select(chip => chip.Word)
                .ToArray();

            CollectionAssert.Contains(russian, "красивый");
        });
    }

    [TestMethod]
    public void History_walks_back_and_forward_between_words()
    {
        var lexical = CreateLexical();
        if (lexical is null)
        {
            Assert.Inconclusive("No lexical pack in this build output.");
        }

        WithPage(page =>
        {
            page.Bind(lexical!, TranslationIndex.TryOpen());

            var back = (Button)page.FindName("BackButton")!;
            var forward = (Button)page.FindName("ForwardButton")!;
            var word = (System.Windows.Controls.TextBlock)page.FindName("WordText")!;

            Assert.IsFalse(back.IsEnabled, "Back is offered before anything has been looked up.");
            Assert.IsFalse(forward.IsEnabled, "Forward is offered before anything has been looked up.");

            Search(page, "красивый");
            Assert.IsTrue(Pump(() => word.Text == "красивый"), "First lookup did not render.");

            Search(page, "книга");
            Assert.IsTrue(Pump(() => word.Text == "книга"), "Second lookup did not render.");
            Assert.IsTrue(back.IsEnabled, "Back is not offered after two lookups.");

            Click(page, "BackButton");
            Assert.IsTrue(Pump(() => word.Text == "красивый"), "Back did not return to the previous word.");
            Assert.IsTrue(forward.IsEnabled, "Forward is not offered after going back.");

            Click(page, "ForwardButton");
            Assert.IsTrue(Pump(() => word.Text == "книга"), "Forward did not return to the later word.");
            Assert.IsFalse(forward.IsEnabled, "Forward is still offered at the end of the trail.");
        });
    }

    /// <summary>
    /// Clicking a synonym opens that word — the whole point of the chips.
    /// </summary>
    /// <remarks>
    /// Raises the chip's own Click event, so the assertion covers the handler being
    /// attached, not just the chip being drawn. A chip that renders and does nothing
    /// is exactly the failure this page was redesigned to remove.
    /// </remarks>
    [TestMethod]
    public void Clicking_a_synonym_opens_that_word_and_extends_the_trail()
    {
        var lexical = CreateLexical();
        if (lexical is null)
        {
            Assert.Inconclusive("No lexical pack in this build output.");
        }

        WithPage(page =>
        {
            page.Bind(lexical!, TranslationIndex.TryOpen());
            Search(page, "красивый");

            var word = (System.Windows.Controls.TextBlock)page.FindName("WordText")!;
            Assert.IsTrue(Pump(() => word.Text == "красивый"), "The first article did not render.");

            var chip = Chips(page).First(c => c.HasArticle && c.Language == LexicalLanguage.Russian);
            var target = chip.Word;

            chip.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            Assert.IsTrue(
                Pump(() => word.Text == target),
                $"Clicking the chip «{target}» did not open its article.");

            // And the trail can be walked back, which is what makes following chips safe.
            var back = (Button)page.FindName("BackButton")!;
            Assert.IsTrue(back.IsEnabled, "Following a chip left no way back.");

            Click(page, "BackButton");
            Assert.IsTrue(Pump(() => word.Text == "красивый"), "Back did not return to the word we came from.");
        });
    }

    /// <summary>
    /// The loading placeholder must never outlive the thing it was standing in for.
    /// </summary>
    /// <remarks>
    /// It did. The skeleton was scheduled for 140 ms after the lookup started and had
    /// no idea the lookup had already finished, so every fast entry — which is nearly
    /// all of them, the store answers in microseconds — was drawn and then covered by
    /// a placeholder that never went away.
    /// </remarks>
    [TestMethod]
    public void Skeleton_does_not_reappear_over_a_rendered_article()
    {
        var lexical = CreateLexical();
        if (lexical is null)
        {
            Assert.Inconclusive("No lexical pack in this build output.");
        }

        WithPage(page =>
        {
            page.Bind(lexical!, TranslationIndex.TryOpen());
            Search(page, "красивый");

            var article = (FrameworkElement)page.FindName("Article")!;
            var loading = (FrameworkElement)page.FindName("LoadingState")!;

            Assert.IsTrue(Pump(() => article.Visibility == Visibility.Visible), "The article never rendered.");

            // Well past the skeleton's delay: if it is still scheduled, this is when
            // it lands on top of the article.
            Pump(() => false, 500);

            Assert.AreEqual(
                Visibility.Collapsed,
                loading.Visibility,
                "The loading skeleton reappeared after the article had rendered.");
            Assert.AreEqual(Visibility.Visible, article.Visibility);
        });
    }

    /// <summary>
    /// A word the packs do not have must say so rather than rendering an empty article
    /// or, worse, inventing one.
    /// </summary>
    [TestMethod]
    public void Unknown_word_shows_the_empty_state()
    {
        var lexical = CreateLexical();
        if (lexical is null)
        {
            Assert.Inconclusive("No lexical pack in this build output.");
        }

        WithPage(page =>
        {
            page.Bind(lexical!, TranslationIndex.TryOpen());
            Search(page, "щщщнесуществующееслово");

            var empty = (FrameworkElement)page.FindName("LookupEmptyState")!;
            var article = (FrameworkElement)page.FindName("Article")!;

            Assert.IsTrue(
                Pump(() => empty.Visibility == Visibility.Visible && article.Visibility != Visibility.Visible),
                "An unknown word did not fall back to the empty state.");
        });
    }
}
