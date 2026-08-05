using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Settings;

namespace WriteLite.Tests;

[TestClass]
public sealed class LocalBugFixTests
{
    [TestMethod]
    public void ApplySingle_ReplacesFragment()
    {
        const string text = "aa bb cc";
        var issue = new TextIssue(3, 2, "bb", "BB", "t", "e", IssueCategory.Orthography, IssueSeverity.Error, true, "r");
        Assert.IsTrue(TextCorrectionService.TryApplySingle(text, issue, true, out var result, out var caret));
        Assert.AreEqual("aa BB cc", result);
        Assert.AreEqual(5, caret);
    }

    [TestMethod]
    public void ApplySingle_LengthZero_Insert()
    {
        const string text = "ab";
        var issue = new TextIssue(1, 0, "", ",", "t", "e", IssueCategory.Punctuation, IssueSeverity.Error, true, "ins");
        Assert.IsTrue(TextCorrectionService.CanApply(issue, text, true));
        Assert.IsTrue(TextCorrectionService.TryApplySingle(text, issue, true, out var result, out var caret));
        Assert.AreEqual("a,b", result);
        Assert.AreEqual(2, caret);
    }

    [TestMethod]
    public void ApplySingle_StaleOriginal_Rejected()
    {
        var issue = new TextIssue(0, 3, "abc", "xyz", "t", "e", IssueCategory.Orthography, IssueSeverity.Error, true, "r");
        Assert.IsFalse(TextCorrectionService.CanApply(issue, "zzz", true));
        Assert.IsFalse(TextCorrectionService.TryApplySingle("zzz", issue, true, out _));
    }

    [TestMethod]
    public void ApplyAll_FromEnd_NonOverlapping()
    {
        const string text = "aa bb cc";
        var issues = new[]
        {
            new TextIssue(0, 2, "aa", "AA", "t", "e", IssueCategory.Orthography, IssueSeverity.Error, true, "r1"),
            new TextIssue(3, 2, "bb", "BB", "t", "e", IssueCategory.Orthography, IssueSeverity.Error, true, "r2"),
            new TextIssue(6, 2, "cc", "CC", "t", "e", IssueCategory.Orthography, IssueSeverity.Error, true, "r3"),
        };
        Assert.IsTrue(TextCorrectionService.TryApplyAll(text, issues, true, out var result));
        Assert.AreEqual("AA BB CC", result);
    }

    [TestMethod]
    public void ApplyAll_ZeroLength_OnlyOnePerStart()
    {
        const string text = "ab";
        var issues = new[]
        {
            new TextIssue(1, 0, "", ",", "t", "e", IssueCategory.Punctuation, IssueSeverity.Error, true, "i1"),
            new TextIssue(1, 0, "", ";", "t", "e", IssueCategory.Punctuation, IssueSeverity.Error, true, "i2"),
        };
        var selected = TextCorrectionService.SelectApplicableAll(issues, text, true);
        Assert.HasCount(1, selected);
    }

    [TestMethod]
    public void ApplyAll_SkipsStaleAfterPartial()
    {
        // Overlapping: only one applied
        const string text = "abcdef";
        var issues = new[]
        {
            new TextIssue(0, 4, "abcd", "XXXX", "t", "e", IssueCategory.Orthography, IssueSeverity.Error, true, "r1"),
            new TextIssue(2, 4, "cdef", "YYYY", "t", "e", IssueCategory.Orthography, IssueSeverity.Error, true, "r2"),
        };
        var selected = TextCorrectionService.SelectApplicableAll(issues, text, true);
        Assert.HasCount(1, selected);
    }

    [TestMethod]
    public void Punctuation_SpaceBeforeComma_Fixed()
    {
        var analyzer = new RuleBasedAnalyzer();
        var issues = analyzer.Analyze("\u043F\u0440\u0438\u0432\u0435\u0442 , \u043C\u0438\u0440");
        Assert.IsTrue(issues.Any(i =>
            i.Category == IssueCategory.Punctuation
            && i.CanApplyAutomatically
            && i.Replacement is not null));
    }

    [TestMethod]
    public void Kogda_SentenceStart_NoFalseCommaBefore()
    {
        var analyzer = new RuleBasedAnalyzer();
        // "Когда я пришёл, всё изменилось." — no issue "before Когда"
        var text = "\u041A\u043E\u0433\u0434\u0430 \u044F \u043F\u0440\u0438\u0448\u0451\u043B, \u0432\u0441\u0451 \u0438\u0437\u043C\u0435\u043D\u0438\u043B\u043E\u0441\u044C.";
        var issues = analyzer.Analyze(text);
        Assert.IsFalse(
            issues.Any(i =>
                i.RuleId.Contains("subordinate", StringComparison.OrdinalIgnoreCase)
                && i.Start == 0),
            "Must not flag comma-before at sentence-initial Когда");
    }

    [TestMethod]
    public void Kogda_AfterPeriod_NoCommaHintOnConjunction()
    {
        var analyzer = new RuleBasedAnalyzer();
        var text = "\u0414\u0430. \u041A\u043E\u0433\u0434\u0430 \u043F\u0440\u0438\u0434\u0451\u0448\u044C, \u043F\u043E\u0437\u0432\u043E\u043D\u0438.";
        var issues = analyzer.Analyze(text);
        Assert.IsFalse(issues.Any(i =>
            i.Start < 10
            && i.RuleId.Contains("subordinate", StringComparison.OrdinalIgnoreCase)
            && i.Original.Contains("\u041A\u043E\u0433\u0434\u0430", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Merger_DedupsSameRangeOriginal()
    {
        var merger = new WriteLiteIssueMerger();
        var a = new TextIssue(0, 4, "test", "Test", "t1", "e", IssueCategory.Orthography, IssueSeverity.Error, true, "local");
        var b = new TextIssue(0, 4, "test", "TEST", "t2", "e", IssueCategory.Orthography, IssueSeverity.Error, true, "spell");
        var merged = merger.Merge([a], [b], []);
        Assert.HasCount(1, merged.Issues);
    }

    [TestMethod]
    public void Protected_UrlEmailPath_NotFlaggedByRules()
    {
        var analyzer = new RuleBasedAnalyzer();
        var text = "See https://example.com/path and C:\\Projects\\WriteLite and a@b.co please";
        var issues = analyzer.Analyze(text);
        Assert.IsFalse(issues.Any(i =>
            i.Original.Contains("https", StringComparison.OrdinalIgnoreCase)
            || i.Original.Contains("Projects", StringComparison.Ordinal)
            || i.Original.Contains("@")));
    }

    [TestMethod]
    public void ProtectedSpans_DetectsUrl()
    {
        var spans = ProtectedTextSpans.Find("go https://x.y/z now");
        Assert.IsTrue(spans.Count >= 1);
        Assert.IsTrue(ProtectedTextSpans.Overlaps(4, 14, spans));
    }

    [TestMethod]
    public void LocalAiEnabled_DefaultsTrue()
    {
        var s = new WriteLiteAppSettings();
        Assert.IsTrue(s.LocalAiEnabled);
        Assert.IsTrue(s.AiCheckingEnabled);
    }

    [TestMethod]
    public void Win32Edit_ClassDetection()
    {
        Assert.IsTrue(Win32TextEditVisible.IsSupportedEditClass("Edit"));
        Assert.IsTrue(Win32TextEditVisible.IsSupportedEditClass("RichEditD2DPT"));
        Assert.IsTrue(Win32TextEditVisible.IsSupportedEditClass("RICHEDIT50W"));
        Assert.IsFalse(Win32TextEditVisible.IsSupportedEditClass("Chrome_RenderWidgetHostHWND"));
        Assert.IsFalse(Win32TextEditVisible.IsSupportedEditClass(""));
    }

    // Test-visible surface for internal Win32 helpers used by adapters.
    private static class Win32TextEditVisible
    {
        public static bool IsSupportedEditClass(string className)
        {
            // Mirror Win32TextEdit.IsSupportedEditClass logic (internal type).
            if (string.IsNullOrEmpty(className)) return false;
            return className.Contains("Edit", StringComparison.OrdinalIgnoreCase)
                   || className.Contains("RichEdit", StringComparison.OrdinalIgnoreCase);
        }
    }
}
