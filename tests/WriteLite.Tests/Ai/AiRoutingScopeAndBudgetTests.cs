using WriteLite.AI.Local;
using WriteLite.Models;
using WriteLite.Services.Ai;

namespace WriteLite.Tests.Ai;

/// <summary>
/// Two defects that only appear once the input is longer than one sentence.
/// </summary>
/// <remarks>
/// <para>Both were invisible on the path the AI layer was originally built for. The
/// system-wide field monitor reads one text box at a time, so the text handed to the routing
/// policy was a sentence and the text handed to the model was a sentence. The editor analyses
/// whole paragraphs, and at that size the policy stopped consulting the model and the model's
/// answer stopped fitting in its reply budget — neither with any symptom beyond a document
/// that came back clean.</para>
/// </remarks>
[TestClass]
public sealed class AiRoutingScopeAndBudgetTests
{
    private static TextIssue Resolved(int start, string original, IssueCategory category)
        => new(start, original.Length, original, "исправлено", "t", "e", category,
            IssueSeverity.Warning, false, "ru.test.resolved", LinguisticIssueCategory.Typo, 0.95);

    // ── Routing is per sentence, not per document ───────────────────────────

    /// <summary>
    /// One resolved finding must not silence the model for the rest of the document.
    /// </summary>
    /// <remarks>
    /// The "nothing found here, but this looks risky" branch is the only one that can add
    /// detection rather than second-guess it. While it was gated on the whole input having no
    /// findings, a single confident correction in the opening sentence turned it off for every
    /// other sentence in the document — including the ones no rule had an opinion about, which
    /// are precisely the ones it exists to reach.
    /// </remarks>
    [TestMethod]
    public void ADocumentWhereOnlyOneSentenceIsCovered_StillRoutesToAi()
    {
        const string document =
            "Согласно нового плана мы закончим работу. "
            + "Когда я пришел домой я увидел что дверь была открыта и свет горел";

        // The finding sits in the first sentence and is fully resolved, so nothing about it
        // asks for the model. The second sentence has no finding at all.
        var deterministic = new[] { Resolved(document.IndexOf("нового", StringComparison.Ordinal), "нового", IssueCategory.Grammar) };

        var decision = new LocalAiRoutingPolicy().ShouldConsult(document, deterministic);

        Assert.IsTrue(decision.Consult, $"not routed: {decision.Reason}");
        Assert.AreEqual("contextual-risk-markers", decision.Reason);
    }

    /// <summary>The gate is still a gate: covered or unremarkable sentences are not routed.</summary>
    [TestMethod]
    public void ADocumentWhoseRiskySentenceIsAlreadyCovered_DoesNotRouteToAi()
    {
        const string document =
            "Наша компания открыла новый офис в центре города. "
            + "Когда я пришел домой я увидел что дверь была открыта и свет горел";

        // This time the finding lands inside the risky sentence, so that sentence is covered
        // and the clean one carries no markers.
        var deterministic = new[]
        {
            Resolved(document.IndexOf("дверь", StringComparison.Ordinal), "дверь", IssueCategory.Punctuation)
                with { Confidence = 0.95, Replacement = "дверь," }
        };

        var decision = new LocalAiRoutingPolicy().ShouldConsult(document, deterministic);

        Assert.IsFalse(decision.Consult, $"routed anyway: {decision.Reason}");
        Assert.AreEqual("no-contextual-uncertainty", decision.Reason);
    }

    [TestMethod]
    public void ADocumentOfCleanSentences_DoesNotRouteToAi()
    {
        const string document =
            "Наша компания открыла новый офис в центре города. "
            + "Мы пригласили партнёров на открытие. "
            + "Встреча прошла в дружеской обстановке.";

        Assert.IsFalse(new LocalAiRoutingPolicy().ShouldConsult(document, []).Consult);
    }

    // ── The reply budget has to fit the reply ───────────────────────────────

    /// <summary>
    /// The model returns the whole corrected text, so its budget cannot be a constant.
    /// </summary>
    /// <remarks>
    /// At a fixed 96 tokens the server stopped mid-string on anything longer than a short
    /// sentence, the truncated JSON did not parse, and the entire answer was dropped as a null
    /// response — reported as "model consulted" and "no findings" rather than as a failure.
    /// Measured against the shipped server: a 327-character document produced exactly 96
    /// completion tokens and unparseable output; the same document with room to answer
    /// produced 143 and valid JSON.
    /// </remarks>
    [TestMethod]
    public async Task CompletionBudget_GrowsWithTheTextItMustReturn()
    {
        await using var backend = new QwenModelBackend(
            Path.Combine(AppContext.BaseDirectory, "models", "writelight-qwen"));

        var shortField = backend.CompletionBudgetFor("Я сегодня небыл дома.");
        var paragraph = backend.CompletionBudgetFor(new string('я', 400));

        Assert.AreEqual(backend.MaxNewTokens, shortField, "A short field must keep the budget it always had.");
        Assert.IsGreaterThan(shortField, paragraph, "A paragraph was given no more room to answer than a sentence.");
    }

    /// <summary>The budget is bounded, so prompt plus answer stays inside the server context.</summary>
    [TestMethod]
    public async Task CompletionBudget_IsCappedForTheServerContextWindow()
    {
        await using var backend = new QwenModelBackend(
            Path.Combine(AppContext.BaseDirectory, "models", "writelight-qwen"));

        Assert.AreEqual(backend.MaxCompletionTokens, backend.CompletionBudgetFor(new string('я', 10_000)));

        // The largest input the interactive window can produce must still leave the shipped
        // 768-token context room for the prompt in front of it.
        var largest = backend.CompletionBudgetFor(new string('я', 480));
        Assert.IsLessThan(QwenModelBackend.DefaultServerContextTokens / 2, largest);
    }
}
