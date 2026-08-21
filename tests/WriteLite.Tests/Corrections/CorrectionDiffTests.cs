using WriteLite.Language.Core;
using WriteLite.Models;
using WriteLite.Services;

namespace WriteLite.Tests.Corrections;

/// <summary>
/// §4, §17 and §20: what the card draws must always put the correction back together, and it
/// must never make a correct replacement look broken.
/// </summary>
[TestClass]
public sealed class CorrectionDiffTests
{
    [DataTestMethod]
    [DataRow("роботает", "работает")]
    [DataRow("Сечас", "Сейчас")]
    [DataRow("превет", "привет")]
    [DataRow("работет", "работает")]
    [DataRow("интиресный", "интересный")]
    [DataRow("пожалуста", "пожалуйста")]
    [DataRow("сделаный", "сделанный")]
    public void Reconstructs_TheRequiredRegressionSet(string original, string replacement)
    {
        var diff = CorrectionDiff.Compute(original, replacement);

        Assert.AreEqual(original, diff.ReconstructedOriginal);
        Assert.AreEqual(replacement, diff.ReconstructedReplacement);
        Assert.IsTrue(CorrectionDiff.Reconstructs(diff, original, replacement));
    }

    [DataTestMethod]
    // Same length, first / middle / last character.
    [DataRow("зот", "кот")]
    [DataRow("кит", "кот")]
    [DataRow("коп", "кот")]
    // Longer, shorter, adjacent pair.
    [DataRow("кот", "котик")]
    [DataRow("котик", "кот")]
    [DataRow("коеы", "котыы")]
    // One contained in the other.
    [DataRow("работа", "работает")]
    [DataRow("работает", "работа")]
    // Insertion in the middle, deletion from the middle.
    [DataRow("сдать", "создать")]
    [DataRow("создать", "сдать")]
    // Punctuation and quotes.
    [DataRow("слово,", "слово.")]
    [DataRow("\"слово\"", "«слово»")]
    // ё, em dash, non-breaking space.
    [DataRow("елка", "ёлка")]
    [DataRow("a - b", "a — b")]
    [DataRow("10 %", "10 %")]
    // Multi-word replacements: the shape that used to render as a broken word.
    [DataRow("вообщем", "в общем")]
    [DataRow("потомучто", "потому что")]
    [DataRow("необходимобыло", "необходимо было")]
    // Emoji on either side of the change.
    [DataRow("😀роботает", "😀работает")]
    [DataRow("роботает😀", "работает😀")]
    // Nothing in common at all.
    [DataRow("абв", "хуz")]
    // Empty sides.
    [DataRow("", ",")]
    [DataRow("лишнее", "")]
    public void Reconstructs_EveryEditShape(string original, string replacement)
    {
        var diff = CorrectionDiff.Compute(original, replacement);

        Assert.AreEqual(original, diff.ReconstructedOriginal);
        Assert.AreEqual(replacement, diff.ReconstructedReplacement);
    }

    [TestMethod]
    public void Reconstructs_OverGeneratedPairs()
    {
        // A property test rather than a case list: the invariant has to hold for pairs
        // nobody thought to write down, which is where the malformed fragments came from.
        const string alphabet = "абвгдеёжз ,.«»— ";
        var random = new Random(20260820);

        for (var i = 0; i < 4000; i++)
        {
            var left = RandomString(random, alphabet);
            var right = RandomString(random, alphabet);
            var diff = CorrectionDiff.Compute(left, right);

            Assert.IsTrue(
                CorrectionDiff.Reconstructs(diff, left, right),
                $"failed for «{left}» → «{right}»");
        }
    }

    [TestMethod]
    public void Reconstructs_AroundSurrogatePairs()
    {
        string[] emoji = ["😀", "🎉", "👍"];
        var random = new Random(4242);

        for (var i = 0; i < 800; i++)
        {
            var left = emoji[random.Next(emoji.Length)] + RandomString(random, "абвгд") + emoji[random.Next(emoji.Length)];
            var right = emoji[random.Next(emoji.Length)] + RandomString(random, "абвгд") + emoji[random.Next(emoji.Length)];
            var diff = CorrectionDiff.Compute(left, right);

            Assert.IsTrue(CorrectionDiff.Reconstructs(diff, left, right), $"«{left}» → «{right}»");
            AssertNoLoneSurrogate(diff.Prefix);
            AssertNoLoneSurrogate(diff.Suffix);
            AssertNoLoneSurrogate(diff.ChangedOriginal);
            AssertNoLoneSurrogate(diff.ChangedReplacement);
        }
    }

    [TestMethod]
    public void Card_ShowsAMultiWordReplacementAsWords()
    {
        // The reported defect: a correct answer drawn as a fragmented one.
        var issue = Spelling("вообщем", "в общем");

        Assert.AreEqual("вообщем", CorrectionCardText.OriginalDisplay(issue));
        Assert.AreEqual("в общем", CorrectionCardText.ReplacementDisplay(issue));
        Assert.IsFalse(CorrectionCardText.ReplacementDisplay(issue).Contains('·'));
    }

    [TestMethod]
    public void Card_StillMarksWhitespaceThatIsItselfTheCorrection()
    {
        // A doubled space and a missing space are invisible without the glyph.
        var doubled = new TextIssue(5, 2, "  ", " ", "Оформление", "e", IssueCategory.Readability, IssueSeverity.Suggestion);
        Assert.AreEqual("··", CorrectionCardText.OriginalDisplay(doubled));
        Assert.AreEqual("·", CorrectionCardText.ReplacementDisplay(doubled));

        var missing = new TextIssue(5, 1, ",", ", ", "Оформление", "e", IssueCategory.Readability, IssueSeverity.Suggestion);
        Assert.AreEqual(",·", CorrectionCardText.ReplacementDisplay(missing));
    }

    [TestMethod]
    public void Card_NeverAltersTheAppliedReplacement()
    {
        // §3: the display may style, it may never become the source of truth.
        foreach (var (original, replacement) in new[]
                 {
                     ("роботает", "работает"),
                     ("вообщем", "в общем"),
                     ("Сечас", "Сейчас"),
                     ("потомучто", "потому что"),
                 })
        {
            var issue = Spelling(original, replacement);
            var diff = CorrectionCardText.Diff(issue);

            Assert.AreEqual(replacement, issue.Replacement);
            Assert.AreEqual(replacement, diff.ReconstructedReplacement);
            Assert.AreEqual(original, diff.ReconstructedOriginal);
        }
    }

    private static TextIssue Spelling(string original, string replacement) => new(
        0, original.Length, original, replacement,
        "Орфография", "Проверьте предложенный вариант.",
        IssueCategory.Orthography, IssueSeverity.Warning,
        CanApplyAutomatically: false, RuleId: "ru.spelling.typo");

    private static void AssertNoLoneSurrogate(string fragment)
    {
        for (var i = 0; i < fragment.Length; i++)
        {
            if (char.IsHighSurrogate(fragment[i]))
            {
                Assert.IsTrue(i + 1 < fragment.Length && char.IsLowSurrogate(fragment[i + 1]), fragment);
                i++;
            }
            else
            {
                Assert.IsFalse(char.IsLowSurrogate(fragment[i]), fragment);
            }
        }
    }

    private static string RandomString(Random random, string alphabet)
    {
        var length = random.Next(0, 12);
        return string.Create(length, (random, alphabet), static (span, state) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = state.alphabet[state.random.Next(state.alphabet.Length)];
            }
        });
    }
}
