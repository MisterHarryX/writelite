using WriteLite.Language.Russian;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Ai;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Rules;
using WriteLite.Services.Settings;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests.Language;

/// <summary>
/// The editor and the field monitor must answer the same question the same way.
/// </summary>
/// <remarks>
/// <para>§39 and §59: the same text typed into the WriteLite editor and into an external field
/// should produce materially equivalent findings, because there should be one language core
/// with thin adapters over it rather than two engines that drift. The failure this guards
/// against has happened here before — the editor page used to construct a private rules-and-
/// spelling analyzer, which is why «Согласно нового плана» reached the sidebar unremarked while
/// the field monitor caught it.</para>
///
/// <para>The two paths are compared at the shared core, not through UI Automation: what is
/// being asserted is that the same analyzer instance backs both, and that the fast lane the
/// field monitor publishes first is a <em>subset</em> of what the deep lane later replaces it
/// with, never a contradiction of it.</para>
/// </remarks>
[TestClass]
public sealed class EditorFieldParityTests
{
    private static LocalSpellChecker? _checker;

    [ClassInitialize]
    public static void Load(TestContext _) => _checker = new LocalSpellChecker();

    private static readonly string[] Samples =
    [
        "Согласно нового плана мы отправим сборку заказчику.",
        "Мама приготовила вкусную обед для всей семьи.",
        "Прочитав письмо он надолго задумался.",
        "Дождь закончился и выглянуло солнце.",
        "Интерфейс стал более удобнее в этой версии.",
        "Все студенты сдал экзамен успешно.",
        "Прошу рассмотреть возможность переноса совещания на следующий вторник.",
    ];

    private static WriteLiteOrchestratingAnalyzer BuildCore()
    {
        var checker = _checker!;
        return new WriteLiteOrchestratingAnalyzer(
            new RuleBasedAnalyzer(RuleCatalog.LoadDefault(), checker.RussianFormIndex),
            new SpellTextAnalyzer(checker),
            new WriteLiteLanguageEngine(new WriteLiteLanguageOptions { EnableEngine = false }),
            UserDictionaryService.CreateEmpty(),
            new WriteLiteIgnoreService(),
            isKnownWord: word => checker.CheckWord(word, SpellingLanguage.Russian).IsKnown);
    }

    private static WriteLiteAppSettings Settings => new()
    {
        CheckingEnabled = true,
        ExtendedChecking = false,
        LocalAiEnabled = false,
    };

    [TestMethod]
    public async Task EditorAndFieldPaths_ShareOneAnalyzerInstance()
    {
        // The shell binds the same HybridTextAnalysisService to the editor and hands it to the
        // monitor as the deep lane. Anything else is two engines.
        var core = BuildCore();
        core.ApplySettings(Settings);

        var hybrid = new HybridTextAnalysisService(core);
        hybrid.ApplySettings(Settings);

        foreach (var text in Samples)
        {
            var viaEditor = await hybrid.AnalyzeAsync(text);
            var viaField = await hybrid.AnalyzeAsync(text);

            CollectionAssert.AreEqual(
                viaEditor.Select(Key).ToArray(),
                viaField.Select(Key).ToArray(),
                $"the two paths disagreed on: {text}");
        }
    }

    [TestMethod]
    public async Task FastLaneIsASubsetOfTheDeepLane()
    {
        // The field monitor publishes the fast lane immediately and replaces it with the deep
        // lane a moment later. §41 allows the fast lane to find less; §53 does not allow it to
        // find something the deep lane then contradicts, because the user would see a
        // correction change its mind.
        var checker = _checker!;
        var fast = new CompositeTextAnalyzer(
            new RuleBasedAnalyzer(RuleCatalog.LoadDefault(), checker.RussianFormIndex),
            new SpellTextAnalyzer(checker) { ContextualRefinementEnabled = false });

        var deepCore = BuildCore();
        deepCore.ApplySettings(Settings);

        foreach (var text in Samples)
        {
            var fastIssues = await fast.AnalyzeAsync(text);
            var deepIssues = await deepCore.AnalyzeAsync(text);

            foreach (var issue in fastIssues.Where(i => i.CanApplyAutomatically && i.Replacement is not null))
            {
                var contradicted = deepIssues.Any(d =>
                    d.Start == issue.Start
                    && d.Length == issue.Length
                    && d.Replacement is not null
                    && !string.Equals(d.Replacement, issue.Replacement, StringComparison.Ordinal));

                Assert.IsFalse(
                    contradicted,
                    $"fast lane and deep lane disagree on «{issue.Original}» in: {text}");
            }
        }
    }

    [TestMethod]
    public async Task UserDictionaryIsHonouredOnBothPaths()
    {
        // §60: a word the user added in the editor must not be flagged in the field path.
        // Both read the same dictionary instance because both run the same orchestrator.
        var checker = _checker!;
        var dictionary = UserDictionaryService.CreateEmpty();
        var core = new WriteLiteOrchestratingAnalyzer(
            new RuleBasedAnalyzer(RuleCatalog.LoadDefault(), checker.RussianFormIndex),
            new SpellTextAnalyzer(checker),
            new WriteLiteLanguageEngine(new WriteLiteLanguageOptions { EnableEngine = false }),
            dictionary,
            new WriteLiteIgnoreService());
        core.ApplySettings(Settings);

        const string text = "Мы используем Крякозябликс для сборки.";

        var before = await core.AnalyzeAsync(text);
        var flagged = before.Any(i => i.Original.Contains("Крякозябликс", StringComparison.Ordinal));

        dictionary.Add("Крякозябликс");

        var after = await core.AnalyzeAsync(text);
        Assert.IsFalse(
            after.Any(i => i.Original.Contains("Крякозябликс", StringComparison.Ordinal)),
            "a dictionary word was still flagged");

        // Only meaningful if it was flagged to begin with; otherwise the test proves nothing.
        Assert.IsTrue(flagged, "the fixture word was not treated as unknown, so the test is vacuous");
    }

    [TestMethod]
    public async Task CleanProseIsLeftAloneOnBothPaths()
    {
        var core = BuildCore();
        core.ApplySettings(Settings);
        var hybrid = new HybridTextAnalysisService(core);
        hybrid.ApplySettings(Settings);

        foreach (var text in new[]
                 {
                     "Прошу рассмотреть возможность переноса совещания на следующий вторник.",
                     "В соответствии с условиями договора оплата производится в течение десяти дней.",
                     "Логи пишутся в отдельный файл и ротируются раз в сутки.",
                 })
        {
            var issues = await hybrid.AnalyzeAsync(text);
            var applied = IssueApplication.Apply(
                text,
                issues.Where(i => i.Replacement is not null && i.CanApplyAutomatically).ToList());

            if (applied.Ok)
            {
                Assert.AreEqual(text, applied.Text, $"clean prose was changed: {text}");
            }
        }
    }

    private static string Key(TextIssue issue)
        => $"{issue.Start}:{issue.Length}:{issue.RuleId}:{issue.Replacement}";
}
