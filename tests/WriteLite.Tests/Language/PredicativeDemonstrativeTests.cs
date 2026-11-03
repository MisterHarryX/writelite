using WriteLite.Language.Russian;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Rules;

namespace WriteLite.Tests.Language;

/// <summary>
/// «это ошибка» is a predicative sentence wherever it stands, not a noun phrase to agree.
/// </summary>
/// <remarks>
/// Reported from the shipping build: in «…указывать почему это ошибка» the card offered
/// «это → эта» at confidence 0.90 with the auto-apply flag set. The exemption for predicative
/// demonstratives only applied at a clause opening, and «почему» was not on the list of words
/// that open one. The fix is structural rather than another list entry: uninflected «это»,
/// «то», «всё» before a noun that can be nominative is always readable as the predicative
/// construction, so the attributive check stands down there and only there.
/// </remarks>
[TestClass]
public sealed class PredicativeDemonstrativeTests
{
    private static RuleBasedAnalyzer? _analyzer;

    [ClassInitialize]
    public static void Load(TestContext _)
        => _analyzer = new RuleBasedAnalyzer(RuleCatalog.LoadDefault(), RussianFormIndex.Load());

    private static IReadOnlyList<TextIssue> Agreement(string text)
    {
        Assert.IsNotNull(_analyzer, "form index is not deployed");
        return _analyzer.Analyze(text).Where(i => i.RuleId == "ru.grammar.agreement").ToArray();
    }

    [TestMethod]
    [DataRow("Главное, чтобы WriteLite мог определять ошибку и указывать почему это ошибка.")]
    [DataRow("Я не понимаю, зачем это функция вообще нужна.")]
    [DataRow("Объясни мне, откуда это информация.")]
    [DataRow("Скажи, где то место, о котором ты говорил.")]
    [DataRow("Мы долго спорили, но всё это ерунда.")]
    [DataRow("Он спросил, неужели это правда.")]
    [DataRow("Видимо это люди из соседнего отдела.")]
    public void Predicative_demonstrative_is_not_an_attribute(string text)
    {
        var found = Agreement(text);
        Assert.IsEmpty(found, string.Join("; ", found.Select(i => $"«{i.Original}» -> «{i.Replacement}»")));
    }

    [TestMethod]
    public void Oblique_noun_after_demonstrative_is_still_caught()
    {
        var found = Agreement("Вчера я прочитал это книгу до конца.");
        Assert.IsTrue(found.Any(i => i.Original == "это"), "«это книгу» must still be reported");
    }

    [TestMethod]
    public void Sentence_opened_by_this_still_checks_its_own_noun_phrase()
    {
        var found = Agreement("Это была интересная фильм.");
        Assert.IsTrue(found.Any(i => i.Original == "интересная"), "«интересная фильм» must still be reported");
    }
}
