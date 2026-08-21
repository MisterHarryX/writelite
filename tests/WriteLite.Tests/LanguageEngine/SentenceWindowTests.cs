using System.Diagnostics;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests.LanguageEngine;

/// <summary>
/// The minimal document-context layer: the sentence a correction is about, plus its
/// immediate neighbours, bounded.
/// </summary>
[TestClass]
public sealed class SentenceWindowTests
{
    [TestMethod]
    public void Splits_OnSentenceTerminators()
    {
        const string text = "Первое предложение. Второе предложение! Третье?";
        var spans = SentenceWindowBuilder.Split(text);

        Assert.HasCount(3, spans);
        Assert.AreEqual("Первое предложение.", text.Substring(spans[0].Start, spans[0].Length).Trim());
        Assert.AreEqual("Третье?", text.Substring(spans[2].Start, spans[2].Length).Trim());
    }

    /// <summary>
    /// Abbreviations are why this is not a one-line regex: splitting on every period would
    /// cut "т. д." in half and hand the model a fragment instead of a sentence.
    /// </summary>
    [TestMethod]
    public void DoesNotSplit_InsideRussianAbbreviations()
    {
        const string text = "Нужно купить хлеб, молоко и т. д. Завтра пойдём в магазин.";
        var spans = SentenceWindowBuilder.Split(text);

        Assert.HasCount(2, spans, "«и т. д.» was treated as a sentence ending");
        StringAssert.Contains(text.Substring(spans[0].Start, spans[0].Length), "и т. д.");
    }

    [TestMethod]
    public void DoesNotSplit_InsideDecimalNumbers()
    {
        const string text = "Версия 1.5 вышла вчера. Она стабильна.";
        Assert.HasCount(2, SentenceWindowBuilder.Split(text), "a decimal point ended a sentence");
    }

    [TestMethod]
    public void Around_ReturnsNeighbouringSentences()
    {
        const string text = "Андрей вошёл в кабинет. Девушка села у окна. Совещание началось.";
        var middle = text.IndexOf("Девушка", StringComparison.Ordinal);
        var window = SentenceWindowBuilder.Around(text, middle);

        StringAssert.Contains(window.Current, "Девушка села у окна");
        StringAssert.Contains(window.Previous, "Андрей вошёл в кабинет");
        StringAssert.Contains(window.Next, "Совещание началось");
    }

    [TestMethod]
    public void Around_AtDocumentEdges_HasEmptyNeighbours()
    {
        const string text = "Одно предложение. Второе предложение.";

        var first = SentenceWindowBuilder.Around(text, 0);
        Assert.AreEqual(string.Empty, first.Previous);
        Assert.AreNotEqual(string.Empty, first.Next);

        var last = SentenceWindowBuilder.Around(text, text.Length - 2);
        Assert.AreNotEqual(string.Empty, last.Previous);
        Assert.AreEqual(string.Empty, last.Next);
    }

    /// <summary>
    /// The property that keeps large documents affordable: context is bounded by a
    /// character budget, so a correction in a hundred-page document costs what a
    /// correction in a one-line note costs.
    /// </summary>
    [TestMethod]
    public void Around_BoundsContextRegardlessOfDocumentSize()
    {
        var huge = string.Join(" ", Enumerable.Repeat("Это довольно длинное предложение для проверки.", 4000));
        var middle = huge.Length / 2;

        var window = SentenceWindowBuilder.Around(huge, middle, contextBudget: 200);

        Assert.IsLessThanOrEqualTo(200, window.Previous.Length);
        Assert.IsLessThanOrEqualTo(200, window.Next.Length);
        Assert.IsLessThanOrEqualTo(
            1000,
            window.Joined.Length,
            "a bounded window must not grow with the document");
    }

    [TestMethod]
    public void Around_IsFastOnALargeDocument()
    {
        var huge = string.Join(" ", Enumerable.Repeat("Это довольно длинное предложение для проверки.", 8000));
        var sw = Stopwatch.StartNew();
        _ = SentenceWindowBuilder.Around(huge, huge.Length / 2);
        sw.Stop();

        Assert.IsLessThan(
            750,
            sw.ElapsedMilliseconds,
            $"windowing a large document took {sw.ElapsedMilliseconds} ms");
    }

    [TestMethod]
    public void EmptyText_ProducesAnEmptyWindow()
    {
        Assert.IsTrue(SentenceWindowBuilder.Around(string.Empty, 0).IsEmpty);
        Assert.IsEmpty(SentenceWindowBuilder.Split(string.Empty));
    }
}

/// <summary>
/// Proves the contextual pass keeps working past the reranker's 64-token window.
/// </summary>
/// <remarks>
/// Before sentence-windowing, <c>AddRealWordFindings</c> handed the entire analysed text
/// to a model whose encoder truncates at 64 tokens. A real-word error in the first
/// sentence was seen; the identical error further down a document was invisible, because
/// the token had been truncated away before the model ever saw it. Nothing measured this,
/// because the frozen benchmark is one sentence per item.
/// </remarks>
[TestClass]
public sealed class ContextualWindowingTests
{
    private static LocalSpellChecker? _checker;

    [ClassInitialize]
    public static void Load(TestContext _) => _checker = new LocalSpellChecker();

    [ClassCleanup]
    public static void Unload() => _checker?.Dispose();

    /// <summary>"будит" cannot follow "должен" — the case the reranker is measurably sure about.</summary>
    private const string ErrorSentence = "Он должен будит прийти на встречу завтра.";

    private static SpellTextAnalyzer Analyzer()
    {
        if (_checker is null) Assert.Inconclusive("spell checker unavailable");
        return new SpellTextAnalyzer(_checker!) { ContextualRefinementEnabled = true };
    }

    [TestMethod]
    public void RealWordError_IsFoundDeepInsideALongDocument()
    {
        var analyzer = Analyzer();
        if (!analyzer.ContextualRefinementEnabled)
        {
            Assert.Inconclusive("reranker not deployed next to the test binaries");
            return;
        }

        // Well past 64 tokens of preceding text.
        var filler = string.Join(" ", Enumerable.Repeat(
            "Сегодня погода была довольно хорошая и мы гуляли в парке.", 12));
        var document = filler + " " + ErrorSentence + " " + filler;

        var issues = analyzer.Analyze(document);
        var found = issues.FirstOrDefault(i => i.Original == "будит");

        Assert.IsNotNull(
            found,
            "the real-word error was invisible deep in the document: the contextual pass is "
            + "still being handed more text than its 64-token encoder can see");

        var expectedStart = document.IndexOf("будит", StringComparison.Ordinal);
        Assert.AreEqual(
            expectedStart,
            found.Start,
            "offsets must map back to the document, not to the sentence they were found in");
    }

    [TestMethod]
    public void LongCorrectDocument_GainsNoContextualFalsePositives()
    {
        var analyzer = Analyzer();
        if (!analyzer.ContextualRefinementEnabled)
        {
            Assert.Inconclusive("reranker not deployed next to the test binaries");
            return;
        }

        // Running the model on every sentence instead of once is only an improvement if it
        // stays quiet: more sentences scored is more chances to be wrong about correct text.
        var document = string.Join(" ", Enumerable.Repeat(
            "Наша компания открыла новый офис в центре города. "
            + "Предвыборная кампания продлится до конца месяца. "
            + "Он должен прийти на встречу завтра утром.", 8));

        var contextual = analyzer.Analyze(document)
            .Where(i => i.RuleId == "ru.context.real-word")
            .ToList();

        Assert.IsEmpty(
            contextual,
            "contextual pass flagged correct text: "
            + string.Join(", ", contextual.Select(i => $"{i.Original}->{i.Replacement}")));
    }
}
