using WriteLite.Language.Russian;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Ai;
using WriteLite.Services.Rules;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests.Language;

/// <summary>
/// The destructive corrections this project has actually shipped, as permanent tests.
/// </summary>
/// <remarks>
/// <para>§2 of the sprint brief lists six of them and asks that they become regression tests
/// and that the fix be the underlying class rather than the string. Both halves matter: a test
/// that asserts «более лучшее решение» survives can be passed by special-casing that phrase,
/// which is why every case here is paired with a <em>generalisation</em> case — a different
/// phrase of the same shape, which no exception list would cover.</para>
///
/// <para>The guard under test reads the sentence a correction would leave behind, so the tests
/// feed it corrections directly rather than waiting for a model to propose one. That is the
/// only way to test it without a live model, and it is also the honest way: the question is
/// what happens when this edit arrives, not whether today's model happens to produce it.</para>
/// </remarks>
[TestClass]
public sealed class DestructiveCorrectionRegressionTests
{
    private static RussianFormIndex? _index;
    private static MorphologicalAcceptanceGuard? _guard;

    [ClassInitialize]
    public static void Load(TestContext _)
    {
        _index = RussianFormIndex.Load();
        _guard = MorphologicalAcceptanceGuard.TryCreate(_index);
    }

    private static MorphologicalAcceptanceGuard Guard
    {
        get
        {
            Assert.IsNotNull(_guard, "form index is not deployed; the guard cannot be tested");
            return _guard;
        }
    }

    private static TextIssue Edit(string text, string original, string replacement)
    {
        var start = text.IndexOf(original, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start, $"«{original}» is not in the test text");
        return new TextIssue(
            start,
            original.Length,
            original,
            replacement,
            "AI",
            "AI",
            IssueCategory.Grammar,
            IssueSeverity.Warning,
            CanApplyAutomatically: false,
            RuleId: "WL-AI-DIFF-REPLACE",
            Confidence: 0.99);
    }

    // ---- the six historical failures, and their generalisations ---------------

    [TestMethod]
    [DataRow("Это более лучшее решение для нас.", "более лучшее решение", "лучше решение")]
    [DataRow("Это более удобное решение для нас.", "более удобное решение", "удобнее решение")]
    [DataRow("Нам нужен более быстрый ответ.", "более быстрый ответ", "быстрее ответ")]
    public void Comparative_BeforeNoun_IsRejected(string text, string original, string replacement)
    {
        // «лучше решение» is not a noun phrase in any reading: a synthetic comparative does not
        // decline and cannot be an attribute. Rejecting it needs no knowledge of which words
        // are involved, which is why the two invented cases fail the same way as the reported one.
        var reason = Guard.Reject(text, Edit(text, original, replacement));
        Assert.IsNotNull(reason, $"«{replacement}» should have been rejected");
        StringAssert.Contains(reason, "morph-guard");
    }

    [TestMethod]
    [DataRow("Интерфейс стал более удобнее в этой версии.", "более удобнее", "удобнее")]
    [DataRow("Проверка стала более тщательнее в этом релизе.", "более тщательнее", "тщательнее")]
    [DataRow("Это более лучшее решение для нас.", "более лучшее", "лучшее")]
    [DataRow("Работает более быстрее, чем раньше.", "более быстрее", "быстрее")]
    public void DoubleComparative_IsRepairedDeterministically(string text, string original, string expected)
    {
        // The historical damage began with the model being asked to repair these phrases and
        // answering «более удобная» or «лучше решение». The deterministic rule owns the span
        // now, so the routing policy sees a high-confidence finding and never asks — the class
        // of damage is removed by removing the opportunity.
        var analyzer = new RuleBasedAnalyzer(RuleCatalog.LoadDefault(), _index);
        var issues = analyzer.Analyze(text);

        var finding = issues.FirstOrDefault(i => i.RuleId == "ru.grammar.double-comparative");
        Assert.IsNotNull(finding, $"no double-comparative finding for «{original}»");
        Assert.AreEqual(expected, finding.Replacement);

        var applied = IssueApplication.Apply(text, [finding]);
        Assert.IsTrue(applied.Ok);
        StringAssert.Contains(applied.Text, expected);
    }

    [TestMethod]
    public void AnalyticComparative_IsLeftAlone()
    {
        // «более удобное» is the correct analytic comparative and must survive the rule that
        // deletes «более» from the double one.
        var analyzer = new RuleBasedAnalyzer(RuleCatalog.LoadDefault(), _index);
        var issues = analyzer.Analyze("Это более удобное решение для нас.");
        Assert.IsFalse(issues.Any(i => i.RuleId == "ru.grammar.double-comparative"));
    }

    [TestMethod]
    [DataRow("Завтра мы отправим сборку заказчику.", "отправим", "отправить")]
    [DataRow("Сегодня мы покажем макет команде.", "покажем", "показать")]
    public void FiniteVerb_TurnedIntoInfinitive_IsRejected(string text, string original, string replacement)
    {
        var reason = Guard.Reject(text, Edit(text, original, replacement));
        Assert.IsNotNull(reason, $"«{original}» → «{replacement}» should have been rejected");
        StringAssert.Contains(reason, "finite predicate");
    }

    [TestMethod]
    public void Infinitive_LicensedByModal_IsNotRejected()
    {
        // The mirror of the case above: a sentence whose predicate is legitimately an
        // infinitive must stay correctable. Without this the fix would be "never touch a verb".
        const string text = "Нам нужно отправим сборку заказчику.";
        Assert.IsNull(Guard.Reject(text, Edit(text, "отправим", "отправить")));
    }

    [TestMethod]
    public void Comparative_WithGenitive_IsNotRejected()
    {
        // «лучше решения» — "better than the solution" — is correct Russian and shares its
        // shape with the rejected «лучше решение». Only the case of the noun separates them.
        const string text = "Наш вариант оказался хуже решения конкурентов.";
        Assert.IsNull(Guard.Reject(text, Edit(text, "хуже решения", "лучше решения")));
    }

    // ---- «что то» → «, что»: structurally impossible, not merely rejected ----

    [TestMethod]
    public void MalformedDiff_ThatDoesNotReconstructTheRewrite_IsDiscarded()
    {
        // The historical shape: a span list that, applied to the original, does not produce
        // the text the model returned. §28 asks that this be impossible to show a user, and
        // the pipeline enforces it by discarding any list that fails to round-trip.
        const string original = "Он сказал что то важное.";
        const string corrected = "Он сказал что-то важное.";

        var malformed = new List<TextIssue>
        {
            Edit(original, "что то", ", что"),
        };

        Assert.IsFalse(IssueApplication.Reconstructs(original, corrected, malformed));

        var wellFormed = new List<TextIssue> { Edit(original, "что то", "что-то") };
        Assert.IsTrue(IssueApplication.Reconstructs(original, corrected, wellFormed));
    }

    [TestMethod]
    public void Apply_RejectsStaleAndOverlappingSpans()
    {
        const string text = "Он сказал что то важное.";

        var stale = new List<TextIssue>
        {
            new(0, 2, "Она", "Он", "t", "e", IssueCategory.Grammar, IssueSeverity.Warning),
        };
        Assert.AreEqual(IssueApplication.ApplyStatus.OriginalMismatch, IssueApplication.Apply(text, stale).Status);

        var overlapping = new List<TextIssue>
        {
            Edit(text, "сказал что", "сказал, что"),
            Edit(text, "что то", "что-то"),
        };
        Assert.AreEqual(IssueApplication.ApplyStatus.Overlapping, IssueApplication.Apply(text, overlapping).Status);

        var outOfRange = new List<TextIssue>
        {
            new(text.Length + 5, 3, "abc", "abd", "t", "e", IssueCategory.Grammar, IssueSeverity.Warning),
        };
        Assert.AreEqual(IssueApplication.ApplyStatus.OutOfRange, IssueApplication.Apply(text, outOfRange).Status);
    }

    [TestMethod]
    [DataRow("Он сказал что то важное.")]
    [DataRow("Потому-что было поздно, мы ушли.")]
    [DataRow("Завтра мы отправим сборку заказчику.")]
    [DataRow("Это более лучшее решение для нас.")]
    public void DeterministicPipeline_NeverProducesTheHistoricalDamage(string text)
    {
        // The other half of §2: whatever the deterministic layers do to these sentences, the
        // specific damaged strings must not come out. This is the end-to-end statement, and it
        // holds by construction rather than by exception — none of the rules mentions them.
        var index = _index;
        var analyzer = new RuleBasedAnalyzer(RuleCatalog.LoadDefault(), index);
        var issues = analyzer.Analyze(text);

        var applied = IssueApplication.Apply(
            text,
            issues.Where(i => i.Replacement is not null && i.CanApplyAutomatically).ToList());

        if (!applied.Ok) return;

        foreach (var damage in new[] { "лучше решение", "потому-то", "отправить сборку", ", что то" })
        {
            StringAssert.DoesNotMatch(
                applied.Text,
                new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(damage)),
                $"«{damage}» reappeared in: {applied.Text}");
        }
    }

    [TestMethod]
    public void SpellingPipeline_LeavesKnownSafePhrasesAlone()
    {
        var checker = new LocalSpellChecker();
        var analyzer = new SpellTextAnalyzer(checker);

        foreach (var text in new[]
                 {
                     "Завтра мы отправим сборку заказчику.",
                     "Это более удобное решение для нас.",
                     "Он сказал что-то важное.",
                 })
        {
            var issues = analyzer.Analyze(text);
            var applied = IssueApplication.Apply(
                text,
                issues.Where(i => i.Replacement is not null).ToList());
            if (applied.Ok)
            {
                Assert.AreEqual(text, applied.Text, $"spelling changed a correct sentence: {text}");
            }
        }
    }
}
