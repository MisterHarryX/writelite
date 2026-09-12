using WriteLite.Language.Russian;
using WriteLite.Models;

namespace WriteLite.Services.Grammar;

/// <summary>
/// Agreement and government, decided from grammatical features rather than from patterns.
/// </summary>
/// <remarks>
/// <para>Four relations, all resolved by the same question — can these two words be describing
/// the same grammatical slot: a modifier with the noun it stands before, a predicate with its
/// subject, a noun phrase with the preposition that governs it, and a counted noun with its
/// numeral. Each is a closed structural relation, so none of them needs a phrase list; what
/// they need is a reliable answer to "what can this form be", which is
/// <see cref="RussianMorphology"/>.</para>
///
/// <para><b>Detecting a mismatch and knowing which side to repair are separate problems.</b>
/// «интерфейс более удобная» and «Профессор объяснил новую тема» are both disagreements, but
/// the first is repaired on the modifier and the second on the noun. The rule that separates
/// them is grammatical rather than statistical: gender and number are lexical or semantic
/// properties of the noun and cannot be the writer's slip, so a gender or number clash is
/// always repaired on the modifier; a clash in case alone means the phrase as a whole is in
/// the wrong case, and the governor — a preposition, a numeral — says which case is right.
/// When nothing in the sentence identifies a governor the finding is still reported, but as a
/// suggestion with no automatic application, because the direction of the repair is a
/// judgement rather than a deduction.</para>
///
/// <para><b>Everything here fails silent.</b> A word the morphology cannot analyse yields an
/// empty reading set, an empty reading set agrees with everything, and agreement with
/// everything produces no finding. Indeclinables, borrowings, surnames and irregular paradigms
/// therefore cost recall and never precision.</para>
/// </remarks>
public sealed class RussianAgreementAnalyzer : GrammarAnalyzerBase
{
    private readonly RussianMorphology _morphology;

    public RussianAgreementAnalyzer(RussianMorphology morphology)
        => _morphology = morphology ?? throw new ArgumentNullException(nameof(morphology));

    public static RussianAgreementAnalyzer? TryCreate(RussianFormIndex? index)
    {
        var morphology = RussianMorphology.TryCreate(index);
        return morphology is null ? null : new RussianAgreementAnalyzer(morphology);
    }

    public static RussianAgreementAnalyzer? TryCreate(RussianMorphology? morphology)
        => morphology is null ? null : new RussianAgreementAnalyzer(morphology);

    /// <summary>The longest modifier run the attributive rule will consider.</summary>
    private const int MaxModifiers = 3;

    /// <summary>How many tokens may separate a subject from its predicate.</summary>
    private const int SubjectWindow = 3;

    protected override void CollectCore(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        var tokens = TokensAtLeast(text, 2);

        CollectAttributive(text, tokens, protectedSpans, issues);
        CollectPrepositionGovernment(text, tokens, protectedSpans, issues);
        CollectVerbGovernment(text, tokens, protectedSpans, issues);
        CollectSubjectPredicate(text, tokens, protectedSpans, issues);
        CollectNumeralGovernment(text, tokens, protectedSpans, issues);
    }

    // ================= attributive agreement =================================

    private void CollectAttributive(
        string text,
        List<RussianToken> tokens,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        for (var i = 1; i < tokens.Count; i++)
        {
            var noun = tokens[i];
            if (!IsContentNoun(noun.Value)) continue;
            if (_morphology.IsProperName(noun.Value)) continue;

            var nounAnalysis = _morphology.Nominal(noun.Value);
            if (nounAnalysis.IsEmpty) continue;

            // The contiguous run of modifiers standing immediately before the noun.
            var first = i;
            while (first > 0
                   && i - first < MaxModifiers
                   && _morphology.IsModifier(tokens[first - 1].Value)
                   && RussianTokens.OnlySpacesBetween(text, tokens[first - 1], tokens[first]))
            {
                first--;
            }

            if (first == i) continue;

            // A modifier run that opens after a comma is a detached construction agreeing with
            // something earlier in the sentence, not an attribute of what follows it: in «На
            // полке стояла фотография, сделанная лет двадцать назад» the participle belongs to
            // «фотография» and the rule would otherwise try to agree it with «лет».
            if (OpensAfterPunctuation(text, tokens[first])) continue;

            for (var m = first; m < i; m++)
            {
                var modifier = tokens[m];
                if (ProtectedTextSpans.Overlaps(modifier.Start, modifier.Length, protectedSpans)) continue;

                // «Это отличная идея» is a predicative sentence whose subject is «это», not a
                // noun phrase whose attribute it is. The demonstratives that can head such a
                // sentence are excluded at a clause opening, where that reading is available.
                if (PredicativeDemonstratives.Contains(modifier.Value.ToLowerInvariant())
                    && OpensClause(text, tokens, m))
                {
                    continue;
                }

                // A synthetic comparative does not decline and is not an attribute.
                if (RussianMorphology.IsComparativeShaped(modifier.Value)) continue;

                var modifierAnalysis = _morphology.Nominal(modifier.Value);
                if (modifierAnalysis.IsEmpty) continue;
                if (modifierAnalysis.AgreesWith(nounAnalysis)) continue;

                // A genitive noun after a non-genitive word is that word's dependent, not a
                // head it modifies: «ключ доступа», «в переменной окружения», «сборная
                // Аргентины». Agreement is not expected across that relation and demanding it
                // reported all three as errors.
                if (OnlyGenitive(nounAnalysis) && !modifierAnalysis.CanBe(RuCase.Gen)) continue;

                var issue = ResolveAttributive(
                    text, tokens, first, i, m, modifierAnalysis, nounAnalysis, protectedSpans);
                if (issue is not null) issues.Add(issue);
            }
        }
    }

    private TextIssue? ResolveAttributive(
        string text,
        List<RussianToken> tokens,
        int runStart,
        int nounIndex,
        int modifierIndex,
        RuAnalysis modifierAnalysis,
        RuAnalysis nounAnalysis,
        IReadOnlyList<(int Start, int End)> protectedSpans)
    {
        var modifier = tokens[modifierIndex];
        var noun = tokens[nounIndex];
        var caseOnly = SharesCaseOnlyMismatch(modifierAnalysis, nounAnalysis);

        var governed = GovernedCases(text, tokens, runStart);

        if (!caseOnly)
        {
            // Gender or number: the noun is the fixed point.
            var quantifier = PluralQuantifiers.Contains(modifier.Value.ToLowerInvariant());
            if (quantifier && NumberMismatchOnly(modifierAnalysis, nounAnalysis))
            {
                return RepairNoun(text, noun, nounAnalysis, modifierAnalysis, protectedSpans, governed,
                    "Существительное согласуется с определением в числе",
                    $"Определение «{modifier.Value}» стоит во множественном числе — "
                    + $"существительное «{noun.Value}» должно стоять в том же числе.");
            }

            var target = ChooseTarget(nounAnalysis, governed);
            if (target is null) return null;

            var replacement = _morphology.Inflect(modifier.Value, target.Value);
            if (replacement is null) return null;

            return Build(
                modifier,
                text,
                RussianTokens.MatchLeadingCase(modifier.Value, replacement),
                "Определение не согласовано с существительным",
                $"«{modifier.Value}» и «{noun.Value}» должны совпадать в роде, числе и падеже: "
                + $"нужно «{RussianTokens.MatchLeadingCase(modifier.Value, replacement)} {noun.Value}».",
                confidence: 0.9,
                safe: true,
                protectedSpans);
        }

        // Case alone. A governor identifies which side moved.
        if (governed.Count > 0)
        {
            var nounFits = nounAnalysis.CanBeAny(governed);
            var modifierFits = modifierAnalysis.CanBeAny(governed);

            if (nounFits && !modifierFits)
            {
                var target = ChooseTarget(nounAnalysis, governed);
                if (target is null) return null;
                var replacement = _morphology.Inflect(modifier.Value, target.Value);
                if (replacement is null) return null;

                return Build(
                    modifier,
                    text,
                    RussianTokens.MatchLeadingCase(modifier.Value, replacement),
                    "Определение не согласовано в падеже",
                    $"«{noun.Value}» стоит в форме, которой требует предлог; определение "
                    + $"«{modifier.Value}» должно стоять в том же падеже.",
                    confidence: 0.88,
                    safe: true,
                    protectedSpans);
            }

            if (modifierFits && !nounFits)
            {
                return RepairNoun(text, noun, nounAnalysis, modifierAnalysis, protectedSpans, governed,
                    "Существительное не согласовано в падеже",
                    $"Определение «{modifier.Value}» стоит в падеже, которого требует предлог; "
                    + $"существительное должно стоять в том же падеже.");
            }

            return null;
        }

        // No governor: report, but do not choose a side automatically. The oblique reading is
        // offered because a noun left in its dictionary form is the commoner slip, and the
        // finding stays a suggestion precisely because that is a tendency and not a rule.
        if (IsNominativeOnly(nounAnalysis) && !IsNominativeOnly(modifierAnalysis))
        {
            var target = ChooseTarget(modifierAnalysis, []);
            if (target is null) return null;
            var replacement = _morphology.Inflect(
                noun.Value,
                target.Value with { Gender = nounAnalysis.LexicalGender });
            if (replacement is null) return null;

            return Build(
                noun,
                text,
                RussianTokens.MatchLeadingCase(noun.Value, replacement),
                "Определение и существительное в разных падежах",
                $"«{tokens[modifierIndex].Value}» стоит в косвенном падеже, а «{noun.Value}» — "
                + "в именительном. Вероятно, существительное осталось в начальной форме.",
                confidence: 0.62,
                safe: false,
                protectedSpans);
        }

        return null;
    }

    private TextIssue? RepairNoun(
        string text,
        RussianToken noun,
        RuAnalysis nounAnalysis,
        RuAnalysis modifierAnalysis,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        List<RuCase> governed,
        string title,
        string explanation)
    {
        var target = ChooseTarget(modifierAnalysis, governed);
        if (target is null) return null;

        var replacement = _morphology.Inflect(
            noun.Value,
            target.Value with { Gender = target.Value.Number == RuNumber.Plur ? RuGender.None : nounAnalysis.LexicalGender });
        if (replacement is null) return null;

        return Build(
            noun,
            text,
            RussianTokens.MatchLeadingCase(noun.Value, replacement),
            title,
            explanation,
            confidence: 0.85,
            safe: true,
            protectedSpans);
    }

    /// <summary>True when every reading pair matches on gender and number but never on case.</summary>
    private static bool SharesCaseOnlyMismatch(RuAnalysis modifier, RuAnalysis noun)
    {
        foreach (var a in modifier.Readings)
        {
            foreach (var b in noun.Readings)
            {
                if (a.Number != b.Number) continue;
                if (a.Number == RuNumber.Sing
                    && a.Gender != RuGender.None
                    && b.Gender != RuGender.None
                    && a.Gender != b.Gender)
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    private static bool NumberMismatchOnly(RuAnalysis modifier, RuAnalysis noun)
    {
        foreach (var a in modifier.Readings)
        {
            foreach (var b in noun.Readings)
            {
                if (a.Number != b.Number && a.Case == b.Case) return true;
            }
        }

        return false;
    }

    private static bool IsNominativeOnly(RuAnalysis analysis)
    {
        if (analysis.IsEmpty) return false;
        foreach (var reading in analysis.Readings)
        {
            if (reading.Case != RuCase.Nom) return false;
        }

        return true;
    }

    /// <summary>
    /// The reading a correction should be built toward.
    /// </summary>
    /// <remarks>
    /// Preference order is: a reading the governor allows, then a reading in an oblique case,
    /// then whatever there is. Preferring the oblique matters when the correct side is itself
    /// ambiguous — «документы» is nominative and accusative at once, and building toward the
    /// nominative would silently drop the accusative reading the sentence actually wanted.
    /// </remarks>
    private static RuReading? ChooseTarget(RuAnalysis analysis, IReadOnlyList<RuCase> governed)
    {
        if (analysis.IsEmpty) return null;

        if (governed.Count > 0)
        {
            foreach (var reading in analysis.Readings)
            {
                foreach (var allowed in governed)
                {
                    if (reading.Case == allowed) return reading;
                }
            }
        }

        // Unambiguous readings first: a single-reading analysis is a stronger target than one
        // slot chosen out of five.
        if (analysis.Readings.Count == 1) return analysis.Readings[0];

        foreach (var reading in analysis.Readings)
        {
            if (reading.Case != RuCase.Nom && reading.Case != RuCase.Acc) return reading;
        }

        return analysis.Readings[0];
    }

    // ================= preposition government ================================

    private void CollectPrepositionGovernment(
        string text,
        List<RussianToken> tokens,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            var allowed = AllowedCases(text, tokens, i);
            if (allowed.Count == 0) continue;

            // The head of the phrase: the first noun after the preposition, across at most
            // three modifiers. Anything else and the relation is not the one being claimed.
            var head = FindHead(text, tokens, i);

            if (head < 0) continue;

            var noun = tokens[head];
            if (ProtectedTextSpans.Overlaps(noun.Start, noun.Length, protectedSpans)) continue;
            if (_morphology.IsProperName(noun.Value) && noun.Value.Length <= 2) continue;
            if (NumeralNouns.Contains(noun.Value)) continue;

            if (!IsContentNoun(noun.Value)) continue;

            var analysis = _morphology.Nominal(noun.Value);
            if (analysis.IsEmpty) continue;
            if (analysis.CanBeAny(allowed)) continue;

            var number = analysis.Readings[0].Number;
            var target = new RuReading(allowed[0], number, number == RuNumber.Plur ? RuGender.None : analysis.LexicalGender);
            var replacement = _morphology.Inflect(noun.Value, target);
            if (replacement is null) continue;

            var preposition = tokens[i].Value.ToLowerInvariant();
            issues.Add(Build(
                noun,
                text,
                RussianTokens.MatchLeadingCase(noun.Value, replacement),
                "Ошибка в управлении падежом",
                $"Предлог «{preposition}» требует {CaseName(allowed[0])} падежа: "
                + $"{CaseQuestion(allowed[0])} — «{replacement}».",
                confidence: 0.87,
                safe: true,
                protectedSpans)!);
        }
    }

    /// <summary>The cases a preposition at <paramref name="index"/> may govern, or none.</summary>
    private List<RuCase> AllowedCases(string text, List<RussianToken> tokens, int index)
    {
        var word = tokens[index].Value.ToLowerInvariant();
        if (!Prepositions.TryGetValue(word, out var cases)) return [];

        // «о» governs the accusative only after a small closed set of verbs of impact —
        // «ударился о стену». Everywhere else it is prepositional, and admitting the
        // accusative everywhere would exempt «думаю о поездку» from the rule that catches it.
        if (word is "о" or "об" or "обо")
        {
            var impact = index > 0
                         && ImpactVerbs.Contains(_morphology.Lemma(tokens[index - 1].Value));
            return impact ? [RuCase.Loc, RuCase.Acc] : [RuCase.Loc];
        }

        // A preposition immediately followed by a comma or a bracket is not governing the
        // next word — «вопреки, кажется, новым правилам».
        if (index + 1 < tokens.Count && !RussianTokens.OnlySpacesBetween(text, tokens[index], tokens[index + 1]))
        {
            return [];
        }

        return [.. cases];
    }

    /// <summary>The cases required by a preposition standing before a modifier run.</summary>
    private List<RuCase> GovernedCases(string text, List<RussianToken> tokens, int runStart)
    {
        if (runStart == 0) return [];
        if (!RussianTokens.OnlySpacesBetween(text, tokens[runStart - 1], tokens[runStart])) return [];
        return AllowedCases(text, tokens, runStart - 1);
    }

    // ================= verb government =======================================

    /// <summary>
    /// «гордимся нашего города» → «нашим городом», «интересуется историю» → «историей».
    /// </summary>
    /// <remarks>
    /// <para>Case government is a property of the verb's lemma, so this is a lexicon of verbs
    /// rather than of phrases: an entry covers every form of the verb, every noun it can take,
    /// and every sentence they occur in. Fifty-odd entries is not a phrase table — it is the
    /// same kind of data as the preposition list above, which nobody would build any other
    /// way.</para>
    ///
    /// <para>Only verbs whose government is <em>unambiguous</em> are listed. «Ждать» takes the
    /// genitive or the accusative depending on definiteness, «просить» likewise; entries like
    /// those would flag correct text, so they are absent. The rule also stands down when the
    /// object is separated from the verb by a preposition, because at that point the
    /// preposition governs and the verb does not.</para>
    /// </remarks>
    private void CollectVerbGovernment(
        string text,
        List<RussianToken> tokens,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            var verb = _morphology.Verb(tokens[i].Value);
            if (verb is null) continue;

            var lemma = _morphology.Lemma(tokens[i].Value);
            if (lemma.Length == 0) continue;
            if (!VerbGovernment.TryGetValue(lemma, out var required)) continue;

            // A preposition between the verb and the noun takes over the government.
            if (Prepositions.ContainsKey(tokens[i + 1].Value.ToLowerInvariant())) continue;

            var head = FindHead(text, tokens, i);
            if (head < 0) continue;

            var noun = tokens[head];
            if (ProtectedTextSpans.Overlaps(noun.Start, noun.Length, protectedSpans)) continue;
            if (NumeralNouns.Contains(noun.Value)) continue;

            var analysis = _morphology.Nominal(noun.Value);
            if (analysis.IsEmpty || analysis.CanBe(required)) continue;

            // A nominative reading here usually means the noun is the subject, not the object.
            if (analysis.CanBe(RuCase.Nom) && head < i) continue;

            var number = analysis.Readings[0].Number;
            var target = new RuReading(
                required, number, number == RuNumber.Plur ? RuGender.None : analysis.LexicalGender);
            var replacement = _morphology.Inflect(noun.Value, target);
            if (replacement is null) continue;

            issues.Add(Build(
                noun,
                text,
                RussianTokens.MatchLeadingCase(noun.Value, replacement),
                "Ошибка в управлении падежом",
                $"Глагол «{lemma}» требует {CaseName(required)} падежа: "
                + $"{CaseQuestion(required)} — «{replacement}».",
                confidence: 0.85,
                safe: true,
                protectedSpans)!);
        }
    }

    // ================= subject and predicate =================================

    private void CollectSubjectPredicate(
        string text,
        List<RussianToken> tokens,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            var verb = _morphology.Verb(tokens[i].Value);
            if (verb is null || verb.Value.IsInfinitive) continue;
            if (ProtectedTextSpans.Overlaps(tokens[i].Start, tokens[i].Length, protectedSpans)) continue;

            // A word that is also a modifier of the noun after it is being used as one:
            // «собрал целую коллекцию» is not two predicates, and «целую» is not the first
            // person of «целовать» here whatever the index says about the spelling.
            if (i + 1 < tokens.Count
                && _morphology.IsModifier(tokens[i].Value)
                && RussianTokens.OnlySpacesBetween(text, tokens[i], tokens[i + 1])
                && _morphology.IsNoun(tokens[i + 1].Value)
                && _morphology.Nominal(tokens[i].Value).AgreesWith(_morphology.Nominal(tokens[i + 1].Value)))
            {
                continue;
            }

            var subject = FindSubject(text, tokens, i);
            if (subject is null) continue;

            var (number, gender, person) = subject.Value.Features;
            if (Agrees(verb.Value, number, gender, person)) continue;

            // A plural predicate with a singular candidate subject is also the shape of an
            // indefinite-personal sentence, where the singular noun is the object and there is
            // no subject at all: «В аэропорту задержали несколько рейсов», «Роман экранизировали
            // дважды». The two are separated by adjacency — a real subject of a plural verb sits
            // next to it, with at most its own modifiers in between — and by nothing else that
            // does not require parsing the sentence.
            if (verb.Value.Number == RuNumber.Plur
                && number == RuNumber.Sing
                && !OnlyModifiersBetween(text, tokens, i, subject.Value.Index))
            {
                continue;
            }

            var replacement = verb.Value.IsPast
                ? _morphology.InflectPast(tokens[i].Value, number, gender)
                : _morphology.InflectFinite(tokens[i].Value, number, person);
            if (replacement is null) continue;

            issues.Add(Build(
                tokens[i],
                text,
                RussianTokens.MatchLeadingCase(tokens[i].Value, replacement),
                "Сказуемое не согласовано с подлежащим",
                $"Подлежащее «{subject.Value.Word}» — {DescribeSubject(number, gender)}, "
                + $"поэтому сказуемое должно иметь форму «{replacement}».",
                confidence: 0.88,
                safe: true,
                protectedSpans)!);
        }
    }

    private readonly record struct Subject(
        string Word,
        int Index,
        (RuNumber Number, RuGender Gender, RuPerson Person) Features);

    /// <summary>True when every token strictly between the two indices is a modifier.</summary>
    private bool OnlyModifiersBetween(string text, List<RussianToken> tokens, int a, int b)
    {
        var (from, to) = a < b ? (a, b) : (b, a);
        if (!NothingButWordsBetween(text, tokens, from, to)) return false;

        for (var k = from + 1; k < to; k++)
        {
            if (!_morphology.IsModifier(tokens[k].Value)) return false;
        }

        return true;
    }

    /// <summary>
    /// The subject of the verb at <paramref name="verbIndex"/>, looking left then right.
    /// </summary>
    /// <remarks>
    /// <para><b>Only unambiguously nominative candidates qualify, and that restriction is what
    /// makes the rule safe.</b> Russian masculine inanimate and neuter nouns spell the
    /// accusative exactly like the nominative, so «Стол купили вчера» offers «Стол» as a
    /// singular subject for a plural verb — a false positive on perfectly correct text, since
    /// «стол» is the object of an indefinite-personal sentence. Requiring the candidate to have
    /// no accusative reading removes that whole class, at the cost of never checking agreement
    /// where the subject happens to be such a noun.</para>
    ///
    /// <para>Personal pronouns are admitted separately because their features are known exactly
    /// and none of them is ambiguous between nominative and accusative.</para>
    /// </remarks>
    private Subject? FindSubject(string text, List<RussianToken> tokens, int verbIndex)
    {
        for (var j = verbIndex - 1; j >= 0 && verbIndex - j <= SubjectWindow; j--)
        {
            if (!NothingButWordsBetween(text, tokens, j, verbIndex)) break;
            var candidate = AsSubject(tokens, j);
            if (candidate is not null) return candidate;
            if (Prepositions.ContainsKey(tokens[j].Value.ToLowerInvariant())) break;
        }

        for (var j = verbIndex + 1; j < tokens.Count && j - verbIndex <= SubjectWindow; j++)
        {
            if (!NothingButWordsBetween(text, tokens, verbIndex, j)) break;

            var right = tokens[j].Value.ToLowerInvariant();

            // Everything after a preposition belongs to its phrase. «Сборная Аргентины
            // выиграла в дополнительное время» offered «время» as a neuter subject, which is
            // how a correct sentence acquired a predicate-agreement error.
            if (Prepositions.ContainsKey(right)) break;

            // A conjunction ends this verb's clause, and everything past it belongs to the
            // next one. Without this the rightward scan reached across the boundary for a
            // pronoun and agreed the verb with the wrong clause's subject:
            //
            //   «объём данных растёт быстрее чем мы ожидали» → «растём»
            //   «период пройдёт спокойнее и мы сможем»       → «пройдём»
            //
            // Both were offered at confidence 0.88 with the auto-apply flag set, so "fix
            // everything safe" turned correct Russian into ungrammatical Russian. The
            // leftward scan needs no such guard: an inverted subject precedes its verb and
            // therefore precedes any conjunction that would separate them.
            if (RussianClauseBoundaryWords.Contains(right)) break;

            var candidate = AsSubject(tokens, j);
            if (candidate is not null) return candidate;
        }

        return null;
    }

    private Subject? AsSubject(List<RussianToken> tokens, int index)
    {
        var word = tokens[index].Value;
        var lower = word.ToLowerInvariant();

        if (PersonalPronouns.TryGetValue(lower, out var pronoun))
        {
            return new Subject(word, index, pronoun);
        }

        // A word governed by a preposition is inside a prepositional phrase, never a subject.
        if (index > 0 && Prepositions.ContainsKey(tokens[index - 1].Value.ToLowerInvariant())) return null;

        // The index records the single letters as nouns — they are the letters' names — so
        // «В аэропорту задержали …» offered «В» as a singular subject for a plural verb.
        // A preposition is not a subject whatever the index calls it.
        if (Prepositions.ContainsKey(lower)) return null;
        if (lower.Length <= 1) return null;

        if (!_morphology.IsNoun(word)) return null;

        var analysis = _morphology.Nominal(word);
        if (analysis.IsEmpty) return null;

        var nominative = false;
        foreach (var reading in analysis.Readings)
        {
            if (reading.Case == RuCase.Acc) return null;
            if (reading.Case == RuCase.Nom) nominative = true;
            else return null;
        }

        if (!nominative) return null;

        var number = analysis.Readings[0].Number;
        return new Subject(
            word,
            index,
            (number, number == RuNumber.Plur ? RuGender.None : analysis.LexicalGender, RuPerson.Third));
    }

    private static bool Agrees(RuVerbForm verb, RuNumber number, RuGender gender, RuPerson person)
    {
        if (verb.Number != number) return false;
        if (verb.IsPast)
        {
            return verb.Gender == RuGender.None || gender == RuGender.None || verb.Gender == gender;
        }

        return verb.Person == RuPerson.None || person == RuPerson.None || verb.Person == person;
    }

    /// <summary>
    /// The noun a preposition or numeral at <paramref name="from"/> governs, or -1.
    /// </summary>
    /// <remarks>
    /// A word can be both a modifier and a noun: «новым» belongs to the substantivised «новое»,
    /// so «в новым доме» offered «новым» as the head and reported it as a government error
    /// against the accusative, when the head is «доме» and the real error is the agreement.
    /// A word that is both is treated as a modifier exactly when another nominal follows it,
    /// which is the configuration in which it can be one.
    /// </remarks>
    private int FindHead(string text, List<RussianToken> tokens, int from)
    {
        var limit = Math.Min(tokens.Count, from + 1 + MaxModifiers + 1);
        for (var j = from + 1; j < limit; j++)
        {
            if (!RussianTokens.OnlySpacesBetween(text, tokens[j - 1], tokens[j])) break;

            if (!IsContentNoun(tokens[j].Value) && !_morphology.IsModifier(tokens[j].Value)) break;

            var isNoun = IsContentNoun(tokens[j].Value);
            var isModifier = _morphology.IsModifier(tokens[j].Value);

            if (isNoun && isModifier && j + 1 < limit
                && RussianTokens.OnlySpacesBetween(text, tokens[j], tokens[j + 1])
                && (_morphology.IsNoun(tokens[j + 1].Value) || _morphology.IsModifier(tokens[j + 1].Value)))
            {
                continue;
            }

            if (isNoun) return j;
            if (!isModifier) break;
        }

        return -1;
    }

    /// <summary>
    /// True when a token is a noun the agreement rules may reason about.
    /// </summary>
    /// <remarks>
    /// The index records the single letters and several prepositions as nouns — those are the
    /// letters' names and homonymous lexemes — so «Урожай зерновых в этом году» offered «в» as
    /// a masculine noun for «зерновых» to disagree with. A synthetic comparative is excluded
    /// for the other reason: «тише» is filed as a noun and does not decline, so «стал тише»
    /// was reported as a government error against the instrumental.
    /// </remarks>
    private bool IsContentNoun(string word)
    {
        if (word.Length <= 1) return false;
        if (Prepositions.ContainsKey(word)) return false;
        if (RussianMorphology.IsComparativeShaped(word)) return false;
        return _morphology.IsNoun(word);
    }

    /// <summary>True when the token is preceded by a comma, dash, bracket or colon.</summary>
    private static bool OpensAfterPunctuation(string text, RussianToken token)
    {
        for (var i = token.Start - 1; i >= 0; i--)
        {
            if (text[i] == ' ') continue;
            return text[i] is ',' or ';' or ':' or '—' or '–' or '(' or '«' or '"';
        }

        return false;
    }

    /// <summary>True when the token opens the text, a sentence or a comma-delimited clause.</summary>
    private static bool OpensClause(string text, List<RussianToken> tokens, int index)
    {
        var token = tokens[index];
        if (RussianTokens.IsSentenceStart(text, token.Start)) return true;
        if (OpensAfterPunctuation(text, token)) return true;

        // «Врач сказал, что это гарант…» — the subordinator opens a clause whose subject is
        // «это», with no comma to mark it.
        return index > 0 && Subordinators.Contains(tokens[index - 1].Value.ToLowerInvariant());
    }

    private static readonly HashSet<string> Subordinators = new(StringComparer.OrdinalIgnoreCase)
    {
        "что", "чтобы", "как", "если", "когда", "будто", "словно", "ведь", "значит", "и", "а", "но",
    };


    /// <summary>True when every reading is genitive.</summary>
    private static bool OnlyGenitive(RuAnalysis analysis)
    {
        if (analysis.IsEmpty) return false;
        foreach (var reading in analysis.Readings)
        {
            if (reading.Case != RuCase.Gen) return false;
        }

        return true;
    }

    /// <summary>True when only words and single spaces lie between two token indices.</summary>
    private static bool NothingButWordsBetween(string text, List<RussianToken> tokens, int from, int to)
    {
        for (var k = from; k < to; k++)
        {
            if (!RussianTokens.OnlySpacesBetween(text, tokens[k], tokens[k + 1])) return false;
        }

        return true;
    }

    // ================= numeral government ====================================

    /// <summary>
    /// «два раз» → «два раза», «три новые книга» → «три новые книги».
    /// </summary>
    /// <remarks>
    /// The numerals 2–4 and compounds ending in them take the genitive singular of the counted
    /// noun; 5 and above take the genitive plural. Only the small cardinals are handled,
    /// because they are the ones written as words often enough to be typed wrong, and because
    /// «пять книга» is caught by the same genitive requirement without needing the paradigm to
    /// distinguish singular from plural genitive reliably.
    /// </remarks>
    private void CollectNumeralGovernment(
        string text,
        List<RussianToken> tokens,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (!SmallCardinals.TryGetValue(tokens[i].Value.ToLowerInvariant(), out var number)) continue;

            var head = FindHead(text, tokens, i);

            if (head < 0) continue;

            var noun = tokens[head];
            if (ProtectedTextSpans.Overlaps(noun.Start, noun.Length, protectedSpans)) continue;

            var analysis = _morphology.Nominal(noun.Value);
            if (analysis.IsEmpty) continue;

            var satisfied = false;
            foreach (var reading in analysis.Readings)
            {
                if (reading.Case != RuCase.Gen) continue;
                if (reading.Number == number) { satisfied = true; break; }
            }

            // The genitive singular a numeral wants is spelt like the nominative plural for
            // most nouns — «две тетради», «три книги» — so the reading set of a correct phrase
            // already contains it and this check passes. A genitive of the *other* number is
            // not a pass: «два раз» has only the genitive plural, and that is the error.
            // The repair is still gated on the generator being able to produce the right form,
            // so a noun whose paradigm is not understood produces nothing rather than a guess.
            // For «пять» and above the required genitive plural is spelt like several other
            // slots, and time-of-day idioms — «девять утра» — use the genitive singular
            // legitimately. Any genitive reading is accepted there; for 2–4 the number of the
            // genitive is what the rule is about, so only the right one passes.
            if (satisfied) continue;
            if (number == RuNumber.Plur && analysis.CanBe(RuCase.Gen)) continue;

            var target = new RuReading(
                RuCase.Gen, number, number == RuNumber.Plur ? RuGender.None : analysis.LexicalGender);
            var replacement = _morphology.Inflect(noun.Value, target);
            if (replacement is null) continue;

            issues.Add(Build(
                noun,
                text,
                RussianTokens.MatchLeadingCase(noun.Value, replacement),
                "Существительное при числительном",
                $"После «{tokens[i].Value.ToLowerInvariant()}» существительное ставится в родительном падеже "
                + $"{(number == RuNumber.Sing ? "единственного" : "множественного")} числа — «{replacement}».",
                confidence: 0.86,
                safe: true,
                protectedSpans)!);
        }
    }

    // ================= shared =================================================

    private static TextIssue? Build(
        RussianToken token,
        string text,
        string replacement,
        string title,
        string explanation,
        double confidence,
        bool safe,
        IReadOnlyList<(int Start, int End)> protectedSpans)
    {
        if (ProtectedTextSpans.Overlaps(token.Start, token.Length, protectedSpans)) return null;

        var original = text.Substring(token.Start, token.Length);
        if (string.Equals(original, replacement, StringComparison.Ordinal)) return null;

        return new TextIssue(
            token.Start,
            token.Length,
            original,
            replacement,
            title,
            explanation,
            IssueCategory.Grammar,
            IssueSeverity.Error,
            CanApplyAutomatically: safe,
            RuleId: "ru.grammar.agreement",
            LinguisticCategory: LinguisticIssueCategory.AgreementError,
            Confidence: confidence);
    }

    private static string DescribeSubject(RuNumber number, RuGender gender)
        => number == RuNumber.Plur
            ? "множественное число"
            : gender switch
            {
                RuGender.Fem => "женский род, единственное число",
                RuGender.Neut => "средний род, единственное число",
                RuGender.Masc => "мужской род, единственное число",
                _ => "единственное число",
            };

    private static string CaseName(RuCase value) => value switch
    {
        RuCase.Nom => "именительного",
        RuCase.Gen => "родительного",
        RuCase.Dat => "дательного",
        RuCase.Acc => "винительного",
        RuCase.Ins => "творительного",
        _ => "предложного",
    };

    private static string CaseQuestion(RuCase value) => value switch
    {
        RuCase.Gen => "кого? чего?",
        RuCase.Dat => "кому? чему?",
        RuCase.Acc => "кого? что?",
        RuCase.Ins => "кем? чем?",
        RuCase.Loc => "о ком? о чём?",
        _ => "кто? что?",
    };

    // ---- closed lexical data -------------------------------------------------

    /// <summary>Prepositions and the cases they govern.</summary>
    /// <remarks>
    /// Government is a property of the preposition, not of the phrase, so this is a lexicon of
    /// about sixty entries rather than a table of phrases — it applies to every noun in the
    /// language, including ones neither this list nor the index has seen. Prepositions that
    /// govern more than one case list all of them: an alternative that is not listed becomes a
    /// false positive on correct text.
    /// </remarks>
    private static readonly Dictionary<string, RuCase[]> Prepositions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["без"] = [RuCase.Gen], ["безо"] = [RuCase.Gen],
            ["близ"] = [RuCase.Gen], ["вблизи"] = [RuCase.Gen], ["вдоль"] = [RuCase.Gen],
            ["вместо"] = [RuCase.Gen], ["вне"] = [RuCase.Gen], ["внутри"] = [RuCase.Gen],
            ["возле"] = [RuCase.Gen], ["вокруг"] = [RuCase.Gen], ["впереди"] = [RuCase.Gen],
            ["для"] = [RuCase.Gen], ["до"] = [RuCase.Gen], ["из"] = [RuCase.Gen],
            ["изо"] = [RuCase.Gen], ["из-за"] = [RuCase.Gen], ["из-под"] = [RuCase.Gen],
            ["кроме"] = [RuCase.Gen], ["мимо"] = [RuCase.Gen], ["около"] = [RuCase.Gen],
            ["от"] = [RuCase.Gen], ["ото"] = [RuCase.Gen], ["подле"] = [RuCase.Gen],
            ["после"] = [RuCase.Gen], ["против"] = [RuCase.Gen], ["ради"] = [RuCase.Gen],
            ["сверх"] = [RuCase.Gen], ["среди"] = [RuCase.Gen], ["у"] = [RuCase.Gen],
            ["насчёт"] = [RuCase.Gen], ["вследствие"] = [RuCase.Gen], ["ввиду"] = [RuCase.Gen],

            ["к"] = [RuCase.Dat], ["ко"] = [RuCase.Dat], ["благодаря"] = [RuCase.Dat],
            ["вопреки"] = [RuCase.Dat], ["согласно"] = [RuCase.Dat], ["наперекор"] = [RuCase.Dat],
            ["навстречу"] = [RuCase.Dat], ["подобно"] = [RuCase.Dat], ["вслед"] = [RuCase.Dat],

            ["про"] = [RuCase.Acc], ["сквозь"] = [RuCase.Acc], ["через"] = [RuCase.Acc],
            ["чрез"] = [RuCase.Acc],

            ["над"] = [RuCase.Ins], ["надо"] = [RuCase.Ins], ["перед"] = [RuCase.Ins],
            ["передо"] = [RuCase.Ins], ["пред"] = [RuCase.Ins],

            ["при"] = [RuCase.Loc],

            // Two cases and up: every reading has to be listed or correct text is flagged.
            ["в"] = [RuCase.Acc, RuCase.Loc], ["во"] = [RuCase.Acc, RuCase.Loc],
            ["на"] = [RuCase.Acc, RuCase.Loc],
            ["за"] = [RuCase.Acc, RuCase.Ins], ["под"] = [RuCase.Acc, RuCase.Ins],
            ["подо"] = [RuCase.Acc, RuCase.Ins],
            ["между"] = [RuCase.Ins, RuCase.Gen], ["меж"] = [RuCase.Ins, RuCase.Gen],
            ["с"] = [RuCase.Gen, RuCase.Ins, RuCase.Acc],
            ["со"] = [RuCase.Gen, RuCase.Ins, RuCase.Acc],
            ["по"] = [RuCase.Dat, RuCase.Acc, RuCase.Loc],
            ["о"] = [RuCase.Loc], ["об"] = [RuCase.Loc], ["обо"] = [RuCase.Loc],
        };

    /// <summary>
    /// Verbs whose object case is fixed, by lemma.
    /// </summary>
    /// <remarks>
    /// Deliberately restricted to verbs with one government. Anything that alternates —
    /// «ждать поезда»/«ждать поезд», «просить помощи»/«просить деньги», «хотеть чая»/«хотеть
    /// чай» — is left out, because an entry for it would report correct Russian as an error.
    /// </remarks>
    private static readonly Dictionary<string, RuCase> VerbGovernment =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Instrumental.
            ["гордиться"] = RuCase.Ins, ["интересоваться"] = RuCase.Ins,
            ["заниматься"] = RuCase.Ins, ["управлять"] = RuCase.Ins,
            ["руководить"] = RuCase.Ins, ["владеть"] = RuCase.Ins,
            ["пользоваться"] = RuCase.Ins, ["восхищаться"] = RuCase.Ins,
            ["любоваться"] = RuCase.Ins, ["наслаждаться"] = RuCase.Ins,
            ["дорожить"] = RuCase.Ins, ["рисковать"] = RuCase.Ins,
            ["жертвовать"] = RuCase.Ins, ["командовать"] = RuCase.Ins,
            ["обладать"] = RuCase.Ins, 
             
             
             
            ["заведовать"] = RuCase.Ins, ["злоупотреблять"] = RuCase.Ins,
            ["пренебрегать"] = RuCase.Ins, 

            // Genitive.
            ["бояться"] = RuCase.Gen, ["опасаться"] = RuCase.Gen,
            ["избегать"] = RuCase.Gen, ["достигать"] = RuCase.Gen,
            ["достичь"] = RuCase.Gen, ["лишиться"] = RuCase.Gen,
            ["лишать"] = RuCase.Gen, ["касаться"] = RuCase.Gen,
            ["коснуться"] = RuCase.Gen, ["добиваться"] = RuCase.Gen,
            ["добиться"] = RuCase.Gen, ["придерживаться"] = RuCase.Gen,
            ["стыдиться"] = RuCase.Gen, ["заслуживать"] = RuCase.Gen,

            // Dative.
            ["помогать"] = RuCase.Dat, ["помочь"] = RuCase.Dat,
            ["мешать"] = RuCase.Dat, ["помешать"] = RuCase.Dat,
            ["верить"] = RuCase.Dat, ["поверить"] = RuCase.Dat,
            ["доверять"] = RuCase.Dat, ["радоваться"] = RuCase.Dat,
            ["удивляться"] = RuCase.Dat, ["завидовать"] = RuCase.Dat,
            ["принадлежать"] = RuCase.Dat, ["соответствовать"] = RuCase.Dat,
            ["противоречить"] = RuCase.Dat, ["сочувствовать"] = RuCase.Dat,
            ["угрожать"] = RuCase.Dat, ["способствовать"] = RuCase.Dat,
            ["научиться"] = RuCase.Dat, ["учиться"] = RuCase.Dat,
        };

    /// <summary>Verbs after which «о» takes the accusative: «ударился о камень».</summary>
    private static readonly HashSet<string> ImpactVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ударить", "удариться", "биться", "разбиться", "опереться", "опираться",
        "споткнуться", "стукнуться", "точить", "вытереть", "вытирать", "запнуться",
    };

    private static readonly Dictionary<string, (RuNumber, RuGender, RuPerson)> PersonalPronouns =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["я"] = (RuNumber.Sing, RuGender.None, RuPerson.First),
            ["ты"] = (RuNumber.Sing, RuGender.None, RuPerson.Second),
            ["он"] = (RuNumber.Sing, RuGender.Masc, RuPerson.Third),
            ["она"] = (RuNumber.Sing, RuGender.Fem, RuPerson.Third),
            ["оно"] = (RuNumber.Sing, RuGender.Neut, RuPerson.Third),
            ["мы"] = (RuNumber.Plur, RuGender.None, RuPerson.First),
            ["вы"] = (RuNumber.Plur, RuGender.None, RuPerson.Second),
            ["они"] = (RuNumber.Plur, RuGender.None, RuPerson.Third),
        };

    /// <summary>Demonstratives that can be the subject of a predicative sentence.</summary>
    private static readonly HashSet<string> PredicativeDemonstratives = new(StringComparer.OrdinalIgnoreCase)
    {
        "это", "то", "всё", "все",
    };

    /// <summary>
    /// Words the index records as nouns but which are read as numerals in running text.
    /// </summary>
    /// <remarks>
    /// «сорок» is the magpie and the number forty; «тысяча» is a noun that behaves as a
    /// quantifier inside a compound numeral. Both were reported as government errors — «на
    /// сорок минут» wanted the accusative «сороки», «в тысяча девятьсот четырнадцатом году»
    /// wanted «тысячу» — because the noun reading is the only one the index has. Compound
    /// numerals do not decline their non-final parts, so these are simply not heads.
    /// </remarks>
    private static readonly HashSet<string> NumeralNouns = new(StringComparer.OrdinalIgnoreCase)
    {
        "сорок", "тысяча", "тысячи", "тысяч", "миллион", "миллиона", "миллионов",
        "миллиард", "миллиарда", "сто", "двести", "триста", "четыреста", "полтора",
        "полторы", "ноль", "нуль", "пара", "пары",
    };

    /// <summary>Determiners that are inherently plural and fix the noun's number.</summary>
    private static readonly HashSet<string> PluralQuantifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "все", "всех", "всеми", "многие", "многих", "многими", "некоторые", "некоторых",
        "оба", "обе", "обоих", "обеих", "никаких", "любые", "прочие", "остальные",
    };

    /// <summary>Cardinals written as words, with the number of the genitive they require.</summary>
    private static readonly Dictionary<string, RuNumber> SmallCardinals =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["два"] = RuNumber.Sing, ["две"] = RuNumber.Sing,
            ["три"] = RuNumber.Sing, ["четыре"] = RuNumber.Sing,
            ["пять"] = RuNumber.Plur, ["шесть"] = RuNumber.Plur, ["семь"] = RuNumber.Plur,
            ["восемь"] = RuNumber.Plur, ["девять"] = RuNumber.Plur, ["десять"] = RuNumber.Plur,
            ["несколько"] = RuNumber.Plur, ["много"] = RuNumber.Plur, ["мало"] = RuNumber.Plur,
        };
}
