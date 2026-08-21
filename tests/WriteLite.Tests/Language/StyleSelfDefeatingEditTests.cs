using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Stylistics;

namespace WriteLite.Tests.Language;

/// <summary>
/// A shortening must not introduce the repetition it was meant to remove.
/// </summary>
/// <remarks>
/// <para>From the same real-text run as <see cref="AgreementClauseBoundaryTests"/>. The report
/// contained «На данный момент времени мы сейчас завершили основной этап», the redundancy rule
/// fired correctly, and «Исправить все безопасные» produced «Сейчас мы сейчас завершили» —
/// shorter, still redundant, and now redundant in a way the writer had not written.</para>
///
/// <para>§28 asks the style layer to preserve meaning; this is the weaker sibling of that
/// requirement, which is that a stylistic edit should leave a sentence better than it found
/// it. The rule keeps firing wherever the replacement is genuinely new to the sentence, which
/// is what the second half of these tests holds down.</para>
/// </remarks>
[TestClass]
public sealed class StyleSelfDefeatingEditTests
{
    private static IReadOnlyList<TextIssue> Style(string text)
        => new RussianStyleAnalyzer(StyleProfile.General)
            .Analyze(text, ProtectedTextSpans.Find(text));

    [TestMethod]
    public void The_measured_regression_is_not_offered()
        => Assert.IsEmpty(
            Style("На данный момент времени мы сейчас завершили этап.")
                .Where(issue => issue.RuleId == "ru.style.redundant-moment"),
            "replacing the phrase with «Сейчас» would leave «Сейчас мы сейчас завершили»");

    [TestMethod]
    public void The_same_phrase_is_still_corrected_when_it_adds_something()
    {
        var found = Style("На данный момент времени мы завершили этап.")
            .Where(issue => issue.RuleId == "ru.style.redundant-moment")
            .ToArray();

        Assert.IsNotEmpty(found, "with no «сейчас» in the sentence the shortening is a real improvement");
        Assert.AreEqual("Сейчас", found[0].Replacement);
    }

    [TestMethod]
    public void The_guard_is_scoped_to_the_sentence_and_not_the_document()
        => Assert.IsNotEmpty(
            Style("Сейчас идёт подготовка. На данный момент времени мы завершили этап.")
                .Where(issue => issue.RuleId == "ru.style.redundant-moment"),
            "a repetition in a neighbouring sentence is not a repetition in this one");
}
