using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Ai;

namespace WriteLite.Tests;

[TestClass]
public sealed class LocalAnalyzerGrammarRegressionTests
{
    private const string ClimateSource =
        "В современном мире проблема изменения климата становиться всё более актуальной, " +
        "и многие учёные утверждают, что человечество должно предпринять немедленные меры, " +
        "что бы минимизировать негативные последствия. Согласно последним исследованиям, " +
        "опубликованным в авторитетных журналах, повышение средней температуры на планете " +
        "приводит к тому, что ледники тают с невиданной скоростью, а уровень мирового океана " +
        "поднимается быстрее, чем прогнозировалось ранее.\n" +
        "Несмотря на то, что большинство стран подписали международные соглашения по сокращению " +
        "выбросов парниковых газов, реальное выполнение этих обязательств оставляет желать лучшего. " +
        "Многие компании продолжают использовать устаревшие технологии, предпочитая краткосрочную " +
        "прибыль долгосрочной устойчивости. Например, в развивающихся странах, где экономический " +
        "рост ставиться на первое место, экологические нормы часто игнорируются, что приводит к " +
        "загрязнению рек и уничтожению лесных массивов.\n" +
        "Эксперты предупреждают, что если не изменить подход к использованию природных ресурсов " +
        "уже в ближайшие десятилетия, то последствия могут стать необратимыми. Однако оптимисты " +
        "верят, что развитие зелёных технологий, таких как солнечная и ветровая энергетика, а также " +
        "внедрение искусственного интеллекта в мониторинг окружающей среды, позволит найти " +
        "эффективные решения. В конечном итоге всё зависит от того, насколько ответственно каждое " +
        "государство и каждый человек отнесётся к этой глобальной проблеме.";

    private const string ClimateExpected =
        "В современном мире проблема изменения климата становится всё более актуальной, " +
        "и многие учёные утверждают, что человечество должно предпринять немедленные меры, " +
        "чтобы минимизировать негативные последствия. Согласно последним исследованиям, " +
        "опубликованным в авторитетных журналах, повышение средней температуры на планете " +
        "приводит к тому, что ледники тают с невиданной скоростью, а уровень мирового океана " +
        "поднимается быстрее, чем прогнозировалось ранее.\n" +
        "Несмотря на то, что большинство стран подписали международные соглашения по сокращению " +
        "выбросов парниковых газов, реальное выполнение этих обязательств оставляет желать лучшего. " +
        "Многие компании продолжают использовать устаревшие технологии, предпочитая краткосрочную " +
        "прибыль долгосрочной устойчивости. Например, в развивающихся странах, где экономический " +
        "рост ставится на первое место, экологические нормы часто игнорируются, что приводит к " +
        "загрязнению рек и уничтожению лесных массивов.\n" +
        "Эксперты предупреждают, что если не изменить подход к использованию природных ресурсов " +
        "уже в ближайшие десятилетия, то последствия могут стать необратимыми. Однако оптимисты " +
        "верят, что развитие зелёных технологий, таких как солнечная и ветровая энергетика, а также " +
        "внедрение искусственного интеллекта в мониторинг окружающей среды, позволит найти " +
        "эффективные решения. В конечном итоге всё зависит от того, насколько ответственно каждое " +
        "государство и каждый человек отнесётся к этой глобальной проблеме.";

    [TestMethod]
    public void ClimateText_FindsFourCoreGrammarFixes()
    {
        var analyzer = new RuleBasedAnalyzer();
        var issues = analyzer.Analyze(ClimateSource);

        var stan = issues.FirstOrDefault(i =>
            i.Original.Equals("становиться", StringComparison.OrdinalIgnoreCase)
            && i.Replacement?.Equals("становится", StringComparison.OrdinalIgnoreCase) == true);
        Assert.IsNotNull(stan, "Expected становиться → становится");
        Assert.IsTrue(stan!.CanApplyAutomatically);
        Assert.AreEqual(IssueCategory.Grammar, stan.Category);

        var chtoBy = issues.FirstOrDefault(i =>
            i.Original.Contains("что", StringComparison.OrdinalIgnoreCase)
            && i.Original.Contains("бы", StringComparison.OrdinalIgnoreCase)
            && i.Replacement?.Equals("чтобы", StringComparison.OrdinalIgnoreCase) == true);
        Assert.IsNotNull(chtoBy, "Expected что бы → чтобы");
        Assert.IsTrue(chtoBy!.CanApplyAutomatically);
        Assert.AreEqual(IssueCategory.Grammar, chtoBy.Category);
        StringAssert.Contains(chtoBy.Explanation, "слитно");

        var stav = issues.FirstOrDefault(i =>
            i.Original.Equals("ставиться", StringComparison.OrdinalIgnoreCase)
            && i.Replacement?.Equals("ставится", StringComparison.OrdinalIgnoreCase) == true);
        Assert.IsNotNull(stav, "Expected ставиться → ставится");
        Assert.IsTrue(stav!.CanApplyAutomatically);

        // Source already has comma before «что бы»; no mandatory insert required there.
        // Still verify the dedicated insert rule on a missing-comma fragment.
        var missingComma = analyzer.Analyze("предпринять меры чтобы минимизировать последствия");
        var comma = missingComma.FirstOrDefault(i =>
            i.RuleId == "ru.punctuation.comma-before-chtoby" && i.CanApplyAutomatically);
        Assert.IsNotNull(comma, "Expected comma insert before чтобы");
        Assert.AreEqual(0, comma!.Length);
        Assert.AreEqual("", comma.Original);
        Assert.AreEqual(",", comma.Replacement);
        Assert.IsFalse(comma.Explanation.Contains("желательн", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(comma.Explanation.Contains("может", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ClimateText_ApplyAll_ProducesExpectedAndNoMarkdown()
    {
        var analyzer = new RuleBasedAnalyzer();
        var issues = analyzer.Analyze(ClimateSource);
        var safe = issues.Where(i => i.CanApplyAutomatically).ToList();

        Assert.IsTrue(safe.Any(i => i.Original.Equals("становиться", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(safe.Any(i => i.Replacement == "чтобы"));
        Assert.IsTrue(safe.Any(i => i.Original.Equals("ставиться", StringComparison.OrdinalIgnoreCase)));

        // «Несмотря на то, что» must not be auto-fixed.
        Assert.IsFalse(safe.Any(i =>
            i.Original.Contains("Несмотря на то", StringComparison.OrdinalIgnoreCase)
            || (i.Original.Contains("несмотря", StringComparison.OrdinalIgnoreCase)
                && i.CanApplyAutomatically
                && i.Replacement is not null
                && i.Replacement.Contains("что", StringComparison.OrdinalIgnoreCase))));

        Assert.IsTrue(TextCorrectionService.TryApplyAll(ClimateSource, safe, true, out var result));
        Assert.IsFalse(result.Contains("**", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("Анализ", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.Contains("Основные ошибки", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.Contains("становиться", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.Contains("что бы", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.Contains("ставиться", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(result.Contains("становится", StringComparison.Ordinal));
        Assert.IsTrue(result.Contains("чтобы минимизировать", StringComparison.Ordinal));
        Assert.IsTrue(result.Contains("ставится", StringComparison.Ordinal));
        Assert.IsTrue(result.Contains("Несмотря на то, что", StringComparison.Ordinal));
        Assert.AreEqual(ClimateExpected, result);

        // Re-analysis: core errors must not return as auto-apply.
        var again = analyzer.Analyze(result);
        Assert.IsFalse(again.Any(i =>
            i.CanApplyAutomatically
            && (i.Original.Equals("становиться", StringComparison.OrdinalIgnoreCase)
                || i.Original.Equals("ставиться", StringComparison.OrdinalIgnoreCase)
                || (i.Original.Contains("что", StringComparison.Ordinal)
                    && i.Original.Contains("бы", StringComparison.Ordinal)
                    && i.Replacement == "чтобы"))));
    }

    [TestMethod]
    public void Chtoby_CommaInsert_ExactShape()
    {
        var analyzer = new RuleBasedAnalyzer();
        const string text = "предпринять меры чтобы минимизировать последствия";
        var issues = analyzer.Analyze(text);
        var comma = issues.Single(i => i.RuleId == "ru.punctuation.comma-before-chtoby");
        Assert.AreEqual(0, comma.Length);
        Assert.AreEqual("", comma.Original);
        Assert.AreEqual(",", comma.Replacement);
        Assert.IsTrue(comma.CanApplyAutomatically);
        Assert.IsTrue(TextCorrectionService.TryApplySingle(text, comma, true, out var fixedText, out _));
        Assert.AreEqual("предпринять меры, чтобы минимизировать последствия", fixedText);
    }

    [TestMethod]
    public void ChtoBy_SeparateSpelling_NotMerged()
    {
        var analyzer = new RuleBasedAnalyzer();
        string[] keepSeparate =
        [
            "Что бы ты сделал?",
            "Что бы ни произошло, позвони мне.",
            "Не знаю, что бы выбрать."
        ];

        foreach (var text in keepSeparate)
        {
            var issues = analyzer.Analyze(text);
            Assert.IsFalse(
                issues.Any(i =>
                    i.RuleId == "ru.grammar.chtoby-solid"
                    && i.CanApplyAutomatically),
                $"Must not merge «что бы» in: {text}");
        }
    }

    [TestMethod]
    public void ChtoBy_Purpose_MergedSolid()
    {
        var analyzer = new RuleBasedAnalyzer();
        const string text = "Нужно принять меры, что бы избежать ошибки.";
        var issues = analyzer.Analyze(text);
        var merge = issues.FirstOrDefault(i => i.RuleId == "ru.grammar.chtoby-solid");
        Assert.IsNotNull(merge);
        Assert.AreEqual("чтобы", merge!.Replacement);
        Assert.IsTrue(merge.CanApplyAutomatically);
        StringAssert.Contains(merge.Explanation, "слитно");
    }

    [TestMethod]
    public void Reflexive_FiniteRequired_AndModalBlocked()
    {
        var analyzer = new RuleBasedAnalyzer();

        void ExpectFix(string text, string from, string to)
        {
            var issues = analyzer.Analyze(text);
            Assert.IsTrue(
                issues.Any(i =>
                    i.CanApplyAutomatically
                    && i.Original.Equals(from, StringComparison.OrdinalIgnoreCase)
                    && i.Replacement?.Equals(to, StringComparison.OrdinalIgnoreCase) == true),
                $"Expected {from}→{to} in: {text}");
        }

        void ExpectNoAuto(string text, string form)
        {
            var issues = analyzer.Analyze(text);
            Assert.IsFalse(
                issues.Any(i =>
                    i.CanApplyAutomatically
                    && i.Original.Equals(form, StringComparison.OrdinalIgnoreCase)),
                $"Must not auto-fix {form} in: {text}");
        }

        ExpectFix("Проблема становится актуальной.".Replace("становится", "становиться"), "становиться", "становится");
        ExpectFix("Экономический рост ставиться на первое место.", "ставиться", "ставится");
        ExpectFix("Ситуация меняться каждый день.".Replace("меняется", "меняться"), "меняться", "меняется");

        ExpectNoAuto("Проблема может становиться серьёзнее.", "становиться");
        ExpectNoAuto("Рост должен ставиться на первое место.", "ставиться");
        ExpectNoAuto("Программа будет запускаться автоматически.", "запускаться");
        ExpectNoAuto("Он решил остановиться.", "остановиться");
        ExpectNoAuto("Программа должна запускаться автоматически.", "запускаться");
    }

    [TestMethod]
    public void NesmotrjaNaToChto_NotSafeAuto()
    {
        var analyzer = new RuleBasedAnalyzer();
        var text = "Несмотря на то, что большинство стран подписали соглашения, прогресс слабый.";
        var issues = analyzer.Analyze(text);
        Assert.IsFalse(issues.Any(i =>
            i.CanApplyAutomatically
            && (i.Original.Contains("Несмотря", StringComparison.OrdinalIgnoreCase)
                || i.Explanation.Contains("несмотря", StringComparison.OrdinalIgnoreCase))));
    }

    [TestMethod]
    public void SafeMessage_CannotHedge()
    {
        Assert.IsTrue(TextOutputSanitizer.MessageContradictsSafeApply(
            "Запятая желательна перед союзом", true));
        Assert.IsTrue(TextOutputSanitizer.MessageContradictsSafeApply(
            "В некоторых стилях можно поставить запятую", true));
        Assert.IsFalse(TextOutputSanitizer.MessageContradictsSafeApply(
            "Перед придаточной частью цели с союзом «чтобы» нужна запятая.", true));
        Assert.IsFalse(TextOutputSanitizer.MessageContradictsSafeApply(
            "Возможно, стоит проверить", false));
    }

    [TestMethod]
    public void TextOutputSanitizer_StripsInjectedStars()
    {
        var cleaned = TextOutputSanitizer.SanitizeCorrection(
            "меры, чтобы",
            "меры**,** чтобы");
        Assert.AreEqual("меры, чтобы", cleaned);
    }

    [TestMethod]
    public void TextOutputSanitizer_KeepsUserMarkdown()
    {
        const string original = "**важный текст**";
        var cleaned = TextOutputSanitizer.SanitizeCorrection(original, "**важный текст**");
        Assert.AreEqual("**важный текст**", cleaned);
    }

    [TestMethod]
    public void TextOutputSanitizer_KeepsCodeStarsWhenOriginalHasFence()
    {
        const string original = "```csharp\nif (value ** 2 > 10)\n```";
        var cleaned = TextOutputSanitizer.SanitizeCorrection(original, original);
        Assert.IsNotNull(cleaned);
        Assert.IsTrue(cleaned!.Contains("**", StringComparison.Ordinal));
        Assert.IsTrue(cleaned.Contains("```", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TextOutputSanitizer_RejectsMetaCommentary()
    {
        var cleaned = TextOutputSanitizer.SanitizeCorrection(
            "текст",
            "Полностью исправленный вариант:\nтекст");
        Assert.IsNull(cleaned);
    }

    [TestMethod]
    public void Validator_NormalizesMarkdownCorrectedText()
    {
        var v = new AnalysisResultValidator();
        var n = v.NormalizeCorrectedText("меры, чтобы", "меры**,** чтобы");
        Assert.AreEqual("меры, чтобы", n);
    }

}
