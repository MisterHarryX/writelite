using WriteLite.Language.Russian;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Rules;

namespace WriteLite.Tests.Language;

/// <summary>
/// A comma before «и» belongs there only when the second half has a subject of its own.
/// </summary>
/// <remarks>
/// <para><b>Where this came from.</b> The same real-text run that produced
/// <see cref="AgreementClauseBoundaryTests"/>. Among the fourteen edits «Исправить все
/// безопасные» applied to a 480-word report, one put a comma into</para>
///
/// <para><i>«Они оперативно реагировали на обращения пользователей и во многих случаях
/// находили причину до того как проблема становилась заметной.»</i></para>
///
/// <para>which is one subject and two homogeneous predicates and takes no comma. The rule
/// already knew that test — it asks whether a subject follows the conjunction — but its scan
/// ran through «до того как» and adopted «проблема» from the subordinate clause. The
/// discrimination the rule is built on was correct; the window it applied it over was not.</para>
///
/// <para>The pairs below are the point: each "no comma" sentence has a "comma" twin of the
/// same length and shape, differing only in whether the second predicate brings its own
/// subject. A fix that simply stopped inserting commas would fail the second half of every
/// pair.</para>
/// </remarks>
[TestClass]
public sealed class HomogeneousPredicateCommaTests
{
    private static RuleBasedAnalyzer? _analyzer;

    [ClassInitialize]
    public static void Load(TestContext _)
        => _analyzer = new RuleBasedAnalyzer(RuleCatalog.LoadDefault(), RussianFormIndex.Load());

    private static IReadOnlyList<TextIssue> Coordination(string text)
    {
        Assert.IsNotNull(_analyzer, "form index is not deployed");
        return _analyzer.Analyze(text)
            .Where(issue => issue.RuleId == "ru.punctuation.clause-coordination")
            .ToArray();
    }

    // ── One subject, two predicates: no comma ────────────────────────────────

    [TestMethod]
    public void The_measured_regression_is_not_offered_a_comma()
        => Assert.IsEmpty(
            Coordination(
                "Они оперативно реагировали на обращения пользователей и во многих случаях "
                + "находили причину до того как проблема становилась заметной."),
            "«реагировали … и … находили» share one subject and take no comma");

    [TestMethod]
    [DataRow("Инженер проверил стенд и запустил сборку до того как начались тесты.")]
    [DataRow("Аналитик собрал данные и подготовил отчёт пока шло совещание.")]
    [DataRow("Команда обсудила план и утвердила сроки когда появился заказчик.")]
    [DataRow("Он открыл документ и внёс правки которые просил редактор.")]
    public void Homogeneous_predicates_take_no_comma_even_before_a_subordinate_clause(string sentence)
    {
        var found = Coordination(sentence);
        Assert.IsEmpty(
            found,
            $"«{sentence}» has one subject; the rule offered: "
            + string.Join("; ", found.Select(i => $"«{i.Original}» -> «{i.Replacement}»")));
    }

    // ── Two subjects, two clauses: the comma is still required ───────────────

    [TestMethod]
    public void The_sentence_next_to_it_in_the_same_document_still_gets_its_comma()
        => Assert.IsNotEmpty(
            Coordination("Надеюсь что следующий период пройдёт спокойнее и мы сможем отдохнуть."),
            "«период пройдёт … и мы сможем» is two clauses with two subjects");

    [TestMethod]
    [DataRow("Инженер проверил стенд и заказчик утвердил результат.")]
    [DataRow("Дождь закончился и выглянуло солнце.")]
    [DataRow("Совещание закончилось и мы разошлись по кабинетам.")]
    public void Two_subjects_still_take_a_comma(string sentence)
        => Assert.IsNotEmpty(
            Coordination(sentence),
            $"«{sentence}» is two clauses and needs its comma");
}
