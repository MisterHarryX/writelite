using System.Text.Json;
using WriteLite.Language.Core;
using WriteLite.Language.Packs;
using WriteLite.Language.Punctuation;
using WriteLite.Services;
using WriteLite.Services.Lexical;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests.Language;

[TestClass]
public sealed class ExpandedLanguagePackTests
{
    [TestMethod]
    public void LexicalCore_HasHundredsOfEntries_AndPrivet()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "resources", "lexical", "writelight-lexical-core.json");
        Assert.IsTrue(File.Exists(path), path);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var count = doc.RootElement.GetProperty("manifest").GetProperty("entryCount").GetInt32();
        Assert.IsGreaterThanOrEqualTo(170, count);
        var svc = new OfflineLexicalKnowledgeService();
        svc.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "resources", "lexical"));
        Assert.IsTrue(svc.IsPackLoaded);
        var entry = svc.LookupEntry("привет", LexicalLanguage.Russian);
        Assert.IsNotNull(entry);
        Assert.IsTrue(entry!.Definitions.Count > 0);
        Assert.IsTrue(entry.Synonyms.Count > 0);
    }

    [TestMethod]
    public void Spelling_CommonWords_NoIssues()
    {
        using var checker = new LocalSpellChecker();
        var analyzer = new SpellTextAnalyzer(checker);
        foreach (var w in new[] { "привет", "здравствуйте", "программа", "приложение", "человек" })
        {
            Assert.AreEqual(0, analyzer.Analyze(w).Count, w);
        }
    }

    [TestMethod]
    public void Spelling_Typos_NonIdentical()
    {
        using var checker = new LocalSpellChecker();
        var analyzer = new SpellTextAnalyzer(checker);
        var issues = analyzer.Analyze("хочю");
        Assert.IsTrue(issues.Count >= 1);
        Assert.AreEqual("хочу", issues[0].Replacement);
        Assert.IsFalse(CorrectionCandidateValidityPolicy.IsIdenticalCorrection(issues[0].Original, issues[0].Replacement!));
    }

    [TestMethod]
    public void Punctuation_DoubleSpace_AndCommaSpace()
    {
        var a = new RuleBasedAnalyzer();
        var issues = a.Analyze("слово  слово ,да");
        Assert.IsTrue(issues.Any(i => i.Category is Models.IssueCategory.Punctuation or Models.IssueCategory.Readability));
    }

    [TestMethod]
    public void PackRegistry_FindsSpellingAndLexical()
    {
        var packs = new LanguagePackRegistry().Discover();
        Assert.IsTrue(packs.Any(p => p.Kind == "spelling" && p.Exists));
        Assert.IsTrue(packs.Any(p => p.Kind == "lexical" && p.Exists));
    }

    [TestMethod]
    public void PunctuationCatalog_HasRules()
    {
        Assert.IsGreaterThanOrEqualTo(10, PunctuationRuleCatalog.All.Count);
        Assert.IsGreaterThan(0, PunctuationRuleCatalog.CountRu);
        Assert.IsTrue(PunctuationRuleCatalog.All.All(rule => rule.Language == "ru"));
    }
}
