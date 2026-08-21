using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Ai;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests.Language;

/// <summary>
/// What the correction popup actually receives, inspected as a product surface rather than
/// as a metric.
/// </summary>
/// <remarks>
/// <para>Precision and recall are blind to this. A finding can be counted a true positive and
/// still be unusable — <c>'' → 'ир'</c> matched a gold span in Phase 4 and told the user
/// nothing. §34 asks for the opposite check: take issues as the UI gets them and require
/// each one to be something a person can act on.</para>
///
/// <para>The AI half of these assertions runs against a stubbed provider rather than the
/// local model, so the test measures the pipeline's shaping of a model answer and stays
/// deterministic. Whether the model's answers are any good is what the benchmark is for.</para>
/// </remarks>
[TestClass]
public sealed class CorrectionPopupQualityTests
{
    private static readonly string[] Sentences =
    [
        "Он сьел весь обед и попросил добавки.",
        "Мне кажется что он опоздает.",
        "К сожалению поезд опоздал на полчаса.",
        "Всё было готово однако никто не пришёл.",
        "Ghbdtn, как дела у тебя?",
        "Он обещал приехать crjhj.",
        "Собака радостно виляла хвостм.",
        "Наш сосед очень доброжелательнй человек.",
        "Это очень очень важно.",
        "Гагарен первым полетел в космос.",
    ];

    [TestMethod]
    public void EveryDeterministicIssueIsSomethingAUserCanActOn()
    {
        using var spellChecker = new LocalSpellChecker();
        var analyzer = new CompositeTextAnalyzer(
            new RuleBasedAnalyzer(),
            new SpellTextAnalyzer(spellChecker) { ContextualRefinementEnabled = false });

        var inspected = 0;
        foreach (var sentence in Sentences)
        {
            foreach (var issue in analyzer.Analyze(sentence))
            {
                AssertUsable(sentence, issue);
                inspected++;
            }
        }

        Assert.IsGreaterThan(0, inspected, "no issues were produced, so nothing was inspected");
        Console.WriteLine($"inspected {inspected} deterministic issues across {Sentences.Length} sentences");
    }

    [TestMethod]
    public void ModelDerivedIssuesAreWordShaped_NotDiffFragments()
    {
        var diff = new TextCorrectionDiffService();

        // Left column is what the model returns as a rewritten sentence. Each of these
        // produced a sub-word fragment through the character diff this replaced.
        (string Original, string Corrected)[] rewrites =
        [
            ("Он был там вчера.", "Он были там вчера."),
            ("Мне кажется что он опоздает.", "Мне кажется, что он опоздает."),
            ("Я думаю что он придет.", "Я думаю, что он придёт."),
            ("Между сторонами возник спор.", "Между сторон нами возник спор."),
            ("Он хочет учится дальше.", "Он хочет учиться дальше."),
            ("Мы шли по дороге домой.", "Мы шли по работу домой."),
        ];

        var inspected = 0;
        foreach (var (original, corrected) in rewrites)
        {
            foreach (var issue in diff.BuildIssues(original, corrected))
            {
                AssertUsable(original, issue);

                // The specific shape §34 rejects: an insertion of letters with no space
                // around them, which fuses onto the neighbouring word.
                if (issue.Length == 0 && issue.Replacement is { Length: > 0 } inserted)
                {
                    var carriesLetters = inserted.Any(char.IsLetterOrDigit);
                    var spaced = inserted[0] == ' ' || inserted[^1] == ' ';
                    Assert.IsTrue(
                        !carriesLetters || spaced,
                        $"'{original}' -> '{corrected}' produced the fragment '' -> '{inserted}'");
                }

                inspected++;
            }
        }

        Assert.IsGreaterThan(0, inspected);
        Console.WriteLine($"inspected {inspected} model-derived issues across {rewrites.Length} rewrites");
    }

    [TestMethod]
    public void ModelDerivedIssuesCarryACategoryPrior_NotPerfectCertainty()
    {
        var diff = new TextCorrectionDiffService();
        var issues = diff.BuildIssues("Я думаю что он придет.", "Я думаю, что он придёт.");

        Assert.IsNotEmpty(issues);
        foreach (var issue in issues)
        {
            // Phase 4: every AI finding carried exactly 1.000, which made every acceptance
            // threshold below 1.0 a no-op.
            Assert.IsLessThan(1.0, issue.Confidence, $"'{issue.Original}' claims perfect certainty");
            Assert.IsGreaterThan(0.0, issue.Confidence);
            Assert.AreNotEqual(
                IssueSeverity.Error,
                issue.Severity,
                "a model-derived suggestion must not present with the authority of a dictionary hit");
        }
    }

    [TestMethod]
    public void AutoApplyIsNeverOfferedForAPhraseRewrite()
    {
        var diff = new TextCorrectionDiffService();

        foreach (var issue in diff.BuildIssues("Он ушёл не смотря на дождь.", "Он ушёл несмотря на дождь."))
        {
            if (issue.Original.Contains(' '))
            {
                Assert.IsFalse(
                    issue.CanApplyAutomatically,
                    $"a multi-word rewrite '{issue.Original}' -> '{issue.Replacement}' offered itself for "
                    + "silent application");
            }
        }
    }

    /// <summary>Every field the popup renders has to carry something.</summary>
    private static void AssertUsable(string sentence, TextIssue issue)
    {
        var where = $"{issue.RuleId} at {issue.Start}+{issue.Length}";

        Assert.IsTrue(
            issue.Start >= 0 && issue.Length >= 0 && issue.Start + issue.Length <= sentence.Length,
            $"{where}: span outside the text");

        Assert.AreEqual(
            sentence.Substring(issue.Start, issue.Length),
            issue.Original,
            $"{where}: span does not contain the text it claims");

        Assert.IsNotEmpty(issue.Title, $"{where}: no title");
        Assert.IsNotEmpty(issue.Explanation, $"{where}: no explanation");
        Assert.AreNotEqual("Нужна запятая.", issue.Explanation, $"{where}: generic explanation");
        Assert.IsGreaterThan(0.0, issue.Confidence, $"{where}: zero confidence");
        Assert.IsLessThanOrEqualTo(1.0, issue.Confidence, $"{where}: confidence above 1");
        Assert.IsTrue(Enum.IsDefined(issue.Category), $"{where}: category is not a defined value");

        if (issue.Replacement is null)
        {
            // Detection without a replacement is legitimate — "this word is unknown" — but
            // it must not also claim it can be applied.
            Assert.IsFalse(issue.CanApplyAutomatically, $"{where}: auto-apply with nothing to apply");
            return;
        }

        Assert.AreNotEqual(issue.Original, issue.Replacement, $"{where}: replacement equals original");

        // A zero-length span is only a correction when it inserts something.
        if (issue.Length == 0)
        {
            Assert.IsNotEmpty(issue.Replacement, $"{where}: empty insertion");
        }
    }
}
