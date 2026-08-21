using WriteLite.Language.Russian;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Lexical;
using WriteLite.Services.Stylistics;

namespace WriteLite.Tests.Language;

/// <summary>
/// The style layer: what it says, what it refuses to say, and how much of it a user gets.
/// </summary>
/// <remarks>
/// §14–§20 and §47. The measurement that matters most here is the last one — style findings
/// on correct, ordinary prose — because a layer that is right about wordiness and also lights
/// up five times on a clean paragraph is a layer people switch off.
/// </remarks>
[TestClass]
public sealed class RussianStyleAnalyzerTests
{
    private static IReadOnlyList<TextIssue> Analyze(
        string text,
        StyleProfile profile = StyleProfile.General)
        => new RussianStyleAnalyzer(profile).Analyze(text, ProtectedTextSpans.Find(text));

    private static IReadOnlyList<TextIssue> AnalyzeWithMorphology(string text)
        => new RussianStyleAnalyzer(StyleProfile.General, new ComparativeSignals())
            .Analyze(text, ProtectedTextSpans.Find(text));

    // ---- everything here is STYLE, never an error -------------------------

    [TestMethod]
    public void EveryStyleFindingIsClassifiedAsStyleAndNeverAutoApplied()
    {
        // §14: a stylistic recommendation is not a grammatical error, and the distinction has
        // to survive to the card. This is that assertion.
        const string text =
            "На данный момент времени мы сейчас занимаемся разработкой приложения. "
            + "Я был очень сильно наслышан о твоих достижениях. "
            + "Данный подход имеет место быть в связи с тем что он производит оплату.";

        var issues = Analyze(text, StyleProfile.Business);

        Assert.IsNotEmpty(issues);
        foreach (var issue in issues)
        {
            Assert.AreEqual(IssueCategory.Style, issue.Category, issue.RuleId);
            Assert.AreEqual(IssueClass.Style, issue.Class, issue.RuleId);
            Assert.IsFalse(issue.CanApplyAutomatically, issue.RuleId);
            Assert.AreNotEqual(CorrectionCertainty.Certain, issue.Certainty, issue.RuleId);
            Assert.IsNotEmpty(issue.Explanation, issue.RuleId);
        }
    }

    // ---- §16 wordiness ----------------------------------------------------

    [TestMethod]
    public void Wordiness_IsReportedWithAReasonThatNamesTheDuplication()
    {
        var issues = Analyze("На данный момент времени мы занимаемся разработкой.");

        var finding = issues.SingleOrDefault(i => i.RuleId == "ru.style.redundant-moment");
        Assert.IsNotNull(finding);

        // Capitalised, because the phrase opens the sentence — a suggestion the user accepts
        // must not lowercase the first word.
        Assert.AreEqual("Сейчас", finding.Replacement);
        StringAssert.Contains(finding.Explanation, "время");
    }

    // ---- §17 intensifier stacking -----------------------------------------

    [TestMethod]
    public void StackedIntensifiers_AreAStyleSuggestionNotAGrammarError()
    {
        var issues = Analyze("Я был очень сильно наслышан о твоих достижениях.");

        var finding = issues.SingleOrDefault(i => i.RuleId == "ru.style.double-intensifier");
        Assert.IsNotNull(finding);
        Assert.AreEqual(IssueClass.Style, finding.Class);

        // The span takes one of the surrounding spaces with it, because the suggestion is a
        // deletion and leaving both spaces behind would produce «Я был  наслышан».
        StringAssert.Contains(finding.Original, "очень сильно");
        Assert.AreEqual(13, finding.Length);
        Assert.AreEqual(string.Empty, finding.Replacement);
        StringAssert.Contains(finding.Explanation, "усилива");
    }

    [TestMethod]
    public void DoubleComparative_IsCaught()
    {
        var issues = Analyze("Это более лучший вариант.");

        var finding = issues.SingleOrDefault(i => i.RuleId == "ru.style.double-comparative");
        Assert.IsNotNull(finding);
        Assert.AreEqual("лучший", finding.Replacement);
    }

    [TestMethod]
    [DataRow("Это более лучшее решение.", "лучшее")]
    [DataRow("Мы выбрали более лучшую схему.", "лучшую")]
    [DataRow("Он доволен более лучшим результатом.", "лучшим")]
    public void DoubleComparative_PreservesInflection(string text, string expected)
    {
        var finding = Analyze(text)
            .SingleOrDefault(i => i.RuleId == "ru.style.double-comparative");

        Assert.IsNotNull(finding);
        Assert.AreEqual(expected, finding.Replacement);
        var corrected = text[..finding.Start] + finding.Replacement + text[(finding.Start + finding.Length)..];
        Assert.DoesNotContain("лучше решение", corrected, StringComparison.OrdinalIgnoreCase);
    }

    // ---- §18 profiles ------------------------------------------------------

    [DataTestMethod]
    [DataRow("Это более удобнее.", "удобнее")]
    [DataRow("Нужно работать более тщательнее.", "тщательнее")]
    public void GeneralDoubleComparative_UsesMorphology(string text, string expected)
    {
        var finding = AnalyzeWithMorphology(text)
            .Single(i => i.RuleId == "ru.style.double-comparative");

        Assert.AreEqual(expected, finding.Replacement);
        Assert.IsFalse(finding.CanApplyAutomatically);
    }

    [TestMethod]
    public void CorrectAnalyticalComparative_IsNotFlagged()
        => Assert.IsFalse(AnalyzeWithMorphology("Это более раннее решение.")
            .Any(i => i.RuleId == "ru.style.double-comparative"));

    private sealed class ComparativeSignals : ILexicalSignalSource
    {
        public LexicalSignals For(string word)
            => word.Equals("удобнее", StringComparison.OrdinalIgnoreCase)
               || word.Equals("тщательнее", StringComparison.OrdinalIgnoreCase)
                ? LexicalSignals.Unknown with
                {
                    IsKnown = true,
                    Lemma = word.Equals("удобнее", StringComparison.OrdinalIgnoreCase)
                        ? "удобный"
                        : "тщательный",
                    PartOfSpeech = RussianPartOfSpeech.Comparative,
                }
                : LexicalSignals.Unknown with
                {
                    IsKnown = true,
                    PartOfSpeech = RussianPartOfSpeech.AdjectiveFull,
                };
    }

    [TestMethod]
    public void FillerWordsAreMentionedInFormalProfilesAndLeftAloneInChat()
    {
        const string text = "Это как бы важное решение, так сказать.";

        var academic = Analyze(text, StyleProfile.Academic)
            .Where(i => i.RuleId.StartsWith("ru.style.filler", StringComparison.Ordinal))
            .ToList();
        var social = Analyze(text, StyleProfile.Social)
            .Where(i => i.RuleId.StartsWith("ru.style.filler", StringComparison.Ordinal))
            .ToList();

        Assert.IsNotEmpty(academic, "an academic text should hear about filler");
        Assert.IsEmpty(social, "§19: casual writing keeps its voice");
    }

    [TestMethod]
    public void RepetitionIsNotReportedInCreativeProse()
    {
        // §19/§20: repetition is a device in prose and a defect in a report.
        const string text =
            "Комната стояла пустой. Комната была старой и тихой. Комната помнила всех жильцов.";

        Assert.IsNotEmpty(
            Analyze(text, StyleProfile.Business)
                .Where(i => i.RuleId == "ru.style.repetition-across-sentences"));
        Assert.IsEmpty(
            Analyze(text, StyleProfile.Creative)
                .Where(i => i.RuleId == "ru.style.repetition-across-sentences"));
    }

    // ---- §20 repetition ----------------------------------------------------

    [TestMethod]
    public void RepetitionAcrossNeighbouringSentences_IsReportedWithoutARewrite()
    {
        const string text =
            "Мы разработали приложение. Приложение помогает писать текст. Приложение работает локально.";

        var finding = Analyze(text)
            .SingleOrDefault(i => i.RuleId == "ru.style.repetition-across-sentences");

        Assert.IsNotNull(finding);
        StringAssert.Contains(finding.Explanation, "приложение");

        // §20: suggest alternatives only when they preserve meaning — nothing here knows
        // whether a synonym would, so no replacement is offered.
        Assert.IsNull(finding.Replacement);
    }

    [TestMethod]
    public void ASingleSentenceRepeatingAWord_IsNotACrossSentenceRepetition()
        => Assert.IsEmpty(
            Analyze("Приложение и ещё одно приложение и третье приложение.")
                .Where(i => i.RuleId == "ru.style.repetition-across-sentences"));

    [TestMethod]
    public void CommonFunctionWordsAreNotReportedAsRepetition()
    {
        const string text =
            "Это подход, который работает. Это метод, который проверен. Это то, который нужен.";

        Assert.IsEmpty(
            Analyze(text).Where(i => i.RuleId == "ru.style.repetition-across-sentences"),
            "«который» and «это» are how Russian works, not a style defect");
    }

    // ---- §47 density and false positives ------------------------------------

    [TestMethod]
    public void StyleDensityIsCapped()
    {
        // Six different rules firing on one short paragraph is an argument, not help.
        const string text =
            "На данный момент времени данный подход имеет место быть в связи с тем что "
            + "он производит оплату, и это как бы более лучший вариант, так сказать.";

        var issues = Analyze(text, StyleProfile.Academic);
        var words = System.Text.RegularExpressions.Regex.Matches(text, @"\p{L}+").Count;
        var allowed = Math.Max(1, (int)Math.Floor(words * 3.0 / 100.0));

        Assert.IsLessThanOrEqualTo(allowed, issues.Count, $"{issues.Count} findings on {words} words");
    }

    [TestMethod]
    public void OrdinaryCorrectProse_DrawsNoStyleFindings()
    {
        // §47, measured rather than asserted in the abstract: a grammatically correct normal
        // sentence must not light up. Each of these is unremarkable Russian.
        string[] clean =
        [
            "Мы обсудили задачу и договорились о сроках.",
            "Отчёт готов, я отправлю его завтра утром.",
            "Приложение работает локально и не требует интернета.",
            "Спасибо за подробный ответ и за ссылки на документацию.",
            "В прошлом квартале выручка выросла на пятнадцать процентов.",
            "Команда закончила тестирование и передала сборку заказчику.",
            "Погода была хорошей, и мы решили пойти пешком.",
            "Он открыл окно, потому что в комнате было душно.",
            "Сейчас мы разрабатываем приложение для проверки текста.",
            "Согласно приказу директора отдел переезжает в новый офис.",
        ];

        var total = 0;
        var words = 0;
        foreach (var text in clean)
        {
            var issues = Analyze(text, StyleProfile.Business);
            Assert.IsEmpty(issues, $"{text} -> {string.Join(", ", issues.Select(i => i.RuleId))}");
            total += issues.Count;
            words += System.Text.RegularExpressions.Regex.Matches(text, @"\p{L}+").Count;
        }

        Assert.AreEqual(0, total, $"{total} style findings across {words} words of correct prose");
    }

    [TestMethod]
    public void ProtectedContentIsNotRestyled()
    {
        // §42: commands, paths and code are not prose.
        const string text = "Запустите `npm run build` в папке C:\\Users\\Test\\data и откройте https://example.com/более-лучший.";

        Assert.IsEmpty(Analyze(text));
    }

    [TestMethod]
    public void OverlappingPhrasesAreReportedOnceAsTheLongerOne()
    {
        var issues = Analyze("На данный момент времени всё готово.");

        // «данный» is inside «на данный момент времени»; reporting both would be two cards
        // for one phrase.
        Assert.HasCount(1, issues);
        Assert.AreEqual("ru.style.redundant-moment", issues[0].RuleId);
    }

    [TestMethod]
    public void AppliedSuggestionsProduceWellFormedText()
    {
        // A style suggestion the user accepts must not leave a double space or a broken
        // sentence behind.
        (string Text, string Expected)[] cases =
        [
            ("На данный момент времени всё готово.", "Сейчас всё готово."),
            ("Это более лучший вариант.", "Это лучший вариант."),
            ("Я был очень сильно наслышан.", "Я был наслышан."),
        ];

        foreach (var (text, expected) in cases)
        {
            var issue = Analyze(text).FirstOrDefault(i => i.Replacement is not null);
            Assert.IsNotNull(issue, text);

            var applied = text[..issue.Start] + issue.Replacement + text[(issue.Start + issue.Length)..];
            Assert.AreEqual(expected, applied);
        }
    }
}
