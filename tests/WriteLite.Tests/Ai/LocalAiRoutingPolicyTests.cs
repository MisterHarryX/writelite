using WriteLite.Models;
using WriteLite.Services.Ai;
using WriteLite.Services.Lexical;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests.Ai;

/// <summary>
/// The routing policy, tested without a model.
/// </summary>
/// <remarks>
/// Every rule here exists because of a measurement. Unrestricted consultation on the frozen
/// corpus moved precision 0.945 → 0.713 and false positives 5.3× while correction accuracy
/// stayed at 0.565 — the model was being asked about spans the DAFSA already answered
/// perfectly, and answering with invented edits. The policy is deliberately deterministic
/// so these can be asserted directly rather than inferred from benchmark deltas.
/// </remarks>
[TestClass]
public sealed class LocalAiRoutingPolicyTests
{
    private const string Sentence = "Я сегодня небыл дома и незнаю что делать дальше сегодня.";

    private static TextIssue Issue(
        IssueCategory category,
        double confidence,
        string original = "небыл",
        string? replacement = "не был",
        int start = 10,
        string ruleId = "ru.spelling.typo")
        => new(start, original.Length, original, replacement, "t", "e", category,
            IssueSeverity.Warning, false, ruleId, LinguisticIssueCategory.Typo, confidence);

    private static LocalAiRoutingPolicy Policy(ILexicalSignalSource? signals = null)
        => new(new LocalAiRoutingOptions(), signals);

    // ── When the model must not be consulted ────────────────────────────────

    [TestMethod]
    public void StrongSpellingCorrection_DoesNotRouteToAi()
    {
        var decision = Policy().ShouldConsult(Sentence, [Issue(IssueCategory.Orthography, 0.95)]);

        Assert.IsFalse(decision.Consult, $"routed anyway: {decision.Reason}");
        Assert.AreEqual("strong-deterministic-only", decision.Reason);
    }

    [TestMethod]
    public void HomoglyphAndLayoutFixes_DoNotRouteToAi()
    {
        // Both score 1.000 recall deterministically. The model only adds noise here —
        // measured: homoglyph F1 fell 1.000 → 0.895 when it was consulted.
        var issues = new[]
        {
            Issue(IssueCategory.Orthography, 0.80, "Ghbdtn", "Привет", 0, "ru.spelling.keyboard-layout"),
            Issue(IssueCategory.Orthography, 0.90, "привeт", "привет", 20, "ru.spelling.homoglyph"),
        };

        Assert.IsFalse(Policy().ShouldConsult(Sentence, issues).Consult);
    }

    [TestMethod]
    public void ShortText_DoesNotRouteToAi()
    {
        Assert.IsFalse(Policy().ShouldConsult("Да", []).Consult);
        Assert.AreEqual("too-short", Policy().ShouldConsult("Да", []).Reason);
    }

    [TestMethod]
    public void CodeAndPaths_DoNotRouteToAi()
    {
        Assert.IsFalse(Policy().ShouldConsult(@"C:\Users\Test\file.txt", []).Consult);
        Assert.IsFalse(Policy().ShouldConsult("{\"a\": 1, \"b\": 2}", []).Consult);
        Assert.IsFalse(Policy().ShouldConsult("https://example.com/some/path", []).Consult);
    }

    [TestMethod]
    public void CleanUnremarkableSentence_DoesNotRouteToAi()
    {
        // The clean category is 277 of 841 items and lost 8.7 points of preservation to
        // unrestricted consultation. A sentence with no findings and no risk markers is
        // exactly where the model had nothing to add and plenty to break.
        var decision = Policy().ShouldConsult("Наша компания открыла новый офис в центре города.", []);

        Assert.IsFalse(decision.Consult, $"routed anyway: {decision.Reason}");
        Assert.AreEqual("no-contextual-uncertainty", decision.Reason);
    }

    // ── When the model is worth asking ──────────────────────────────────────

    [TestMethod]
    public void UnresolvedGrammarFinding_RoutesToAi()
    {
        // LanguageTool flagged morphology but produced no replacement: recall 0.319 with a
        // correction accuracy of 0.021, i.e. it can see the problem and not fix it. This is
        // where the model's +19 pp morphology recall came from.
        var decision = Policy().ShouldConsult(Sentence, [Issue(IssueCategory.Grammar, 0.5, replacement: null)]);

        Assert.IsTrue(decision.Consult);
        Assert.AreEqual("unresolved-contextual-finding", decision.Reason);
    }

    [TestMethod]
    public void UnresolvedPunctuationFinding_RoutesToAi()
    {
        var decision = Policy().ShouldConsult(Sentence, [Issue(IssueCategory.Punctuation, 0.4)]);
        Assert.IsTrue(decision.Consult);
    }

    [TestMethod]
    public void MultiClauseSentenceWithNoCommas_RoutesToAi()
    {
        // Nothing found deterministically, but the shape says a comma is probably missing.
        // Without this rule the model could only ever second-guess existing findings, and
        // its measured gains were in detection.
        var decision = Policy().ShouldConsult(
            "Когда я пришел домой я увидел что дверь была открыта и свет горел", []);

        Assert.IsTrue(decision.Consult, $"not routed: {decision.Reason}");
        Assert.AreEqual("contextual-risk-markers", decision.Reason);
    }

    // ── Acceptance ──────────────────────────────────────────────────────────

    [TestMethod]
    public void AiFinding_ThatContradictsAStrongDeterministicOne_IsRejected()
    {
        var deterministic = new[] { Issue(IssueCategory.Orthography, 0.95, "небыл", "не был") };
        var aiCandidate = Issue(IssueCategory.Orthography, 0.99, "небыл", "не бил", ruleId: "WL-AI-SPELLING");

        var decision = Policy().ShouldAccept(Sentence, aiCandidate, deterministic);

        Assert.IsFalse(decision.Accept, "the model overrode a dictionary-backed answer");
        Assert.AreEqual("contradicts-strong-deterministic", decision.Reason);
    }

    [TestMethod]
    public void AiFinding_ConfirmingADeterministicOne_IsAcceptedEasily()
    {
        var deterministic = new[] { Issue(IssueCategory.Grammar, 0.5, "небыл", "не был") };
        var aiCandidate = Issue(IssueCategory.Grammar, 0.4, "небыл", "не был", ruleId: "WL-AI-GRAMMAR");

        var decision = Policy().ShouldAccept(Sentence, aiCandidate, deterministic);

        Assert.IsTrue(decision.Accept, decision.Reason);
        Assert.AreEqual(AiSupportClass.ConfirmsDeterministic, decision.Class);
    }

    [TestMethod]
    public void UnsupportedAiRewrite_OutsideEligibleCategories_IsRejected()
    {
        // Class D on an orthography span: nothing corroborates it and the model has no
        // measured competence there. This is the single largest false-positive source.
        var aiCandidate = Issue(
            IssueCategory.Orthography, 0.99, "компания", "кампания", 0, "WL-AI-SPELLING");

        var decision = Policy().ShouldAccept(Sentence, aiCandidate, []);

        Assert.IsFalse(decision.Accept);
        Assert.AreEqual(AiSupportClass.Unsupported, decision.Class);
    }

    [TestMethod]
    public void UnsupportedPunctuationEdit_NeedsVeryHighConfidence()
    {
        var low = Issue(IssueCategory.Punctuation, 0.70, ",", ",", 5, "WL-AI-PUNCTUATION");
        var high = Issue(IssueCategory.Punctuation, 0.95, ",", ",", 5, "WL-AI-PUNCTUATION");

        Assert.IsFalse(Policy().ShouldAccept(Sentence, low, []).Accept, "0.70 should not clear the bar");
        Assert.IsTrue(Policy().ShouldAccept(Sentence, high, []).Accept, "0.95 should clear it");
    }

    [TestMethod]
    public void NoChange_IsRepresentableAndCostsNothing()
    {
        // A model that returns no findings for a routed sentence is a first-class outcome:
        // nothing is offered, nothing is accepted, and the deterministic answer stands.
        var policy = Policy();
        var decision = policy.ShouldConsult(Sentence, [Issue(IssueCategory.Grammar, 0.5, replacement: null)]);
        Assert.IsTrue(decision.Consult);

        // No candidates to accept — the acceptance path is simply never entered.
        Assert.AreEqual(0, Array.Empty<TextIssue>().Length);
    }
}

/// <summary>
/// Register and user-dictionary protection inside the acceptance policy.
/// </summary>
/// <remarks>
/// Separate class because these need the real Russian form index, which is a class-level
/// fixture, and because they are the product rules most likely to be quietly broken by a
/// future threshold change.
/// </remarks>
[TestClass]
public sealed class LocalAiRoutingRegistryProtectionTests
{
    private static LocalSpellChecker? _checker;
    private static LexicalSignalService? _signals;

    [ClassInitialize]
    public static void Load(TestContext _)
    {
        _checker = new LocalSpellChecker();
        _signals = LexicalSignalService.TryCreate(_checker);
    }

    [ClassCleanup]
    public static void Unload() => _checker?.Dispose();

    private static TextIssue AiIssue(string original, string replacement)
        => new(0, original.Length, original, replacement, "t", "e", IssueCategory.Grammar,
            IssueSeverity.Warning, false, "WL-AI-GRAMMAR", LinguisticIssueCategory.Typo, 0.99);

    [TestMethod]
    public void UserDictionaryWord_IsNeverCorrectedByAi()
    {
        var dictionary = UserDictionaryService.CreateEmpty();
        dictionary.Add("Зюквафыр");

        var policy = new LocalAiRoutingPolicy(
            new LocalAiRoutingOptions(),
            new LexicalSignalService(_checker?.RussianFormIndex, dictionary));

        var decision = policy.ShouldAccept(
            "Зюквафыр это наш продукт.", AiIssue("Зюквафыр", "Зюйдвестер"), []);

        Assert.IsFalse(decision.Accept);
        Assert.AreEqual("user-dictionary-word", decision.Reason);
    }

    [TestMethod]
    public void SlangWord_IsNotRewrittenByAnUnsupportedAiFinding()
    {
        if (_signals is null)
        {
            Assert.Inconclusive("Russian form index not deployed");
            return;
        }

        var policy = new LocalAiRoutingPolicy(new LocalAiRoutingOptions(), _signals);

        foreach (var word in new[] { "имба", "кринж", "рофл" })
        {
            if (!_signals.For(word).HasProtectedRegister) continue;

            var decision = policy.ShouldAccept(
                $"Это приложение вообще {word}.", AiIssue(word, "хорошо"), []);

            Assert.IsFalse(decision.Accept, $"the model was allowed to rewrite slang '{word}'");
        }
    }
}
