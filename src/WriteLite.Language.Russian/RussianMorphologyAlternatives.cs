namespace WriteLite.Language.Russian;

/// <summary>
/// Alternatives for a word that is <em>already spelled correctly</em>.
/// </summary>
/// <remarks>
/// <para>The spelling generator answers "which known word did the writer mean to type", and
/// it can only ask that question about a token the index does not recognise. That is what
/// gives the pipeline its no-change accuracy for free, and it is also the reason 35.1 % of
/// single-token targets are unreachable: «учится» is a real Russian word, so nothing is ever
/// offered for it, and «учиться» cannot be selected because it was never generated.</para>
///
/// <para>This class is the other question — "given that this is a real word, which real word
/// is it confusable with" — and it is deliberately kept small. Every rule here must be a
/// closed relation between two specific forms, not an expansion over a paradigm. Offering
/// every same-lemma inflection of every valid noun would raise coverage and simultaneously
/// turn each correct sentence into a series of questions, which is the trade §5 of the phase
/// brief calls false-candidate explosion and the trade this project has repeatedly refused.
/// The measured cost of what is here is reported by <c>tools/candcov</c> as the offer rate on
/// correct tokens.</para>
///
/// <para>Offering an alternative is not correcting anything. Everything produced here is a
/// question for a context-aware decider — the reranker today, a trained model if one earns
/// its place — and a word with no alternatives is left alone exactly as before.</para>
/// </remarks>
public sealed class RussianMorphologyAlternatives
{
    /// <summary>
    /// The most alternatives any single token may receive.
    /// </summary>
    /// <remarks>
    /// Four, because the rules below are relations rather than expansions and none of them
    /// naturally produces more: a тся/ться pair has one partner, and the confusion sets
    /// average two members. The cap exists so that a future rule cannot quietly turn this into
    /// a paradigm dump without the number changing in the report.
    /// </remarks>
    public const int MaxAlternatives = 4;

    private readonly RussianFormIndex _index;
    private readonly RussianConfusionSets? _confusions;

    public RussianMorphologyAlternatives(RussianFormIndex index, RussianConfusionSets? confusions = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _confusions = confusions;
    }

    public int ConfusionSetCount => _confusions?.SetCount ?? 0;

    /// <summary>
    /// Plausible alternative forms for a correctly spelled word, best-motivated first.
    /// </summary>
    /// <returns>Empty for any token this class has no specific reason to question.</returns>
    public IReadOnlyList<string> For(string word)
    {
        // No minimum length. Both rules below carry their own evidence — a closed set
        // membership, or a total orthographic relation — so the usual "too short to guess
        // about" guard would only remove the cases they are best at: «ни»/«не», «в»/«во»,
        // «ей»/«ней» are among the most frequently confused pairs in the language, and they
        // are two letters long.
        if (string.IsNullOrWhiteSpace(word)) return [];

        var lower = word.ToLowerInvariant();
        if (!_index.Contains(lower)) return [];

        var found = new List<string>(MaxAlternatives);

        void Offer(string? candidate)
        {
            if (string.IsNullOrEmpty(candidate)) return;
            if (found.Count >= MaxAlternatives) return;
            if (string.Equals(candidate, lower, StringComparison.OrdinalIgnoreCase)) return;
            if (found.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return;
            if (!_index.Contains(candidate)) return;
            found.Add(RestoreCase(word, candidate));
        }

        Offer(ReflexivePartner(lower));

        if (_confusions is not null)
        {
            foreach (var alternative in _confusions.AlternativesFor(lower))
            {
                Offer(alternative.ToLowerInvariant());
            }
        }

        return found;
    }

    /// <summary>
    /// «учится» ↔ «учиться», «извинится» ↔ «извиниться», «злится» ↔ «злиться».
    /// </summary>
    /// <remarks>
    /// <para>Both members are valid dictionary words, both are common, and which one is
    /// correct is decided by the sentence — «он учится» against «он хочет учиться». Spelling
    /// distance cannot express that question because neither form is misspelled, and a
    /// frequency table cannot answer it because both are frequent.</para>
    ///
    /// <para>The relation is purely orthographic and total: the soft sign is present or it is
    /// not. Generating the partner is therefore exact rather than a guess, and the membership
    /// check in <see cref="For"/> discards the cases where the partner is not a word — «весится»
    /// has a partner, «яйца» does not.</para>
    /// </remarks>
    private static string? ReflexivePartner(string lower)
    {
        if (lower.EndsWith("ться", StringComparison.Ordinal))
        {
            return string.Concat(lower.AsSpan(0, lower.Length - 4), "тся");
        }

        if (lower.EndsWith("тся", StringComparison.Ordinal))
        {
            return string.Concat(lower.AsSpan(0, lower.Length - 3), "ться");
        }

        return null;
    }

    private static string RestoreCase(string original, string candidate)
    {
        if (candidate.Length == 0 || original.Length == 0) return candidate;
        return char.IsUpper(original[0])
            ? char.ToUpperInvariant(candidate[0]) + candidate[1..]
            : candidate;
    }
}
