using WriteLite.Language.Core;
using WriteLite.Services;

namespace WriteLite.Tests.Corrections;

/// <summary>
/// §2 and §16: a suggester that offers to break a word in two must show its working.
/// </summary>
/// <remarks>
/// Hunspell's own suggestions for the words in the brief, recorded from the shipped ru_RU
/// dictionary: «роботает» → «ро ботает», «Сечас» → «Се час» and «Сеча с», «пожалуста» →
/// «пожал уста», «непомню» → «не помню». Any of those becoming the top candidate puts a
/// broken word on the card and, if applied, in the user's field.
/// </remarks>
[TestClass]
public sealed class SplitCandidateTests
{
    /// <summary>A lexicon that knows real Russian words and nothing else.</summary>
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "ро", "ботает", "се", "час", "сеча", "с", "не", "помню",
        "пожал", "уста", "необходимо", "было", "как", "будто", "потому", "что",
    };

    private static bool IsKnown(string word) => Known.Contains(word);

    [DataTestMethod]
    [DataRow("роботает", "ро ботает")]   // «ро» is two letters — not evidence of anything.
    [DataRow("Сечас", "Се час")]         // same.
    [DataRow("Сечас", "Сеча с")]         // a one-letter tail.
    [DataRow("непомню", "не помню")]     // «не» is two letters.
    public void RejectsSplitsWhoseHalvesAreTooShortToMeanAnything(string original, string candidate)
    {
        Assert.IsTrue(CorrectionCandidateValidityPolicy.IsSplitCandidate(original, candidate));
        Assert.IsFalse(CorrectionCandidateValidityPolicy.IsAdmissibleSplit(original, candidate, IsKnown));
    }

    [DataTestMethod]
    [DataRow("необходимобыло", "необходимо было")]
    [DataRow("какбудто", "как будто")]
    [DataRow("пожалуста", "пожал уста")]
    public void AcceptsSplitsIntoTwoRealWords(string original, string candidate)
    {
        Assert.IsTrue(CorrectionCandidateValidityPolicy.IsAdmissibleSplit(original, candidate, IsKnown));
    }

    [TestMethod]
    public void RejectsASplitThatAlsoChangesLetters()
    {
        // Two guesses stacked on one another: the split is unverifiable if the letters moved.
        Assert.IsFalse(
            CorrectionCandidateValidityPolicy.IsAdmissibleSplit("непомню", "не помним", IsKnown));
    }

    [TestMethod]
    public void RejectsSplitsIntoMoreThanTwoParts()
    {
        Assert.IsFalse(
            CorrectionCandidateValidityPolicy.IsAdmissibleSplit("какбудто", "как буд то", IsKnown));
    }

    [TestMethod]
    public void FilterDropsSplitsWhenThereIsNoLexiconToJudgeThem()
    {
        var filtered = CorrectionCandidateValidityPolicy.FilterSuggestions(
            "роботает", ["ро ботает", "работает"]);

        CollectionAssert.AreEqual(new[] { "работает" }, filtered.ToArray());
    }

    [TestMethod]
    public void FilterKeepsAJustifiedSplitAndDropsTheRest()
    {
        var filtered = CorrectionCandidateValidityPolicy.FilterSuggestions(
            "необходимобыло",
            ["необходимо было", "необходимость", "необ ходимобыло"],
            isKnownWord: IsKnown);

        CollectionAssert.AreEqual(
            new[] { "необходимо было", "необходимость" }, filtered.ToArray());
    }

    [TestMethod]
    public void CuratedCorrectionsBypassTheGate()
    {
        // «вкурсе» → «в курсе» produces a one-letter preposition, which the evidentiary test
        // rightly refuses for a guess and wrongly would refuse for a stated rule of Russian.
        var filtered = CorrectionCandidateValidityPolicy.FilterCuratedSuggestions("вкурсе", ["в курсе"]);
        CollectionAssert.AreEqual(new[] { "в курсе" }, filtered.ToArray());

        Assert.AreEqual(
            0,
            CorrectionCandidateValidityPolicy.FilterSuggestions("вкурсе", ["в курсе"], isKnownWord: IsKnown).Count);
    }

    [TestMethod]
    public void ADictionaryAdditionIsAWholeWordOrNothing()
    {
        // §18: «В словарь» must never receive a fragment.
        Assert.AreEqual("роботает", DictionaryWordPolicy.ResolveWord("роботает"));
        Assert.AreEqual("кто-то", DictionaryWordPolicy.ResolveWord("кто-то"));
        Assert.AreEqual("роботает", DictionaryWordPolicy.ResolveWord("  роботает  "));

        Assert.IsNull(DictionaryWordPolicy.ResolveWord("робота тет"));
        Assert.IsNull(DictionaryWordPolicy.ResolveWord("роботает,"));
        Assert.IsNull(DictionaryWordPolicy.ResolveWord("-роботает"));
        Assert.IsNull(DictionaryWordPolicy.ResolveWord(""));
        Assert.IsNull(DictionaryWordPolicy.ResolveWord("я"));
        Assert.IsNull(DictionaryWordPolicy.ResolveWord("."));
    }
}
