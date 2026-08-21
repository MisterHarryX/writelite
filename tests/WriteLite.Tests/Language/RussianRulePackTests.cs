using System.IO;
using WriteLite.Services;
using WriteLite.Services.Rules;

namespace WriteLite.Tests.Language;

[TestClass]
public sealed class RussianRulePackTests
{
    [TestMethod]
    public void DefaultPack_IsVersionedRussianOnlyAndHasEmbeddedInvariants()
    {
        var catalog = RuleCatalog.LoadDefault();

        // ru-1.3.0 adds the Phase 7 rules: ru.punctuation.vocative-comma,
        // ru.punctuation.greeting-sentence-boundary and ru.grammar.case-government-dative.
        // The pack version is pinned deliberately: every fragment must agree on it, so a rule
        // added to one file without a version bump fails loudly here.
        Assert.AreEqual("ru-1.6.0", catalog.PackVersion);
        Assert.IsGreaterThan(80, catalog.Rules.Count);
        Assert.IsTrue(catalog.Rules.All(rule => rule.Language == "ru"));
        Assert.IsTrue(catalog.Rules.All(rule => rule.RuleId.StartsWith("ru.", StringComparison.Ordinal)));
        Assert.AreEqual(catalog.Rules.Count, catalog.Rules.Select(rule => rule.RuleId).Distinct(StringComparer.Ordinal).Count());

        foreach (var rule in catalog.Rules)
        {
            Assert.IsNotEmpty(rule.BadExamples, rule.RuleId);
            Assert.IsNotEmpty(rule.GoodExamples, rule.RuleId);
            Assert.IsNotEmpty(rule.Suggestions, rule.RuleId);
            Assert.IsTrue(rule.Tests.Any(test => test.ShouldMatch), $"{rule.RuleId}: missing positive test");
            Assert.IsTrue(rule.Tests.Any(test => !test.ShouldMatch), $"{rule.RuleId}: missing negative test");
        }
    }

    [TestMethod]
    public void RuntimeIssues_AlwaysResolveToCanonicalMetadata()
    {
        var catalog = RuleCatalog.LoadDefault();
        var analyzer = new RuleBasedAnalyzer(catalog);
        const string text =
            "первое. второе предложение  содержит вообщем ошибку ,текст потомучто небыл готов. " +
            "Он пришёл что бы помочь и предпринять меры чтобы закончить работу";

        var issues = analyzer.Analyze(text);

        Assert.IsNotEmpty(issues);
        Assert.IsTrue(issues.All(issue => catalog.TryGet(issue.RuleId, out _)),
            string.Join(", ", issues.Select(issue => issue.RuleId)));
        Assert.AreEqual(issues.Count, issues.Select(issue => issue.RuleId).Distinct(StringComparer.Ordinal).Count()
            + issues.GroupBy(issue => issue.RuleId).Sum(group => Math.Max(0, group.Count() - 1)));
        Assert.IsFalse(issues.Any(issue => issue.RuleId.StartsWith("ru.builtin.", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Catalog_RejectsDuplicateIds()
    {
        var source = DefaultRuleDirectory();
        var temp = CreateTemporaryDirectory();
        try
        {
            File.Copy(Path.Combine(source, "grammar.json"), Path.Combine(temp, "grammar-a.json"));
            File.Copy(Path.Combine(source, "grammar.json"), Path.Combine(temp, "grammar-b.json"));

            Assert.ThrowsExactly<InvalidDataException>(() => RuleCatalog.LoadFromDirectory(temp));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void Catalog_RejectsUnsupportedSchemaAndLanguage()
    {
        var source = File.ReadAllText(Path.Combine(DefaultRuleDirectory(), "grammar.json"));
        var temp = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(temp, "bad-schema.json"),
                source.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99", StringComparison.Ordinal));
            Assert.ThrowsExactly<InvalidDataException>(() => RuleCatalog.LoadFromDirectory(temp));

            File.Delete(Path.Combine(temp, "bad-schema.json"));
            File.WriteAllText(
                Path.Combine(temp, "bad-language.json"),
                source.Replace("\"language\": \"ru\"", "\"language\": \"en\"", StringComparison.Ordinal));
            Assert.ThrowsExactly<InvalidDataException>(() => RuleCatalog.LoadFromDirectory(temp));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void TechnicalBlocksCommandsHashesAndSkus_AreProtected()
    {
        var analyzer = new RuleBasedAnalyzer();
        const string text =
            "```\nвообщем  код ,тест\n```\n" +
            "git commit --message вообщем  тест\n" +
            "SHA256: f6047416a0204adbecf3a451b874ec8a97ee37e2cbc714466ef04d8dbcc0d6fc\n" +
            "Артикул WL-2026_01 готов.";

        var issues = analyzer.Analyze(text);
        var protectedSpans = ProtectedTextSpans.Find(text);

        Assert.IsFalse(issues.Any(issue => ProtectedTextSpans.Overlaps(issue.Start, issue.Length, protectedSpans)));
    }

    [TestMethod]
    public void ExpressivePunctuationAndSoftStructure_AreNeverAutoApplied()
    {
        var analyzer = new RuleBasedAnalyzer();
        var issues = analyzer.Analyze("Правда??? Я думаю что это важно");

        Assert.IsTrue(issues.Any(issue => issue.RuleId == "ru.punctuation.repeated-mark"));
        Assert.IsFalse(issues.Where(issue => issue.RuleId is
            "ru.punctuation.repeated-mark" or
            "ru.punctuation.subordinate-comma" or
            "ru.punctuation.subordinate-comma.info").Any(issue => issue.CanApplyAutomatically));
    }

    private static string DefaultRuleDirectory()
        => Path.Combine(AppContext.BaseDirectory, "resources", "rules", "ru");

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "writelite-rule-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
