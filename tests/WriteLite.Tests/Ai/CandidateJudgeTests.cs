using WriteLite.AI.Contracts;
using WriteLite.AI.Local;

namespace WriteLite.Tests.Ai;

/// <summary>
/// The candidate-judge contract: one id from a closed list, or nothing.
/// </summary>
/// <remarks>
/// The rejection cases carry the weight here. A judge that quietly accepts a rewritten
/// sentence, or repairs a malformed answer into a plausible one, is a generation path
/// wearing a classifier's name — and Phase 4 measured what the generation path costs
/// (0.115 precision, every false positive an invented token). Each rejection below is a way
/// the model could smuggle generation back in.
/// </remarks>
[TestClass]
public sealed class CandidateJudgeTests
{
    private static readonly IReadOnlyList<JudgeCandidate> Offered =
    [
        new("A", "одел"),
        new("B", "надел"),
        new(CandidateJudge.NoChangeId, string.Empty),
    ];

    // ── Accepted answers ────────────────────────────────────────────────────────

    [TestMethod]
    public void ASingleOfferedId_IsAccepted()
    {
        var verdict = CandidateJudge.Parse("""{"candidateId":"B"}""", Offered);

        Assert.AreEqual(CandidateJudgeOutcome.Selected, verdict.Outcome);
        Assert.AreEqual("B", verdict.CandidateId);
        Assert.AreEqual("надел", verdict.Text);
    }

    [TestMethod]
    public void NoChange_IsAFirstClassAnswer()
    {
        var verdict = CandidateJudge.Parse("""{"candidateId":"NO_CHANGE"}""", Offered);

        Assert.AreEqual(CandidateJudgeOutcome.NoChange, verdict.Outcome);
        Assert.AreEqual(string.Empty, verdict.Text);
        Assert.IsNull(verdict.RejectionReason);
    }

    [TestMethod]
    public void ConfidenceIsCarriedWhenOffered_AndClamped()
    {
        Assert.AreEqual(0.75, CandidateJudge.Parse("""{"candidateId":"A","confidence":0.75}""", Offered).Confidence);
        Assert.AreEqual(1.0, CandidateJudge.Parse("""{"candidateId":"A","confidence":4}""", Offered).Confidence);
        Assert.IsNull(CandidateJudge.Parse("""{"candidateId":"A"}""", Offered).Confidence);
    }

    [TestMethod]
    public void AFencedOrPaddedObject_IsStillRead()
    {
        foreach (var answer in new[]
        {
            "```json\n{\"candidateId\":\"B\"}\n```",
            "  {\"candidateId\":\"B\"}  ",
            "Ответ: {\"candidateId\":\"B\"}",
        })
        {
            Assert.AreEqual(CandidateJudgeOutcome.Selected, CandidateJudge.Parse(answer, Offered).Outcome, answer);
        }
    }

    // ── Rejected answers (§21) ──────────────────────────────────────────────────

    [TestMethod]
    [DataRow("""{"candidateId":"Z"}""", "unknown-candidate")]
    [DataRow("""{"candidateId":"надел"}""", "unknown-candidate")]
    [DataRow("""{"candidateId":["A","B"]}""", "multiple-candidates")]
    [DataRow("""{"candidateId":2}""", "candidateId-not-a-string")]
    [DataRow("""{"candidateId":""}""", "empty-candidateId")]
    [DataRow("""{"replacement":"надел"}""", "missing-candidateId")]
    [DataRow("""{"candidateId":}""", "invalid-json")]
    [DataRow("""{"candidateId":"B" """, "not-json")]
    [DataRow("Я надел куртку.", "not-json")]
    [DataRow("B", "not-json")]
    [DataRow("", "not-json")]
    public void MalformedOrInventedAnswers_AreRejectedWithAReason(string answer, string reason)
    {
        var verdict = CandidateJudge.Parse(answer, Offered);

        Assert.AreEqual(CandidateJudgeOutcome.Rejected, verdict.Outcome);
        Assert.AreEqual(reason, verdict.RejectionReason);
        Assert.IsNull(verdict.Text);
    }

    [TestMethod]
    public void AnInventedReplacement_CannotReachTheCaller()
    {
        // The model answering with a token nobody offered is the §24 case. It must not be
        // accepted under any shape.
        foreach (var answer in new[]
        {
            """{"candidateId":"C","text":"надевал"}""",
            """{"candidateId":"A","replacement":"надевал"}""",
        })
        {
            var verdict = CandidateJudge.Parse(answer, Offered);
            Assert.AreNotEqual("надевал", verdict.Text, answer);
        }
    }

    // ── Candidate construction (§23) ────────────────────────────────────────────

    [TestMethod]
    public void NoChangeIsAlwaysOffered_EvenWithNoCandidates()
    {
        var options = CandidateJudge.BuildCandidates("одел", []);

        Assert.HasCount(1, options);
        Assert.AreEqual(CandidateJudge.NoChangeId, options[0].Id);
    }

    [TestMethod]
    public void TheTargetIsNeverOfferedAsAReplacement()
    {
        // Offering "одел" both as A and implicitly as NO_CHANGE splits the vote for leaving
        // the text alone between two ids.
        var options = CandidateJudge.BuildCandidates("одел", ["одел", "надел", "надел"]);

        Assert.HasCount(2, options);
        Assert.AreEqual("надел", options[0].Text);
        Assert.AreEqual(CandidateJudge.NoChangeId, options[1].Id);
    }

    [TestMethod]
    public void IdsAreAssignedInOrderFromA()
    {
        var options = CandidateJudge.BuildCandidates("х", ["один", "два", "три"]);

        CollectionAssert.AreEqual(
            new[] { "A", "B", "C", CandidateJudge.NoChangeId },
            options.Select(o => o.Id).ToArray());
    }

    [TestMethod]
    public void TheCandidateListIsBounded()
    {
        var many = Enumerable.Range(0, 40).Select(i => $"вариант{i}");
        var options = CandidateJudge.BuildCandidates("х", many);

        Assert.IsLessThanOrEqualTo(9, options.Count);
        Assert.AreEqual(CandidateJudge.NoChangeId, options[^1].Id);
    }

    // ── The prompt shows a menu, never a blank ──────────────────────────────────

    [TestMethod]
    public void ThePromptOffersEveryCandidateAndNoChange()
    {
        var request = new CandidateJudgeRequest(
            "Я одел куртку.",
            2,
            4,
            CandidateJudge.BuildCandidates("одел", ["надел"]));

        var prompt = CandidateJudge.BuildPrompt(request);

        StringAssert.Contains(prompt, "Я одел куртку.");
        StringAssert.Contains(prompt, "одел");
        StringAssert.Contains(prompt, "A: надел");
        StringAssert.Contains(prompt, "NO_CHANGE");
    }

    [TestMethod]
    public void TheTargetIsResolvedFromTheSentenceSpan()
    {
        var request = new CandidateJudgeRequest("Я одел куртку.", 2, 4, Offered);
        Assert.AreEqual("одел", request.Target);
    }
}
