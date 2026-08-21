using WriteLite.Language.Russian;
using WriteLite.Models;

namespace WriteLite.Services.Grammar;

/// <summary>
/// Prepositions that govern the dative, written with a genitive: «вопреки новых целей» →
/// «вопреки новым целям», «согласно приказа» → «согласно приказу».
/// </summary>
/// <remarks>
/// <para>This is the most common case-government error in written Russian and it is one a
/// closed list settles: seven prepositions, all of which require the dative, all of which are
/// routinely written with the genitive by analogy with «в течение», «в результате» and the
/// other genitive-governing compounds.</para>
///
/// <para><b>Why the trigger is genitive specifically, and not "anything that is not dative".</b>
/// The form index records one analysis per surface form, so a word that is dative and
/// something else at once comes back as whichever analysis OpenCorpora listed first. «новым»
/// is recorded instrumental; «инструкции» is recorded genitive although it is also the
/// dative. Firing on "not dative" would therefore rewrite correct text. Firing only when the
/// head noun is recorded genitive <em>and</em> a distinct dative form of the same lemma exists
/// means a homonymous form produces nothing, which is the right answer for it.</para>
///
/// <para><b>Why modifiers use an ending relation and the head uses the index.</b> Same reason,
/// from the other side. «новых» is recorded as a noun form of «новое», so same-lemma search
/// never reaches «новым» — the adjective's paradigm is not connected to it in the data. The
/// genitive→dative endings of full adjectives and participles are a closed relation
/// (-ого→-ому, -его→-ему, -ых→-ым, -их→-им), so the modifier is transformed by rule and the
/// result is only accepted if the index already contains it. Nouns take the opposite route
/// because their endings are not a closed relation, but their paradigms are in the data.</para>
///
/// <para><b>The one ambiguity that is handled by exception.</b> «благодаря» is also the gerund
/// of «благодарить», which governs the accusative: «благодаря друга за помощь» is correct
/// Russian meaning "thanking the friend". Thanking is done to animates, so the rule requires
/// an inanimate head for that preposition only.</para>
/// </remarks>
public sealed class RussianCaseGovernmentAnalyzer
{
    private readonly RussianFormIndex _index;

    public RussianCaseGovernmentAnalyzer(RussianFormIndex index) => _index = index;

    /// <summary>Returns null when the form index is not deployed; the rule then does nothing.</summary>
    public static RussianCaseGovernmentAnalyzer? TryCreate(RussianFormIndex? index)
        => index is null ? null : new RussianCaseGovernmentAnalyzer(index);

    /// <summary>Prepositions requiring the dative, with the question that names the case.</summary>
    private static readonly Dictionary<string, string> DativePrepositions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["к"] = "к чему?",
            ["вопреки"] = "вопреки чему?",
            ["согласно"] = "согласно чему?",
            ["благодаря"] = "благодаря чему?",
            ["наперекор"] = "наперекор чему?",
            ["навстречу"] = "навстречу чему?",
            ["подобно"] = "подобно чему?",
            ["вслед"] = "вслед чему?",
        };

    /// <summary>Genitive → dative for full adjectives and participles.</summary>
    private static readonly (string From, string To)[] ModifierEndings =
    [
        ("ого", "ому"), ("его", "ему"), ("ых", "ым"), ("их", "им"),
    ];

    /// <summary>The longest noun phrase the rule will rewrite, in tokens.</summary>
    /// <remarks>
    /// Three. Beyond that the phrase is likely to contain a second, genitive-governed noun
    /// («вопреки правилам компании») whose case is correct and must not be touched — and the
    /// window is walked longest-first, so a longer window would start claiming those.
    /// </remarks>
    private const int MaxPhraseTokens = 3;

    public void Collect(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var tokens = RussianTokens.Split(text);
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (!DativePrepositions.TryGetValue(tokens[i].Value, out var question)) continue;
            if (ProtectedTextSpans.Overlaps(tokens[i].Start, tokens[i].Length, protectedSpans)) continue;

            var phrase = ResolvePhrase(text, tokens, i, tokens[i].Value);
            if (phrase is null) continue;

            var (start, length, original, replacement, headWord) = phrase.Value;
            if (ProtectedTextSpans.Overlaps(start, length, protectedSpans)) continue;

            issues.Add(new TextIssue(
                start,
                length,
                original,
                replacement,
                "Ошибка в управлении падежом",
                $"Предлог «{tokens[i].Value.ToLowerInvariant()}» требует дательного падежа: "
                + $"{question} — {replacement ?? headWord}.",
                IssueCategory.Grammar,
                IssueSeverity.Error,
                CanApplyAutomatically: replacement is not null,
                RuleId: "ru.grammar.case-government-dative",
                LinguisticCategory: LinguisticIssueCategory.EndingError,
                Confidence: replacement is not null ? 0.93 : 0.7));
        }
    }

    /// <summary>
    /// The genitive noun phrase after the preposition and its dative rewrite, or null when
    /// there is nothing to correct.
    /// </summary>
    /// <remarks>
    /// Windows are tried longest first so «новых целей» is rewritten as a phrase rather than
    /// as «новых целям», and a window is only accepted when <em>every</em> token in it
    /// converts. A partial rewrite of a noun phrase is worse than no rewrite: it leaves the
    /// text in a state no writer would have produced.
    /// </remarks>
    private (int Start, int Length, string Original, string? Replacement, string Head)? ResolvePhrase(
        string text,
        List<RussianToken> tokens,
        int prepositionIndex,
        string preposition)
    {
        for (var size = MaxPhraseTokens; size >= 1; size--)
        {
            var last = prepositionIndex + size;
            if (last >= tokens.Count) continue;

            // Nothing but spaces from the preposition through the phrase, or the relation
            // the rule is claiming does not hold.
            var contiguous = true;
            for (var k = prepositionIndex; k < last; k++)
            {
                if (!RussianTokens.OnlySpacesBetween(text, tokens[k], tokens[k + 1])) { contiguous = false; break; }
            }

            if (!contiguous) continue;

            var head = tokens[last];
            var headInfo = _index.GetInfo(head.Value.ToLowerInvariant());
            if (headInfo.PartOfSpeech != RussianPartOfSpeech.Noun) continue;
            if (!headInfo.Tag.Contains("gent", StringComparison.Ordinal)) continue;

            // «благодаря» is also a gerund governing the accusative, and thanking is done to
            // animates. An inanimate head removes that reading.
            if (preposition.Equals("благодаря", StringComparison.OrdinalIgnoreCase)
                && !headInfo.Tag.Contains("inan", StringComparison.Ordinal))
            {
                continue;
            }

            var dativeHead = DativeOf(head.Value, headInfo);
            if (dativeHead is null) continue;

            var parts = new List<string>();
            var converted = true;
            for (var k = prepositionIndex + 1; k < last; k++)
            {
                var modifier = ConvertModifier(tokens[k].Value);
                if (modifier is null) { converted = false; break; }
                parts.Add(modifier);
            }

            if (!converted) continue;

            parts.Add(dativeHead);

            var start = tokens[prepositionIndex + 1].Start;
            var length = head.End - start;
            var original = text.Substring(start, length);
            var replacement = RussianTokens.MatchLeadingCase(original, string.Join(" ", parts));

            // A rewrite that changes nothing is not a finding.
            if (string.Equals(original, replacement, StringComparison.Ordinal)) return null;

            return (start, length, original, replacement, head.Value);
        }

        return null;
    }

    /// <summary>The dative form of a noun, from the index, matching the original's number.</summary>
    private string? DativeOf(string word, RussianFormIndex.FormInfo info)
    {
        if (info.Lemma.Length == 0) return null;

        var lower = word.ToLowerInvariant();
        var plural = info.Tag.Contains("plur", StringComparison.Ordinal);
        string? best = null;

        foreach (var match in _index.FindWithin(lower, 2, 4096))
        {
            if (string.Equals(match.Word, lower, StringComparison.Ordinal)) continue;
            var candidate = _index.GetInfo(match.Word);
            if (!string.Equals(candidate.Lemma, info.Lemma, StringComparison.OrdinalIgnoreCase)) continue;
            if (!candidate.Tag.Contains("datv", StringComparison.Ordinal)) continue;
            if (candidate.Tag.Contains("plur", StringComparison.Ordinal) != plural) continue;

            // Ties are broken toward the shorter form only to make the result deterministic;
            // a lemma has one dative per number, so ties are not expected.
            if (best is null || string.CompareOrdinal(match.Word, best) < 0) best = match.Word;
        }

        return best;
    }

    /// <summary>The dative of a modifier by the closed ending relation, gated on the index.</summary>
    private string? ConvertModifier(string word)
    {
        var lower = word.ToLowerInvariant();
        foreach (var (from, to) in ModifierEndings)
        {
            if (!lower.EndsWith(from, StringComparison.Ordinal)) continue;
            if (lower.Length <= from.Length + 1) continue;

            var candidate = lower[..^from.Length] + to;
            if (_index.Contains(candidate)) return candidate;
        }

        return null;
    }
}
