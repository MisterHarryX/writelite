using WriteLite.Language.Russian;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Rules;

namespace WriteLite.Tests.Language;

/// <summary>
/// Слитно / раздельно / через дефис, and the false corrections the spell checker used to make
/// in their place («незнаю» → «нежною», «нехочу» → «нахожу»).
/// </summary>
[TestClass]
public sealed class CompoundSpellingTests
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

    private static string Fix(string text)
    {
        var fixedText = text;
        foreach (var issue in _analyzer.Analyze(text)
                     .Where(i => !string.IsNullOrEmpty(i.Replacement)
                                 && i.Category == IssueCategory.Orthography
                                 && !i.RuleId.Contains("sentence-capital", StringComparison.Ordinal))
                     .OrderByDescending(i => i.Start))
        {
            fixedText = fixedText[..issue.Start] + issue.Replacement + fixedText[(issue.Start + issue.Length)..];
        }

        return fixedText;
    }

    [TestMethod]
    [DataRow("Я незнаю, что сказать.", "Я не знаю, что сказать.")]
    [DataRow("Я нехочу туда идти.", "Я не хочу туда идти.")]
    [DataRow("Программа неработает после обновления.", "Программа не работает после обновления.")]
    [DataRow("Незная броду, не суйся в воду.", "Не зная броду, не суйся в воду.")]
    [DataRow("Ты непонимаешь меня.", "Ты не понимаешь меня.")]
    public void NeWithVerbs_IsSeparate(string text, string expected) => Assert.AreEqual(expected, Fix(text));

    [TestMethod]
    [DataRow("Я не навижу ложь.", "Я ненавижу ложь.")]
    [DataRow("Это было не лепое решение.", "Это было нелепое решение.")]
    [DataRow("Он не брежно бросил куртку.", "Он небрежно бросил куртку.")]
    public void WordsThatDoNotExistWithoutNe_AreSolid(string text, string expected) => Assert.AreEqual(expected, Fix(text));

    [TestMethod]
    [DataRow("Кое что я всё же понял.", "Кое-что я всё же понял.")]
    [DataRow("Позвони мне когда нибудь.", "Позвони мне когда-нибудь.")]
    [DataRow("Он говорит по русски без акцента.", "Он говорит по-русски без акцента.")]
    [DataRow("Она говорит поанглийски.", "Она говорит по-английски.")]
    [DataRow("Это было давным давно.", "Это было давным-давно.")]
    [DataRow("Я ждал пол часа.", "Я ждал полчаса.")]
    [DataRow("Мы купили пол лимона.", "Мы купили пол-лимона.")]
    [DataRow("У неё светло синее платье.", "У неё светло-синее платье.")]
    [DataRow("Это северо западный ветер.", "Это северо-западный ветер.")]
    [DataRow("Вице президент выступил с речью.", "Вице-президент выступил с речью.")]
    [DataRow("Он из за дождя опоздал.", "Он из-за дождя опоздал.")]
    [DataRow("Смотри ка, кто пришёл!", "Смотри-ка, кто пришёл!")]
    [DataRow("Он работает в интернет магазине.", "Он работает в интернет-магазине.")]
    public void Hyphenated_IsRestored(string text, string expected) => Assert.AreEqual(expected, Fix(text));

    [TestMethod]
    [DataRow("Я ненавижу ложь.")]
    [DataRow("Я недооценил его.")]
    [DataRow("Мне нездоровится сегодня.")]
    [DataRow("Он недоумевает, почему так вышло.")]
    [DataRow("По моему мнению, это верно.")]
    [DataRow("Он сделал всё по новому плану.")]
    [DataRow("Мы шли по русскому полю.")]
    [DataRow("Пол в комнате был деревянный.")]
    [DataRow("Это не большой, а огромный дом.")]
    [DataRow("Он вовсе не глупый человек.")]
    public void CorrectText_IsLeftAlone(string text) => Assert.AreEqual(text, Fix(text));
}
