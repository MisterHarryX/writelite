using WriteLite.Models;
using WriteLite.Services.Grammar;

namespace WriteLite.Tests.Language;

/// <summary>
/// The тся/ться pair, in both directions.
/// </summary>
/// <remarks>
/// Phase 6 added the direction this analyzer never had — a finite form written where the
/// sentence requires an infinitive — because both members are dictionary words, so no
/// spelling layer generates a candidate and nothing downstream can choose one. The negatives
/// below carry more weight than the positives: this rule applies automatically, and its
/// entire safety property is that the governor must be the immediately preceding token.
/// </remarks>
[TestClass]
public sealed class ReflexiveVerbFormTests
{
    private static IReadOnlyList<TextIssue> Analyze(string text)
    {
        var issues = new List<TextIssue>();
        ReflexiveVerbFormAnalyzer.Collect(text, [], issues);
        return issues;
    }

    private static TextIssue? Reflexive(string text)
        => Analyze(text).FirstOrDefault(i => i.RuleId is "ru.grammar.reflexive-infinitive"
                                                     or "ru.grammar.reflexive-finite");

    [TestMethod]
    [DataRow("Он хочет учится каждый день.", "учится", "учиться")]
    [DataRow("Она должна извинится перед ним.", "извинится", "извиниться")]
    [DataRow("Не нужно злится из-за мелочей.", "злится", "злиться")]
    [DataRow("Он может сосредоточится на работе.", "сосредоточится", "сосредоточиться")]
    [DataRow("Мы решили встретится в субботу.", "встретится", "встретиться")]
    public void ModalGovernor_RequiresInfinitive(string text, string original, string expected)
    {
        var issue = Reflexive(text);
        Assert.IsNotNull(issue, $"no finding for: {text}");
        Assert.AreEqual(original, issue.Original);
        Assert.AreEqual(expected, issue.Replacement);
        Assert.AreEqual(text.IndexOf(original, StringComparison.Ordinal), issue.Start);
    }

    /// <summary>
    /// A governor that is <em>near</em> is not a governor. Every one of these is correct
    /// Russian in which some token within the old five-token window governs an infinitive,
    /// while the -тся word next to it is a perfectly good third-person predicate.
    /// </summary>
    [TestMethod]
    [DataRow("Он должен, кажется, уйти сегодня.")]
    [DataRow("Мне нужно то, что находится в столе.")]
    [DataRow("Он хочет узнать, как это делается.")]
    [DataRow("Она может прийти, если получится.")]
    [DataRow("Нужно спросить у того, кто занимается этим.")]
    public void NonAdjacentGovernor_IsLeftAlone(string text)
        => Assert.IsNull(Reflexive(text), $"false positive on: {text}");

    /// <summary>Correct text in both directions must come back untouched.</summary>
    [TestMethod]
    [DataRow("Он учится в университете.")]
    [DataRow("Он хочет учиться в университете.")]
    [DataRow("Ситуация меняется каждый день.")]
    [DataRow("Всё может меняться со временем.")]
    [DataRow("Ей нравится, как это работает.")]
    [DataRow("Договор заключается на год.")]
    public void CorrectText_ProducesNothing(string text)
        => Assert.IsNull(Reflexive(text), $"false positive on: {text}");

    /// <summary>
    /// A -тся word opening a sentence or following punctuation has no governor at all, which
    /// is the same structural test the adjacency check performs.
    /// </summary>
    [TestMethod]
    [DataRow("Учится он хорошо.")]
    [DataRow("Он устал, и всё меняется.")]
    [DataRow("Что получается в итоге?")]
    public void NoGovernor_ProducesNothing(string text)
        => Assert.IsNull(Reflexive(text), $"false positive on: {text}");

    [TestMethod]
    public void Capitalisation_IsCarriedOnto_TheReplacement()
    {
        // A capitalised finite form after a governor is rare but must not lose its case.
        var issue = Reflexive("Он хочет Учится там.");
        Assert.IsNotNull(issue);
        Assert.AreEqual("Учиться", issue.Replacement);
    }

    [TestMethod]
    public void InfinitiveWrittenAsFinite_StillWorks()
    {
        // The pre-existing direction, unchanged by the addition of the new one.
        var issue = Reflexive("Погода становиться всё хуже.");
        Assert.IsNotNull(issue);
        Assert.AreEqual("становиться", issue.Original);
        Assert.AreEqual("становится", issue.Replacement);
    }

    [TestMethod]
    public void BothDirections_NeverFireOnTheSameToken()
    {
        foreach (var text in new[]
                 {
                     "Он хочет учится каждый день.",
                     "Погода становиться всё хуже.",
                     "Он учится в университете.",
                 })
        {
            var spans = Analyze(text)
                .Where(i => i.RuleId?.StartsWith("ru.grammar.reflexive", StringComparison.Ordinal) == true)
                .Select(i => i.Start)
                .ToList();
            Assert.AreEqual(spans.Count, spans.Distinct().Count(), $"overlapping findings on: {text}");
        }
    }
}
