using WriteLite.Language.Russian;
using WriteLite.Services.Lexical;

namespace WriteLite.Tests.Lexical;

/// <summary>
/// The dictionary card's grammar block, read from the form index rather than guessed.
/// </summary>
/// <remarks>
/// Reported from the shipping build: double-clicking «заняться» showed «вид: несов.?». The
/// card's morphology was a suffix heuristic that called every verb not starting with «по»,
/// «с», «на» or «вы» imperfective and marked the guess with a question mark on screen.
/// </remarks>
[TestClass]
public sealed class FormIndexMorphologyTests
{
    private static FormIndexWordMorphologyService? _service;

    [ClassInitialize]
    public static void Load(TestContext _)
    {
        var morphology = RussianMorphology.TryCreate(RussianFormIndex.Load());
        if (morphology is not null) _service = new FormIndexWordMorphologyService(() => morphology);
    }

    private static MorphologicalFeatures Features(string word)
    {
        Assert.IsNotNull(_service, "form index is not deployed");
        return _service.AnalyzeFeatures(word, LexicalLanguage.Russian);
    }

    [TestMethod]
    [DataRow("заняться", "совершенный")]
    [DataRow("займусь", "совершенный")]
    [DataRow("занимался", "несовершенный")]
    [DataRow("сделать", "совершенный")]
    [DataRow("делать", "несовершенный")]
    [DataRow("написал", "совершенный")]
    public void Aspect_comes_from_the_index(string word, string aspect)
        => Assert.AreEqual(aspect, Features(word).Aspect);

    [TestMethod]
    public void Reflexive_intransitive_verb_says_so()
        => Assert.AreEqual("непереходный, возвратный", Features("заняться").Transitivity);

    [TestMethod]
    public void Finite_verb_reports_tense_and_person()
    {
        var f = Features("займусь");
        Assert.AreEqual("будущее", f.Tense);
        Assert.AreEqual("1-е", f.Person);
        Assert.AreEqual("единственное", f.Number);
    }

    [TestMethod]
    public void Noun_reports_every_case_its_spelling_can_fill()
    {
        // The index stores «стол» as a nominative only; the accusative is the same spelling.
        var f = Features("стол");
        Assert.AreEqual("именительный или винительный", f.Case);
        Assert.AreEqual("мужской", f.Gender);
        Assert.AreEqual("неодушевлённое", f.Animacy);
    }

    [TestMethod]
    public void Spelling_that_spans_both_numbers_names_each()
    {
        var f = Features("книги");
        StringAssert.Contains(f.Case, "родительный ед.");
        StringAssert.Contains(f.Case, "мн.");
        Assert.IsNull(f.Number, "number is ambiguous for «книги» and must not be asserted");
    }

    [TestMethod]
    public void No_feature_is_ever_a_guess()
    {
        foreach (var word in new[] { "заняться", "стол", "книги", "красивая", "быстро", "кринж", "абырвалгх" })
        {
            foreach (var line in Features(word).ToDisplayList())
            {
                Assert.DoesNotContain("?", line, $"«{word}»: {line}");
            }
        }
    }

    [TestMethod]
    public void Unknown_word_gets_no_grammar_rather_than_a_guess()
        => Assert.IsEmpty(Features("абырвалгх").ToDisplayList());

    [TestMethod]
    public void Lemma_and_part_of_speech_come_from_the_index()
    {
        Assert.IsNotNull(_service, "form index is not deployed");
        Assert.AreEqual("заниматься", _service.GetLemma("занимался", LexicalLanguage.Russian));
        Assert.AreEqual(LexicalPartOfSpeech.Verb, _service.GetPartOfSpeech("заняться", LexicalLanguage.Russian));
    }
}
