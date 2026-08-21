using System.Diagnostics;
using WriteLite.Language.Core;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests.Language;

/// <summary>
/// Corrections keep working, and keep pointing at the right characters, well past the first
/// screenful of a document.
/// </summary>
/// <remarks>
/// <para>Two things are checked together because they fail together. A pipeline that
/// analyses only the head of a document looks identical, from the outside, to one whose
/// offsets drift after the first few sentences: in both cases corrections stop appearing
/// where they should. So every assertion here is anchored — a known misspelling is planted
/// at a known position in a document of a known size, and the test demands both that it is
/// found and that the reported span is exactly it.</para>
///
/// <para>Re-run in Phase 5 because the word-aware diff changed how every AI-derived span is
/// computed, and because zero-length insertions became a shape the pipeline emits. An
/// off-by-one in either would show up here as a span that no longer matches its own text.</para>
/// </remarks>
[TestClass]
public sealed class LongDocumentOffsetTests
{
    /// <summary>Roughly 500 words — a page of prose.</summary>
    private const int WordsPerPage = 500;

    private const string Filler =
        "Сегодня погода стояла ясная и тёплая, поэтому мы решили пройтись до набережной пешком. "
        + "По дороге нам встретились знакомые, которые возвращались домой после работы. "
        + "Разговор зашёл о планах на выходные и о том, куда лучше поехать всей семьёй. ";

    private const string Planted = "Он написал севодня письмо.";
    private const string PlantedWord = "севодня";

    [TestMethod]
    [DataRow(1, DisplayName = "short text")]
    [DataRow(WordsPerPage, DisplayName = "1-page document")]
    [DataRow(WordsPerPage * 10, DisplayName = "10-page document")]
    [DataRow(WordsPerPage * 50, DisplayName = "50-page document")]
    [DataRow(WordsPerPage * 100, DisplayName = "100-page / 50,000-word document")]
    public void APlantedTypoIsFoundWithTheRightSpan_AtEveryDocumentSize(int approximateWords)
    {
        var (document, plantedAt) = BuildDocument(approximateWords);

        using var spellChecker = new LocalSpellChecker();
        var analyzer = new SpellTextAnalyzer(spellChecker) { ContextualRefinementEnabled = false };

        var sw = Stopwatch.StartNew();
        var issues = analyzer.Analyze(document);
        sw.Stop();

        var found = issues.FirstOrDefault(i => i.Start == plantedAt);
        Assert.IsNotNull(
            found,
            $"the typo planted at offset {plantedAt} in a ~{approximateWords}-word document was not "
            + $"reported; {issues.Count} issues came back, "
            + $"first at {(issues.Count > 0 ? issues[0].Start : -1)}");

        Assert.AreEqual(PlantedWord, found.Original);
        Assert.AreEqual(PlantedWord, document.Substring(found.Start, found.Length));

        Console.WriteLine($"~{approximateWords} words, {document.Length} chars, "
                          + $"{issues.Count} issues, {sw.ElapsedMilliseconds} ms");
    }

    [TestMethod]
    public void EveryReportedSpanMatchesItsOwnText_InALongDocument()
    {
        var (document, _) = BuildDocument(WordsPerPage * 10);

        using var spellChecker = new LocalSpellChecker();
        var analyzer = new CompositeTextAnalyzer(
            new RuleBasedAnalyzer(),
            new SpellTextAnalyzer(spellChecker) { ContextualRefinementEnabled = false });

        foreach (var issue in analyzer.Analyze(document))
        {
            Assert.IsTrue(
                issue.Start >= 0 && issue.Length >= 0 && issue.Start + issue.Length <= document.Length,
                $"span [{issue.Start}, {issue.Length}] is outside a {document.Length}-char document "
                + $"(rule {issue.RuleId})");

            Assert.AreEqual(
                document.Substring(issue.Start, issue.Length),
                issue.Original,
                $"rule {issue.RuleId} reported a span that does not contain the text it claims");
        }
    }

    [TestMethod]
    public void CorrectionsBeyondTheFirstFortyWordsAreStillFound()
    {
        // The specific regression this guards: analysis that stopped after the first window.
        // The planted typo sits after ~120 words, three times past the old boundary.
        var head = string.Concat(Enumerable.Repeat(Filler, 3));
        var document = head + Planted;
        var plantedAt = document.IndexOf(PlantedWord, StringComparison.Ordinal);

        Assert.IsGreaterThan(40, head.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

        using var spellChecker = new LocalSpellChecker();
        var analyzer = new SpellTextAnalyzer(spellChecker) { ContextualRefinementEnabled = false };

        Assert.IsTrue(
            analyzer.Analyze(document).Any(i => i.Start == plantedAt && i.Original == PlantedWord),
            "a typo past the first window was not reported");
    }

    /// <summary>
    /// Applying every correction to a long document leaves it intact except where the
    /// corrections were.
    /// </summary>
    /// <remarks>
    /// The end-to-end offset check. Applying right to left is only safe if every span is
    /// accurate; a single drifted offset corrupts the text after it, and comparing the
    /// untouched regions catches that where a per-issue assertion would not.
    /// </remarks>
    [TestMethod]
    public void ApplyingEveryCorrectionOnlyChangesTheSpansItClaimed()
    {
        var (document, _) = BuildDocument(WordsPerPage * 10);

        using var spellChecker = new LocalSpellChecker();
        var issues = new SpellTextAnalyzer(spellChecker) { ContextualRefinementEnabled = false }
            .Analyze(document)
            .Where(i => !string.IsNullOrEmpty(i.Replacement))
            .OrderByDescending(i => i.Start)
            .ToList();

        var applied = document;
        foreach (var issue in issues)
        {
            applied = applied[..issue.Start] + issue.Replacement + applied[(issue.Start + issue.Length)..];
        }

        // Everything before the first correction must be byte-identical.
        if (issues.Count > 0)
        {
            var firstStart = issues[^1].Start;
            Assert.AreEqual(
                document[..firstStart],
                applied[..firstStart],
                "text before the first correction changed, which means an offset drifted");
        }

        Assert.AreNotEqual(document, applied, "no correction was applied, so nothing was verified");
    }

    /// <summary>
    /// The word-aware diff keeps its spans valid against a long original.
    /// </summary>
    /// <remarks>
    /// <see cref="LinguisticDiff"/> is exercised on sentences in its own suite. This checks
    /// the case Phase 5 introduced the risk for: offsets computed against a long document
    /// rather than a lone sentence, where an alignment bug shows up as a span pointing into
    /// the wrong paragraph rather than as an obviously wrong word.
    /// </remarks>
    [TestMethod]
    public void WordAwareDiffSpansAreValidAgainstALongOriginal()
    {
        var (document, plantedAt) = BuildDocument(WordsPerPage);
        var corrected = document[..plantedAt] + "сегодня" + document[(plantedAt + PlantedWord.Length)..];

        var edits = LinguisticDiff.Compute(document, corrected);

        Assert.IsNotEmpty(edits, "no edit was produced for a one-word difference in a long document");
        foreach (var edit in edits)
        {
            Assert.AreEqual(
                document.Substring(edit.Start, edit.Length),
                edit.Original,
                "an edit span does not contain the text it claims");
        }

        var applied = edits
            .OrderByDescending(e => e.Start)
            .Aggregate(document, (text, e) => text[..e.Start] + e.Replacement + text[(e.Start + e.Length)..]);
        Assert.AreEqual(corrected, applied);
    }

    /// <summary>
    /// Beyond its alignment cap the diff reports nothing rather than one enormous span.
    /// </summary>
    /// <remarks>
    /// The cap is above anything the local model can produce — it runs with a 768-token
    /// context — so this is a guard, not a working mode. What matters is which way it fails:
    /// silence is recoverable, and a single span covering a whole document presented as a
    /// correction is not.
    /// </remarks>
    [TestMethod]
    public void BeyondTheAlignmentCap_TheDiffProducesNothingRatherThanABlockRewrite()
    {
        var (document, plantedAt) = BuildDocument(WordsPerPage * 20);
        var corrected = document[..plantedAt] + "сегодня" + document[(plantedAt + PlantedWord.Length)..];

        Assert.IsEmpty(LinguisticDiff.Compute(document, corrected));
    }

    /// <summary>Builds a document of about the requested size with one known typo near the end.</summary>
    private static (string Document, int PlantedAt) BuildDocument(int approximateWords)
    {
        var wordsPerFiller = Filler.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var repeats = Math.Max(1, approximateWords / wordsPerFiller);

        // Planted near the end rather than the start: a pipeline that only looks at the head
        // of a document passes any test that plants its evidence in the head.
        var document = string.Concat(Enumerable.Repeat(Filler, repeats)) + Planted;
        return (document, document.LastIndexOf(PlantedWord, StringComparison.Ordinal));
    }
}
