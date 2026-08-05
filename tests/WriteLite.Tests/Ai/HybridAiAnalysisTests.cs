using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Ai;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Settings;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests.Ai;

[TestClass]
public sealed class HybridAiAnalysisTests
{
    [TestMethod]
    public void Diff_PunctuationRestore_ProducesIssues()
    {
        // "When I came home I noticed" style without comma
        var original = "\u041A\u043E\u0433\u0434\u0430 \u044F \u043F\u0440\u0438\u0448\u0435\u043B \u0434\u043E\u043C\u043E\u0439 \u044F \u0437\u0430\u043C\u0435\u0442\u0438\u043B";
        var corrected = "\u041A\u043E\u0433\u0434\u0430 \u044F \u043F\u0440\u0438\u0448\u0451\u043B \u0434\u043E\u043C\u043E\u0439, \u044F \u0437\u0430\u043C\u0435\u0442\u0438\u043B";
        var diff = new TextCorrectionDiffService();
        var issues = diff.BuildIssues(original, corrected);
        Assert.IsGreaterThan(0, issues.Count);
        Assert.IsTrue(issues.All(i => i.Start >= 0 && i.Start + i.Length <= original.Length));
        Assert.IsTrue(issues.All(i => string.Equals(original.Substring(i.Start, i.Length), i.Original, StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Diff_Identical_Empty()
    {
        var diff = new TextCorrectionDiffService();
        Assert.IsEmpty(diff.BuildIssues("\u043F\u0440\u0438\u0432\u0435\u0442", "\u043F\u0440\u0438\u0432\u0435\u0442"));
    }

    [TestMethod]
    public void Validator_RejectsOutOfRange()
    {
        var v = new AnalysisResultValidator();
        const string text = "abcdef";
        Assert.IsFalse(v.IsRangeValid(text, 4, 10));
        Assert.IsFalse(v.OriginalMatches(text, 0, 3, "zzz"));
        Assert.IsFalse(v.IsReplacementSane("достаточно длинный текст", "я"));
        Assert.IsFalse(v.IsCorrectedTextSane("long enough source text here", "x"));
    }

    [TestMethod]
    public void Validator_FiltersStaleOriginal()
    {
        var v = new AnalysisResultValidator();
        const string text = "abc def";
        var issues = new[]
        {
            new TextIssue(0, 3, "abc", "ABC", "t", "e", IssueCategory.Orthography, IssueSeverity.Error, true, "r1"),
            new TextIssue(0, 3, "zzz", "ZZZ", "t", "e", IssueCategory.Orthography, IssueSeverity.Error, true, "r2"),
        };
        var filtered = v.FilterValidIssues(text, issues);
        Assert.HasCount(1, filtered);
        Assert.AreEqual("abc", filtered[0].Original);
    }

    [TestMethod]
    public async Task Hybrid_LocalOnly_WhenAiDisabled()
    {
        var local = CreateLocalOrch();
        var hybrid = new HybridTextAnalysisService(local, new AiTextAnalysisService(new FakeAiProvider(enabled: false)));
        hybrid.ApplySettings(new WriteLiteAppSettings
        {
            CheckingEnabled = true,
            AiCheckingEnabled = false,
            ExtendedChecking = false
        });

        var issues = await hybrid.AnalyzeAsync("\u044D\u0442\u043E \u044D\u0442\u043E \u0442\u0435\u0441\u0442");
        Assert.IsTrue(issues.Any(i => i.LinguisticCategory == LinguisticIssueCategory.RepeatedWord
                                      || i.RuleId.Contains("repeated", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task Hybrid_MergesAiWithLocal()
    {
        var local = CreateLocalOrch();
        var provider = new FakeAiProvider(enabled: true)
        {
            Response = new AiTextAnalysisResponse
            {
                CorrectedText = "\u042F \u0437\u043D\u0430\u044E, \u0447\u0442\u043E \u044D\u0442\u043E \u0432\u0430\u0436\u043D\u043E.",
                Issues = []
            }
        };
        var hybrid = new HybridTextAnalysisService(local, new AiTextAnalysisService(provider));
        hybrid.ApplySettings(new WriteLiteAppSettings
        {
            CheckingEnabled = true,
            AiCheckingEnabled = true,
            ExtendedChecking = false,
            AiDebounceMs = 800,
            AiMinTextLength = 4
        });

        // Short AI debounce wait is inside hybrid — use short text with local+AI.
        var text = "\u042F \u0437\u043D\u0430\u044E \u0447\u0442\u043E \u044D\u0442\u043E \u0432\u0430\u0436\u043D\u043E";
        var issues = await hybrid.AnalyzeAsync(text);
        Assert.IsNotNull(issues);
        // AI should contribute at least one punctuation-related issue via diff.
        Assert.IsTrue(issues.Count >= 1, "Expected hybrid issues from local and/or AI");
    }

    [TestMethod]
    public async Task Hybrid_DropsStaleAiWhenTextFingerprintMismatch()
    {
        var provider = new FakeAiProvider(enabled: true)
        {
            Response = new AiTextAnalysisResponse
            {
                Issues =
                [
                    new AiTextIssueDto
                    {
                        Type = "spelling",
                        Original = "zzz",
                        Replacement = "yyy",
                        Start = 0,
                        Length = 3,
                        Message = "test",
                        Confidence = 0.99,
                        SafeToApply = true
                    }
                ]
            }
        };
        var hybrid = new HybridTextAnalysisService(new EmptyLocal(), new AiTextAnalysisService(provider));
        hybrid.ApplySettings(new WriteLiteAppSettings
        {
            CheckingEnabled = true,
            AiCheckingEnabled = true,
            AiDebounceMs = 800,
            AiMinTextLength = 1
        });

        var issues = await hybrid.AnalyzeAsync("abc");
        // original zzz does not match abc → filtered out
        Assert.IsEmpty(issues);
    }

    [TestMethod]
    public void LocalRules_CapitalizationAndSpaces()
    {
        var analyzer = new RuleBasedAnalyzer();
        var issues = analyzer.Analyze("\u043F\u0440\u0438\u0432\u0435\u0442  \u043C\u0438\u0440");
        Assert.IsTrue(issues.Any(i => i.Original.Contains("  ") || i.RuleId.Contains("space", StringComparison.OrdinalIgnoreCase)
                                      || i.LinguisticCategory == LinguisticIssueCategory.ExtraSpace));
    }

    private static WriteLiteOrchestratingAnalyzer CreateLocalOrch()
    {
        var engine = new WriteLiteLanguageEngine(new WriteLiteLanguageOptions { EnableEngine = false });
        var orch = new WriteLiteOrchestratingAnalyzer(
            new RuleBasedAnalyzer(),
            new SpellTextAnalyzer(new LocalSpellChecker()),
            engine,
            UserDictionaryService.CreateEmpty(),
            new WriteLiteIgnoreService());
        orch.ApplySettings(new WriteLiteAppSettings { CheckingEnabled = true, ExtendedChecking = false });
        return orch;
    }

    private sealed class EmptyLocal : ITextAnalyzer
    {
        public IReadOnlyList<TextIssue> Analyze(string text) => [];
    }

    private sealed class FakeAiProvider : IAiTextProvider
    {
        private readonly bool _enabled;
        public FakeAiProvider(bool enabled) => _enabled = enabled;
        public bool IsConfigured => _enabled;
        public AiTextAnalysisResponse Response { get; set; } = new() { Issues = [] };

        public Task<AiTextAnalysisResponse> AnalyzeAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult(Response);
    }
}
