using WriteLite.Language.Russian;
using WriteLite.Models;

namespace WriteLite.Services.Grammar;

/// <summary>
/// The double comparative: «более лучше», «более удобнее», «более быстрее», «более тщательнее».
/// </summary>
/// <remarks>
/// <para>Russian forms the comparative either analytically, with «более» plus the positive
/// degree, or synthetically with «-ее»/«-ше». Using both at once is the single commonest
/// grammatical error in written Russian, and it has a repair that is always correct and always
/// minimal: drop «более». §31 of the sprint brief asks for exactly that — if the only problem
/// is a redundant word, remove the word and change nothing else.</para>
///
/// <para><b>Why this is here rather than left to the model.</b> Every historical destructive
/// correction in this repository's §2 list is a model trying to repair one of these phrases:
/// «более лучшее решение» → «лучше решение», «более удобнее» → «более удобная», «более
/// тщательнее» → «более тщательная». The deterministic rule owns the span now, which means the
/// routing policy sees a high-confidence finding there and does not ask the model about it —
/// so the class of damage is removed by not creating the opportunity, not by catching the
/// result.</para>
///
/// <para><b>The comparative is identified from the index, not from the ending.</b> «-ее» is
/// also the neuter of a full adjective: «лучшее» is the superlative of «хороший» and «более
/// лучшее решение» must be repaired to «лучшее решение», keeping the adjective. Reading the
/// recorded part of speech separates the two; guessing from the ending merges them, which is
/// how a rule of this shape produces «лучше решение».</para>
/// </remarks>
public sealed class RussianComparativeAnalyzer : GrammarAnalyzerBase
{
    private readonly RussianMorphology _morphology;

    public RussianComparativeAnalyzer(RussianMorphology morphology)
        => _morphology = morphology ?? throw new ArgumentNullException(nameof(morphology));

    public static RussianComparativeAnalyzer? TryCreate(RussianFormIndex? index)
    {
        var morphology = RussianMorphology.TryCreate(index);
        return morphology is null ? null : new RussianComparativeAnalyzer(morphology);
    }

    protected override void CollectCore(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        var tokens = TokensAtLeast(text, 2);
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            var head = tokens[i].Value.ToLowerInvariant();
            if (head is not ("более" or "менее")) continue;
            if (!RussianTokens.OnlySpacesBetween(text, tokens[i], tokens[i + 1])) continue;

            var second = tokens[i + 1];
            if (!IsSynthetic(second.Value)) continue;

            // The whole «более X» span is replaced by «X», so the edit is one span and the
            // spacing takes care of itself.
            var start = tokens[i].Start;
            var length = second.End - start;
            if (ProtectedTextSpans.Overlaps(start, length, protectedSpans)) continue;

            var original = text.Substring(start, length);
            var replacement = RussianTokens.MatchLeadingCase(original, second.Value.ToLowerInvariant());
            if (string.Equals(original, replacement, StringComparison.Ordinal)) continue;

            issues.Add(new TextIssue(
                start,
                length,
                original,
                replacement,
                "Двойная степень сравнения",
                $"Сравнительная степень образуется либо словом «{head}» с начальной формой, "
                + $"либо суффиксом: «{second.Value.ToLowerInvariant()}» уже сравнительная степень, "
                + $"поэтому «{head}» лишнее.",
                IssueCategory.Grammar,
                IssueSeverity.Error,
                CanApplyAutomatically: true,
                RuleId: "ru.grammar.double-comparative",
                LinguisticCategory: LinguisticIssueCategory.AgreementError,
                Confidence: 0.95));
        }
    }

    /// <summary>
    /// True for a synthetic comparative, and deliberately false for a full adjective.
    /// </summary>
    /// <remarks>
    /// «более лучшее решение» is also wrong, but its repair is «лучшее решение» — the adjective
    /// stays and only «более» goes, which is what removing the whole «более X» span and putting
    /// back «X» does. The distinction that matters here is not whether the phrase is wrong; it
    /// is whether the second word declines, because the answer decides what the sentence reads
    /// like afterwards.
    /// </remarks>
    private bool IsSynthetic(string word)
    {
        var lower = word.ToLowerInvariant();
        if (Suppletive.Contains(lower)) return true;
        if (_morphology.PartOfSpeech(lower) == RussianPartOfSpeech.Comparative) return true;

        // A superlative already outranks «более»: «более лучшее решение» is the same pleonasm
        // and takes the same repair, and because the word is a declining adjective the phrase
        // that comes out — «лучшее решение» — still agrees with its noun. That is the
        // difference from «лучше решение», which is the damage this rule exists to prevent.
        return _morphology.IsSuperlative(lower);
    }

    /// <summary>
    /// Comparatives with no positive-degree stem, which the index files as particles or adverbs.
    /// </summary>
    private static readonly HashSet<string> Suppletive = new(StringComparer.OrdinalIgnoreCase)
    {
        "лучше", "хуже", "больше", "меньше",
    };
}
