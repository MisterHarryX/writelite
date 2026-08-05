using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests.LanguageEngine;

[TestClass]
public sealed class WriteLiteOrchestrationTests
{
    [TestMethod]
    public async Task Orchestrator_MergesLocalAnalyzers_WithoutEngine()
    {
        var engine = new WriteLiteLanguageEngine(new WriteLiteLanguageOptions { EnableEngine = false });
        var dict = UserDictionaryService.CreateEmpty();
        var ignore = new WriteLiteIgnoreService();
        var orch = new WriteLiteOrchestratingAnalyzer(
            new RuleBasedAnalyzer(),
            new SpellTextAnalyzer(new LocalSpellChecker()),
            engine,
            dict,
            ignore);

        var issues = await orch.AnalyzeAsync("это это тест  с пробелами");
        Assert.IsTrue(issues.Any(i => i.LinguisticCategory == LinguisticIssueCategory.RepeatedWord)
                      || issues.Any(i => i.Title.Contains("Повтор", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(issues.Any(i => i.Original.Contains("  ") || i.Title.Contains("пробел", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task Orchestrator_FallbackWhenEngineDisabled_DoesNotThrow()
    {
        var engine = new WriteLiteLanguageEngine(new WriteLiteLanguageOptions { EnableEngine = false });
        var orch = new WriteLiteOrchestratingAnalyzer(
            new RuleBasedAnalyzer(),
            new FixedAnalyzer([]),
            engine,
            UserDictionaryService.CreateEmpty(),
            new WriteLiteIgnoreService());

        var issues = await orch.AnalyzeAsync("Привет мир");
        Assert.IsNotNull(issues);
    }

    [TestMethod]
    public void UserDictionary_AddRemove_CaseInsensitive()
    {
        var dict = UserDictionaryService.CreateEmpty();
        const string token = "ТестСловоДляСловаря";
        dict.Add(token);
        Assert.IsTrue(dict.Contains(token.ToLowerInvariant()));
        Assert.IsTrue(dict.Remove(token));
        Assert.IsFalse(dict.Contains(token));
        dict.Add(token);
        dict.Clear();
        Assert.AreEqual(0, dict.Count);
    }

    [TestMethod]
    public void IgnoreService_WordRuleAndIssue()
    {
        var ignore = new WriteLiteIgnoreService();
        ignore.NotifyText("aaa bbb");
        var issue = new TextIssue(0, 3, "aaa", "bbb", "t", "e", IssueCategory.Orthography,
            IssueSeverity.Error, true, "WL-SPELL-TEST");

        ignore.IgnoreWord("aaa");
        Assert.IsTrue(ignore.IsIgnored(issue));

        ignore = new WriteLiteIgnoreService();
        ignore.NotifyText("aaa bbb");
        ignore.IgnoreRule("WL-SPELL-TEST");
        Assert.IsTrue(ignore.IsIgnored(issue));

        ignore = new WriteLiteIgnoreService();
        ignore.NotifyText("aaa bbb");
        ignore.IgnoreIssueUntilTextChanges(issue);
        Assert.IsTrue(ignore.IsIgnored(issue));
        ignore.NotifyText("changed text length enough");
        Assert.IsFalse(ignore.IsIgnored(issue));
    }

    [TestMethod]
    public void IgnoreService_FilterRemovesIgnored()
    {
        var ignore = new WriteLiteIgnoreService();
        ignore.NotifyText("xxx");
        var a = new TextIssue(0, 3, "xxx", "yyy", "t", "e", IssueCategory.Orthography,
            IssueSeverity.Error, true, "WL-A");
        var b = new TextIssue(4, 3, "zzz", "www", "t", "e", IssueCategory.Orthography,
            IssueSeverity.Error, true, "WL-B");
        ignore.IgnoreRule("WL-A");
        var filtered = ignore.Filter([a, b]);
        Assert.HasCount(1, filtered);
        Assert.AreEqual("WL-B", filtered[0].RuleId);
    }

    [TestMethod]
    public void Chunking_RecalculatesOffsets()
    {
        var text = new string('а', 100) + ". " + new string('б', 100);
        var chunks = SemanticTextChunker.CreatePlan(text, 80);
        Assert.IsGreaterThan(1, chunks.Count);
        Assert.AreEqual(0, chunks[0].Start);
        // Owned ranges, unlike request overlap, partition the document exactly.
        var covered = chunks.Sum(c => c.OwnedLength);
        Assert.AreEqual(text.Length, covered);
        Assert.IsTrue(chunks.Skip(1).Any(c => c.Start < chunks[c.Index - 1].End));
    }

    [TestMethod]
    public void Chunking_DoesNotSplitSurrogate()
    {
        var text = new string('a', 10) + "😀" + new string('b', 50);
        var chunks = SemanticTextChunker.CreatePlan(text, 12);
        foreach (var planned in chunks)
        {
            var chunk = planned.Text;
            for (var i = 0; i < chunk.Length; i++)
            {
                if (char.IsHighSurrogate(chunk[i]))
                {
                    Assert.IsTrue(i + 1 < chunk.Length && char.IsLowSurrogate(chunk[i + 1]));
                }
            }
        }
    }

    [TestMethod]
    public void SafeApplyAll_AppliesFromEnd()
    {
        const string text = "aa bb";
        var issues = new[]
        {
            new TextIssue(0, 2, "aa", "AA", "t", "e", IssueCategory.Orthography, IssueSeverity.Error, true, "r1"),
            new TextIssue(3, 2, "bb", "BB", "t", "e", IssueCategory.Orthography, IssueSeverity.Error, true, "r2")
        };
        Assert.IsTrue(TextCorrectionService.TryApplyAll(text, issues, true, out var result));
        Assert.AreEqual("AA BB", result);
    }

    [TestMethod]
    public void SafeApplyAll_SkipsOverlapping()
    {
        const string text = "abcde";
        var issues = new[]
        {
            new TextIssue(0, 3, "abc", "XXX", "t", "e", IssueCategory.Orthography, IssueSeverity.Error, true, "r1"),
            new TextIssue(2, 3, "cde", "YYY", "t", "e", IssueCategory.Orthography, IssueSeverity.Error, true, "r2")
        };
        var selected = TextCorrectionService.SelectApplicableAll(issues, text, true);
        Assert.HasCount(1, selected);
    }

    [TestMethod]
    public void Apply_VerifiesOriginalFragment()
    {
        var issue = new TextIssue(0, 3, "abc", "xyz", "t", "e", IssueCategory.Orthography,
            IssueSeverity.Error, true, "r");
        Assert.IsFalse(TextCorrectionService.CanApply(issue, "zzz", true));
        Assert.IsTrue(TextCorrectionService.CanApply(issue, "abcdef", true));
    }

    [TestMethod]
    public void Merger_ThreeAnalyzers_Dedup()
    {
        var merger = new WriteLiteIssueMerger();
        var shared = new TextIssue(0, 4, "хочю", "хочу", "t", "e", IssueCategory.Orthography,
            IssueSeverity.Error, true, "local");
        var engine = shared with { RuleId = "WL-SPELL-AAA" };
        var merged = merger.Merge([shared], [shared], [engine]);
        Assert.HasCount(1, merged.Issues);
        Assert.AreEqual("local", merged.Issues[0].RuleId);
    }

    [TestMethod]
    public void UserFacingEngineStatus_HasNoThirdPartyBrand()
    {
        var engine = new WriteLiteLanguageEngine(new WriteLiteLanguageOptions { EnableEngine = false });
        Assert.DoesNotContain("LanguageTool", engine.UserFacingStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Java", engine.UserFacingStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("JAR", engine.UserFacingStatus, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FixedAnalyzer(IReadOnlyList<TextIssue> issues) : ITextAnalyzer
    {
        public IReadOnlyList<TextIssue> Analyze(string text) => issues;
    }
}
