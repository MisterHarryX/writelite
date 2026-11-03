using WriteLite.Language.Russian;
using WriteLite.Models;

namespace WriteLite.Services.Grammar;

/// <summary>
/// «Не» with adverbs: «не далеко» → «недалеко», «работала не правильно» → «неправильно».
/// </summary>
/// <remarks>
/// <para>An adverb in «-о» is written together with «не» when the pair means one thing —
/// «недалеко» is «близко», «неправильно» is «ошибочно» — and apart when «не» negates it
/// against something else: «не далеко, а близко». The index decides the first half of that:
/// a merged spelling the language does not have («не очень», «не так») is never proposed,
/// because «неочень» is not in it. The contrast decides the second half, and the rule stands
/// down when «а» or «но» follows in the same clause, or when an intensifier of negation —
/// «вовсе не», «отнюдь не», «ничуть не» — makes the «не» a particle.</para>
///
/// <para>Predicatives of state are left alone: «мне не холодно» negates a state and is
/// written apart, while «на улице нехолодно» is a different sentence. The index files those
/// words as predicatives, and a dative experiencer before «не» marks the construction.</para>
/// </remarks>
public sealed class RussianNeSpellingAnalyzer : GrammarAnalyzerBase
{
    private readonly RussianMorphology _morphology;

    public RussianNeSpellingAnalyzer(RussianMorphology morphology)
        => _morphology = morphology ?? throw new ArgumentNullException(nameof(morphology));

    public static RussianNeSpellingAnalyzer? TryCreate(RussianFormIndex? index)
    {
        var morphology = RussianMorphology.TryCreate(index);
        return morphology is null ? null : new RussianNeSpellingAnalyzer(morphology);
    }

    protected override void CollectCore(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        var tokens = TokensAtLeast(text, 2);

        for (var i = 0; i < tokens.Count - 1; i++)
        {
            var ne = tokens[i];
            if (!ne.Value.Equals("не", StringComparison.OrdinalIgnoreCase)) continue;

            var word = tokens[i + 1];
            if (!RussianTokens.OnlySpacesBetween(text, ne, word)) continue;

            var lower = word.Value.ToLowerInvariant();
            if (TryAttributiveAdjective(text, tokens, i, protectedSpans, issues)) continue;
            if (!lower.EndsWith('о') || lower.Length < 4) continue;
            if (AmbiguousPairs.Contains(lower)) continue;
            if (_morphology.PartOfSpeech(lower) != RussianPartOfSpeech.Adverb) continue;

            var merged = "не" + lower;
            if (!_morphology.Knows(merged)) continue;
            if (_morphology.PartOfSpeech(merged) is not (RussianPartOfSpeech.Adverb or RussianPartOfSpeech.Predicative)) continue;

            if (i > 0 && (NegationIntensifiers.Contains(tokens[i - 1].Value) || DativeExperiencers.Contains(tokens[i - 1].Value)))
            {
                continue;
            }

            if (ContrastFollows(text, tokens, i + 1)) continue;

            var start = ne.Start;
            var length = word.Start + word.Length - ne.Start;
            if (ProtectedTextSpans.Overlaps(start, length, protectedSpans)) continue;

            var original = text.Substring(start, length);
            var replacement = RussianTokens.MatchLeadingCase(ne.Value, merged);

            issues.Add(new TextIssue(
                start,
                length,
                original,
                replacement,
                "«Не» с наречием пишется слитно",
                $"«{merged}» — одно слово: его можно заменить синонимом без «не», и противопоставления "
                + "с «а» или «но» здесь нет.",
                IssueCategory.Orthography,
                IssueSeverity.Error,
                CanApplyAutomatically: true,
                RuleId: "ru.spelling.ne-adverb",
                LinguisticCategory: LinguisticIssueCategory.NeWithPartsOfSpeech,
                Confidence: 0.86));
        }
    }

    /// <summary>True when «а» or «но» follows within the same clause: «не далеко, а близко».</summary>
    /// <summary>
    /// «не большой дом» → «небольшой дом»: a full adjective directly before its noun, with no
    /// contrast («не большой, а огромный») and no intensifier («вовсе не большой»).
    /// </summary>
    /// <remarks>
    /// Only the attributive position: as a predicate («дом не большой») the separate spelling is
    /// the normal way to deny the property, and the writer's intent cannot be read from the text.
    /// Offered, not auto-applied — the contrast can sit in the next sentence.
    /// </remarks>
    private bool TryAttributiveAdjective(
        string text,
        List<RussianToken> tokens,
        int i,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        if (i + 2 >= tokens.Count) return false;
        var ne = tokens[i];
        var adjective = tokens[i + 1];
        var noun = tokens[i + 2];
        var lower = adjective.Value.ToLowerInvariant();
        if (_morphology.PartOfSpeech(lower) != RussianPartOfSpeech.AdjectiveFull) return false;
        if (!RussianTokens.OnlySpacesBetween(text, adjective, noun) || !_morphology.IsNoun(noun.Value)) return false;
        if (!_morphology.Nominal(adjective.Value).AgreesWith(_morphology.Nominal(noun.Value))) return false;

        var merged = "не" + lower;
        if (!_morphology.Knows(merged) || _morphology.PartOfSpeech(merged) != RussianPartOfSpeech.AdjectiveFull) return false;
        if (i > 0 && (NegationIntensifiers.Contains(tokens[i - 1].Value) || DativeExperiencers.Contains(tokens[i - 1].Value))) return false;
        if (ContrastFollows(text, tokens, i + 1)) return false;

        var start = ne.Start;
        var length = adjective.End - ne.Start;
        if (ProtectedTextSpans.Overlaps(start, length, protectedSpans)) return false;
        var original = text.Substring(start, length);
        var replacement = RussianTokens.MatchLeadingCase(ne.Value, merged);
        issues.Add(new TextIssue(
            start,
            length,
            original,
            replacement,
            "«Не» с прилагательным пишется слитно",
            $"Без противопоставления «не» с прилагательным пишется слитно: «{merged}» можно заменить "
            + "синонимом без «не». Раздельно — если есть «а» или «но» («не большой, а огромный») "
            + "или усиление отрицания («вовсе не большой»).",
            IssueCategory.Orthography,
            IssueSeverity.Warning,
            CanApplyAutomatically: false,
            RuleId: "ru.spelling.ne-adjective",
            LinguisticCategory: LinguisticIssueCategory.NeWithPartsOfSpeech,
            Confidence: 0.8));
        return true;
    }

    private static bool ContrastFollows(string text, List<RussianToken> tokens, int from)
    {
        for (var j = from + 1; j < tokens.Count && j <= from + 5; j++)
        {
            var between = text.AsSpan(tokens[j - 1].Start + tokens[j - 1].Length, tokens[j].Start - tokens[j - 1].Start - tokens[j - 1].Length);
            if (between.IndexOfAny(".!?;") >= 0) return false;

            var value = tokens[j].Value;
            if (value.Equals("а", StringComparison.OrdinalIgnoreCase) || value.Equals("но", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Pairs whose merged and separate spellings mean different things and are both common:
    /// «не много» (not much) and «немного» (a little), «не скоро» and «нескоро».
    /// </summary>
    private static readonly HashSet<string> AmbiguousPairs = new(StringComparer.OrdinalIgnoreCase)
    {
        "много", "мало", "скоро", "обязательно", "долго",
    };

    private static readonly HashSet<string> NegationIntensifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "вовсе", "отнюдь", "ничуть", "нисколько", "далеко", "совсем", "ни", "никогда", "никак",
    };

    private static readonly HashSet<string> DativeExperiencers = new(StringComparer.OrdinalIgnoreCase)
    {
        "мне", "тебе", "ему", "ей", "нам", "вам", "им",
    };
}
