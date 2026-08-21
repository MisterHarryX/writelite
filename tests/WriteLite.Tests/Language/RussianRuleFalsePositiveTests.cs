using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Rules;

namespace WriteLite.Tests.Language;

/// <summary>
/// The rule pack grew from 35 to 88 rules. WriteLite's stated priority is a low
/// false-positive rate, so correct Russian prose must stay completely clean:
/// a rule that fires on valid text is worse than a rule that does not exist.
/// </summary>
[TestClass]
public sealed class RussianRuleFalsePositiveTests
{
    private static RuleBasedAnalyzer Analyzer() => new(RuleCatalog.LoadDefault());

    /// <summary>Correct Russian prose, including forms that look like the wrong ones.</summary>
    private static readonly string[] CleanText =
    [
        "В общем, всё готово к отправке.",
        "По-моему, так будет намного лучше.",
        "Во-первых, это дорого. Во-вторых, это долго. В-третьих, это сложно.",
        "Кто-то звонил, но я был где-то в другом месте.",
        "Принеси что-нибудь почитать.",
        "Кто-либо может это подтвердить?",
        "Он всё-таки пришёл из-под навеса.",
        "Он по-прежнему работает по-своему.",
        "Мне как-то неловко об этом говорить.",
        "Надо сделать это сегодня, а не завтра.",
        "Здесь очень тихо по вечерам.",
        "Извините за опоздание.",
        "Он работает в агентстве недвижимости.",
        "Следующий вопрос будет о будущем годе.",
        "Это чересчур дорого для нас.",
        "На льду легко поскользнуться.",
        "Он обещал прийти вовремя.",
        "Инцидент исчерпан, прецедент опасен.",
        "Можно констатировать факт.",
        "Скрупулёзный подход к делу.",
        "У него разборчивый почерк.",
        "Это их дом, а не наш.",
        "Надо положить книги на стол и сложить бумаги.",
        "Так стало лучше, и это наилучший вариант.",
        "Это имеет большое значение и играет большую роль.",
        "Нужно оплатить проезд заранее.",
        "Нужно заплатить за проезд заранее.",
        "Вследствие дождя матч отменили.",
        "В течение недели всё изменится.",
        "Прибор наподобие часов лежал на столе.",
        "В целом работа сделана в срок.",
        "В то же время это совершенно верно.",
        "Несмотря на дождь мы вышли на улицу.",
        "Москва — столица России.",
        "Книги, тетради и т. п. лежали на полке.",
        "Отменили, т. к. был сильный дождь.",
        "Он замолчал… и больше ничего не сказал.",
        "Интернет-магазин работает круглосуточно.",
        "Она купила чёрно-белую фотографию.",
    ];

    [TestMethod]
    public void CorrectProse_ProducesNoErrorLevelIssues()
    {
        var analyzer = Analyzer();

        foreach (var text in CleanText)
        {
            var issues = analyzer.Analyze(text)
                .Where(issue => issue.Severity == IssueSeverity.Error)
                .ToArray();

            Assert.IsEmpty(issues,
                $"false positive on correct text: \"{text}\" -> " +
                string.Join("; ", issues.Select(i => $"{i.RuleId}:'{i.Original}'=>'{i.Replacement}'")));
        }
    }

    [TestMethod]
    public void KnownErrors_AreStillDetected()
    {
        var analyzer = Analyzer();
        var cases = new (string Text, string RuleId)[]
        {
            ("Вобщем, я согласен.", "ru.spelling.v-obschem"),
            ("Помоему, так будет лучше.", "ru.spelling.po-moemu"),
            ("Вопервых, это дорого.", "ru.spelling.vo-pervyh"),
            ("Ктото звонил.", "ru.spelling.particle-to"),
            ("Он опоздал потому-что задержался поезд.", "ru.spelling.potomu-chto"),
            ("Принеси чтонибудь.", "ru.spelling.particle-nibud"),
            ("Надо зделать это сегодня.", "ru.spelling.sdelat"),
            ("Сдесь очень тихо.", "ru.spelling.zdes"),
            ("Он обещал придти вовремя.", "ru.spelling.priyti"),
            ("Это ихний дом.", "ru.spelling.ihniy"),
            ("Вследствии дождя матч отменили.", "ru.spelling.vsledstvie"),
            ("книги и т.п.", "ru.punctuation.abbreviation-tp"),
            ("Отменили, т.к. был дождь.", "ru.punctuation.abbreviation-tk"),
        };

        foreach (var (text, ruleId) in cases)
        {
            var issues = analyzer.Analyze(text);
            Assert.IsTrue(issues.Any(issue => issue.RuleId == ruleId),
                $"rule {ruleId} did not fire on \"{text}\"; got: " +
                string.Join(", ", issues.Select(i => i.RuleId)));
        }
    }

    [TestMethod]
    public void SentenceInitialCorrections_KeepTheirCapitalLetter()
    {
        // A case-insensitive rule carries a lowercase replacement. Applying it at
        // the start of a sentence must not silently lowercase the sentence.
        var analyzer = Analyzer();

        foreach (var (text, expectedPrefix) in new[]
                 {
                     ("Вообщем, всё готово.", "В"),
                     ("Вобщем, я согласен.", "В"),
                     ("Помоему, так лучше.", "П"),
                     ("Ктото звонил.", "К"),
                     ("Зделать надо сегодня.", "С"),
                 })
        {
            var issue = analyzer.Analyze(text)
                .FirstOrDefault(i => i.Replacement is not null && i.Start == 0);

            Assert.IsNotNull(issue, $"no correction produced for \"{text}\"");
            StringAssert.StartsWith(issue!.Replacement, expectedPrefix,
                $"\"{text}\": correction '{issue.Replacement}' lost its capital letter");
        }
    }

    [TestMethod]
    public void MidSentenceCorrections_StayLowercase()
    {
        var analyzer = Analyzer();

        var issue = analyzer.Analyze("Я думаю, вобщем, что это верно.")
            .FirstOrDefault(i => i.RuleId == "ru.spelling.v-obschem");

        Assert.IsNotNull(issue);
        Assert.AreEqual("в общем", issue!.Replacement);
    }

    [TestMethod]
    public void EveryAutoApplicableRule_ChangesTheTextItMatches()
    {
        // A "safe to apply" rule whose replacement equals the original would show
        // the user a correction that does nothing -- the invariant the existing
        // CorrectionCandidateValidityPolicy protects.
        var catalog = RuleCatalog.LoadDefault();

        foreach (var compiled in catalog.RegexRules)
        {
            var rule = compiled.Definition;
            if (!rule.SafeToApply) continue;

            foreach (var test in rule.Tests.Where(t => t.ShouldMatch))
            {
                var match = compiled.Pattern.Match(test.Input);
                Assert.IsTrue(match.Success, $"{rule.RuleId}: positive test no longer matches");

                var replacement = match.Result(rule.Implementation.Replacement!);
                Assert.AreNotEqual(match.Value, replacement,
                    $"{rule.RuleId}: auto-applicable correction is identical to the original");
            }
        }
    }
}
