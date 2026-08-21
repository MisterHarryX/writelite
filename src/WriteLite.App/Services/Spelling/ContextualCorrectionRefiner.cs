using System.Text.RegularExpressions;
using WriteLite.AI.Local;
using WriteLite.Language.Russian;

namespace WriteLite.Services.Spelling;

/// <summary>
/// The context-aware pass over a sentence, after the lexical pass has done what
/// dictionaries can do.
/// </summary>
/// <remarks>
/// Two jobs the lexicon cannot do alone:
///
/// 1. Reranking. The lexical scorer picks between "хочу" and "хожу" on edit
///    distance and frequency; only the sentence can say which one belongs.
/// 2. Real-word errors. "Кампания проводит исследование" contains no unknown
///    word, so nothing upstream will ever look at it. Tokens listed in the
///    confusion sets get scored against their alternatives here.
///
/// Both are gated behind a margin rather than a bare comparison. A model that
/// merely prefers an alternative is not evidence of an error — it has to prefer
/// it clearly — because a false correction on text the user already wrote is
/// the most damaging thing this application can do.
///
/// The whole pass is optional: with no reranker deployed the refiner reports
/// itself unavailable and the pipeline keeps its lexical results unchanged.
/// </remarks>
public sealed partial class ContextualCorrectionRefiner : IDisposable
{
    /// <summary>
    /// How much better the alternative must read before an in-dictionary word is
    /// questioned.
    /// </summary>
    /// <remarks>
    /// Swept over the frozen benchmark's real_word and clean categories by
    /// <c>ai/scripts/calibrate_realword_margin.py</c>. Everything from 0.20 to
    /// 0.60 behaves identically on that data, so the value sits mid-plateau
    /// rather than on an edge.
    ///
    /// What this actually catches, measured: real words that are grammatically
    /// impossible in place ("должен будит" scores +0.83 for "будет"). What it
    /// does not catch: pairs separated by meaning rather than grammar
    /// (компания/кампания leans the right way by only ~0.02). Widening the
    /// candidate source from the confusion sets to every real word one edit
    /// away was measured and made things strictly worse — 6x the candidates,
    /// more false positives, no extra recall — so the gate stays narrow.
    /// </remarks>
    public const double RealWordMargin = 0.35;

    /// <summary>Reranking only reorders existing candidates, so it can be looser.</summary>
    public const double RerankMargin = 0.05;

    private readonly RussianContextualReranker _reranker;
    private readonly RussianConfusionSets? _confusions;
    private bool _disposed;

    private ContextualCorrectionRefiner(RussianContextualReranker reranker, RussianConfusionSets? confusions)
    {
        _reranker = reranker;
        _confusions = confusions;
    }

    public bool IsAvailable => !_disposed;
    public string ModelVersion => _reranker.ModelVersion;
    public int ConfusionSetCount => _confusions?.SetCount ?? 0;

    public static ContextualCorrectionRefiner? TryLoad(string? modelDirectory = null)
    {
        var reranker = RussianContextualReranker.TryLoad(modelDirectory);
        if (reranker is null)
        {
            CompatibilityLogger.Technical("contextual-reranker-missing", "lexical ranking only");
            return null;
        }

        var confusions = RussianConfusionSets.TryLoad();
        CompatibilityLogger.Technical(
            "contextual-reranker-loaded",
            $"version={reranker.ModelVersion} sets={confusions?.SetCount ?? 0} ms={reranker.LoadTime.TotalMilliseconds:F0}");
        return new ContextualCorrectionRefiner(reranker, confusions);
    }

    public readonly record struct ContextualFinding(
        int Start,
        int Length,
        string Original,
        string Replacement,
        double Confidence,
        bool IsRealWordError);

    /// <summary>
    /// Reorders a suggestion list by how well each candidate reads in place.
    /// Returns the input unchanged when the model has no clear opinion.
    /// </summary>
    public IReadOnlyList<string> Rerank(
        string text,
        int start,
        int length,
        IReadOnlyList<string> suggestions,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || suggestions.Count < 2) return suggestions;

        var reranked = _reranker.Rerank(text, start, length, suggestions, cancellationToken);
        if (reranked.Count == 0) return suggestions;

        // Leave the lexical order alone unless the top two are actually
        // separated; an arbitrary reshuffle of near-ties is noise, not insight.
        if (reranked.Count > 1 && reranked[0].Acceptability - reranked[1].Acceptability < RerankMargin)
        {
            return suggestions;
        }

        return reranked.Select(r => r.Word).ToArray();
    }

    /// <summary>
    /// Finds words that are spelled correctly but do not belong in this
    /// sentence. Only tokens present in the confusion sets are considered.
    /// </summary>
    public IReadOnlyList<ContextualFinding> FindRealWordErrors(
        string text,
        CancellationToken cancellationToken = default)
    {
        var findings = new List<ContextualFinding>();
        if (_disposed || _confusions is null || string.IsNullOrWhiteSpace(text)) return findings;

        // Scored once, not per token: the sentence as written does not change while we
        // walk it, and this is a model call rather than a lookup.
        double? asWrittenCache = null;

        foreach (Match match in WordRegex().Matches(text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var alternatives = _confusions.AlternativesFor(match.Value);
            if (alternatives.Count == 0) continue;

            var asWritten = asWrittenCache ??= _reranker.ScoreSentence(text);
            var reranked = _reranker.Rerank(text, match.Index, match.Length, alternatives, cancellationToken);
            if (reranked.Count == 0) continue;

            var best = reranked[0];
            if (best.Acceptability - asWritten < RealWordMargin) continue;

            findings.Add(new ContextualFinding(
                match.Index,
                match.Length,
                match.Value,
                PreserveCase(match.Value, best.Word),
                best.Acceptability,
                IsRealWordError: true));
        }

        return findings;
    }

    private static string PreserveCase(string original, string replacement)
    {
        if (string.IsNullOrEmpty(replacement) || string.IsNullOrEmpty(original)) return replacement;
        if (char.IsUpper(original[0]) && !char.IsUpper(replacement[0]))
        {
            return char.ToUpperInvariant(replacement[0]) + replacement[1..];
        }

        return replacement;
    }

    [GeneratedRegex(@"[\p{L}][\p{L}\p{N}]*", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reranker.Dispose();
    }
}
