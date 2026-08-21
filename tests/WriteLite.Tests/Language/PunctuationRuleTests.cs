using WriteLite.Models;
using WriteLite.Services;

namespace WriteLite.Tests.Language;

/// <summary>
/// The two deterministic comma rules added in Phase 5, and the cases they must not touch.
/// </summary>
/// <remarks>
/// Both were chosen by clustering the 24 punctuation errors the deterministic pipeline
/// missed on the frozen corpus, not by inventing plausible rules: introductory words were
/// the largest cluster describable by a closed list (4 of 24), and adversative conjunctions
/// the next (2 of 24). The hard negatives matter more than the positives here — a comma rule
/// that raises punctuation recall while damaging no-change accuracy is a net loss, and
/// no-change is measured over 349 items the pipeline must leave alone.
/// </remarks>
[TestClass]
public sealed class PunctuationRuleTests
{
    // ── Introductory words ──────────────────────────────────────────────────────

    [TestMethod]
    [DataRow("К сожалению поезд опоздал на полчаса.", "К сожалению, поезд опоздал на полчаса.")]
    [DataRow("Кроме того нам нужно закончить отчёт.", "Кроме того, нам нужно закончить отчёт.")]
    [DataRow("Конечно я тебе обязательно помогу.", "Конечно, я тебе обязательно помогу.")]
    [DataRow("Например это можно сделать иначе.", "Например, это можно сделать иначе.")]
    [DataRow("Во-первых это слишком дорого.", "Во-первых, это слишком дорого.")]
    [DataRow("Итак начнём с самого начала.", "Итак, начнём с самого начала.")]
    [DataRow("По-моему он совершенно прав.", "По-моему, он совершенно прав.")]
    public void IntroductoryWord_GetsItsComma(string source, string expected)
    {
        var issue = Single(source, "ru.punctuation.intro-word-comma");
        Assert.AreEqual(expected, Apply(source, issue));
        Assert.IsTrue(issue.CanApplyAutomatically);
        Assert.IsGreaterThan(0.8, issue.Confidence);
        Assert.IsNotEmpty(issue.Explanation);
    }

    [TestMethod]
    public void IntroductoryWordAfterASentenceBoundary_IsAlsoFound()
    {
        const string source = "Он ушёл. К сожалению поезд опоздал.";
        var issue = Single(source, "ru.punctuation.intro-word-comma");
        Assert.AreEqual("Он ушёл. К сожалению, поезд опоздал.", Apply(source, issue));
    }

    [TestMethod]
    [DataRow("К сожалению, поезд опоздал на полчаса.")]      // already correct
    [DataRow("Конечно.")]                                    // a one-word reply
    [DataRow("Он сказал что конечно поможет.")]              // not sentence-initial
    [DataRow("Вчера поезд опоздал на полчаса.")]             // not an introductory word
    [DataRow("Однако никто не пришёл.")]                     // «однако» opens = «но», no comma
    [DataRow("Наконец мы приехали домой.")]                  // adverbial, not parenthetical
    [DataRow("Таким образом мы получили результат.")]        // adverbial reading is as common
    [DataRow("Правда всегда одна.")]                         // «правда» as a subject
    [DataRow("Вообще это интересный вопрос.")]               // ambiguous, deliberately absent
    public void IntroductoryWordRule_LeavesTheseAlone(string source)
        => Assert.IsEmpty(Find(source, "ru.punctuation.intro-word-comma"));

    // ── Adversative conjunctions ────────────────────────────────────────────────

    [TestMethod]
    [DataRow("Всё было готово однако никто не пришёл.", "Всё было готово, однако никто не пришёл.")]
    [DataRow("Работа трудная зато интересная.", "Работа трудная, зато интересная.")]
    public void AdversativeConjunction_GetsItsComma(string source, string expected)
    {
        var issue = Single(source, "ru.punctuation.adversative-comma");
        Assert.AreEqual(expected, Apply(source, issue));
        Assert.IsTrue(issue.CanApplyAutomatically);
        Assert.IsNotEmpty(issue.Explanation);

        // An insertion at a word boundary, not a replacement of the preceding word's last
        // letter — the span the user sees highlighted has to make sense.
        Assert.AreEqual(0, issue.Length);
        Assert.AreEqual(",", issue.Replacement);
    }

    [TestMethod]
    [DataRow("Всё было готово, однако никто не пришёл.")]    // already correct
    [DataRow("Однако никто не пришёл.")]                     // sentence-initial = «но»
    [DataRow("Всё было готово — однако никто не пришёл.")]   // dash already separates
    [DataRow("Он, однако, не пришёл.")]                      // parenthetical, already marked
    [DataRow("Именно поэтому он ушёл.")]                     // «поэтому» excluded on purpose
    [DataRow("Она устала поэтому легла раньше.")]            // excluded even where gold wants it
    public void AdversativeRule_LeavesTheseAlone(string source)
        => Assert.IsEmpty(Find(source, "ru.punctuation.adversative-comma"));

    // ── The rules must not fire on ordinary correct prose ───────────────────────

    [TestMethod]
    public void NeitherRuleFiresOnCorrectlyPunctuatedText()
    {
        foreach (var sentence in new[]
        {
            "Сегодня хорошая погода, и мы решили пойти гулять.",
            "Он сказал, что придёт завтра вечером.",
            "Добавь middleware в pipeline.",
            "Запусти npm run build и подожди.",
            "Дети, которые играли во дворе, разошлись по домам.",
            "Мы поехали в город, а они остались дома.",
            "Книга лежит на столе.",
        })
        {
            var issues = new RuleBasedAnalyzer().Analyze(sentence)
                .Where(i => i.RuleId is "ru.punctuation.intro-word-comma" or "ru.punctuation.adversative-comma")
                .ToList();
            Assert.IsEmpty(issues, $"fired on correct text: '{sentence}'");
        }
    }

    [TestMethod]
    public void EveryProducedIssueIsApplySafe()
    {
        foreach (var sentence in new[]
        {
            "К сожалению поезд опоздал.",
            "Всё было готово однако никто не пришёл.",
            "Конечно я помогу. Кроме того я позвоню.",
        })
        {
            foreach (var issue in Find(sentence, "ru.punctuation.intro-word-comma")
                         .Concat(Find(sentence, "ru.punctuation.adversative-comma")))
            {
                Assert.AreEqual(
                    sentence.Substring(issue.Start, issue.Length),
                    issue.Original,
                    $"span/original mismatch in '{sentence}'");
                Assert.IsNotNull(issue.Replacement);
                Assert.AreNotEqual(issue.Original, issue.Replacement);
            }
        }
    }

    private static IReadOnlyList<TextIssue> Find(string text, string ruleId)
        => new RuleBasedAnalyzer().Analyze(text).Where(i => i.RuleId == ruleId).ToList();

    private static TextIssue Single(string text, string ruleId)
    {
        var found = Find(text, ruleId);
        Assert.HasCount(1, found, $"expected exactly one {ruleId} in '{text}'");
        return found[0];
    }

    private static string Apply(string text, TextIssue issue)
        => text[..issue.Start] + issue.Replacement + text[(issue.Start + issue.Length)..];
}
