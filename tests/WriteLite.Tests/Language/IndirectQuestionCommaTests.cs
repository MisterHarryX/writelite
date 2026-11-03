using WriteLite.Language.Russian;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Rules;

namespace WriteLite.Tests.Language;

/// <summary>
/// A comma before an indirect question: «указывать, почему это ошибка».
/// </summary>
/// <remarks>
/// Reported from the user's own text, which the checker passed as clean: «…и указывать почему
/// это ошибка». Nothing looked at interrogatives other than «что» and «когда», and «когда» only
/// produced a hint with no fix.
/// </remarks>
[TestClass]
public sealed class IndirectQuestionCommaTests
{
    private static RuleBasedAnalyzer? _analyzer;

    [ClassInitialize]
    public static void Load(TestContext _)
        => _analyzer = new RuleBasedAnalyzer(RuleCatalog.LoadDefault(), RussianFormIndex.Load());

    private static IReadOnlyList<TextIssue> Analyze(string text)
    {
        Assert.IsNotNull(_analyzer, "form index is not deployed");
        return _analyzer.Analyze(text);
    }

    [TestMethod]
    [DataRow("WriteLite должен определять ошибку и указывать почему это ошибка.", "указывать", "указывать,")]
    [DataRow("Я не понимаю зачем это нужно.", "понимаю", "понимаю,")]
    [DataRow("Скажи где ты был вчера.", "Скажи", "Скажи,")]
    [DataRow("Он спросил когда мы приедем.", "спросил", "спросил,")]
    [DataRow("Мы так и не решили куда поедем летом.", "решили", "решили,")]
    [DataRow("Никто не знает сколько это стоит.", "знает", "знает,")]
    [DataRow("Я помню какой была эта улица.", "помню", "помню,")]
    public void Adds_the_comma_after_the_verb(string text, string original, string replacement)
    {
        var issue = Analyze(text).SingleOrDefault(i => i.RuleId == "ru.punctuation.indirect-question");
        Assert.IsNotNull(issue, "no indirect-question finding for: " + text);
        Assert.AreEqual(original, issue.Original);
        Assert.AreEqual(replacement, issue.Replacement);
        Assert.IsTrue(issue.CanApplyAutomatically);
    }

    [TestMethod]
    [DataRow("Не знаю почему.")]
    [DataRow("Не знаю почему, но мне грустно.")]
    [DataRow("Я не понимаю, зачем это нужно.")]
    [DataRow("Он где-то видел кого-то похожего.")]
    [DataRow("Я видел когда-нибудь такое?")]
    [DataRow("Ищи где угодно.")]
    [DataRow("Он уехал бог знает куда.")]
    [DataRow("Цена зависит смотря где покупать.")]
    [DataRow("Он пришёл когда все уже ушли.")]
    [DataRow("Мы знаем о чём говорим.")]
    public void Stands_down(string text)
        => Assert.IsFalse(
            Analyze(text).Any(i => i.RuleId == "ru.punctuation.indirect-question"),
            "unexpected indirect-question finding for: " + text);

    [TestMethod]
    public void Replaces_the_soft_hint_on_the_same_comma()
    {
        var issues = Analyze("Он спросил когда мы приедем.");
        Assert.IsFalse(
            issues.Any(i => i.RuleId == "ru.punctuation.subordinate-comma.info"),
            "the hint about «когда» duplicates the fix: " + string.Join(", ", issues.Select(i => i.RuleId)));
    }
}

/// <summary>
/// Two safe repairs of one span must not disagree, or "fix all" picks one at random.
/// </summary>
[TestClass]
public sealed class ConflictingRepairTests
{
    private static RuleBasedAnalyzer? _analyzer;

    [ClassInitialize]
    public static void Load(TestContext _)
        => _analyzer = new RuleBasedAnalyzer(RuleCatalog.LoadDefault(), RussianFormIndex.Load());

    [TestMethod]
    public void Misspelt_infinitive_is_repaired_as_an_infinitive_only()
    {
        Assert.IsNotNull(_analyzer, "form index is not deployed");
        var issues = _analyzer.Analyze("Мы должны встретится до конца недели.")
            .Where(i => i.Original.Contains("встретится", StringComparison.Ordinal))
            .ToArray();

        Assert.AreEqual(1, issues.Length, string.Join(" | ", issues.Select(i => $"{i.RuleId}: {i.Replacement}")));
        Assert.AreEqual("встретиться", issues[0].Replacement);
    }

    [TestMethod]
    public void Split_chtoby_gets_no_competing_comma_before_its_first_half()
    {
        Assert.IsNotNull(_analyzer, "form index is not deployed");
        var issues = _analyzer.Analyze("Она пришла что бы помочь нам с переездом.");

        Assert.IsTrue(issues.Any(i => i.RuleId == "ru.grammar.chtoby-solid"));
        Assert.IsFalse(
            issues.Any(i => i.RuleId.StartsWith("ru.punctuation.subordinate-comma", StringComparison.Ordinal)),
            string.Join(" | ", issues.Select(i => $"{i.RuleId}: «{i.Original}» → «{i.Replacement}»")));
    }
}

/// <summary>
/// «что» inside an indefinite pronoun or in «если что» is not a conjunction.
/// </summary>
[TestClass]
public sealed class IndefiniteChtoCommaTests
{
    private static RuleBasedAnalyzer? _analyzer;

    [ClassInitialize]
    public static void Load(TestContext _)
        => _analyzer = new RuleBasedAnalyzer(RuleCatalog.LoadDefault(), RussianFormIndex.Load());

    [TestMethod]
    [DataRow("Давай просто закажем пиццу и посмотрим что-нибудь.")]
    [DataRow("Инкриминировать ему что-либо оказалось невозможно.")]
    [DataRow("У меня телефон почти разрядился, если что, пиши в мессенджер.")]
    public void No_comma_is_asked_for(string text)
    {
        Assert.IsNotNull(_analyzer, "form index is not deployed");
        var hits = _analyzer.Analyze(text).Where(i => i.RuleId.StartsWith("ru.punctuation.subordinate-comma", StringComparison.Ordinal)).ToArray();
        Assert.IsEmpty(hits, string.Join(" | ", hits.Select(i => $"«{i.Original}» → «{i.Replacement}»")));
    }

    [TestMethod]
    public void A_real_conjunction_still_gets_its_comma()
    {
        Assert.IsNotNull(_analyzer, "form index is not deployed");
        Assert.IsTrue(_analyzer.Analyze("Я знаю что ты прав.").Any(i => i.RuleId == "ru.punctuation.subordinate-comma"));
    }
}

/// <summary>
/// Everyday slang and work jargon are words, with a grammar the rules can use.
/// </summary>
/// <remarks>
/// Each of these was "corrected" by the shipping build — «ротируются» → «ретируются»,
/// «хавать» → «хвать», «Сорян» → «Сорен» — and the curated vocabulary's forms carried no part of
/// speech of their own, so «задеплоил» was indexed as an infinitive.
/// </remarks>
[TestClass]
public sealed class ModernVocabularyTests
{
    private static RussianFormIndex? _index;

    [ClassInitialize]
    public static void Load(TestContext _) => _index = RussianFormIndex.Load();

    [TestMethod]
    [DataRow("инвалидируется")]
    [DataRow("ротируются")]
    [DataRow("хавать")]
    [DataRow("сорян")]
    [DataRow("нахрена")]
    [DataRow("еби")]
    [DataRow("продакшен")]
    [DataRow("двухфакторную")]
    [DataRow("пересобрал")]
    [DataRow("задеплоили")]
    [DataRow("пофиксил")]
    [DataRow("кринжовая")]
    public void Is_a_known_word(string word)
    {
        Assert.IsNotNull(_index, "form index is not deployed");
        Assert.IsTrue(_index.Contains(word), word);
    }

    [TestMethod]
    public void Curated_forms_carry_their_own_part_of_speech()
    {
        Assert.IsNotNull(_index, "form index is not deployed");
        var morphology = new RussianMorphology(_index);
        Assert.AreEqual(RussianPartOfSpeech.Verb, _index.GetInfo("задеплоил").PartOfSpeech);
        Assert.IsFalse(morphology.Verb("задеплоил")!.Value.IsInfinitive);
        Assert.AreEqual(RuGender.Masc, morphology.Nominal("кринж").LexicalGender);
    }

    [TestMethod]
    [DataRow("америкал")]
    [DataRow("хейтру")]
    [DataRow("хайповыя")]
    public void Guessed_nonsense_forms_are_not_words(string word)
    {
        Assert.IsNotNull(_index, "form index is not deployed");
        Assert.IsFalse(_index.Contains(word), word + " came from a wrong reading of a curated lemma");
    }
}
