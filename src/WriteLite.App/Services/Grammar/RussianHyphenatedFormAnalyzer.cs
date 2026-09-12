using System.Text.RegularExpressions;
using WriteLite.Language.Russian;
using WriteLite.Models;

namespace WriteLite.Services.Grammar;

/// <summary>
/// Compounds written as two words that are spelled with a hyphen: «во первых» → «во-первых»,
/// «всё таки» → «всё-таки», «По моему» → «По-моему».
/// </summary>
/// <remarks>
/// <para>Found by the Phase 7.5 real-world set, where the spelling category scored 0 of 4.
/// Each of these is a closed pair — the two words never stand next to each other in that order
/// for any other reason — with one exception that has to be handled by looking at the sentence
/// rather than by a list.</para>
///
/// <para><b>«по моему» is the exception.</b> It is correct, unhyphenated, whenever it is the
/// preposition governing a dative noun phrase: «по моему мнению», «по моему приказу». It is
/// «по-моему» when it is the parenthetical adverb. The two readings are separated by what
/// follows: a dative noun means the preposition, anything else means the adverb. That is a
/// question for the form index, and asking it is why this analyzer needs one.</para>
///
/// <para>The same shape applies to «по твоему», «по нашему» and «по вашему». «во первых» and
/// «всё таки» have no competing reading and are settled by the list alone.</para>
/// </remarks>
public sealed partial class RussianHyphenatedFormAnalyzer : GrammarAnalyzerBase
{
    private readonly RussianFormIndex? _index;

    public RussianHyphenatedFormAnalyzer(RussianFormIndex? index) => _index = index;

    /// <summary>Pairs with no competing reading.</summary>
    private static readonly (string Pattern, string Replacement)[] Unambiguous =
    [
        (@"\bво\s+первых\b", "во-первых"),
        (@"\bво\s+вторых\b", "во-вторых"),
        (@"\bв\s+третьих\b", "в-третьих"),
        (@"\bв\s+четвёртых\b", "в-четвёртых"),
        (@"\bв\s+четвертых\b", "в-четвертых"),
        (@"\bв\s+пятых\b", "в-пятых"),
        (@"\bвсё\s+таки\b", "всё-таки"),
        (@"\bвсе\s+таки\b", "все-таки"),
        (@"\bкое\s+как\b", "кое-как"),
        (@"\bточь\s+в\s+точь\b", "точь-в-точь"),
        (@"\bпо\s+прежнему\b", "по-прежнему"),
        (@"\bпо\s+настоящему\b", "по-настоящему"),
        (@"\bпо\s+разному\b", "по-разному"),
        (@"\bпо\s+другому\b", "по-другому"),
    ];

    /// <summary>Possessive adverbs that collide with «по» + dative.</summary>
    private static readonly (string Pattern, string Replacement)[] PossessiveAdverbs =
    [
        (@"\bпо\s+моему\b", "по-моему"),
        (@"\bпо\s+твоему\b", "по-твоему"),
        (@"\bпо\s+нашему\b", "по-нашему"),
        (@"\bпо\s+вашему\b", "по-вашему"),
        (@"\bпо\s+своему\b", "по-своему"),
    ];

    protected override void CollectCore(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        foreach (var (pattern, replacement) in Unambiguous)
        {
            Emit(text, pattern, replacement, protectedSpans, issues, requiresAdverbReading: false);
        }

        foreach (var (pattern, replacement) in PossessiveAdverbs)
        {
            Emit(text, pattern, replacement, protectedSpans, issues, requiresAdverbReading: true);
        }

        EmitSeparatedParticleTo(text, protectedSpans, issues);
    }

    /// <summary>
    /// Handles a separated indefinite particle (for example, «кто то сказал»).
    /// </summary>
    /// <remarks>
    /// The space is genuinely ambiguous in clauses such as «я знаю, что то решение верно»,
    /// where «то» modifies a following noun.  The rule therefore stands down after a comma
    /// and before a noun/adjective/pronoun phrase.  This is intentionally narrower than the
    /// joined-form catalog rule: a missed hyphen is preferable to rewriting a valid clause.
    /// </remarks>
    private void EmitSeparatedParticleTo(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        foreach (Match match in SeparatedParticleToRegex().Matches(text))
        {
            if (ProtectedTextSpans.Overlaps(match.Index, match.Length, protectedSpans)) continue;

            var before = match.Index - 1;
            while (before >= 0 && char.IsWhiteSpace(text[before])) before--;
            if (before >= 0 && text[before] == ',') continue;

            var following = RussianTokens.Split(text, match.Index + match.Length).FirstOrDefault();
            if (!string.IsNullOrEmpty(following.Value))
            {
                var onlySpacesBetween = true;
                for (var i = match.Index + match.Length; i < following.Start; i++)
                {
                    if (!char.IsWhiteSpace(text[i]))
                    {
                        onlySpacesBetween = false;
                        break;
                    }
                }

                if (onlySpacesBetween && _index is not null)
                {
                    var part = _index.GetInfo(following.Value).PartOfSpeech;
                    if (part is RussianPartOfSpeech.Noun
                        or RussianPartOfSpeech.AdjectiveFull
                        or RussianPartOfSpeech.Pronoun
                        or RussianPartOfSpeech.Numeral)
                    {
                        continue;
                    }
                }
            }

            var replacement = match.Groups[1].Value + "-то";
            replacement = RussianTokens.MatchLeadingCase(match.Value, replacement);
            issues.Add(new TextIssue(
                match.Index,
                match.Length,
                match.Value,
                replacement,
                "Частица «-то» пишется через дефис",
                $"В неопределённом местоимении «{replacement}» частица «-то» присоединяется дефисом.",
                IssueCategory.Orthography,
                IssueSeverity.Error,
                CanApplyAutomatically: true,
                RuleId: "ru.spelling.particle-to",
                LinguisticCategory: LinguisticIssueCategory.JoinedOrSeparateSpelling,
                Confidence: 0.93));
        }
    }

    private void Emit(
        string text,
        string pattern,
        string replacement,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues,
        bool requiresAdverbReading)
    {
        foreach (Match match in Regex.Matches(
            text, pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            WriteLiteDefaults.Analysis.StyleRegexMatchTimeout))
        {
            if (ProtectedTextSpans.Overlaps(match.Index, match.Length, protectedSpans)) continue;
            if (requiresAdverbReading && !IsAdverbReading(text, match.Index + match.Length)) continue;

            var fixedForm = RussianTokens.MatchLeadingCase(match.Value, replacement);
            if (string.Equals(match.Value, fixedForm, StringComparison.Ordinal)) continue;

            issues.Add(new TextIssue(
                match.Index,
                match.Length,
                match.Value,
                fixedForm,
                "Пишется через дефис",
                $"«{fixedForm}» — наречие, оно пишется через дефис.",
                IssueCategory.Orthography,
                IssueSeverity.Error,
                CanApplyAutomatically: true,
                RuleId: "ru.spelling.hyphenated-adverb",
                LinguisticCategory: LinguisticIssueCategory.JoinedOrSeparateSpelling,
                Confidence: 0.92));
        }
    }

    /// <summary>
    /// True when what follows is not a dative noun phrase — i.e. «по моему» is the adverb
    /// rather than the preposition.
    /// </summary>
    /// <remarks>
    /// Conservative in the safe direction: without an index, or with an unknown next word, the
    /// rule stands down. Proposing «по-моему мнению» would be a worse error than the one being
    /// corrected, and «по моему» in the prepositional reading is common in exactly the formal
    /// writing where a wrong hyphen is most visible.
    /// </remarks>
    private bool IsAdverbReading(string text, int afterMatch)
    {
        if (_index is null) return false;

        var tokens = RussianTokens.Split(text, afterMatch);
        if (tokens.Count == 0) return true;

        var next = tokens[0];

        // Only the immediately following word decides; anything further away belongs to a
        // different phrase.
        for (var i = afterMatch; i < next.Start; i++)
        {
            if (text[i] != ' ') return true;
        }

        var info = _index.GetInfo(next.Value.ToLowerInvariant());
        if (info.PartOfSpeech != RussianPartOfSpeech.Noun) return true;

        return !info.Tag.Contains("datv", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\b(кто|что|где|куда|когда|почему|зачем)\s+то\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex SeparatedParticleToRegex();
}
