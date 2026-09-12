using WriteLite.Models;

namespace WriteLite.Services.Grammar;

/// <summary>
/// Shared <c>Collect</c> ceremony for the rule-based grammar analyzers: the empty-text
/// guard and the token-splitting entry point. The five token-walking analyzers were
/// identical through the guard and the <see cref="RussianTokens.Split"/> call (differing
/// only in a minimum-token floor); the two regex-walking ones only inherit the guard.
/// </summary>
public abstract class GrammarAnalyzerBase
{
    public void Collect(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        CollectCore(text, protectedSpans, issues);
    }

    /// <summary>Performs the analysis. Only called for non-blank text.</summary>
    protected abstract void CollectCore(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues);

    /// <summary>
    /// Splits the text and returns the tokens, or an empty list when there are fewer than
    /// <paramref name="minimumCount"/>. Analyzers that walk a short token list skip the
    /// sentence entirely rather than inspect a fragment.
    /// </summary>
    private protected static List<RussianToken> TokensAtLeast(string text, int minimumCount)
    {
        var tokens = RussianTokens.Split(text);
        return tokens.Count >= minimumCount ? tokens : [];
    }
}