namespace WriteLite.AI.Contracts;

/// <summary>
/// One option the judge may choose. Ids are assigned by the caller and are the only thing
/// the model is allowed to return.
/// </summary>
/// <param name="Id">Single letter, "A" upwards, or <see cref="CandidateJudge.NoChangeId"/>.</param>
/// <param name="Text">The replacement this option stands for; empty for NO_CHANGE.</param>
public sealed record JudgeCandidate(string Id, string Text);

/// <summary>
/// What the judge is asked: a sentence, the span in question, and a closed list of options.
/// </summary>
/// <remarks>
/// Deliberately not a correction request. The model is never shown a blank to fill in — it
/// is shown a finite menu and asked to point at one item. That is the whole design: Phase 4
/// measured the free-generation path at 0.115 precision, and every one of its false
/// positives was a token the model invented. A judge cannot invent a token, so its worst
/// case is picking the wrong existing option, which is bounded by the quality of the
/// candidate generator rather than by the model's imagination.
/// </remarks>
public sealed record CandidateJudgeRequest(
    string Sentence,
    int TargetStart,
    int TargetLength,
    IReadOnlyList<JudgeCandidate> Candidates)
{
    public string Target => Sentence.Substring(TargetStart, TargetLength);
}

/// <summary>Why a judgement did or did not produce a usable answer.</summary>
public enum CandidateJudgeOutcome
{
    /// <summary>The model chose a replacement from the list.</summary>
    Selected = 0,

    /// <summary>The model chose NO_CHANGE. A real answer, not a failure.</summary>
    NoChange = 1,

    /// <summary>The model was not consulted: unavailable, cancelled, or nothing to ask.</summary>
    NotConsulted = 2,

    /// <summary>The answer did not parse, or named an option that was not offered.</summary>
    Rejected = 3,
}

/// <summary>
/// The judge's answer, already validated against the offered list.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="CandidateId">The chosen id, when one was chosen.</param>
/// <param name="Text">The chosen replacement; empty for NO_CHANGE.</param>
/// <param name="Confidence">Reported by the model when it offers one; null otherwise.</param>
/// <param name="RejectionReason">Set when <see cref="Outcome"/> is
/// <see cref="CandidateJudgeOutcome.Rejected"/>, for forensics.</param>
public sealed record CandidateJudgeVerdict(
    CandidateJudgeOutcome Outcome,
    string? CandidateId = null,
    string? Text = null,
    double? Confidence = null,
    string? RejectionReason = null)
{
    public static CandidateJudgeVerdict NotConsulted(string? reason = null)
        => new(CandidateJudgeOutcome.NotConsulted, RejectionReason: reason);

    public static CandidateJudgeVerdict Reject(string reason)
        => new(CandidateJudgeOutcome.Rejected, RejectionReason: reason);
}
