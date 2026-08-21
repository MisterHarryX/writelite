using WriteLite.Language.Core;

namespace WriteLite.Tests.Language;

/// <summary>
/// The word-aware correction diff.
/// </summary>
/// <remarks>
/// The cases named "fragment" below are verbatim shapes from
/// <c>docs/ai-false-positive-analysis.md</c>: 22 of the local model's 123 false positives on
/// the frozen corpus were sub-word slices produced by the character diff this replaces.
/// They are tests rather than examples because a future change to alignment can bring them
/// straight back, and nothing else in the suite would notice.
/// </remarks>
[TestClass]
public sealed class LinguisticDiffTests
{
    // ── The fragments the character diff used to produce ────────────────────────

    [TestMethod]
    public void WordEnding_IsOneWordReplacement_NotAnEmptyToLetterFragment()
    {
        var edits = LinguisticDiff.Compute("Он был там.", "Он были там.");

        Assert.HasCount(1, edits);
        Assert.AreEqual(TextEditType.WordReplacement, edits[0].EditType);
        Assert.AreEqual("был", edits[0].Original);
        Assert.AreEqual("были", edits[0].Replacement);
        Assert.AreEqual(3, edits[0].Start);
        Assert.AreEqual(3, edits[0].Length);
    }

    [TestMethod]
    public void MergedWord_IsOneReplacement_NotASliceOfEachHalf()
    {
        // The character diff reported this pair as 'сторон' -> 'нами'.
        var edits = LinguisticDiff.Compute("Между сторонами спор.", "Между сторон нами спор.");

        Assert.HasCount(1, edits);
        Assert.AreEqual("сторонами", edits[0].Original);
        Assert.AreEqual("сторон нами", edits[0].Replacement);
    }

    [TestMethod]
    public void NoEdit_HasAnEmptyOriginalUnlessItIsARealInsertion()
    {
        foreach (var (original, corrected) in new[]
        {
            ("Он был там.", "Он были там."),
            ("Мир большой.", "Миры большие."),
            ("Она пришла домой.", "Она пришли домой."),
        })
        {
            foreach (var edit in LinguisticDiff.Compute(original, corrected))
            {
                Assert.IsFalse(
                    edit.Length == 0,
                    $"'{original}' -> '{corrected}' produced a zero-length span for a word change");
            }
        }
    }

    [TestMethod]
    public void SubWordFragment_IsNeverProduced()
    {
        var edits = LinguisticDiff.Compute("Мы шли по дороге.", "Мы шли по работу.");

        foreach (var edit in edits)
        {
            Assert.IsFalse(edit.IsSubWordFragment, $"fragment: '{edit.Original}' -> '{edit.Replacement}'");
        }
    }

    // ── Punctuation is an insertion, and that is legitimate ─────────────────────

    [TestMethod]
    public void MissingComma_IsAZeroLengthPunctuationInsertionAfterTheWord()
    {
        var edits = LinguisticDiff.Compute("Мне кажется что он опоздает.", "Мне кажется, что он опоздает.");

        Assert.HasCount(1, edits);
        Assert.AreEqual(TextEditType.PunctuationInsertion, edits[0].EditType);
        Assert.AreEqual(0, edits[0].Length);
        Assert.AreEqual(",", edits[0].Replacement);
        Assert.AreEqual("Мне кажется".Length, edits[0].Start);
        Assert.IsFalse(edits[0].IsSubWordFragment);
    }

    [TestMethod]
    public void PunctuationInsertion_AppliesToTheExpectedText()
    {
        const string source = "Когда он пришёл было уже поздно.";
        var edits = LinguisticDiff.Compute(source, "Когда он пришёл, было уже поздно.");

        Assert.AreEqual("Когда он пришёл, было уже поздно.", Apply(source, edits));
    }

    // ── Two independent corrections stay two corrections (§5) ───────────────────

    [TestMethod]
    public void IndependentCommaAndWordFix_AreSeparateEdits_NotOneSentenceRewrite()
    {
        const string source = "Я думаю что он придет.";
        var edits = LinguisticDiff.Compute(source, "Я думаю, что он придёт.");

        Assert.HasCount(2, edits);

        var comma = edits.Single(e => e.EditType == TextEditType.PunctuationInsertion);
        Assert.AreEqual(",", comma.Replacement);
        Assert.AreEqual("Я думаю".Length, comma.Start);

        var word = edits.Single(e => e.EditType == TextEditType.WordReplacement);
        Assert.AreEqual("придет", word.Original);
        Assert.AreEqual("придёт", word.Replacement);

        Assert.AreEqual("Я думаю, что он придёт.", Apply(source, edits));
    }

    [TestMethod]
    public void TwoAdjacentWordFixes_AreSplitBackApart()
    {
        var edits = LinguisticDiff.Compute("Он живёт в новым доме.", "Он живёт в новом доме.");

        Assert.HasCount(1, edits);
        Assert.AreEqual("новым", edits[0].Original);
        Assert.AreEqual("новом", edits[0].Replacement);
    }

    [TestMethod]
    public void UnrelatedAdjacentWords_StayOnePhraseEdit()
    {
        // "не смотря на" -> "несмотря на" cannot be split: the correction is the merge.
        var edits = LinguisticDiff.Compute("Он ушёл не смотря на дождь.", "Он ушёл несмотря на дождь.");

        Assert.HasCount(1, edits);
        Assert.AreEqual(TextEditType.PhraseReplacement, edits[0].EditType);
        Assert.AreEqual("не смотря", edits[0].Original);
        Assert.AreEqual("несмотря", edits[0].Replacement);
    }

    // ── Phrase-level corrections are first class (§6) ───────────────────────────

    [TestMethod]
    public void ReflexiveEndingInAPhrase_KeepsTheWordAsTheUnit()
    {
        var edits = LinguisticDiff.Compute("Он хочет учится дальше.", "Он хочет учиться дальше.");

        Assert.HasCount(1, edits);
        Assert.AreEqual(TextEditType.WordReplacement, edits[0].EditType);
        Assert.AreEqual("учится", edits[0].Original);
        Assert.AreEqual("учиться", edits[0].Replacement);
    }

    [TestMethod]
    public void TenseCorrectionAcrossTwoWords_IsAPhraseEdit()
    {
        const string source = "Мы будем ждали вас у входа.";
        var edits = LinguisticDiff.Compute(source, "Мы будем ждать вас у входа.");

        Assert.AreEqual("Мы будем ждать вас у входа.", Apply(source, edits));
        Assert.IsTrue(edits.All(e => e.WordCount <= LinguisticDiff.OverWideWordCount));
    }

    // ── Other edit kinds ────────────────────────────────────────────────────────

    [TestMethod]
    public void MissingSpaceAfterComma_IsAWhitespaceCorrection()
    {
        const string source = "Привет,мир и все остальные.";
        var edits = LinguisticDiff.Compute(source, "Привет, мир и все остальные.");

        Assert.HasCount(1, edits);
        Assert.AreEqual(TextEditType.WhitespaceCorrection, edits[0].EditType);
        Assert.AreEqual("Привет, мир и все остальные.", Apply(source, edits));
    }

    [TestMethod]
    public void RepeatedWord_IsAWordDeletion_ThatTakesItsSpaceWithIt()
    {
        const string source = "Это очень очень важно.";
        var edits = LinguisticDiff.Compute(source, "Это очень важно.");

        Assert.HasCount(1, edits);
        Assert.AreEqual(TextEditType.WordDeletion, edits[0].EditType);
        Assert.AreEqual(" очень", edits[0].Original);
        Assert.AreEqual(string.Empty, edits[0].Replacement);
        Assert.AreEqual("Это очень важно.", Apply(source, edits));
    }

    [TestMethod]
    public void ASentenceWithNothingInCommon_IsNotReportedAsACorrection()
    {
        Assert.IsEmpty(LinguisticDiff.Compute(
            "Сегодня хорошая погода.",
            "Завтра будет сильный дождь."));
    }

    [TestMethod]
    public void InsertedWord_CarriesTheSpaceItNeeds()
    {
        const string source = "Я пошёл магазин.";
        var edits = LinguisticDiff.Compute(source, "Я пошёл в магазин.");

        Assert.HasCount(1, edits);
        Assert.AreEqual(TextEditType.WordInsertion, edits[0].EditType);
        Assert.AreEqual("Я пошёл в магазин.", Apply(source, edits));
    }

    [TestMethod]
    public void InsertedWordAtTheStart_PutsTheSpaceOnTheOtherSide()
    {
        const string source = "Пошёл в магазин.";
        var edits = LinguisticDiff.Compute(source, "Он пошёл в магазин.");

        Assert.AreEqual("Он пошёл в магазин.", Apply(source, edits));
    }

    [TestMethod]
    public void ExchangedMark_IsAPunctuationReplacement()
    {
        var edits = LinguisticDiff.Compute("Ты идёшь.", "Ты идёшь?");

        Assert.HasCount(1, edits);
        Assert.AreEqual(TextEditType.PunctuationReplacement, edits[0].EditType);
    }

    // ── Spans stay apply-safe ───────────────────────────────────────────────────

    [TestMethod]
    public void EveryEditSpanMatchesTheOriginalTextItClaims()
    {
        foreach (var (source, corrected) in Corpus())
        {
            foreach (var edit in LinguisticDiff.Compute(source, corrected))
            {
                Assert.IsTrue(
                    edit.Start >= 0 && edit.Length >= 0 && edit.Start + edit.Length <= source.Length,
                    $"span out of range for '{source}'");
                Assert.AreEqual(
                    source.Substring(edit.Start, edit.Length),
                    edit.Original,
                    $"span/original mismatch for '{source}'");
            }
        }
    }

    [TestMethod]
    public void ApplyingEveryEditReproducesTheCorrectedText()
    {
        foreach (var (source, corrected) in Corpus())
        {
            Assert.AreEqual(
                corrected,
                Apply(source, LinguisticDiff.Compute(source, corrected)),
                $"round trip failed for '{source}'");
        }
    }

    [TestMethod]
    public void IdenticalText_ProducesNothing()
        => Assert.IsEmpty(LinguisticDiff.Compute("Всё уже правильно.", "Всё уже правильно."));

    [TestMethod]
    public void TextWithoutTokens_ProducesNothingRatherThanABlockRewrite()
        => Assert.IsEmpty(LinguisticDiff.Compute("...", "Совершенно другой текст."));

    private static IEnumerable<(string Source, string Corrected)> Corpus() =>
    [
        ("Он был там.", "Он были там."),
        ("Мне кажется что он опоздает.", "Мне кажется, что он опоздает."),
        ("Я думаю что он придет.", "Я думаю, что он придёт."),
        ("Дети которые играли во дворе разошлись по домам.", "Дети, которые играли во дворе, разошлись по домам."),
        ("Он хочет учится дальше.", "Он хочет учиться дальше."),
        ("Мы будем ждали вас у входа.", "Мы будем ждать вас у входа."),
        ("Привет,мир и все остальные.", "Привет, мир и все остальные."),
        ("Это очень очень важно.", "Это очень важно."),
        ("Я пошёл магазин.", "Я пошёл в магазин."),
        ("Он живёт в новым доме на окраине.", "Он живёт в новом доме на окраине."),
        ("Я поехал к бабушки в деревню.", "Я поехал к бабушке в деревню."),
        ("Он ушёл не смотря на дождь.", "Он ушёл несмотря на дождь."),
        ("Добавь middleware в pipeline.", "Добавь middleware в pipeline!"),
    ];

    /// <summary>Applies edits right to left so earlier offsets stay valid.</summary>
    private static string Apply(string source, IReadOnlyList<TextEdit> edits)
    {
        var text = source;
        foreach (var edit in edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.Length))
        {
            text = text[..edit.Start] + edit.Replacement + text[(edit.Start + edit.Length)..];
        }

        return text;
    }
}
