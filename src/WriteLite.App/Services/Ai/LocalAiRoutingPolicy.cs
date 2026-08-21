using WriteLite.Models;
using WriteLite.Language.Russian;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Lexical;

namespace WriteLite.Services.Ai;

/// <summary>
/// How much deterministic backing an AI finding has.
/// </summary>
/// <remarks>
/// The classes are ordered by how much independent evidence exists for the claim, and each
/// gets a different acceptance bar. Phase 3 measured what happens with no such distinction:
/// precision fell from 0.945 to 0.713 and false positives rose 5.3× while correction
/// accuracy did not move at all, because unsupported model inventions were merged as peers
/// with dictionary-backed findings.
/// </remarks>
public enum AiSupportClass
{
    /// <summary>The model flagged a span a deterministic layer had already flagged, and agrees on the fix.</summary>
    ConfirmsDeterministic = 0,

    /// <summary>Same span, different choice among candidates the deterministic layer generated.</summary>
    RerankExisting = 1,

    /// <summary>New span, but morphology, register or rules corroborate it.</summary>
    RuleSupported = 2,

    /// <summary>New span with nothing corroborating it. The highest-risk class.</summary>
    Unsupported = 3,
}

/// <param name="Consult">Whether to spend an inference on this sentence.</param>
/// <param name="Reason">Machine-stable reason, for the decision trace and for tests.</param>
public sealed record AiRoutingDecision(bool Consult, string Reason)
{
    public static AiRoutingDecision No(string reason) => new(false, reason);
    public static AiRoutingDecision Yes(string reason) => new(true, reason);
}

/// <param name="Accept">Whether this finding survives.</param>
/// <param name="Class">How well corroborated it was.</param>
/// <param name="RequiredConfidence">The bar it had to clear.</param>
public sealed record AiAcceptanceDecision(
    bool Accept,
    string Reason,
    AiSupportClass Class,
    double RequiredConfidence);

/// <summary>Tunable thresholds. Defaults are justified in <see cref="LocalAiRoutingPolicy"/>.</summary>
public sealed class LocalAiRoutingOptions
{
    /// <summary>Deterministic confidence at or above which the AI is not consulted about a span.</summary>
    public double StrongDeterministicConfidence { get; set; } = 0.75;

    /// <summary>Acceptance bar for a finding that merely confirms a deterministic one.</summary>
    public double ConfirmThreshold { get; set; } = 0.30;

    /// <summary>Acceptance bar for choosing among existing candidates.</summary>
    public double RerankThreshold { get; set; } = 0.45;

    /// <summary>Acceptance bar for a new span with corroboration.</summary>
    public double RuleSupportedThreshold { get; set; } = 0.60;

    /// <summary>Acceptance bar for a new span with nothing behind it.</summary>
    public double UnsupportedThreshold { get; set; } = 0.90;

    /// <summary>Categories where the model earned its keep, measured on the frozen corpus.</summary>
    public HashSet<IssueCategory> AiEligibleCategories { get; } =
    [
        IssueCategory.Grammar,
        IssueCategory.Punctuation,
    ];

    /// <summary>Shortest text worth an inference.</summary>
    public int MinimumChars { get; set; } = 12;
}

/// <summary>Decides when to consult the local model, and when to believe it.</summary>
public interface ILocalAiRoutingPolicy
{
    AiRoutingDecision ShouldConsult(string text, IReadOnlyList<TextIssue> deterministic);

    AiAcceptanceDecision ShouldAccept(
        string text,
        TextIssue candidate,
        IReadOnlyList<TextIssue> deterministic);
}

/// <summary>
/// The measured routing policy: consult the model where it demonstrably helps, ignore it
/// where the deterministic stack is already strong, and require corroboration before
/// believing anything it invents.
/// </summary>
/// <remarks>
/// <para>
/// Every rule here comes from the Phase 3 full-corpus measurement, not from intuition.
/// Unrestricted consultation produced:
/// </para>
/// <list type="bullet">
/// <item>morphology recall 0.319 → 0.511 and punctuation 0.273 → 0.394 — real, and
/// unavailable from rules or the lexicon;</item>
/// <item>precision 0.945 → 0.713, false positives 5.3×, no-change 0.963 → 0.865;</item>
/// <item>correction accuracy unchanged at 0.565 — more spans flagged, not one more
/// corrected properly;</item>
/// <item>F1 falling on every category the deterministic layer already handles well
/// (typo_simple 0.986 → 0.944, homoglyph 1.000 → 0.895) with recall unchanged, i.e. pure
/// added noise.</item>
/// </list>
/// <para>
/// Those two groups are disjoint, which is the whole opportunity: the gains and the losses
/// happen on different categories, so asking the model less often should keep most of the
/// former and remove most of the latter.
/// </para>
/// <para>
/// The policy is deliberately deterministic and side-effect free so it can be tested
/// without a model, and every decision returns a reason string so a routing trace can
/// explain itself.
/// </para>
/// </remarks>
public sealed class LocalAiRoutingPolicy : ILocalAiRoutingPolicy
{
    private readonly LocalAiRoutingOptions _options;
    private readonly ILexicalSignalSource? _signals;
    private readonly MorphologicalAcceptanceGuard? _morphologyGuard;

    /// <param name="morphologyGuard">
    /// Reads the corrected sentence before the correction is shown. Optional: without it the
    /// other acceptance bars are unchanged, so a deployment with no form index is no less safe
    /// than it was, only less able to catch a fluent-but-ungrammatical rewrite.
    /// </param>
    public LocalAiRoutingPolicy(
        LocalAiRoutingOptions? options = null,
        ILexicalSignalSource? lexicalSignals = null,
        MorphologicalAcceptanceGuard? morphologyGuard = null)
    {
        _options = options ?? new LocalAiRoutingOptions();
        _signals = lexicalSignals;
        _morphologyGuard = morphologyGuard;
    }

    public LocalAiRoutingOptions Options => _options;

    // ── Should we spend an inference at all? ────────────────────────────────

    public AiRoutingDecision ShouldConsult(string text, IReadOnlyList<TextIssue> deterministic)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < _options.MinimumChars)
        {
            return AiRoutingDecision.No("too-short");
        }

        // Structured content is not prose and the model has no business rewriting it.
        if (AI.Local.AiCallRouter.IsProtectedOnly(text) || AI.Local.AiCallRouter.LooksLikeCodeOrJson(text))
        {
            return AiRoutingDecision.No("protected-or-code");
        }

        if (!text.Any(char.IsLetter))
        {
            return AiRoutingDecision.No("no-letters");
        }

        // The strong-category bypass. When everything the deterministic stack found is a
        // high-confidence typo, layout or homoglyph fix, the model measurably cannot
        // improve the answer and measurably does add noise, so it is not asked.
        if (deterministic.Count > 0 && deterministic.All(IsStrongDeterministicFinding))
        {
            return AiRoutingDecision.No("strong-deterministic-only");
        }

        // A deterministic layer suspects grammar or punctuation but could not settle it.
        // This is where the model's measured gains live.
        if (deterministic.Any(IsUnresolvedContextualFinding))
        {
            return AiRoutingDecision.Yes("unresolved-contextual-finding");
        }

        // A sentence with nothing found, but with the shape of one that hides a punctuation
        // or agreement problem. Without this the model could never add detection, only
        // second-guess what was already found — and detection is where its gains were.
        if (HasUncoveredRiskySentence(text, deterministic))
        {
            return AiRoutingDecision.Yes("contextual-risk-markers");
        }

        return AiRoutingDecision.No("no-contextual-uncertainty");
    }

    /// <summary>
    /// True when some sentence has no deterministic finding of its own and carries the
    /// markers of a hidden punctuation or agreement problem.
    /// </summary>
    /// <remarks>
    /// <para><b>Per sentence, because that is the unit the policy was measured in.</b> This
    /// test used to read <c>deterministic.Count == 0</c> over the entire text handed in. That
    /// is correct for the one-sentence inputs the field monitor produces and wrong for a
    /// document: the editor analyses whole paragraphs, so a single confident finding anywhere
    /// — one dative-government fix in the opening line — made the whole document "already
    /// covered" and the model was never consulted about any of the other sentences. The
    /// errors that only the model can find are exactly the ones no rule fired on, so the
    /// branch that exists to reach them was switched off by the success of an unrelated
    /// rule three sentences away.</para>
    ///
    /// <para>The gate itself is unchanged and just as narrow: a sentence still has to have
    /// no deterministic finding <em>and</em> to carry risk markers. Nothing about which
    /// findings are believed changes here — that is <see cref="ShouldAccept"/>, and it still
    /// applies <see cref="SemanticEditGuard"/>, the register protections and the per-class
    /// confidence bars to everything that comes back.</para>
    /// </remarks>
    private bool HasUncoveredRiskySentence(string text, IReadOnlyList<TextIssue> deterministic)
    {
        foreach (var (start, length) in SentenceWindowBuilder.Split(text))
        {
            if (length < _options.MinimumChars)
            {
                continue;
            }

            var end = start + length;
            var covered = deterministic.Any(
                finding => finding.Start < end && start < finding.Start + Math.Max(finding.Length, 1));
            if (covered)
            {
                continue;
            }

            if (AI.Local.AiCallRouter.NeedsContextModel(text.Substring(start, length)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A finding the deterministic stack is confident about, in a category it owns.
    /// </summary>
    /// <remarks>
    /// Orthography with a concrete replacement above the confidence floor: the DAFSA, the
    /// keyboard-layout mapping and the homoglyph normaliser all land here, and all score
    /// 0.97–1.00 recall on the frozen corpus without the model.
    /// </remarks>
    private bool IsStrongDeterministicFinding(TextIssue issue)
        => issue.Category == IssueCategory.Orthography
           && !string.IsNullOrEmpty(issue.Replacement)
           && issue.Confidence >= _options.StrongDeterministicConfidence;

    /// <summary>
    /// A grammar or punctuation finding the deterministic stack could not resolve: no
    /// replacement, or low confidence in the one it has.
    /// </summary>
    private bool IsUnresolvedContextualFinding(TextIssue issue)
        => _options.AiEligibleCategories.Contains(issue.Category)
           && (string.IsNullOrEmpty(issue.Replacement)
               || issue.Confidence < _options.StrongDeterministicConfidence);

    // ── Should we believe what came back? ───────────────────────────────────

    public AiAcceptanceDecision ShouldAccept(
        string text,
        TextIssue candidate,
        IReadOnlyList<TextIssue> deterministic)
    {
        var supportClass = Classify(candidate, deterministic);
        var required = RequiredConfidenceFor(supportClass, candidate.Category);

        // Meaning first, before any confidence arithmetic. A model can always claim 0.99,
        // so a confidence floor cannot on its own distinguish a spelling fix from turning
        // 15% into 50%. Applied to every class: even a corroborated edit has no business
        // changing a number, a negation or an identifier.
        if (SemanticEditGuard.Reject(candidate.Original, candidate.Replacement) is { } semanticReason)
        {
            return new AiAcceptanceDecision(false, semanticReason, supportClass, required);
        }

        // Confidence cannot make a finite verb -> infinitive rewrite safe, and an
        // unsupported change to another known lemma is paraphrasing, not minimal grammar
        // correction. An exact deterministic confirmation remains authoritative.
        if (supportClass != AiSupportClass.ConfirmsDeterministic
            && RejectMorphologicallyUnsafeEdit(candidate) is { } morphologyReason)
        {
            return new AiAcceptanceDecision(false, morphologyReason, supportClass, required);
        }

        // §29: read the sentence the correction would leave behind. The check above is about
        // the edit — this one is about the result, which is where «более лучшее решение» →
        // «лучше решение» becomes visible. Applied to every support class, because a
        // deterministic layer flagging the same span says nothing about whether the model's
        // replacement produces a grammatical phrase.
        if (_morphologyGuard?.Reject(text, candidate) is { } acceptanceReason)
        {
            return new AiAcceptanceDecision(false, acceptanceReason, supportClass, required);
        }

        // A span the deterministic stack owns with high confidence is not up for debate.
        // This is the §10 deterministic-authority rule, and it is checked before anything
        // else because no amount of model confidence should be able to override a
        // dictionary-and-morphology-backed answer.
        var contested = deterministic.FirstOrDefault(d => Overlaps(d, candidate));
        if (contested is not null && IsStrongDeterministicFinding(contested)
            && !string.Equals(contested.Replacement, candidate.Replacement, StringComparison.Ordinal))
        {
            return new AiAcceptanceDecision(
                false, "contradicts-strong-deterministic", supportClass, required);
        }

        // Register protection: slang, obscenity, names, abbreviations and borrowings are
        // recognised vocabulary. The model does not get to "fix" them, whatever it thinks.
        if (_signals is not null && LooksLikeSingleWord(candidate.Original))
        {
            var signals = _signals.For(candidate.Original.Trim());
            if (signals.IsUserWord)
            {
                return new AiAcceptanceDecision(false, "user-dictionary-word", supportClass, required);
            }

            if (signals.IsObscene)
            {
                return new AiAcceptanceDecision(false, "obscene-never-sanitised", supportClass, required);
            }

            if (signals.HasProtectedRegister && supportClass == AiSupportClass.Unsupported)
            {
                return new AiAcceptanceDecision(false, "protected-register", supportClass, required);
            }
        }

        // A finding whose category the model has not earned. Grammar and punctuation are
        // where it measurably helps; orthography is where it measurably hurts.
        if (supportClass == AiSupportClass.Unsupported
            && !_options.AiEligibleCategories.Contains(candidate.Category))
        {
            return new AiAcceptanceDecision(false, "unsupported-outside-eligible-category", supportClass, required);
        }

        if (candidate.Confidence < required)
        {
            return new AiAcceptanceDecision(false, "below-confidence-floor", supportClass, required);
        }

        return new AiAcceptanceDecision(true, "accepted", supportClass, required);
    }

    private string? RejectMorphologicallyUnsafeEdit(TextIssue candidate)
    {
        if (_signals is null
            || !LooksLikeSingleWord(candidate.Original)
            || !LooksLikeSingleWord(candidate.Replacement))
        {
            return null;
        }

        var original = _signals.For(candidate.Original.Trim());
        var replacement = _signals.For(candidate.Replacement!.Trim());
        if (!original.IsKnown || !replacement.IsKnown)
        {
            return null;
        }

        if (original.PartOfSpeech == RussianPartOfSpeech.Verb
            && replacement.PartOfSpeech == RussianPartOfSpeech.Infinitive)
        {
            return "finite-verb-to-infinitive";
        }

        if (original.PartOfSpeech == RussianPartOfSpeech.Comparative
            && replacement.PartOfSpeech is RussianPartOfSpeech.AdjectiveFull
                or RussianPartOfSpeech.AdjectiveShort)
        {
            return "comparative-form-corruption";
        }

        if (candidate.Category == IssueCategory.Grammar
            && original.Lemma.Length > 0
            && replacement.Lemma.Length > 0
            && !string.Equals(original.Lemma, replacement.Lemma, StringComparison.OrdinalIgnoreCase))
        {
            return "unsupported-lemma-rewrite";
        }

        return null;
    }

    private static bool Overlaps(TextIssue a, TextIssue b)
        => a.Start < b.Start + b.Length && b.Start < a.Start + a.Length;

    private static bool LooksLikeSingleWord(string? value)
    {
        var trimmed = (value ?? "").Trim();
        return trimmed.Length > 0 && trimmed.All(char.IsLetter);
    }

    private AiSupportClass Classify(TextIssue candidate, IReadOnlyList<TextIssue> deterministic)
    {
        var sameSpan = deterministic.FirstOrDefault(d => Overlaps(d, candidate));
        if (sameSpan is not null)
        {
            return string.Equals(sameSpan.Replacement, candidate.Replacement, StringComparison.Ordinal)
                ? AiSupportClass.ConfirmsDeterministic
                : AiSupportClass.RerankExisting;
        }

        // No deterministic finding here, but the lexicon can still corroborate: a
        // replacement that is a real Russian word for a token that is not.
        if (_signals is not null
            && LooksLikeSingleWord(candidate.Original)
            && LooksLikeSingleWord(candidate.Replacement))
        {
            var original = _signals.For(candidate.Original.Trim());
            var replacement = _signals.For(candidate.Replacement!.Trim());
            if (replacement.IsKnown && !original.IsKnown)
            {
                return AiSupportClass.RuleSupported;
            }
        }

        return AiSupportClass.Unsupported;
    }

    private double RequiredConfidenceFor(AiSupportClass supportClass, IssueCategory category)
    {
        var baseline = supportClass switch
        {
            AiSupportClass.ConfirmsDeterministic => _options.ConfirmThreshold,
            AiSupportClass.RerankExisting => _options.RerankThreshold,
            AiSupportClass.RuleSupported => _options.RuleSupportedThreshold,
            _ => _options.UnsupportedThreshold,
        };

        // Punctuation is recoverable — a stray comma is annoying, not destructive — so it
        // carries the baseline bar. Replacing a word the user chose changes meaning, so an
        // unsupported lexical edit is held to the strictest bar in the policy.
        if (supportClass == AiSupportClass.Unsupported && category != IssueCategory.Punctuation)
        {
            return Math.Max(baseline, _options.UnsupportedThreshold);
        }

        return baseline;
    }
}
