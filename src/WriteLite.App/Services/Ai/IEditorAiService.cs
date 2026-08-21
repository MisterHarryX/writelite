namespace WriteLite.Services.Ai;

/// <summary>The transformations the editor's AI menu can ask for.</summary>
public enum RewriteOperation
{
    Rewrite,
    ImproveStyle,
    Formal,
    Casual,
    Simplify,
    Shorten,
    Expand,
    Grammar,
    Explain,
    Translate,
    Custom
}

/// <summary>
/// One rewrite request: the selection, and just enough around it to make sense.
/// </summary>
/// <param name="Selection">The text the user chose. Only this is ever replaced.</param>
/// <param name="ContextBefore">
/// Up to a few hundred characters preceding the selection, so a pronoun or a
/// half-finished thought can be resolved. Never sent anywhere but the local model.
/// </param>
/// <param name="Instruction">The user's own words, for <see cref="RewriteOperation.Custom"/>.</param>
public sealed record RewriteRequest(
    RewriteOperation Operation,
    string Selection,
    string ContextBefore = "",
    string ContextAfter = "",
    string? Instruction = null);

/// <summary>
/// A proposed replacement, which the user has not accepted yet.
/// </summary>
/// <param name="Backend">
/// Which local path produced this — the neural model or the offline rule fallback.
/// Shown to the user so an answer is never presented as more than it is.
/// </param>
public sealed record RewriteResult(
    string Original,
    string Suggestion,
    RewriteOperation Operation,
    string Backend,
    bool IsAdvisory = false)
{
    /// <summary>True when the model returned nothing usable and the text is unchanged.</summary>
    public bool IsNoOp => string.Equals(Original.Trim(), Suggestion.Trim(), StringComparison.Ordinal);
}

/// <summary>
/// Local text transformation for the editor.
/// </summary>
/// <remarks>
/// Separate from <see cref="IAiTextProvider"/>, which answers "what is wrong with
/// this text" for the corrections panel. This answers "make this text different",
/// which is a user-initiated action with a preview and an undo rather than a
/// background analysis.
///
/// Every implementation must run entirely on this machine.
/// </remarks>
public interface IEditorAiService
{
    /// <summary>True when the neural path is reachable; false means rule fallbacks only.</summary>
    bool IsModelAvailable { get; }

    Task<RewriteResult> RewriteAsync(RewriteRequest request, CancellationToken cancellationToken = default);
}
