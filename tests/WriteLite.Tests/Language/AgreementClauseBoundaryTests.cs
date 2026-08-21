using WriteLite.Language.Russian;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Rules;

namespace WriteLite.Tests.Language;

/// <summary>
/// Subject–predicate agreement must not reach across a clause boundary for its subject.
/// </summary>
/// <remarks>
/// <para><b>Where these came from.</b> Not from a corpus — from driving the shipping build.
/// A 480-word report was pasted into the editor and «Исправить все безопасные» was pressed;
/// among fourteen edits, one turned «объём данных растёт быстрее чем мы ожидали» into
/// «объём данных растём быстрее», at confidence 0.88 with the auto-apply flag set. A second
/// finding of the same shape was waiting in the last paragraph.</para>
///
/// <para><b>Why it happened.</b> The subject search looks left, then right. Leftward it
/// correctly refused «объём» — a masculine inanimate noun whose accusative and nominative are
/// spelled alike, which the rule declines on purpose. Then it looked rightward, crossed «чем»,
/// and found «мы»: first person plural, and the verb was third person singular, so it
/// "fixed" the verb to match a pronoun belonging to a different clause.</para>
///
/// <para><b>What is asserted.</b> Both original sentences, and — because §42 asks for fixes
/// that generalise rather than phrase lists — several sentences of the same shape across
/// different conjunctions, subjects and verbs, none of which appear in the fix. The last two
/// tests hold the other direction: real disagreement inside one clause is still caught, so
/// this is a narrowed rule and not a disabled one.</para>
/// </remarks>
[TestClass]
public sealed class AgreementClauseBoundaryTests
{
    private static RussianFormIndex? _index;
    private static RuleBasedAnalyzer? _analyzer;

    [ClassInitialize]
    public static void Load(TestContext _)
    {
        _index = RussianFormIndex.Load();
        _analyzer = new RuleBasedAnalyzer(RuleCatalog.LoadDefault(), _index);
    }

    private static RuleBasedAnalyzer Analyzer
    {
        get
        {
            Assert.IsNotNull(_analyzer, "form index is not deployed");
            return _analyzer;
        }
    }

    private static IReadOnlyList<TextIssue> Agreement(string text)
        => Analyzer.Analyze(text)
            .Where(issue => issue.RuleId == "ru.grammar.agreement")
            .ToArray();

    private static void AssertNoAgreementFinding(string text)
    {
        var found = Agreement(text);
        Assert.IsEmpty(
            found,
            $"«{text}» is correct, but the rule offered: "
            + string.Join("; ", found.Select(i => $"«{i.Original}» -> «{i.Replacement}»")));
    }

    // ── The two the shipping build actually produced ─────────────────────────

    [TestMethod]
    public void The_measured_regression_comparative_clause()
        => AssertNoAgreementFinding("Объём данных растёт быстрее чем мы ожидали.");

    [TestMethod]
    public void The_measured_regression_coordinated_clause()
        => AssertNoAgreementFinding("Надеюсь что следующий период пройдёт спокойнее и мы сможем отдохнуть.");

    // ── The same shape, in words the fix has never seen ──────────────────────

    [TestMethod]
    [DataRow("Стоимость аренды выросла сильнее чем вы предполагали.")]
    [DataRow("Отчёт вышел позже чем я планировал.")]
    [DataRow("Поезд прибыл раньше чем они ожидали.")]
    [DataRow("Совещание закончилось быстро и мы разошлись.")]
    [DataRow("Договор подписан вчера а вы узнали сегодня.")]
    [DataRow("Заявка обработана но мы ещё ждём подтверждения.")]
    [DataRow("Сервис работает стабильно поскольку мы усилили мониторинг.")]
    [DataRow("Релиз задержится или вы получите частичную поставку.")]
    public void A_subject_beyond_a_conjunction_is_never_this_verbs_subject(string sentence)
        => AssertNoAgreementFinding(sentence);

    // ── The rule still does its job inside one clause ────────────────────────

    /// <remarks>
    /// «играет» rather than «читает» on purpose. The rule declines to correct «Мы читает»,
    /// because <c>InflectFinite</c> cannot generate the first person plural of that verb and
    /// the rule refuses to invent a form — the behaviour §25 asks for, and a pre-existing gap
    /// in form generation rather than anything to do with the clause-boundary fix, which only
    /// touches the rightward scan and never runs for a subject sitting immediately left.
    /// </remarks>
    [TestMethod]
    public void Real_disagreement_between_a_pronoun_and_its_own_verb_is_still_caught()
    {
        var found = Agreement("Мы играет в футбол каждое воскресенье.");
        Assert.IsNotEmpty(
            found,
            "narrowing the subject search must not switch the rule off inside a single clause");
        Assert.AreEqual("играем", found[0].Replacement);
    }

    [TestMethod]
    public void A_plural_noun_subject_with_a_singular_verb_is_still_caught()
    {
        var found = Agreement("Дети играет во дворе.");
        Assert.IsNotEmpty(found, "a noun subject left of its verb is still checked");
        Assert.AreEqual("играют", found[0].Replacement);
    }

    [TestMethod]
    public void An_inverted_subject_before_the_verb_is_still_reachable()
    {
        // The leftward scan is untouched by the fix, and is where an inverted subject lives.
        var found = Agreement("Вы читает документацию каждый день.");
        Assert.IsNotEmpty(found, "a pronoun immediately left of its verb is still checked");
    }
}
