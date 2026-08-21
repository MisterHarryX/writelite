using WriteLite.Language.Russian;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Rules;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests.Language;

/// <summary>
/// Corrections that change what the text means. Phase 7.5 §38 — these fail the release.
/// </summary>
/// <remarks>
/// <para>Found by real use, not by the benchmark. WriteLite offered «юзать» → «юань» and
/// «нерфить» → «неофит»: both are ordinary modern Russian words that the 3.09 M-form index
/// did not contain, so the spelling layer took them for typos and proposed the nearest thing
/// it did know.</para>
///
/// <para><b>Edit distance is not the guard, and that matters for how this is tested.</b>
/// «юзать» → «юань» is two edits and «нерфить» → «неофит» is two; ordinary typo corrections
/// in this corpus are routinely further away than that. There is no distance threshold that
/// separates them, so the fix is vocabulary — the words are now in the curated modern pack —
/// and the test is behavioural: the pipeline must not offer these substitutions, however it
/// comes to that conclusion.</para>
///
/// <para>New slang appears continuously, so this class is a floor rather than a proof. It
/// pins the words that were actually observed failing plus the categories §38 names.</para>
/// </remarks>
[TestClass]
public sealed class SemanticSafetyRegressionTests
{
    private static LocalSpellChecker _spellChecker = null!;
    private static RussianFormIndex? _index;
    private static RuleBasedAnalyzer _rules = null!;
    private static SpellTextAnalyzer _spelling = null!;

    [ClassInitialize]
    public static void Setup(TestContext context)
    {
        _ = context;
        _spellChecker = new LocalSpellChecker();
        _index = _spellChecker.RussianFormIndex;
        _rules = new RuleBasedAnalyzer(RuleCatalog.LoadDefault(), _index);
        _spelling = new SpellTextAnalyzer(_spellChecker);
    }

    [ClassCleanup]
    public static void Cleanup() => _spellChecker?.Dispose();

    private static IReadOnlyList<TextIssue> Analyze(string text)
        => [.. _rules.Analyze(text), .. _spelling.Analyze(text)];

    /// <summary>Every replacement the pipeline offers for <paramref name="text"/>.</summary>
    private static List<(string Original, string Replacement, string RuleId)> Replacements(string text)
        => Analyze(text)
            .Where(i => !string.IsNullOrEmpty(i.Replacement))
            .Select(i => (i.Original, i.Replacement!, i.RuleId))
            .ToList();

    // ---- the observed failures ------------------------------------------

    [TestMethod]
    [DataRow("нерфить", "неофит")]
    [DataRow("юзать", "юань")]
    [DataRow("бафф", "баф")]
    [DataRow("тильт", "тильд")]
    public void TheObservedSemanticCorruptionsAreNotProduced(string word, string corruption)
    {
        var text = $"Разработчики решили {word} перед релизом.";

        foreach (var (original, replacement, ruleId) in Replacements(text))
        {
            if (!original.Contains(word, StringComparison.OrdinalIgnoreCase)) continue;
            Assert.IsFalse(
                replacement.Contains(corruption, StringComparison.OrdinalIgnoreCase),
                $"{ruleId} rewrote «{original}» as «{replacement}»");
        }
    }

    [TestMethod]
    [DataRow("имба")]
    [DataRow("кринж")]
    [DataRow("рофл")]
    [DataRow("вайб")]
    [DataRow("юзать")]
    [DataRow("апнуть")]
    [DataRow("нерфить")]
    [DataRow("бафф")]
    [DataRow("тильт")]
    [DataRow("дефолтный")]
    [DataRow("фикс")]
    [DataRow("релиз")]
    public void ModernVocabularyIsKnownAndNeverRewritten(string word)
    {
        // §37: these are words, and the index has to say so. An unknown word is what starts
        // the search that ends in «юань».
        Assert.IsTrue(_index?.Contains(word) ?? false, $"«{word}» is not in the form index");

        var text = $"Это полный {word} по сравнению с прошлой версией.";
        var offered = Replacements(text)
            .Where(r => r.Original.Contains(word, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.IsEmpty(
            offered,
            string.Join(", ", offered.Select(o => $"{o.RuleId}: «{o.Original}»→«{o.Replacement}»")));
    }

    [TestMethod]
    public void SlangCarriesRegisterSoAFormalProfileCanStillCommentOnIt()
    {
        // Being known must not mean being invisible. §30: a formal profile may say the word
        // is colloquial — it may never say it is misspelled.
        if (_index is null) return;

        foreach (var word in new[] { "юзать", "нерфить", "бафф", "тильт", "имба", "кринж" })
        {
            var info = _index.GetInfo(word);
            Assert.IsTrue(
                info.Flags.HasFlag(RussianFormFlags.Slang) || info.Flags.HasFlag(RussianFormFlags.Informal),
                $"«{word}» lost its register flags");
        }
    }

    // ---- the other §38 categories ---------------------------------------

    [TestMethod]
    [DataRow("Рост составил 15% за квартал.", "15", "50")]
    [DataRow("Релиз назначен на 2026 год.", "2026", "2025")]
    public void NumbersAndDatesAreNeverAltered(string text, string original, string altered)
    {
        foreach (var (from, to, ruleId) in Replacements(text))
        {
            if (!from.Contains(original, StringComparison.Ordinal)) continue;
            Assert.IsFalse(to.Contains(altered, StringComparison.Ordinal), $"{ruleId}: «{from}»→«{to}»");
        }
    }

    [TestMethod]
    public void ProductNamesAreNotTransliterated()
    {
        const string text = "WriteLite использует WriteAI и работает локально.";

        foreach (var (from, to, ruleId) in Replacements(text))
        {
            foreach (var bad in new[] { "Райтлайт", "Райт" })
            {
                Assert.IsFalse(to.Contains(bad, StringComparison.OrdinalIgnoreCase), $"{ruleId}: «{from}»→«{to}»");
            }
        }
    }

    [TestMethod]
    public void NegationIsNeverDropped()
    {
        // The most damaging single-token edit there is: «не работает» → «работает».
        const string text = "Сервис не работает и не отвечает на запросы.";

        foreach (var issue in Analyze(text))
        {
            if (string.IsNullOrEmpty(issue.Replacement)) continue;

            var applied = text[..issue.Start] + issue.Replacement + text[(issue.Start + issue.Length)..];
            var before = System.Text.RegularExpressions.Regex.Matches(text, @"\bне\b").Count;
            var after = System.Text.RegularExpressions.Regex.Matches(applied, @"\bне\b").Count;

            Assert.IsGreaterThanOrEqualTo(before, after, $"{issue.RuleId} dropped a negation: «{applied}»");
        }
    }

    [TestMethod]
    public void CommandsAndPathsAreNotRewritten()
    {
        const string text = @"Запустите npm run build в папке C:\Users\Test\project.";
        var protectedSpans = ProtectedTextSpans.Find(text);

        foreach (var issue in Analyze(text))
        {
            Assert.IsFalse(
                ProtectedTextSpans.Overlaps(issue.Start, Math.Max(issue.Length, 1), protectedSpans),
                $"{issue.RuleId} touched protected content");
        }
    }
}
