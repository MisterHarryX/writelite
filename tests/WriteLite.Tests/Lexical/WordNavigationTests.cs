using WriteLite.Services.Lexical;

namespace WriteLite.Tests.Lexical;

/// <summary>
/// Turning a selection into a dictionary query.
/// </summary>
/// <remarks>
/// The bug this replaced: the editor accepted a selection only if every character was
/// a letter, so double-clicking a word at the end of a sentence produced «слова.» and
/// the lookup silently did nothing. Every case below is a selection a real gesture
/// actually produces.
/// </remarks>
[TestClass]
public sealed class WordNavigationTests
{
    [TestMethod]
    public void A_plain_word_is_returned_as_it_is()
    {
        Assert.AreEqual("слово", WordNavigation.Normalize("слово"));
        Assert.AreEqual("Слово", WordNavigation.Normalize("Слово"), "Case is the reader's, not ours.");
    }

    [TestMethod]
    public void Trailing_sentence_punctuation_is_stripped()
    {
        Assert.AreEqual("слова", WordNavigation.Normalize("слова."));
        Assert.AreEqual("слова", WordNavigation.Normalize("слова,"));
        Assert.AreEqual("слова", WordNavigation.Normalize("слова!"));
        Assert.AreEqual("слова", WordNavigation.Normalize("слова?"));
        Assert.AreEqual("слова", WordNavigation.Normalize("слова…"));
        Assert.AreEqual("слова", WordNavigation.Normalize("слова;"));
    }

    [TestMethod]
    public void Quotes_and_brackets_are_stripped_from_both_ends()
    {
        Assert.AreEqual("цитата", WordNavigation.Normalize("«цитата»"));
        Assert.AreEqual("цитата", WordNavigation.Normalize("\"цитата\""));
        Assert.AreEqual("цитата", WordNavigation.Normalize("(цитата)"));
        Assert.AreEqual("цитата", WordNavigation.Normalize("— цитата —"));
    }

    [TestMethod]
    public void Whitespace_around_a_selection_does_not_count()
    {
        Assert.AreEqual("слово", WordNavigation.Normalize("  слово \r\n"));
        Assert.AreEqual("слово", WordNavigation.Normalize(" слово "), "Non-breaking spaces travel with pasted text.");
    }

    [TestMethod]
    public void A_hyphen_inside_a_word_is_part_of_it()
    {
        Assert.AreEqual("кто-то", WordNavigation.Normalize("кто-то"));
        Assert.AreEqual("кто-то", WordNavigation.Normalize("кто-то."));
        Assert.AreEqual("по-русски", WordNavigation.Normalize("«по-русски»"));
    }

    [TestMethod]
    public void A_dangling_hyphen_from_a_line_wrap_is_dropped()
    {
        Assert.AreEqual("пере", WordNavigation.Normalize("пере-"));
    }

    [TestMethod]
    public void An_apostrophe_inside_a_word_survives_in_either_shape()
    {
        Assert.AreEqual("don't", WordNavigation.Normalize("don't"));
        Assert.AreEqual("don't", WordNavigation.Normalize("don’t"), "A typographic apostrophe is the same word.");
    }

    [TestMethod]
    public void A_soft_hyphen_does_not_split_a_word()
    {
        Assert.AreEqual("предложение", WordNavigation.Normalize("пред­ложение"));
    }

    [TestMethod]
    public void A_phrase_resolves_to_its_first_word_rather_than_to_nothing()
    {
        // Someone who selects a sentence and asks for the dictionary wants an article,
        // not a shrug that sends them back to retype a word.
        Assert.AreEqual("Первое", WordNavigation.Normalize("Первое слово предложения."));
        Assert.AreEqual("Важная", WordNavigation.Normalize("«Важная мысль» — сказал он."));
    }

    [TestMethod]
    public void Nothing_to_look_up_returns_null()
    {
        Assert.IsNull(WordNavigation.Normalize(null));
        Assert.IsNull(WordNavigation.Normalize(""));
        Assert.IsNull(WordNavigation.Normalize("   "));
        Assert.IsNull(WordNavigation.Normalize("...!?"));
        Assert.IsNull(WordNavigation.Normalize("42"));
        Assert.IsNull(WordNavigation.Normalize("—"));
    }

    [TestMethod]
    public void An_over_long_selection_is_prose_not_a_word()
    {
        Assert.IsNull(WordNavigation.Normalize(new string('я', 60)));
    }

    [TestMethod]
    public void CanOpen_agrees_with_Normalize()
    {
        Assert.IsTrue(WordNavigation.CanOpen("слова."));
        Assert.IsTrue(WordNavigation.CanOpen("«цитата»"));
        Assert.IsFalse(WordNavigation.CanOpen("   "));
        Assert.IsFalse(WordNavigation.CanOpen("123"));
    }

    [TestMethod]
    public void The_selection_a_double_click_produces_at_a_sentence_end_opens_an_article()
    {
        // The exact failing case. A double click on the last word of a sentence
        // selects the word plus its full stop in a WPF RichTextBox.
        const string doubleClicked = "предложения.";

        Assert.IsTrue(WordNavigation.CanOpen(doubleClicked));
        Assert.AreEqual("предложения", WordNavigation.Normalize(doubleClicked));
    }
}
