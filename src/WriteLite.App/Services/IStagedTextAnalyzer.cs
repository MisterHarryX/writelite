using WriteLite.Models;

namespace WriteLite.Services;

/// <summary>
/// Which lane produced a set of findings, and therefore how final it is.
/// </summary>
public enum AnalysisLane
{
    /// <summary>Native deterministic rules and spelling. Milliseconds; published first.</summary>
    Fast,

    /// <summary>
    /// The rest of the deterministic stack — the language engine, the punctuation model, style.
    /// Hundreds of milliseconds to a few seconds; published as a second, wider answer.
    /// </summary>
    Deterministic,

    /// <summary>The local model. Seconds to a minute; published last or not at all.</summary>
    Deep
}

/// <summary>
/// An analyzer that can answer more than once: an early deterministic answer while the
/// slower layers are still working, then the full one.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> <see cref="ITextAnalyzer.AnalyzeAsync"/> returns exactly one
/// list, so a stack that ends in a local model can only publish when the model is done.
/// Measured on a 480-word document with the shipping wiring: the deterministic layers had
/// 22 findings after 1.2 s and the task completed after 53.1 s, having accepted 0 model
/// findings. The editor bound that task's result and showed an empty sidebar for the whole
/// 53 s. Nothing about that is a scheduling bug — it is the shape of the contract.</para>
///
/// <para>An analyzer that implements this keeps its <see cref="ITextAnalyzer"/> behaviour
/// unchanged for every caller that does not care; callers that do pass a
/// <paramref name="publish"/> callback and receive each lane as it lands.</para>
/// </remarks>
public interface IStagedTextAnalyzer : ITextAnalyzer
{
    /// <summary>
    /// Analyses <paramref name="text"/>, calling <paramref name="publish"/> once per lane as
    /// that lane finishes, and returning the final merged set.
    /// </summary>
    /// <param name="publish">
    /// Receives an intermediate answer and the lane that produced it. Called on a worker
    /// thread; never called after the returned task completes. Exceptions it throws are the
    /// caller's problem and are not swallowed.
    /// </param>
    Task<IReadOnlyList<TextIssue>> AnalyzeStagedAsync(
        string text,
        Action<AnalysisLane, IReadOnlyList<TextIssue>> publish,
        CancellationToken cancellationToken = default);
}
