using System.Text.RegularExpressions;
using WriteLite.Language.Russian;
using WriteLite.Models;

namespace WriteLite.Services.Grammar;

/// <summary>
/// Three clause boundaries that a closed rule can settle: the end of a fronted subordinate
/// clause, «то» in a paired conjunction, and «поэтому» joining two clauses.
/// </summary>
/// <remarks>
/// <para>All three come from the same measurement — the Phase 7.5 real-world set, where the
/// single largest group of missed punctuation was "the subordinate clause ended and nothing
/// marked it": «Несмотря на то что задача сложная мы её решили», «Когда я пришёл он уже
/// ушёл», «Если будет время я позвоню».</para>
///
/// <para><b>The subordinate rule does not take the first pronoun, and that is the whole
/// design.</b> «Когда я пришёл он уже ушёл» contains two: «я» is the subject of the
/// subordinate clause and «он» of the main one, and a rule that takes the first would write
/// «Когда, я пришёл он уже ушёл». Two positional facts separate them without parsing —
/// the main-clause subject is never adjacent to the conjunction, and it is followed by its
/// own predicate. Requiring at least two tokens of subordinate clause first, and a verb
/// within three tokens after, picks the right pronoun in every shape observed.</para>
///
/// <para>Only nominative personal pronouns qualify. «у нас было мало» contains «нас», which
/// is not a subject and must not attract a comma.</para>
///
/// <para>Every rule here stands down the moment the writer has punctuated anything in the
/// span. A sentence that already carries a comma is one where the writer made a decision, and
/// these rules are for sentences with no punctuation at all.</para>
/// </remarks>
public sealed partial class RussianClauseBoundaryAnalyzer
{
    private readonly RussianFormIndex? _index;

    public RussianClauseBoundaryAnalyzer(RussianFormIndex? index) => _index = index;

    /// <summary>Conjunctions that front a subordinate clause, longest first.</summary>
    private static readonly string[] FrontedConjunctions =
    [
        "несмотря на то что", "невзирая на то что", "в то время как", "после того как",
        "перед тем как", "прежде чем", "как только", "с тех пор как",
        "когда", "если", "хотя", "пока", "поскольку", "раз",
    ];

    /// <summary>Nominative personal pronouns — the only ones that can be a clause subject.</summary>
    private static readonly HashSet<string> NominativePronouns = new(StringComparer.OrdinalIgnoreCase)
    {
        "я", "ты", "он", "она", "оно", "мы", "вы", "они",
    };

    /// <summary>
    /// Words before «поэтому» that make it adverbial rather than a conjunction.
    /// </summary>
    /// <remarks>
    /// «Именно поэтому он ушёл» is one clause with an emphasised adverbial, not two clauses,
    /// and takes no comma. The list is closed on purpose: these are the only readings observed
    /// in the Phase 7.5 set that turn a conjunction into an intensified adverb.
    /// </remarks>
    private static readonly HashSet<string> IntensifiedConsequence = new(StringComparer.OrdinalIgnoreCase)
    {
        "именно", "вот", "и", "а", "но", "только",
    };

    /// <summary>The main-clause subject is never this close to the conjunction.</summary>
    private const int MinSubordinateTokens = 2;

    /// <summary>How far after the pronoun its predicate may be.</summary>
    private const int PredicateWindow = 3;

    public void Collect(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        CollectSubordinateClauseEnd(text, protectedSpans, issues);
        CollectPairedConjunction(text, protectedSpans, issues);
        CollectConsequenceConjunction(text, protectedSpans, issues);
    }

    // ---- «Когда я пришёл он уже ушёл» → «…пришёл, он уже ушёл» ------------

    private void CollectSubordinateClauseEnd(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        foreach (var (sentenceStart, sentenceLength) in Sentences(text))
        {
            if (sentenceLength < 20) continue;

            var sentence = text.Substring(sentenceStart, sentenceLength);
            var conjunction = FrontedConjunctions.FirstOrDefault(
                c => sentence.TrimStart().StartsWith(c, StringComparison.OrdinalIgnoreCase));
            if (conjunction is null) continue;

            var tokens = RussianTokens.Split(sentence);
            var conjunctionTokens = conjunction.Count(ch => ch == ' ') + 1;
            if (tokens.Count < conjunctionTokens + MinSubordinateTokens + 2) continue;

            for (var i = conjunctionTokens + MinSubordinateTokens; i < tokens.Count - 1; i++)
            {
                if (!NominativePronouns.Contains(tokens[i].Value)) continue;

                // The writer has already punctuated this stretch; leave it alone.
                if (sentence[..tokens[i].Start].IndexOfAny([',', ';', ':', '—', '(']) >= 0) break;
                if (!HasPredicateAfter(tokens, i)) continue;

                var insertAt = sentenceStart + tokens[i - 1].End;
                if (ProtectedTextSpans.Overlaps(insertAt, 1, protectedSpans)) break;

                var word = tokens[i - 1].Value;
                issues.Add(new TextIssue(
                    sentenceStart + tokens[i - 1].Start,
                    word.Length,
                    word,
                    word + ",",
                    "Придаточная часть отделяется запятой",
                    $"Придаточная часть с союзом «{conjunction}» заканчивается здесь: "
                    + $"перед главной частью «{tokens[i].Value} …» ставится запятая.",
                    IssueCategory.Punctuation,
                    IssueSeverity.Error,
                    CanApplyAutomatically: true,
                    RuleId: "ru.punctuation.subordinate-clause-end",
                    LinguisticCategory: LinguisticIssueCategory.PunctuationRecommendation,
                    Confidence: 0.87));
                break;
            }
        }
    }

    /// <summary>True when a verb follows the pronoun closely enough to be its predicate.</summary>
    private bool HasPredicateAfter(List<RussianToken> tokens, int pronounIndex)
    {
        if (_index is null) return false;

        var limit = Math.Min(tokens.Count, pronounIndex + 1 + PredicateWindow);
        for (var i = pronounIndex + 1; i < limit; i++)
        {
            var info = _index.GetInfo(tokens[i].Value.ToLowerInvariant());
            if (info.PartOfSpeech is RussianPartOfSpeech.Verb or RussianPartOfSpeech.ParticipleShort)
            {
                return true;
            }
        }

        return false;
    }

    // ---- «если … то …» → comma before «то» -------------------------------

    /// <remarks>
    /// «то» is also a demonstrative («я знал то место») and part of «то есть». The paired
    /// reading is separated by three facts together: a conditional conjunction earlier in the
    /// sentence, a verb immediately after, and no comma already in between. A demonstrative is
    /// followed by a noun, which is what the verb check excludes.
    /// </remarks>
    private void CollectPairedConjunction(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        if (_index is null) return;

        foreach (Match match in PairedToRegex().Matches(text))
        {
            var to = match.Groups["to"];
            if (ProtectedTextSpans.Overlaps(match.Index, match.Length, protectedSpans)) continue;

            var before = text[..to.Index];
            var sentenceStart = before.LastIndexOfAny(['.', '!', '?', '\n']) + 1;
            var clause = text[sentenceStart..to.Index];

            if (!ConditionalRegex().IsMatch(clause)) continue;
            if (clause.IndexOf(',', StringComparison.Ordinal) >= 0) continue;

            var after = RussianTokens.Split(text, to.Index + to.Length);
            if (after.Count == 0) continue;

            var next = _index.GetInfo(after[0].Value.ToLowerInvariant());
            if (next.PartOfSpeech is not (RussianPartOfSpeech.Verb or RussianPartOfSpeech.Infinitive))
            {
                continue;
            }

            issues.Add(new TextIssue(
                match.Index,
                1,
                text[match.Index].ToString(),
                text[match.Index] + ",",
                "Запятая перед «то»",
                "В двойном союзе «если… то…» запятая ставится перед второй частью.",
                IssueCategory.Punctuation,
                IssueSeverity.Error,
                CanApplyAutomatically: true,
                RuleId: "ru.punctuation.paired-conjunction-to",
                LinguisticCategory: LinguisticIssueCategory.PunctuationRecommendation,
                Confidence: 0.85));
        }
    }

    // ---- «… быстро поэтому задержек нет» → «быстро, поэтому …» ------------

    /// <remarks>
    /// Phase 5 left «поэтому» out of the adversative rule because «Именно поэтому он ушёл»
    /// takes no comma and separating the readings "needs to know whether a predicate precedes
    /// it". The intensifiers that produce that reading are a closed list — «именно», «вот»,
    /// «а», «и», «но» — and excluding them is what makes the rest safe.
    /// </remarks>
    private static void CollectConsequenceConjunction(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        foreach (Match match in ConsequenceRegex().Matches(text))
        {
            var previous = match.Groups["prev"];
            if (ProtectedTextSpans.Overlaps(match.Index, match.Length, protectedSpans)) continue;
            if (IntensifiedConsequence.Contains(previous.Value)) continue;

            var insertAfter = previous.Index + previous.Length - 1;
            issues.Add(new TextIssue(
                insertAfter,
                1,
                text[insertAfter].ToString(),
                text[insertAfter] + ",",
                "Запятая перед «поэтому»",
                "Союз «поэтому» присоединяет вторую часть сложного предложения, "
                + "перед ним ставится запятая.",
                IssueCategory.Punctuation,
                IssueSeverity.Warning,
                CanApplyAutomatically: true,
                RuleId: "ru.punctuation.consequence-comma",
                LinguisticCategory: LinguisticIssueCategory.PunctuationRecommendation,
                Confidence: 0.83));
        }
    }

    private static IEnumerable<(int Start, int Length)> Sentences(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('.' or '!' or '?' or '…' or '\n')) continue;
            if (i + 1 > start) yield return (start, i + 1 - start);
            start = i + 1;
        }

        if (start < text.Length) yield return (start, text.Length - start);
    }

    /// <summary>The last letter of the preceding word, then an isolated «то».</summary>
    [GeneratedRegex(@"\p{L}(?=\s+(?<to>то)\s+\p{L})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PairedToRegex();

    [GeneratedRegex(@"\b(если|когда|раз)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConditionalRegex();

    /// <summary>
    /// The whole word before «поэтому». The readings that take no comma are excluded in
    /// code against <see cref="IntensifiedConsequence"/>, which keeps the exception list in
    /// one place rather than duplicated into a lookbehind.
    /// </summary>
    [GeneratedRegex(
        @"(?<prev>\p{L}+)(?=\s+поэтому\s+\p{L})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsequenceRegex();
}
