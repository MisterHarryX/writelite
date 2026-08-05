using WriteLite.Services;
using WriteLite.Services.Spelling;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace WriteLite.Tests;

[TestClass]
public sealed class SpellTextAnalyzerTests
{
    private static readonly CompositeTextAnalyzer Analyzer = new(
        new RuleBasedAnalyzer(),
        new SpellTextAnalyzer(new LocalSpellChecker()));

    [TestMethod]
    [DataRow("пливет", "привет")]
    [DataRow("привте", "привет")]
    [DataRow("вообщем", "в общем")]
    [DataRow("ghbdtn", "привет")]
    [DataRow("Пливет", "Привет")]
    [DataRow("славерное", "словарное")]
    [DataRow("испарвление", "исправление")]
    [DataRow("проверямое", "проверяемое")]
    [DataRow("словареное", "словарное")]
    public async Task AnalyzerSuggestsExpectedReplacement(string text, string expected)
    {
        var issues = await Analyzer.AnalyzeAsync(text);

        Assert.IsTrue(
            issues.Any(issue => issue.Replacement == expected),
            $"Expected '{expected}' for '{text}', got: {string.Join(", ", issues.Select(issue => issue.Replacement))}");
    }

    [TestMethod]
    [DataRow("WriteLite")]
    [DataRow("ChatGPT")]
    [DataRow("Telegram")]
    [DataRow("Windows 11")]
    [DataRow("test@example.com")]
    [DataRow("https://example.com")]
    [DataRow(@"C:\Projects\WriteLite")]
    [DataRow("TextFieldMonitor")]
    [DataRow("hello_world")]
    public async Task AnalyzerSkipsTechnicalTokens(string text)
    {
        var issues = await Analyzer.AnalyzeAsync(text);

        Assert.IsEmpty(issues, $"Unexpected issues: {string.Join(", ", issues.Select(issue => issue.Original))}");
    }

    [TestMethod]
    [DataRow("ошибки")]
    [DataRow("исправления")]
    [DataRow("проверяемое")]
    [DataRow("словарное")]
    [DataRow("лишние пробелы")]
    [DataRow("программа работает")]
    [DataRow("приложение появляется")]
    [DataRow("программу")]
    [DataRow("переключается")]
    [DataRow("который")]
    public async Task AnalyzerAcceptsKnownRussianForms(string text)
    {
        var issues = await Analyzer.AnalyzeAsync(text);

        Assert.IsFalse(
            issues.Any(issue => issue.Category == Models.IssueCategory.Orthography),
            $"Unexpected spelling issues: {string.Join(", ", issues.Select(issue => issue.Original + "->" + issue.Replacement))}");
    }

    [TestMethod]
    public async Task AnalyzerNeverReplacesOshibkiWithOshibka()
    {
        var issues = await Analyzer.AnalyzeAsync("ошибки");

        Assert.IsFalse(issues.Any(issue => issue.Original == "ошибки" && issue.Replacement == "ошибка"));
    }

    [TestMethod]
    public async Task DoubleSpaceHasApplicableRange()
    {
        var issues = await Analyzer.AnalyzeAsync("два  пробела");
        var issue = issues.Single(candidate => candidate.Original == "  ");

        Assert.AreEqual(3, issue.Start);
        Assert.AreEqual(2, issue.Length);
        Assert.AreEqual(" ", issue.Replacement);
        Assert.IsTrue(TextCorrectionService.TryApplySingle("два  пробела", issue, true, out var result));
        Assert.AreEqual("два пробела", result);
    }

    [TestMethod]
    public async Task AnalyzerSupportsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => Analyzer.AnalyzeAsync("пливет helllo", cancellation.Token));
    }

    [TestMethod]
    public void SeedDictionaryIsNotMarkedAsFull()
    {
        var dictionary = SeedSpellDictionary.Load();

        Assert.IsFalse(dictionary.IsFullDictionaryLoaded);
        Assert.IsFalse(dictionary.Stats.IsFullDictionaryLoaded);
    }

    [TestMethod]
    public async Task AnalyzerDetectsRequiredSeedModeIssues()
    {
        var issues = await Analyzer.AnalyzeAsync("пливет испарвление проверямое славерное это это два  пробела");

        Assert.IsTrue(issues.Any(issue => issue.Original == "пливет" && issue.Replacement == "привет"));
        Assert.IsTrue(issues.Any(issue => issue.Original == "испарвление" && issue.Replacement == "исправление"));
        Assert.IsTrue(issues.Any(issue => issue.Original == "проверямое" && issue.Replacement == "проверяемое"));
        Assert.IsTrue(issues.Any(issue => issue.Original == "славерное" && issue.Replacement == "словарное"));
        Assert.IsTrue(issues.Any(issue => issue.RuleId == "ru.style.repeated-word"));
        Assert.IsTrue(issues.Any(issue => issue.Original == "  " && issue.Replacement == " "));
    }

    [TestMethod]
    public async Task AnalyzerDoesNotProduceMassFalsePositivesInSeedMode()
    {
        const string text =
            """
            Сейчас приложение использует временный seed-словарь из нескольких десятков слов,
            поэтому почти каждое нормальное слово не должно считаться ошибкой.
            Программу можно открыть, пока окно переключается, и проверка работает без сотен ложных замечаний.
            Который пользователь видит только исправления для известных опечаток, повторов и лишних пробелов.
            Ошибки появляются только там, где правило действительно уверено в замене.
            """;

        var issues = await Analyzer.AnalyzeAsync(text);
        var orthographyIssues = issues
            .Where(issue => issue.Category == Models.IssueCategory.Orthography)
            .ToArray();

        Assert.IsLessThan(
            5,
            orthographyIssues.Length,
            $"Expected almost no spelling issues in seed mode, got {orthographyIssues.Length}: " +
            string.Join(", ", orthographyIssues.Select(issue => issue.Original + "->" + issue.Replacement)));
    }
}
