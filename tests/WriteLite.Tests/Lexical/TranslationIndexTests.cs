using System.IO;
using WriteLite.Services.Lexical;

namespace WriteLite.Tests.Lexical;

/// <summary>
/// Guards the RU ↔ EN translation index.
/// </summary>
/// <remarks>
/// The dictionary page presents these values as translations. They come from
/// OpenRussian and are never generated, so what matters here is that the data is
/// actually reachable in both directions, that a missing file degrades to "no English
/// section" rather than to an exception, and that a headword written the way a user
/// would type it still resolves.
/// </remarks>
[TestClass]
public sealed class TranslationIndexTests
{
    private static string DatabasePath => Path.Combine(
        AppContext.BaseDirectory, "resources", "lexical", "ru-en-translations.db");

    private static TranslationIndex OpenOrSkip()
    {
        var index = TranslationIndex.TryOpen(DatabasePath);
        if (index is null)
        {
            Assert.Inconclusive("Translation index is not present in this build output.");
        }

        return index!;
    }

    [TestMethod]
    public void Missing_file_opens_as_null_rather_than_throwing()
    {
        var index = TranslationIndex.TryOpen(
            Path.Combine(Path.GetTempPath(), "writelite-no-such-translations.db"));

        Assert.IsNull(index);
    }

    [TestMethod]
    public void Russian_resolves_to_English()
    {
        using var index = OpenOrSkip();

        var translations = index.Translate("красивый", LexicalLanguage.Russian);

        Assert.IsNotEmpty(translations);
        CollectionAssert.Contains(translations.ToArray(), "beautiful");
    }

    [TestMethod]
    public void English_resolves_back_to_Russian()
    {
        using var index = OpenOrSkip();

        var translations = index.Translate("beautiful", LexicalLanguage.English);

        Assert.IsNotEmpty(translations);
        CollectionAssert.Contains(translations.ToArray(), "красивый");
    }

    /// <summary>
    /// Case and ё are how people actually type, and the index is keyed folded.
    /// </summary>
    [TestMethod]
    public void Lookup_folds_case_and_yo()
    {
        using var index = OpenOrSkip();

        Assert.IsNotEmpty(index.Translate("КРАСИВЫЙ", LexicalLanguage.Russian));
        Assert.IsNotEmpty(index.Translate("Beautiful", LexicalLanguage.English));
    }

    /// <summary>
    /// English glosses are stored bare, but a verb reads as "to read" on the card, and
    /// clicking that chip has to find the entry it names.
    /// </summary>
    [TestMethod]
    public void English_particles_are_stripped_before_lookup()
    {
        Assert.AreEqual("read", TranslationIndex.Normalize("to read", LexicalLanguage.English));
        Assert.AreEqual("book", TranslationIndex.Normalize("The Book", LexicalLanguage.English));
        Assert.AreEqual("apple", TranslationIndex.Normalize("an apple", LexicalLanguage.English));

        // Russian is only case- and ё-folded; it has no particles to strip.
        Assert.AreEqual("елка", TranslationIndex.Normalize("Ёлка", LexicalLanguage.Russian));
    }

    [TestMethod]
    public void Unknown_word_returns_empty_rather_than_null()
    {
        using var index = OpenOrSkip();

        Assert.IsEmpty(index.Translate("щщщнесуществующееслово", LexicalLanguage.Russian));
        Assert.IsEmpty(index.Translate(null, LexicalLanguage.Russian));
        Assert.IsEmpty(index.Translate("word", LexicalLanguage.Unknown));
    }
}
