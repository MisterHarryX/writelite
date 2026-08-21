using WriteLite.Language.Russian;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Rules;

namespace WriteLite.Tests.Language;

/// <summary>
/// The Phase 7 linguistic rules: vocative commas, greeting sentence boundaries and dative
/// case government.
/// </summary>
/// <remarks>
/// Negatives outnumber positives deliberately. Each of these rules rewrites correct-looking
/// Russian, and the failure that matters is not a missed comma — it is a comma proposed into
/// a sentence that did not want one. The named hard negatives are the alternative readings
/// each rule's gate exists to exclude, so a gate that is loosened later fails here rather
/// than in a benchmark.
/// </remarks>
[TestClass]
public sealed class Phase7PunctuationAndGrammarTests
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

    private static IReadOnlyList<TextIssue> Rule(string text, string ruleId)
        => _analyzer.Analyze(text).Where(i => i.RuleId == ruleId).ToList();

    private const string Vocative = "ru.punctuation.vocative-comma";
    private const string Boundary = "ru.punctuation.greeting-sentence-boundary";
    private const string Dative = "ru.grammar.case-government-dative";

    // ---- vocatives ------------------------------------------------------

    [TestMethod]
    [DataRow("Привет дорогой друг.", "Привет,")]
    [DataRow("Спасибо Алексей.", "Спасибо,")]
    [DataRow("Слушай Андрей, я хотел сказать.", "Слушай,")]
    [DataRow("Добрый вечер коллеги.", "Добрый вечер,")]
    [DataRow("Здравствуйте Мария.", "Здравствуйте,")]
    public void Vocative_IsSeparatedByComma(string text, string expectedReplacement)
    {
        var issues = Rule(text, Vocative);

        Assert.HasCount(1, issues, text);
        Assert.AreEqual(expectedReplacement, issues[0].Replacement);
        Assert.AreEqual(IssueCategory.Punctuation, issues[0].Category);
    }

    [TestMethod]
    [DataRow("Привет, дорогой друг.")]                 // already correct
    [DataRow("Привет из Москвы.")]                     // «из» is a proper name in the index
    [DataRow("Спасибо большое.")]                      // adjective, no head noun
    [DataRow("Добрый вечер начался хорошо.")]          // greeting is the subject
    [DataRow("Слушай меня внимательно.")]              // imperative with an object
    [DataRow("Привет — дорогой друг.")]                // already separated
    [DataRow("Спасибо за помощь.")]                    // preposition, not an address
    [DataRow("Здравствуй столица нашей родины.")]      // inanimate head
    [DataRow("Извини письмо задержалось.")]            // inanimate head
    public void Vocative_DoesNotFireOnOrdinaryText(string text)
        => Assert.IsEmpty(Rule(text, Vocative), text);

    [TestMethod]
    public void Vocative_ExplanationNamesTheAddress()
    {
        var issues = Rule("Привет дорогой друг.", Vocative);

        Assert.HasCount(1, issues);

        // §13 and §24: the reason must be about the writer's own words, not a generic
        // sentence from the rule pack. This is what the catalog no longer overwrites.
        StringAssert.Contains(issues[0].Explanation, "дорогой друг");
        StringAssert.Contains(issues[0].Explanation, "обращение");
    }

    // ---- sentence boundary ----------------------------------------------

    [TestMethod]
    public void GreetingQuestion_IsProposedAsTwoSentences()
    {
        var issues = Rule("Привет дорогой друг как твои дела", Boundary);

        Assert.HasCount(1, issues);
        Assert.AreEqual("! Как твои дела?", issues[0].Replacement);
        Assert.IsFalse(issues[0].CanApplyAutomatically, "a sentence split is never automatic");
        Assert.AreEqual(IssueSeverity.Suggestion, issues[0].Severity);
    }

    [TestMethod]
    public void GreetingQuestion_ConsumesTheCommaItWouldStrandBehindTheQuestionMark()
    {
        var issues = Rule("Привет дорогой друг как твои дела, я давно тебя не видел", Boundary);

        Assert.HasCount(1, issues);
        Assert.AreEqual("! Как твои дела?", issues[0].Replacement);
        StringAssert.EndsWith(issues[0].Original, ",");
    }

    [TestMethod]
    [DataRow("Привет, дорогой друг! Как твои дела?")]  // already split
    [DataRow("Спасибо Алексей как всегда")]            // «как» is a comparison, not a question
    [DataRow("Привет дорогой друг как дела компании идут")] // prefix of something longer
    public void SentenceBoundary_StaysQuiet(string text)
        => Assert.IsEmpty(Rule(text, Boundary), text);

    // ---- dative government ----------------------------------------------

    [TestMethod]
    [DataRow("Вопреки новых целей мы продолжаем работу.", "новым целям")]
    [DataRow("Согласно приказа отдел закрыт.", "приказу")]
    [DataRow("Вопреки правил компании он ушёл.", "правилам")]
    [DataRow("Я поехал к бабушки на выходные.", "бабушке")]
    [DataRow("Наперекор трудностей он победил.", "трудностям")]
    public void DativeGovernment_IsCorrected(string text, string expected)
    {
        var issues = Rule(text, Dative);

        Assert.HasCount(1, issues, text);
        Assert.AreEqual(expected, issues[0].Replacement);
        Assert.AreEqual(IssueCategory.Grammar, issues[0].Category);
        Assert.AreEqual(IssueSeverity.Error, issues[0].Severity);
    }

    [TestMethod]
    [DataRow("Согласно приказу отдел закрыт.")]           // already dative
    [DataRow("Вопреки новым целям мы продолжаем.")]       // already dative, modifier included
    [DataRow("Согласно инструкции отдел закрыт.")]        // dative and genitive are homonymous
    [DataRow("Благодаря друга за помощь, он ушёл.")]      // gerund reading, animate object
    [DataRow("Вопреки, кажется, новым правилам.")]        // parenthetical breaks the relation
    [DataRow("В течение недели он работал.")]             // not a dative-governing preposition
    public void DativeGovernment_DoesNotFire(string text)
        => Assert.IsEmpty(Rule(text, Dative), text);

    [TestMethod]
    public void DativeGovernment_ExplanationNamesThePrepositionAndTheQuestion()
    {
        var issues = Rule("Вопреки новых целей мы продолжаем работу.", Dative);

        Assert.HasCount(1, issues);
        StringAssert.Contains(issues[0].Explanation, "вопреки");
        StringAssert.Contains(issues[0].Explanation, "дательного падежа");
        StringAssert.Contains(issues[0].Explanation, "вопреки чему?");
    }

    [TestMethod]
    public void DativeGovernment_LeavesTheFollowingGenitiveAlone()
    {
        // «компании» is governed by «правилам», not by «вопреки», and its genitive is correct.
        var issues = Rule("Вопреки правил компании он ушёл.", Dative);

        Assert.HasCount(1, issues);
        Assert.AreEqual("правил", issues[0].Original);
        Assert.AreEqual("правилам", issues[0].Replacement);
    }

    // ---- the §9 real-world regression -----------------------------------

    [TestMethod]
    public void RealWorldMixedErrorSentence_ProducesTheExpectedClassesOfFinding()
    {
        const string text =
            "Привет дорогой друг как твои дела, я был очень сильно наслышан о твоих новых "
            + "достижениях а теперь я хочу заявить чт вопреки новых целей мы будем продолжать "
            + "достигать высот";

        var issues = _analyzer.Analyze(text);

        // The point of this test is that the sentence contains several *different* classes of
        // problem and WriteLite separates them. It deliberately does not assert a total count:
        // that would freeze every unrelated rule's behaviour into this one string.
        Assert.IsNotEmpty(issues.Where(i => i.RuleId == Vocative), "vocative comma");
        Assert.IsNotEmpty(issues.Where(i => i.RuleId == Boundary), "sentence boundary");
        Assert.IsNotEmpty(issues.Where(i => i.RuleId == Dative), "dative government");
        Assert.IsNotEmpty(
            issues.Where(i => i.RuleId == "ru.punctuation.adversative-comma"
                || i.RuleId.StartsWith("ru.punctuation.subordinate", StringComparison.Ordinal)),
            "clause boundary");

        var dative = issues.Single(i => i.RuleId == Dative);
        Assert.AreEqual("новых целей", dative.Original);
        Assert.AreEqual("новым целям", dative.Replacement);

        // Every finding carries the full card contract of §24.
        foreach (var issue in issues)
        {
            Assert.IsNotEmpty(issue.Title, issue.RuleId);
            Assert.IsNotEmpty(issue.Explanation, issue.RuleId);
            Assert.IsGreaterThan(0.0, issue.Confidence, issue.RuleId);
        }
    }

    [TestMethod]
    public void CorrectProse_DrawsNoPhase7Findings()
    {
        // §47/§50: the rules added in this phase must be silent on ordinary correct text.
        string[] clean =
        [
            "Привет, Андрей! Как твои дела?",
            "Согласно приказу директора отдел закрыт с понедельника.",
            "Вопреки прогнозам погода была хорошей.",
            "Мы обсудили это вчера и приняли решение.",
            "Спасибо за подробный ответ и за ссылки.",
            "Добрый день! Отправляю отчёт за прошлый квартал.",
            "Благодаря поддержке коллег проект завершён в срок.",
        ];

        foreach (var text in clean)
        {
            var phase7 = _analyzer.Analyze(text)
                .Where(i => i.RuleId is Vocative or Boundary or Dative)
                .ToList();
            Assert.IsEmpty(phase7, $"{text} -> {string.Join(", ", phase7.Select(i => i.RuleId))}");
        }
    }
}
