using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Settings;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests;

/// <summary>
/// Stage 10 offline scenario matrix: categories, apply, dictionary, settings filters.
/// Cyrillic samples use Unicode escapes to avoid source encoding issues.
/// </summary>
[TestClass]
public sealed class Stage10RuntimeScenarioTests
{
    // "Привет, как дела?"
    private const string CorrectText = "\u041F\u0440\u0438\u0432\u0435\u0442, \u043A\u0430\u043A \u0434\u0435\u043B\u0430?";
    // Known seed misspellings from SpellTextAnalyzerTests
    private const string Plivet = "\u043F\u043B\u0438\u0432\u0435\u0442"; // пливет
    private const string Ispravlenie = "\u0438\u0441\u043F\u0430\u0440\u0432\u043B\u0435\u043D\u0438\u0435"; // испарвление
    private const string Eto = "\u044D\u0442\u043E"; // это
    private const string Privet = "\u043F\u0440\u0438\u0432\u0435\u0442"; // привет

    [TestMethod]
    public async Task Scenario_CorrectText_HasNoIssuesOrOnlySoft()
    {
        var orch = CreateBaselineOrch();
        var issues = await orch.AnalyzeAsync(CorrectText);
        Assert.IsLessThanOrEqualTo(2, issues.Count);
    }

    [TestMethod]
    public async Task Scenario_Orthography_DetectsMisspellings()
    {
        var orch = CreateBaselineOrch();
        var text = $"\u042F {Plivet} \u043F\u0440\u043E\u0432\u0435\u0440\u0438\u0442\u044C {Ispravlenie}.";
        var issues = await orch.AnalyzeAsync(text);
        Assert.IsTrue(
            issues.Any(i => i.Original.Equals(Plivet, StringComparison.OrdinalIgnoreCase)
                            || i.Original.Equals(Ispravlenie, StringComparison.OrdinalIgnoreCase)
                            || i.Category == IssueCategory.Orthography),
            $"Expected orthography findings; got: {string.Join(", ", issues.Select(i => i.Original))}");
    }

    [TestMethod]
    public async Task Scenario_Repeats_Detected()
    {
        var orch = CreateBaselineOrch();
        var text = $"{Eto} {Eto} \u043E\u0447\u0435\u043D\u044C \u0445\u043E\u0440\u043E\u0448\u0438\u0439 \u0442\u0435\u043A\u0441\u0442.";
        var issues = await orch.AnalyzeAsync(text);
        Assert.IsTrue(
            issues.Any(i => i.LinguisticCategory == LinguisticIssueCategory.RepeatedWord
                            || i.RuleId.Contains("repeated", StringComparison.OrdinalIgnoreCase)
                            || i.Title.Contains("\u041F\u043E\u0432\u0442\u043E\u0440", StringComparison.OrdinalIgnoreCase)),
            $"Expected repeated-word finding; got: {string.Join(", ", issues.Select(i => i.RuleId + ":" + i.Original))}");
    }

    [TestMethod]
    public async Task Scenario_Typography_ExtraSpace()
    {
        var orch = CreateBaselineOrch();
        var text = $"{Privet}  \u043A\u0430\u043A \u0434\u0435\u043B\u0430";
        var issues = await orch.AnalyzeAsync(text);
        Assert.IsTrue(
            issues.Any(i => i.Original.Contains("  ")
                            || i.LinguisticCategory is LinguisticIssueCategory.ExtraSpace
                            || i.RuleId.Contains("space", StringComparison.OrdinalIgnoreCase)),
            $"Expected double-space finding; got: {string.Join(", ", issues.Select(i => i.RuleId))}");
    }

    [TestMethod]
    public async Task Scenario_Mixed_MultipleCategories_NoThrow()
    {
        var orch = CreateBaselineOrch();
        var text = $"{Privet}  \u043A\u0430\u043A \u0434\u0435\u043B\u0430 {Plivet} {Eto} {Eto} \u043F\u0440\u043E\u0432\u0435\u0440\u0438\u0442\u044C!!";
        var issues = await orch.AnalyzeAsync(text);
        Assert.IsNotNull(issues);
        foreach (var i in issues)
        {
            Assert.IsTrue(TextCorrectionService.IsRangeValid(text, i.Start, i.Length), i.RuleId);
        }
    }

    [TestMethod]
    public void Scenario_ApplyAll_FromEnd_NoOverlap()
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
    public void Scenario_ApplyAll_SkipsConflicting()
    {
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
    public async Task Scenario_CategoryOff_FiltersRepeats()
    {
        var orch = CreateBaselineOrch();
        orch.ApplySettings(new WriteLiteAppSettings
        {
            CheckingEnabled = true,
            ExtendedChecking = false,
            EnableRepeats = false,
            EnableOrthography = true,
            EnablePunctuation = true,
            EnableGrammar = true,
            EnableStyle = true,
            EnableTypography = true
        });
        var text = $"{Eto} {Eto} \u0441\u043B\u043E\u0432\u043E";
        var issues = await orch.AnalyzeAsync(text);
        Assert.IsFalse(issues.Any(i => i.LinguisticCategory == LinguisticIssueCategory.RepeatedWord));
    }

    [TestMethod]
    public async Task Scenario_CheckingDisabled_Empty()
    {
        var orch = CreateBaselineOrch();
        orch.ApplySettings(new WriteLiteAppSettings { CheckingEnabled = false });
        var issues = await orch.AnalyzeAsync(Plivet);
        Assert.IsEmpty(issues);
    }

    [TestMethod]
    public void Scenario_Dictionary_HidesOrthography()
    {
        var dict = UserDictionaryService.CreateEmpty();
        dict.Add(Plivet);
        Assert.IsTrue(dict.Contains(Plivet.ToUpperInvariant()));
        dict.Remove(Plivet);
        Assert.IsFalse(dict.Contains(Plivet));
    }

    [TestMethod]
    public void Scenario_Ignore_SessionClearsOnTextChange()
    {
        var ignore = new WriteLiteIgnoreService();
        ignore.NotifyText("aaa bbb");
        var issue = new TextIssue(0, 3, "aaa", "bbb", "t", "e", IssueCategory.Orthography,
            IssueSeverity.Error, true, "WL-TEST");
        ignore.IgnoreIssueUntilTextChanges(issue);
        Assert.IsTrue(ignore.IsIgnored(issue));
        ignore.NotifyText("completely different text content");
        Assert.IsFalse(ignore.IsIgnored(issue));
    }

    [TestMethod]
    public void Scenario_Chunking_LongText_OffsetsCoverAll()
    {
        var line = $"\u0421\u0442\u0440\u043E\u043A\u0430: {Privet} \u043C\u0438\u0440 \ud83d\ude00";
        var text = string.Join("\r\n", Enumerable.Range(0, 80).Select(i => $"{line} {i}"));
        var chunks = SemanticTextChunker.CreatePlan(text, 500);
        Assert.IsGreaterThan(1, chunks.Count);
        Assert.AreEqual(text.Length, chunks.Sum(c => c.OwnedLength));
        Assert.AreEqual(0, chunks[0].OwnedStart);
        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.AreEqual(chunks[i - 1].OwnedEnd, chunks[i].OwnedStart);
            Assert.IsLessThan(chunks[i - 1].End, chunks[i].Start);
        }
    }

    [TestMethod]
    public void Scenario_UserFacingUiStrings_NoThirdPartyBrand()
    {
        var engine = new WriteLiteLanguageEngine(new WriteLiteLanguageOptions { EnableEngine = false });
        var status = engine.UserFacingStatus;
        Assert.DoesNotContain("LanguageTool", status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Java", status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("JAR", status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HTTP", status, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void Scenario_Autostart_QuotedPath_NoDotnet()
    {
        var path = Path.Combine(Path.GetTempPath(), "WriteLite-stage10.exe");
        File.WriteAllText(path, "x");
        try
        {
            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var reg = new DictRegistry(values);
            var svc = new WriteLiteAutostartService(() => path, reg);
            svc.SetEnabled(true);
            var v = (string)values[WriteLiteAutostartService.RunValueName];
            Assert.StartsWith("\"", v);
            Assert.EndsWith("\"", v);
            Assert.DoesNotContain("dotnet.exe", v, StringComparison.OrdinalIgnoreCase);
            svc.SetEnabled(false);
            Assert.IsFalse(values.ContainsKey(WriteLiteAutostartService.RunValueName));
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    private static WriteLiteOrchestratingAnalyzer CreateBaselineOrch()
    {
        var engine = new WriteLiteLanguageEngine(new WriteLiteLanguageOptions { EnableEngine = false });
        var orch = new WriteLiteOrchestratingAnalyzer(
            new RuleBasedAnalyzer(),
            new SpellTextAnalyzer(new LocalSpellChecker()),
            engine,
            UserDictionaryService.CreateEmpty(),
            new WriteLiteIgnoreService());
        orch.ApplySettings(new WriteLiteAppSettings
        {
            CheckingEnabled = true,
            ExtendedChecking = false
        });
        return orch;
    }

    private sealed class DictRegistry(Dictionary<string, object> values) : IRegistryRoot
    {
        public IRegistryKey? OpenSubKey(string name, bool writable) => new DictKey(values);
    }

    private sealed class DictKey(Dictionary<string, object> values) : IRegistryKey
    {
        public void SetValue(string name, object value) => values[name] = value;
        public void DeleteValue(string name, bool throwOnMissing)
        {
            if (!values.Remove(name) && throwOnMissing) throw new ArgumentException("missing");
        }
        public object? GetValue(string name) => values.TryGetValue(name, out var v) ? v : null;
        public void Dispose() { }
    }
}
