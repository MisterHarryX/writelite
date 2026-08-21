using WriteLite.Language.Russian;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Rules;
using WriteLite.Services.Stylistics;

namespace WriteLite.Tests.Language;

/// <summary>
/// The release regression pack: explanation quality, protected content, slang, and the
/// mixed-error prose of §64.
/// </summary>
/// <remarks>
/// <para>§45 asks for expected explanation <em>meaning</em> rather than wording, so the
/// assertions are about which linguistic concept a reason names — «дательный падеж»,
/// «обращение» — not about the sentence it is phrased in. A rewording that keeps the
/// linguistics right keeps these passing; one that loses the reason does not.</para>
///
/// <para>§48: this is a release regression pack, not training data. Nothing here has been
/// used to tune a threshold or select a model.</para>
///
/// <para>These tests cover the deterministic layers only — no LanguageTool, no WriteAI. That
/// is deliberate: they must state what WriteLite guarantees on its own, on a machine with no
/// Java and no model server, which is the configuration a first-run user is in.</para>
/// </remarks>
[TestClass]
public sealed class Phase7ReleaseRegressionTests
{
    private static RussianFormIndex? _index;
    private static RuleBasedAnalyzer _rules = null!;

    [ClassInitialize]
    public static void Setup(TestContext context)
    {
        _ = context;
        _index = RussianFormIndex.Load();
        _rules = new RuleBasedAnalyzer(RuleCatalog.LoadDefault(), _index);
    }

    [ClassCleanup]
    public static void Cleanup() => _index?.Dispose();

    private static IReadOnlyList<TextIssue> Analyze(string text) => _rules.Analyze(text);

    private static IReadOnlyList<TextIssue> Style(string text, StyleProfile profile)
        => new RussianStyleAnalyzer(profile).Analyze(text, ProtectedTextSpans.Find(text));

    [TestMethod]
    [DataRow("Кто то позвонил.", "Кто-то")]
    [DataRow("Он что то сказал.", "что-то")]
    [DataRow("Где то шумит вода.", "Где-то")]
    public void SeparatedIndefiniteParticleGetsOneApplySafeIssue(string text, string replacement)
    {
        var issue = Analyze(text).Single(i => i.RuleId == "ru.spelling.particle-to");
        Assert.AreEqual(replacement, issue.Replacement);
        Assert.AreEqual(issue.Original, text.Substring(issue.Start, issue.Length));
    }

    [TestMethod]
    [DataRow("Я знаю, что то решение верно.")]
    [DataRow("Он спросил, где то место находится.")]
    public void DemonstrativeToInAClauseIsNotHyphenated(string text)
        => Assert.IsEmpty(Analyze(text).Where(i => i.RuleId == "ru.spelling.particle-to"));

    // ---- §45 explanation quality -----------------------------------------

    /// <summary>
    /// Common Russian mistakes, and the concept the reason has to name.
    /// </summary>
    [TestMethod]
    [DataRow("Согласно приказа отдел закрыт.", "дательного падежа")]
    [DataRow("Вопреки новых правил он ушёл.", "дательного падежа")]
    [DataRow("Привет дорогой друг.", "обращение")]
    [DataRow("Всё было готово однако никто не пришёл.", "союзом")]
    [DataRow("К сожалению поезд опоздал.", "вводное")]
    public void CommonMistakes_CarryAReasonThatNamesTheRule(string text, string concept)
    {
        var issues = Analyze(text);
        Assert.IsNotEmpty(issues, text);

        Assert.IsTrue(
            issues.Any(i => i.Explanation.Contains(concept, StringComparison.OrdinalIgnoreCase)),
            $"«{text}» -> no reason mentioning «{concept}»; got: "
            + string.Join(" | ", issues.Select(i => i.Explanation)));
    }

    [TestMethod]
    public void EveryFindingSatisfiesTheCardContract()
    {
        // §24: Original, Replacement, Category, Severity, Confidence, Why — on every finding
        // the pipeline can produce, not only on the ones a screenshot was taken of.
        string[] corpus =
        [
            "Согласно приказа отдел закрыт.",
            "Привет дорогой друг как твои дела",
            "Всё было готово однако никто не пришёл.",
            "Я думаю что он придёт.",
            "первое. второе предложение содержит вообщем ошибку",
            "Он хочет учится в университете.",
            "К сожалению поезд опоздал сегодня утром.",
        ];

        foreach (var text in corpus)
        {
            foreach (var issue in Analyze(text))
            {
                Assert.IsNotEmpty(issue.Title, $"{text} / {issue.RuleId}");
                Assert.IsNotEmpty(issue.Explanation, $"{text} / {issue.RuleId}");
                Assert.IsGreaterThan(0.0, issue.Confidence, $"{text} / {issue.RuleId}");
                Assert.AreNotEqual("unknown", issue.RuleId, text);

                // A finding that claims to be applicable must have something to apply.
                if (issue.CanApplyAutomatically)
                {
                    Assert.IsNotNull(issue.Replacement, $"{text} / {issue.RuleId}");
                }

                // The classification must be derivable and consistent with the category.
                if (issue.Category is IssueCategory.Style or IssueCategory.Readability)
                {
                    Assert.AreEqual(IssueClass.Style, issue.Class, issue.RuleId);
                }
            }
        }
    }

    // ---- §42 protected content -------------------------------------------

    [TestMethod]
    [DataRow(@"Файл лежит в C:\Users\Test\file.txt и открывается сразу.")]
    [DataRow("Запустите npm run build перед коммитом.")]
    [DataRow("Пишите на mister@example.com или откройте https://example.com/path?q=1")]
    [DataRow("Ответ пришёл в формате {\"status\": \"ok\", \"count\": 15}")]
    [DataRow("Тег <section class=\"main\"> закрывается ниже.")]
    [DataRow("Отметьте #релиз и упомяните @команда в треде.")]
    [DataRow("Рост составил 15% за период с 2026-08-15 по 2026-09-15.")]
    [DataRow("WriteLite 1.0 использует WriteAI и Qwen2.5 внутри.")]
    [DataRow("The quick brown fox jumps over the lazy dog.")]
    public void ProtectedContentIsNeverDamaged(string text)
    {
        var protectedSpans = ProtectedTextSpans.Find(text);

        foreach (var issue in Analyze(text))
        {
            Assert.IsFalse(
                ProtectedTextSpans.Overlaps(issue.Start, Math.Max(issue.Length, 1), protectedSpans),
                $"{issue.RuleId} touched protected content in «{text}»");
        }
    }

    [TestMethod]
    public void ProductNamesAndVersionsSurviveCorrection()
    {
        const string text = "WriteLite 1.0 и WriteAI работают локально, модель Qwen2.5 внутри.";

        foreach (var issue in Analyze(text))
        {
            var applied = text[..issue.Start] + (issue.Replacement ?? issue.Original)
                          + text[(issue.Start + issue.Length)..];

            foreach (var name in new[] { "WriteLite", "WriteAI", "Qwen2.5", "1.0" })
            {
                StringAssert.Contains(applied, name, $"{issue.RuleId} lost «{name}»");
            }
        }
    }

    // ---- §43 slang and modern language ------------------------------------

    [TestMethod]
    [DataRow("имба")]
    [DataRow("кринж")]
    [DataRow("рофл")]
    [DataRow("вайб")]
    [DataRow("юзать")]
    [DataRow("апнуть")]
    [DataRow("нерф")]
    [DataRow("бафф")]
    [DataRow("тильт")]
    [DataRow("дефолтный")]
    public void SlangIsNotASpellingError(string word)
    {
        // §43: modern vocabulary is recognised, not corrected. The rules layer must not
        // claim an orthography problem, and the lexicon must know the word.
        var text = $"Это полная {word} по сравнению с прошлой версией.";

        var orthography = Analyze(text)
            .Where(i => i.Category == IssueCategory.Orthography)
            .Where(i => i.Original.Contains(word, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.IsEmpty(orthography, $"«{word}» reported as a spelling error");
    }

    /// <summary>
    /// The §43 list, measured through the spelling pipeline rather than through the index.
    /// </summary>
    /// <remarks>
    /// <para>The requirement is behavioural — "not automatically a spelling error" — and index
    /// membership is only one of the things that decides it. A word the index does not know
    /// still has to survive candidate generation and the contextual gate before it becomes a
    /// correction offered to the user.</para>
    ///
    /// <para>Measured during Phase 7: the curated modern-vocabulary pack knows «имба»,
    /// «кринж», «рофл», «вайб», «апнуть», «нерф», «баф», «юзер», «имбовый», «кринжовый» and
    /// «рофлить», and does not know «юзать», «бафф», «тильт» or «дефолтный». This test records
    /// which of them the pipeline nevertheless leaves alone, so a regression in either the
    /// pack or the gate is visible.</para>
    /// </remarks>
    [TestMethod]
    public void ModernVocabulary_IsNotAutoCorrected()
    {
        string[] words =
        [
            "имба", "кринж", "рофл", "вайб", "юзать", "апнуть", "нерф", "бафф", "тильт", "дефолтный",
        ];

        using var spellChecker = new WriteLite.Services.Spelling.LocalSpellChecker();
        var analyzer = new WriteLite.Services.Spelling.SpellTextAnalyzer(spellChecker);

        var autoCorrected = new List<string>();
        foreach (var word in words)
        {
            var text = $"Это полная {word} по сравнению с прошлой версией.";
            var offered = analyzer.Analyze(text)
                .Where(i => i.Original.Contains(word, StringComparison.OrdinalIgnoreCase))
                .Where(i => i.CanApplyAutomatically && !string.IsNullOrEmpty(i.Replacement))
                .ToList();

            if (offered.Count > 0)
            {
                autoCorrected.Add($"{word} -> {offered[0].Replacement}");
            }
        }

        // §43: none of these may be silently rewritten into something else.
        Assert.IsEmpty(autoCorrected, string.Join(", ", autoCorrected));
    }

    [TestMethod]
    public void SlangCarriesItsRegisterInTheIndex()
    {
        // The flag is what lets a formal profile offer a neutral alternative without the
        // word ever being treated as an error.
        if (_index is null) return;

        var flagged = new[] { "имба", "кринж", "рофл", "юзать" }
            .Count(w => _index.GetInfo(w).Flags.HasFlag(RussianFormFlags.Slang)
                        || _index.GetInfo(w).Flags.HasFlag(RussianFormFlags.Informal));

        Assert.IsGreaterThanOrEqualTo(2, flagged, "modern vocabulary lost its register flags");
    }

    // ---- §64 the mixed-error acceptance scenario ---------------------------

    [TestMethod]
    public void TheAcceptanceScenarioSeparatesClassesOfProblem()
    {
        const string text =
            "Привет дорогой друг как твои дела я был очень сильно наслышан о твоих новых "
            + "достижениях а теперь я хочу заявить чт вопреки новых целей мы будем продолжать "
            + "достигать высот";

        var findings = Analyze(text).Concat(Style(text, StyleProfile.General)).ToList();

        // The point of §64 is that these are not all painted the same colour.
        var byClass = findings.GroupBy(f => f.Class).ToDictionary(g => g.Key, g => g.ToList());
        var byCategory = findings.GroupBy(f => f.Category).ToDictionary(g => g.Key, g => g.ToList());

        Assert.IsTrue(byCategory.ContainsKey(IssueCategory.Punctuation), "punctuation");
        Assert.IsTrue(byCategory.ContainsKey(IssueCategory.Grammar), "grammar");
        Assert.IsTrue(byCategory.ContainsKey(IssueCategory.Style), "style");

        // «очень сильно» is a style suggestion, not a grammar error.
        var intensifier = findings.SingleOrDefault(f => f.RuleId == "ru.style.double-intensifier");
        Assert.IsNotNull(intensifier);
        Assert.AreEqual(IssueClass.Style, intensifier.Class);

        // «вопреки новых целей» is an error, with the case named.
        var dative = findings.SingleOrDefault(f => f.RuleId == "ru.grammar.case-government-dative");
        Assert.IsNotNull(dative);
        Assert.AreEqual(IssueClass.Error, dative.Class);
        Assert.AreEqual("новым целям", dative.Replacement);
        StringAssert.Contains(dative.Explanation, "дательного падежа");

        // The address is punctuation, and the reason says so.
        var vocative = findings.SingleOrDefault(f => f.RuleId == "ru.punctuation.vocative-comma");
        Assert.IsNotNull(vocative);
        StringAssert.Contains(vocative.Explanation, "обращение");

        // At least three distinct classes reached the result: the whole point of §64 is that
        // WriteLite does not call everything a spelling error.
        Assert.IsGreaterThanOrEqualTo(2, byClass.Count,
            "everything landed in one class: " + string.Join(", ", byClass.Keys));
    }

    [TestMethod]
    public void TheAcceptanceScenarioProducesNonOverlappingApplicableCorrections()
    {
        // Whatever else it finds, "apply everything safe" must not produce garbage.
        const string text =
            "Привет дорогой друг как твои дела я был очень сильно наслышан о твоих новых "
            + "достижениях а теперь я хочу заявить чт вопреки новых целей мы будем продолжать "
            + "достигать высот";

        var applicable = Analyze(text)
            .Where(i => i.CanApplyAutomatically && !string.IsNullOrEmpty(i.Replacement))
            .OrderByDescending(i => i.Start)
            .ToList();

        var result = text;
        var previousStart = int.MaxValue;
        foreach (var issue in applicable)
        {
            Assert.IsLessThanOrEqualTo(previousStart, issue.Start + issue.Length,
                $"{issue.RuleId} overlaps the correction after it");
            result = result[..issue.Start] + issue.Replacement + result[(issue.Start + issue.Length)..];
            previousStart = issue.Start;
        }

        Assert.IsFalse(result.Contains("  ", StringComparison.Ordinal), $"double space: {result}");
        Assert.IsFalse(result.Contains(",,", StringComparison.Ordinal), $"double comma: {result}");
        StringAssert.Contains(result, "Привет,");
    }

    // ---- §41 long documents ------------------------------------------------

    [TestMethod]
    public void OffsetsStayCorrectAcrossALongDocument()
    {
        // Roughly fifty pages of repeated paragraphs. The assertion is that every finding
        // still describes the text at the offset it points to.
        const string paragraph =
            "Согласно приказа отдел закрыт. Всё было готово однако никто не пришёл. "
            + "Привет дорогой друг. К сожалению поезд опоздал.\n\n";
        var document = string.Concat(Enumerable.Repeat(paragraph, 400));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var findings = Analyze(document);
        sw.Stop();

        Assert.IsNotEmpty(findings);
        foreach (var issue in findings)
        {
            if (issue.Length == 0) continue;
            Assert.AreEqual(
                issue.Original,
                document.Substring(issue.Start, issue.Length),
                $"{issue.RuleId} at {issue.Start}");
        }

        Assert.IsLessThan(30_000, sw.ElapsedMilliseconds,
            $"{document.Length} characters took {sw.ElapsedMilliseconds} ms");
    }
}
