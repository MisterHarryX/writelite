using WriteLite.Language.Russian;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Rules;

namespace WriteLite.Tests.Language;

/// <summary>
/// The Phase 7.5 clause-boundary rules: fronted subordinate clauses, «если… то…», «поэтому».
/// </summary>
/// <remarks>
/// <para>These three were the largest group of missed punctuation on the real-world set. All
/// three insert a comma into text that has none, which is the most damaging thing a
/// punctuation rule can get wrong — so the negatives here outnumber the positives and each
/// one names the alternative reading it protects.</para>
///
/// <para>The subordinate rule's central risk has its own test: «Если я приду завтра будет
/// хорошо» contains a pronoun that is the subject of the <em>subordinate</em> clause, and a
/// rule that took the first pronoun would produce «Если, я приду…».</para>
/// </remarks>
[TestClass]
public sealed class ClauseBoundaryRuleTests
{
    private static RussianFormIndex? _index;
    private static RuleBasedAnalyzer _analyzer = null!;

    [ClassInitialize]
    public static void Setup(TestContext context)
    {
        _ = context;
        _index = RussianFormIndex.Load();
        _analyzer = new RuleBasedAnalyzer(RuleCatalog.LoadDefault(), _index);
    }

    [ClassCleanup]
    public static void Cleanup() => _index?.Dispose();

    private const string SubClause = "ru.punctuation.subordinate-clause-end";
    private const string PairedTo = "ru.punctuation.paired-conjunction-to";
    private const string Consequence = "ru.punctuation.consequence-comma";

    private static IReadOnlyList<TextIssue> Rule(string text, string ruleId)
        => _analyzer.Analyze(text).Where(i => i.RuleId == ruleId).ToList();

    /// <summary>Applies a finding and returns the resulting text.</summary>
    private static string Apply(string text, TextIssue issue)
        => text[..issue.Start] + issue.Replacement + text[(issue.Start + issue.Length)..];

    // ---- fronted subordinate clause --------------------------------------

    [TestMethod]
    [DataRow("Когда я пришёл он уже ушёл.", "Когда я пришёл, он уже ушёл.")]
    [DataRow("Если будет время я позвоню.", "Если будет время, я позвоню.")]
    [DataRow("Если ты хочешь я помогу тебе.", "Если ты хочешь, я помогу тебе.")]
    [DataRow("Несмотря на то что задача сложная мы её решили.", "Несмотря на то что задача сложная, мы её решили.")]
    [DataRow("Хотя погода была плохая они всё равно пошли.", "Хотя погода была плохая, они всё равно пошли.")]
    public void FrontedSubordinateClause_IsClosedWithAComma(string text, string expected)
    {
        var issues = Rule(text, SubClause);

        Assert.HasCount(1, issues, text);
        Assert.AreEqual(expected, Apply(text, issues[0]));
        Assert.AreEqual(IssueCategory.Punctuation, issues[0].Category);
    }

    [TestMethod]
    [DataRow("Когда я пришёл, он уже ушёл.")]              // already punctuated
    [DataRow("Если я приду завтра будет хорошо.")]         // the pronoun is the subordinate subject
    [DataRow("Когда мы работали вместе всё получалось.")]  // main subject is not a pronoun
    [DataRow("Он пришёл домой и сразу лёг спать.")]        // no fronted conjunction
    [DataRow("Если хочешь звони.")]                        // too short to be two clauses
    [DataRow("Когда-то он жил здесь.")]                    // «когда-то» is not a conjunction
    [DataRow("Я не знаю когда он придёт.")]                // conjunction is not sentence-initial
    [DataRow("Если будет время, я позвоню и мы поговорим.")] // writer already punctuated
    public void FrontedSubordinateClause_DoesNotFire(string text)
        => Assert.IsEmpty(Rule(text, SubClause), text);

    [TestMethod]
    public void FrontedSubordinateClause_PicksTheMainClauseSubjectNotTheFirstPronoun()
    {
        // «я» belongs to the subordinate clause; «он» opens the main one.
        const string text = "Когда я говорил с ним он молчал.";
        var issues = Rule(text, SubClause);

        Assert.HasCount(1, issues);
        Assert.AreEqual("Когда я говорил с ним, он молчал.", Apply(text, issues[0]));
    }

    [TestMethod]
    public void FrontedSubordinateClause_ExplanationNamesTheConjunction()
    {
        var issues = Rule("Если будет время я позвоню.", SubClause);

        Assert.HasCount(1, issues);
        StringAssert.Contains(issues[0].Explanation, "если");
        StringAssert.Contains(issues[0].Explanation, "Придаточная часть");
    }

    // ---- «если … то …» ---------------------------------------------------

    [TestMethod]
    public void PairedConjunction_TakesACommaBeforeTo()
    {
        const string text = "Если начать сегодня то успеем.";
        var issues = Rule(text, PairedTo);

        Assert.HasCount(1, issues);
        Assert.AreEqual("Если начать сегодня, то успеем.", Apply(text, issues[0]));
    }

    [TestMethod]
    [DataRow("Если бы я знал то место я бы пошёл туда.")]  // demonstrative before a noun
    [DataRow("Я знаю то что он сказал.")]                  // no conditional conjunction
    [DataRow("Если начать сегодня, то успеем.")]           // already punctuated
    [DataRow("Это то самое место.")]                       // demonstrative, no conjunction
    public void PairedConjunction_DoesNotFire(string text)
        => Assert.IsEmpty(Rule(text, PairedTo), text);

    // ---- «поэтому» -------------------------------------------------------

    [TestMethod]
    public void Consequence_TakesACommaBeforePoetomu()
    {
        const string text = "API отвечает быстро поэтому задержек нет.";
        var issues = Rule(text, Consequence);

        Assert.HasCount(1, issues);
        Assert.AreEqual("API отвечает быстро, поэтому задержек нет.", Apply(text, issues[0]));
    }

    [TestMethod]
    [DataRow("Именно поэтому он ушёл раньше.")]            // intensified, no comma
    [DataRow("Вот поэтому мы и опоздали.")]                // intensified, no comma
    [DataRow("Поэтому мы решили подождать.")]              // sentence-initial
    [DataRow("API отвечает быстро, поэтому задержек нет.")] // already punctuated
    public void Consequence_DoesNotFire(string text)
        => Assert.IsEmpty(Rule(text, Consequence), text);

    // ---- the property that matters more than any single rule --------------

    [TestMethod]
    public void CorrectProse_DrawsNoClauseBoundaryFindings()
    {
        // A rule that inserts commas into correct text is worse than one that misses them.
        string[] clean =
        [
            "Сейчас мы работаем над приложением для проверки текста.",
            "Оно работает локально и не отправляет данные в облако.",
            "Команда закончила тестирование и передала сборку заказчику.",
            "Если появятся вопросы, напишите мне завтра.",
            "Когда я пришёл, он уже ушёл.",
            "Он открыл окно, потому что в комнате было душно.",
            "Погода была хорошей, и мы решили пойти пешком.",
            "Именно поэтому мы выбрали этот подход.",
            "Все изменения внесены и проверены дважды.",
            "В прошлом квартале выручка выросла на пятнадцать процентов.",
        ];

        foreach (var text in clean)
        {
            var findings = _analyzer.Analyze(text)
                .Where(i => i.RuleId is SubClause or PairedTo or Consequence)
                .ToList();
            Assert.IsEmpty(findings, $"{text} -> {string.Join(", ", findings.Select(f => f.RuleId))}");
        }
    }

    [TestMethod]
    public void AppliedFindingsNeverProduceDoublePunctuation()
    {
        string[] texts =
        [
            "Когда я пришёл он уже ушёл.",
            "Если будет время я позвоню.",
            "Если начать сегодня то успеем.",
            "API отвечает быстро поэтому задержек нет.",
            "Несмотря на то что задача сложная мы её решили.",
        ];

        foreach (var text in texts)
        {
            foreach (var issue in _analyzer.Analyze(text)
                .Where(i => i.RuleId is SubClause or PairedTo or Consequence))
            {
                var applied = Apply(text, issue);
                Assert.IsFalse(applied.Contains(",,", StringComparison.Ordinal), applied);
                Assert.IsFalse(applied.Contains(" ,", StringComparison.Ordinal), applied);
                Assert.IsFalse(applied.Contains("  ", StringComparison.Ordinal), applied);
            }
        }
    }
}
