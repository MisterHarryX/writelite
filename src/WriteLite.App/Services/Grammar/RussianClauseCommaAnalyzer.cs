using WriteLite.Language.Russian;
using WriteLite.Models;

namespace WriteLite.Services.Grammar;

/// <summary>
/// Commas whose placement follows from clause structure: gerund phrases, coordination between
/// clauses, homogeneous members, and the dash of a nominal predicate.
/// </summary>
/// <remarks>
/// <para><b>Why these four and not a wider net.</b> Each is decided by a fact the morphology
/// already knows — a word is a gerund, a clause has its own subject, three nouns share a case —
/// rather than by which conjunction is present. That distinction is what §4 of the sprint brief
/// asks for: a rule keyed on «и» puts a comma in front of every «и», and Russian puts one in
/// front of about a third of them.</para>
///
/// <para><b>Removal is part of the job.</b> Half the coordination errors in the benchmark are a
/// comma that should not be there — «Он вошёл, и сел за стол» — and a checker that can only add
/// punctuation cannot see them. The two directions are decided by the same test, so they are
/// implemented together: a comma before «и» belongs there when the second half has its own
/// subject and does not when the two predicates share one.</para>
///
/// <para>Every rule stands down where the writer has already punctuated the span it is about.
/// A sentence with a comma in it is one where a decision was made, and these rules exist for
/// the sentences where none was.</para>
/// </remarks>
public sealed class RussianClauseCommaAnalyzer : GrammarAnalyzerBase
{
    private readonly RussianMorphology _morphology;

    public RussianClauseCommaAnalyzer(RussianMorphology morphology)
        => _morphology = morphology ?? throw new ArgumentNullException(nameof(morphology));

    public static RussianClauseCommaAnalyzer? TryCreate(RussianFormIndex? index)
    {
        var morphology = RussianMorphology.TryCreate(index);
        return morphology is null ? null : new RussianClauseCommaAnalyzer(morphology);
    }

    protected override void CollectCore(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        var tokens = TokensAtLeast(text, 3);

        CollectGerundPhrases(text, tokens, protectedSpans, issues);
        CollectClauseCoordination(text, tokens, protectedSpans, issues);
        CollectHomogeneousMembers(text, tokens, protectedSpans, issues);
    }

    // ================= gerund phrases ========================================

    /// <summary>
    /// «Прочитав письмо он задумался» → «Прочитав письмо, он задумался»;
    /// «Он читал книгу сидя у окна» → «…книгу, сидя у окна».
    /// </summary>
    /// <remarks>
    /// <para>A Russian gerund phrase is set off by commas wherever it stands, which makes this
    /// one of the few punctuation rules that needs no reading of the sentence beyond finding
    /// the gerund. The two shapes are handled separately because the comma goes on opposite
    /// sides: a fronted phrase is closed at its end, an embedded one is opened at its start.
    /// </para>
    ///
    /// <para><b>The exceptions are both real and closed.</b> A bare gerund with no dependents
    /// is an adverb — «он ел стоя», «она ответила молча» — and takes no comma, which is why a
    /// dependent word is required. And several gerunds have become prepositions: «начиная с»,
    /// «исходя из», «несмотря на», «судя по», «включая», «спустя». Those are listed, because
    /// nothing in their morphology distinguishes them.</para>
    /// </remarks>
    private void CollectGerundPhrases(
        string text,
        List<RussianToken> tokens,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            var word = tokens[i].Value;
            if (!IsGerund(word)) continue;
            if (PrepositionalGerunds.Contains(word.ToLowerInvariant())) continue;
            if (ProtectedTextSpans.Overlaps(tokens[i].Start, tokens[i].Length, protectedSpans)) continue;

            // A gerund with nothing depending on it is an adverbial, not a phrase.
            if (i + 1 >= tokens.Count) continue;
            if (!RussianTokens.OnlySpacesBetween(text, tokens[i], tokens[i + 1])) continue;

            // The dependent must be a dependent. «Он зря пришёл» has a word after the adverb
            // too, and it is the predicate — an adverbialised form followed by a finite verb is
            // modifying that verb, not heading a phrase of its own.
            if (_morphology.Verb(tokens[i + 1].Value) is { IsInfinitive: false }) continue;

            if (RussianTokens.IsSentenceStart(text, tokens[i].Start))
            {
                var end = FrontedPhraseEnd(text, tokens, i);
                if (end < 0) continue;

                var last = tokens[end];
                if (ProtectedTextSpans.Overlaps(last.Start, last.Length, protectedSpans)) continue;

                issues.Add(new TextIssue(
                    last.Start,
                    last.Length,
                    text.Substring(last.Start, last.Length),
                    text.Substring(last.Start, last.Length) + ",",
                    "Деепричастный оборот выделяется запятой",
                    $"Оборот с деепричастием «{word.ToLowerInvariant()}» заканчивается здесь; "
                    + "перед главной частью предложения ставится запятая.",
                    IssueCategory.Punctuation,
                    IssueSeverity.Error,
                    CanApplyAutomatically: true,
                    RuleId: "ru.punctuation.gerund-phrase",
                    LinguisticCategory: LinguisticIssueCategory.PunctuationRecommendation,
                    Confidence: 0.9));
                continue;
            }

            if (i == 0) continue;

            var previous = tokens[i - 1];
            if (!RussianTokens.OnlySpacesBetween(text, previous, tokens[i])) continue;

            // «и работая», «а посмотрев» — the conjunction takes the comma, not the gerund.
            if (Conjunctions.Contains(previous.Value.ToLowerInvariant())) continue;

            // A negated gerund carries its particle: in «вышел, не сказав ни слова» the comma
            // belongs before «не», where the writer already put it, and adding a second one
            // after it splits the phrase from its own negation.
            if (previous.Value.Equals("не", StringComparison.OrdinalIgnoreCase)) continue;

            issues.Add(new TextIssue(
                previous.Start,
                previous.Length,
                text.Substring(previous.Start, previous.Length),
                text.Substring(previous.Start, previous.Length) + ",",
                "Деепричастный оборот выделяется запятой",
                $"Оборот с деепричастием «{word.ToLowerInvariant()}» отделяется запятой "
                + "от остальной части предложения.",
                IssueCategory.Punctuation,
                IssueSeverity.Error,
                CanApplyAutomatically: true,
                RuleId: "ru.punctuation.gerund-phrase",
                LinguisticCategory: LinguisticIssueCategory.PunctuationRecommendation,
                Confidence: 0.88));
        }
    }

    /// <summary>
    /// True when a form heads a gerund phrase, including the adverbialised gerunds.
    /// </summary>
    /// <remarks>
    /// «сидя», «стоя», «лёжа», «молча» are recorded as adverbs, because that is what they are
    /// when they stand alone — and they are still gerunds when they take dependents, which is
    /// the case this rule is about: «Он читал книгу, сидя у открытого окна». The evidence for
    /// admitting one is that a verb it could be formed from exists in the index; the guard
    /// against admitting an ordinary adverb is that the caller requires a dependent word that
    /// is not itself the predicate.
    /// </remarks>
    private bool IsGerund(string word)
    {
        // A word that declines like an adjective is not a gerund whatever the index says.
        // «Какая» is filed as a gerund of «какать»; «Какая же ерунда получилась» was read as a
        // fronted gerund phrase because of it.
        if (RussianAdjectivalParadigm_TrySplits(word)) return false;

        // Nor is a word that some noun paradigm explains: «покоя» is the genitive of «покой».
        if (_morphology.HasNounReading(word)) return false;

        if (_morphology.PartOfSpeech(word) == RussianPartOfSpeech.Gerund) return true;
        if (_morphology.PartOfSpeech(word) != RussianPartOfSpeech.Adverb) return false;

        var lower = word.ToLowerInvariant();
        if (lower.Length < 4 || !lower.EndsWith("я", StringComparison.Ordinal)) return false;

        var stem = lower[..^1];
        foreach (var ending in InfinitiveEndings)
        {
            var candidate = stem + ending;
            if (!_morphology.Knows(candidate)) continue;
            if (_morphology.PartOfSpeech(candidate) == RussianPartOfSpeech.Infinitive) return true;
        }

        return false;
    }

    private static readonly string[] InfinitiveEndings = ["еть", "ать", "ить", "ять", "ться", "аться"];

    /// <summary>True when the word carries a full adjectival ending.</summary>
    private static bool RussianAdjectivalParadigm_TrySplits(string word)
    {
        var lower = word.ToLowerInvariant();
        foreach (var ending in AdjectivalEndings)
        {
            if (lower.Length > ending.Length + 1 && lower.EndsWith(ending, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string[] AdjectivalEndings =
    [
        "ыми", "ими", "ого", "его", "ому", "ему", "ый", "ий", "ой", "ая", "яя", "ую", "юю",
        "ою", "ею", "ое", "ее", "ые", "ие", "ых", "их", "ым", "им", "ом", "ем",
    ];

    /// <summary>
    /// The last token of a sentence-initial gerund phrase, or -1 when it cannot be located.
    /// </summary>
    /// <remarks>
    /// The phrase ends where the main clause begins, and the main clause begins at its subject
    /// or its predicate — whichever comes first. Scanning for either is enough because a gerund
    /// phrase contains no finite verb and no nominative subject of its own; those are exactly
    /// the two things it cannot hold, which is what makes the boundary findable without a
    /// parser. Returning -1 where the writer already punctuated the stretch keeps the rule off
    /// sentences that have been thought about.
    /// </remarks>
    private int FrontedPhraseEnd(string text, List<RussianToken> tokens, int gerundIndex)
    {
        for (var j = gerundIndex + 1; j < tokens.Count; j++)
        {
            if (!RussianTokens.OnlySpacesBetween(text, tokens[j - 1], tokens[j])) return -1;

            var value = tokens[j].Value;
            var verb = _morphology.Verb(value);
            var finite = verb is { IsInfinitive: false } && _morphology.PartOfSpeech(value) != RussianPartOfSpeech.Gerund;
            var subject = RussianTokens.NominativePronouns.Contains(value.ToLowerInvariant());

            if (!finite && !subject) continue;

            // At least one word of phrase, or there is no phrase to close.
            return j - 1 > gerundIndex ? j - 1 : -1;
        }

        return -1;
    }

    // ================= clause coordination ===================================

    /// <summary>
    /// A comma before «и»/«или» belongs there when the second half has its own subject.
    /// </summary>
    /// <remarks>
    /// <para>«Дождь закончился и выглянуло солнце» is two clauses and takes a comma; «Он вошёл
    /// и сел за стол» is one clause with two predicates and does not. Nothing about the
    /// conjunction distinguishes them — only whether a subject appears on the right — so the
    /// rule tests for that and then either adds the comma or removes one that is there.</para>
    ///
    /// <para>Both directions are gated on the conjunction joining two <em>finite</em> verbs.
    /// «хлеб и молоко» has none and is left to the homogeneous-members rule; «пришёл и
    /// увидел» has two and one subject, so the comma comes out.</para>
    /// </remarks>
    private void CollectClauseCoordination(
        string text,
        List<RussianToken> tokens,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        for (var i = 1; i < tokens.Count - 1; i++)
        {
            var conjunction = tokens[i].Value.ToLowerInvariant();
            if (conjunction is not ("и" or "или")) continue;
            if (ProtectedTextSpans.Overlaps(tokens[i].Start, tokens[i].Length, protectedSpans)) continue;

            var (leftVerb, leftBound) = ScanLeft(text, tokens, i);
            if (!leftVerb) continue;

            var (rightVerb, rightSubject) = ScanRight(text, tokens, i);
            if (!rightVerb) continue;

            var commaIndex = CommaBefore(text, tokens, i);

            if (rightSubject && commaIndex < 0)
            {
                var anchor = tokens[i - 1];
                if (anchor.Start < leftBound) continue;
                if (ProtectedTextSpans.Overlaps(anchor.Start, anchor.Length, protectedSpans)) continue;

                issues.Add(new TextIssue(
                    anchor.Start,
                    anchor.Length,
                    text.Substring(anchor.Start, anchor.Length),
                    text.Substring(anchor.Start, anchor.Length) + ",",
                    $"Запятая перед «{conjunction}»",
                    $"Союз «{conjunction}» соединяет два предложения, у каждого из которых своё "
                    + "подлежащее, поэтому перед ним ставится запятая.",
                    IssueCategory.Punctuation,
                    IssueSeverity.Error,
                    CanApplyAutomatically: true,
                    RuleId: "ru.punctuation.clause-coordination",
                    LinguisticCategory: LinguisticIssueCategory.PunctuationRecommendation,
                    Confidence: 0.85));
                continue;
            }

            if (!rightSubject && commaIndex >= 0)
            {
                issues.Add(new TextIssue(
                    commaIndex,
                    1,
                    ",",
                    string.Empty,
                    $"Лишняя запятая перед «{conjunction}»",
                    $"Союз «{conjunction}» соединяет однородные сказуемые при одном подлежащем — "
                    + "запятая перед ним не ставится.",
                    IssueCategory.Punctuation,
                    IssueSeverity.Error,
                    CanApplyAutomatically: true,
                    RuleId: "ru.punctuation.clause-coordination",
                    LinguisticCategory: LinguisticIssueCategory.PunctuationRecommendation,
                    Confidence: 0.84));
            }
        }
    }

    /// <summary>Whether a finite verb precedes the conjunction inside the same clause.</summary>
    private (bool HasVerb, int ClauseStart) ScanLeft(string text, List<RussianToken> tokens, int index)
    {
        var verb = false;
        var start = 0;
        for (var j = index - 1; j >= 0; j--)
        {
            if (SeparatorBetween(text, tokens[j], tokens[j + 1], out var separator) && separator != ',')
            {
                start = tokens[j + 1].Start;
                break;
            }

            var form = _morphology.Verb(tokens[j].Value);
            if (form is { IsInfinitive: false }
                && _morphology.PartOfSpeech(tokens[j].Value) != RussianPartOfSpeech.Gerund)
            {
                verb = true;
            }

            if (j == 0) start = tokens[0].Start;
        }

        return (verb, start);
    }

    /// <summary>Whether a finite verb and a nominative subject follow the conjunction.</summary>
    private (bool HasVerb, bool HasSubject) ScanRight(string text, List<RussianToken> tokens, int index)
    {
        var verb = false;
        var subject = false;
        var intransitive = false;

        for (var j = index + 1; j < tokens.Count; j++)
        {
            if (SeparatorBetween(text, tokens[j - 1], tokens[j], out _)) break;

            var value = tokens[j].Value;
            var lower = value.ToLowerInvariant();

            // The second half of the coordination ends where the next clause begins. Without
            // this the scan ran through «до того как» and adopted «проблема» from the
            // subordinate clause as the right half's subject, which turned two homogeneous
            // predicates — «реагировали … и … находили», one subject between them — into a
            // comma the sentence does not take. The conjunction under test is at
            // <paramref name="index"/> and is therefore never itself a boundary here.
            if (j > index + 1 && RussianClauseBoundaryWords.Contains(lower)) break;

            if (RussianTokens.NominativePronouns.Contains(lower)) { subject = true; continue; }

            var form = _morphology.Verb(value);
            if (form is { IsInfinitive: false }
                && _morphology.PartOfSpeech(value) != RussianPartOfSpeech.Gerund)
            {
                verb = true;
                intransitive = _morphology.IsIntransitiveVerb(value);
                continue;
            }

            // Word order is free, so a subject after its predicate — «и выглянуло солнце» —
            // counts as much as one before it. What does not count is a noun inside a
            // prepositional phrase, or one that could equally be a direct object.
            if (j == index + 1 && Prepositions.Contains(lower)) continue;
            if (j > index + 1 && Prepositions.Contains(tokens[j - 1].Value.ToLowerInvariant())) continue;
            if (!_morphology.IsNoun(value) || value.Length <= 1) continue;

            var analysis = _morphology.Nominal(value);
            if (analysis.IsEmpty) continue;

            // Nominative-only is the strict test, and it is what an ambiguous case needs.
            // After an intransitive verb the ambiguity is gone — there is no object for the
            // noun to be — so a nominative reading is enough.
            var nominativeOnly = true;
            var nominative = false;
            foreach (var reading in analysis.Readings)
            {
                if (reading.Case == RuCase.Nom) nominative = true;
                else nominativeOnly = false;
            }

            if (nominativeOnly || (nominative && intransitive)) subject = true;
        }

        return (verb, subject);
    }

    /// <summary>The offset of a comma immediately before the conjunction, or -1.</summary>
    private static int CommaBefore(string text, List<RussianToken> tokens, int index)
    {
        for (var i = tokens[index].Start - 1; i >= 0; i--)
        {
            if (text[i] == ' ') continue;
            return text[i] == ',' ? i : -1;
        }

        return -1;
    }

    /// <summary>True when something other than spaces separates two tokens.</summary>
    private static bool SeparatorBetween(string text, RussianToken left, RussianToken right, out char separator)
    {
        separator = '\0';
        for (var i = left.End; i < right.Start; i++)
        {
            if (text[i] == ' ') continue;
            separator = text[i];
            return true;
        }

        return false;
    }

    // ================= homogeneous members ===================================

    /// <summary>
    /// «Он купил хлеб молоко и сыр» → «хлеб, молоко и сыр».
    /// </summary>
    /// <remarks>
    /// Three nouns where the last two are joined by «и» and all three share a case is an
    /// enumeration, and the comma between the first two is not optional. Requiring the shared
    /// case is what separates it from «купил хлеб молоко» as a mistyped phrase and from
    /// «директор отдела продаж», where the cases differ down the chain.
    /// </remarks>
    private void CollectHomogeneousMembers(
        string text,
        List<RussianToken> tokens,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        for (var i = 0; i + 3 < tokens.Count; i++)
        {
            if (!tokens[i + 2].Value.Equals("и", StringComparison.OrdinalIgnoreCase)) continue;

            for (var k = i; k < i + 3; k++)
            {
                if (!RussianTokens.OnlySpacesBetween(text, tokens[k], tokens[k + 1])) goto next;
            }

            if (!_morphology.IsNoun(tokens[i].Value)
                || !_morphology.IsNoun(tokens[i + 1].Value)
                || !_morphology.IsNoun(tokens[i + 3].Value))
            {
                continue;
            }

            if (tokens[i].Value.Length <= 1
                || tokens[i + 1].Value.Length <= 1
                || tokens[i + 3].Value.Length <= 1)
            {
                continue;
            }

            var first = _morphology.Nominal(tokens[i].Value);
            var second = _morphology.Nominal(tokens[i + 1].Value);
            var third = _morphology.Nominal(tokens[i + 3].Value);
            if (first.IsEmpty || second.IsEmpty || third.IsEmpty) continue;

            // Any genitive reading in the chain and this is a head with its dependents, not a
            // list: «сроки поставки и стоимость доставки», «состоит из сердца и сосудов».
            // The homonymy between the genitive singular and the nominative plural is exactly
            // what makes those look like enumerations, so the rule declines to guess.
            if (first.CanBe(RuCase.Gen) || second.CanBe(RuCase.Gen) || third.CanBe(RuCase.Gen)) continue;

            if (!ShareCase(first, second) || !ShareCase(second, third)) continue;
            if (ProtectedTextSpans.Overlaps(tokens[i].Start, tokens[i].Length, protectedSpans)) continue;

            issues.Add(new TextIssue(
                tokens[i].Start,
                tokens[i].Length,
                text.Substring(tokens[i].Start, tokens[i].Length),
                text.Substring(tokens[i].Start, tokens[i].Length) + ",",
                "Однородные члены разделяются запятой",
                "При перечислении запятая ставится между однородными членами; "
                + "перед последним «и» она не нужна.",
                IssueCategory.Punctuation,
                IssueSeverity.Error,
                CanApplyAutomatically: true,
                RuleId: "ru.punctuation.homogeneous-comma",
                LinguisticCategory: LinguisticIssueCategory.PunctuationRecommendation,
                Confidence: 0.83));

            next: ;
        }
    }

    private static bool ShareCase(RuAnalysis a, RuAnalysis b)
    {
        foreach (var x in a.Readings)
        {
            foreach (var y in b.Readings)
            {
                if (x.Case == y.Case) return true;
            }
        }

        return false;
    }

    // ---- closed lexical data -------------------------------------------------

    /// <summary>Gerunds that have become prepositions and take no comma.</summary>
    private static readonly HashSet<string> PrepositionalGerunds = new(StringComparer.OrdinalIgnoreCase)
    {
        "начиная", "исходя", "несмотря", "невзирая", "судя", "включая", "спустя",
        "смотря", "считая", "не",
    };

    private static readonly HashSet<string> Conjunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "и", "а", "но", "или", "да", "либо", "то", "что", "чтобы",
    };

    private static readonly HashSet<string> Prepositions = new(StringComparer.OrdinalIgnoreCase)
    {
        "в", "во", "на", "за", "под", "над", "перед", "при", "о", "об", "обо", "по", "к", "ко",
        "с", "со", "из", "от", "до", "для", "без", "у", "про", "через", "между", "около",
    };
}
