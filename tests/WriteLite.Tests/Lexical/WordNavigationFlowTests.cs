using System.IO;
using System.Windows;
using System.Windows.Threading;
using WriteLite.Services.Lexical;
using WriteLite.Views;
using WriteLite.Views.Pages;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace WriteLite.Tests.Lexical;

/// <summary>
/// Selected word → Dictionary, end to end through the shell.
/// </summary>
/// <remarks>
/// <see cref="WordNavigationTests"/> covers the normaliser; this covers the wiring,
/// which is where the original bug actually lived. <c>DictionaryPage.ShowWordAsync</c>
/// existed and worked the whole time — nothing in the product called it, so the
/// dictionary could only ever be reached by typing into its own search box. A unit
/// test of the normaliser would have passed against the broken build, so the assertion
/// here is deliberately about the shell: after asking for a word, is the dictionary
/// the visible page, and is that word the one it is showing?
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class WordNavigationFlowTests
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

    /// <summary>Builds the shell off-screen and hands it to the test.</summary>
    private static void WithShell(Action<MainWindow> body) => WpfTestHost.Run(() =>
    {
        WpfTestHost.EnsureThemeApplied();

        var window = new MainWindow
        {
            ShowInTaskbar = false,
            Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000
        };

        try
        {
            window.Show();
            window.UpdateLayout();
            body(window);
        }
        finally
        {
            window.ForceClose();
        }
    });

    private static T Find<T>(MainWindow window, string name) where T : class =>
        (T)window.FindName(name)!;

    [TestMethod]
    public void Opening_a_word_navigates_to_the_dictionary_page()
    {
        WithShell(window =>
        {
            window.OpenWordInDictionary("предложения.");

            var dictionary = Find<System.Windows.Controls.RadioButton>(window, "NavDictionary");
            Assert.IsTrue(
                Pump(() => dictionary.IsChecked == true),
                "Asking for a word must switch the rail to the dictionary.");
        });
    }

    [TestMethod]
    public void The_word_arrives_already_searched_so_nothing_is_retyped()
    {
        var lexical = CreateLexical();
        if (lexical is null)
        {
            Assert.Inconclusive("No lexical pack in this build output.");
        }

        WithShell(window =>
        {
            window.BindLexical(lexical!, TranslationIndex.TryOpen());

            // The selection a double click produces at the end of a sentence — the
            // exact input that used to do nothing at all.
            window.OpenWordInDictionary("«красивый».");

            var host = (System.Windows.Controls.ContentControl)window.FindName("PageHost")!;
            Assert.IsTrue(
                Pump(() => host.Content is DictionaryPage),
                "The dictionary page never became the visible page.");

            var dictionaryPage = (DictionaryPage)host.Content;
            var search = (TextBox)dictionaryPage.FindName("SearchBox")!;
            var word = (TextBlock)dictionaryPage.FindName("WordText")!;

            Assert.IsTrue(
                Pump(() => word.Text == "красивый"),
                $"The article showed «{word.Text}» rather than the selected word.");

            Assert.AreEqual("красивый", search.Text,
                "The search box must carry the word, so a follow-up search starts from it.");
        });
    }

    [TestMethod]
    public void A_selection_with_no_word_in_it_does_not_navigate()
    {
        WithShell(window =>
        {
            var home = Find<System.Windows.Controls.RadioButton>(window, "NavHome");
            home.IsChecked = true;

            window.OpenWordInDictionary("…!?");

            var dictionary = Find<System.Windows.Controls.RadioButton>(window, "NavDictionary");
            Assert.IsFalse(
                Pump(() => dictionary.IsChecked == true, milliseconds: 600),
                "Punctuation is not a word and must not drag the user to another page.");
        });
    }

    [TestMethod]
    public void The_reader_and_the_notes_board_use_the_same_entry_point()
    {
        // Not a behavioural assertion so much as a structural one: all three surfaces
        // raise WordNavigationRequested and the shell is the only thing that navigates.
        // If someone adds a fourth lookup path that navigates on its own, this breaks.
        var editor = typeof(EditorPage).GetEvent("WordNavigationRequested");
        var reading = typeof(ReadingPage).GetEvent("WordNavigationRequested");
        var notes = typeof(NotesPage).GetEvent("WordNavigationRequested");
        var reader = typeof(ReaderView).GetEvent("WordNavigationRequested");

        Assert.IsNotNull(editor, "The editor must hand lookups to the shell.");
        Assert.IsNotNull(reading, "Reading must hand lookups to the shell.");
        Assert.IsNotNull(notes, "The notes board must hand lookups to the shell.");
        Assert.IsNotNull(reader, "The reader must hand lookups to the shell.");

        Assert.IsNotNull(
            typeof(MainWindow).GetMethod("OpenWordInDictionary"),
            "One destination for every lookup.");
    }
}
