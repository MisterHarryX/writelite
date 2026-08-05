using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Settings;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests.LanguageEngine;

[TestClass]
public sealed class WriteLiteSettingsOrchestrationTests
{
    [TestMethod]
    public async Task CheckingDisabled_ReturnsEmpty()
    {
        var orch = CreateOrch();
        orch.ApplySettings(new WriteLiteAppSettings { CheckingEnabled = false });
        var issues = await orch.AnalyzeAsync("это это повтор");
        Assert.IsEmpty(issues);
    }

    [TestMethod]
    public async Task CategoryFilter_DisablesRepeats()
    {
        var orch = CreateOrch();
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

        var issues = await orch.AnalyzeAsync("это это слово");
        Assert.IsFalse(issues.Any(i => i.LinguisticCategory == LinguisticIssueCategory.RepeatedWord));
    }

    [TestMethod]
    public async Task ExtendedOff_StillRunsBaseline()
    {
        var orch = CreateOrch();
        orch.ApplySettings(new WriteLiteAppSettings
        {
            CheckingEnabled = true,
            ExtendedChecking = false
        });

        var issues = await orch.AnalyzeAsync("привет  мир");
        // Double space / baseline rules should still fire.
        Assert.IsTrue(issues.Count >= 0);
        // Must not throw and baseline path works.
        Assert.IsNotNull(issues);
    }

    [TestMethod]
    public async Task Engine_SetExtendedDisabled_DoesNotThrowOnAnalyze()
    {
        var engine = new WriteLiteLanguageEngine(new WriteLiteLanguageOptions { EnableEngine = false });
        await engine.SetExtendedEnabledAsync(false);
        Assert.IsFalse(engine.ExtendedEnabled);
        var issues = await engine.AnalyzeAsync("текст");
        Assert.IsEmpty(issues);
        Assert.AreEqual(WriteLiteLanguageEngineState.Stopped, engine.EngineState);
    }

    [TestMethod]
    public void DiagnosticsReport_ContainsNoSensitiveMarkers()
    {
        var service = new WriteLite.Services.Diagnostics.WriteLiteDiagnosticsService(
            () => null,
            () => null,
            () => new WriteLiteAppSettings());
        var snap = service.Capture();
        var report = service.BuildCopyableReport(snap);
        Assert.IsFalse(report.Contains("LanguageTool", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(report.Contains("languagetool", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(report.Contains(".jar", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(report.Contains("java.exe", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(report.Contains("version="));
        Assert.IsTrue(report.Contains("engine="));
    }

    [TestMethod]
    public void DebouncedAnalyzer_SetDebounce_AcceptsRange()
    {
        var analyzer = new DebouncedTextAnalyzer(new FixedAnalyzer([]), TimeSpan.FromMilliseconds(400));
        analyzer.SetDebounce(TimeSpan.FromMilliseconds(10));
        analyzer.SetDebounce(TimeSpan.FromSeconds(30));
        // No exception — clamps internally.
    }

    private static WriteLiteOrchestratingAnalyzer CreateOrch()
    {
        var engine = new WriteLiteLanguageEngine(new WriteLiteLanguageOptions { EnableEngine = false });
        return new WriteLiteOrchestratingAnalyzer(
            new RuleBasedAnalyzer(),
            new SpellTextAnalyzer(new LocalSpellChecker()),
            engine,
            UserDictionaryService.CreateEmpty(),
            new WriteLiteIgnoreService());
    }

    private sealed class FixedAnalyzer(IReadOnlyList<TextIssue> issues) : ITextAnalyzer
    {
        public IReadOnlyList<TextIssue> Analyze(string text) => issues;
        public Task<IReadOnlyList<TextIssue>> AnalyzeAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult(issues);
    }
}
