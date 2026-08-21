using System.Text.RegularExpressions;
using WriteLite.Language.Russian;
using WriteLite.Models;
using WriteLite.Services.Grammar;
using WriteLite.Services.Rules;

namespace WriteLite.Services;

public sealed partial class RuleBasedAnalyzer : ITextAnalyzer
{
    private readonly RuleCatalog _catalog;
    private readonly RussianVocativeAnalyzer? _vocative;
    private readonly RussianCaseGovernmentAnalyzer? _caseGovernment;
    private readonly RussianHyphenatedFormAnalyzer _hyphenated;
    private readonly RussianClauseBoundaryAnalyzer _clauseBoundary;
    private readonly RussianAgreementAnalyzer? _agreement;
    private readonly RussianClauseCommaAnalyzer? _clauseCommas;
    private readonly RussianComparativeAnalyzer? _comparative;

    public RuleBasedAnalyzer()
        : this(RuleCatalog.LoadDefault())
    {
    }

    public RuleBasedAnalyzer(RuleCatalog catalog)
        : this(catalog, formIndex: null)
    {
    }

    /// <param name="formIndex">
    /// WriteLite's Russian form index. Optional: the rules that need to ask what case a word
    /// is in — vocative detection and dative government — are skipped entirely when it is
    /// absent, and every other rule behaves exactly as before.
    /// </param>
    /// <remarks>
    /// The index is passed in rather than loaded here because the application already has one
    /// open inside <c>LocalSpellChecker</c>. A second copy would be a second 3.09 M-form graph
    /// resident for the lifetime of the process, which is the kind of duplication §39 of the
    /// Phase 7 brief asks to keep out of the release build.
    /// </remarks>
    public RuleBasedAnalyzer(RuleCatalog catalog, RussianFormIndex? formIndex)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _vocative = RussianVocativeAnalyzer.TryCreate(formIndex);
        _caseGovernment = RussianCaseGovernmentAnalyzer.TryCreate(formIndex);
        // Runs with or without an index: the unambiguous pairs need no lexicon, and the
        // «по моему» family stands down when it cannot check the following word.
        _hyphenated = new RussianHyphenatedFormAnalyzer(formIndex);
        _clauseBoundary = new RussianClauseBoundaryAnalyzer(formIndex);
        // Agreement and government need the full grammatical analysis of a form, not the one
        // reading the index records, so this layer is absent without the index rather than
        // degraded — a half-informed agreement rule rewrites correct text.
        _agreement = RussianAgreementAnalyzer.TryCreate(formIndex);
        // Gerund, coordination and enumeration commas all decide from morphology —
        // which word is a gerund, whether the second clause has its own subject — so
        // like the agreement layer this one is absent rather than degraded without an index.
        _clauseCommas = RussianClauseCommaAnalyzer.TryCreate(formIndex);
        // Owning this span deterministically is what stops the model being asked about it,
        // which is where every historical destructive correction in this repository began.
        _comparative = RussianComparativeAnalyzer.TryCreate(formIndex);
    }

    public IReadOnlyList<TextIssue> Analyze(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var protectedSpans = ProtectedTextSpans.Find(text);
        var issues = new List<TextIssue>();

        foreach (var compiled in _catalog.RegexRules)
        {
            var rule = compiled.Definition;
            foreach (Match match in compiled.Pattern.Matches(text))
            {
                if (ProtectedTextSpans.Overlaps(match.Index, match.Length, protectedSpans))
                {
                    continue;
                }

                var replacement = PreserveLeadingCase(
                    match.Value, match.Result(rule.Implementation.Replacement!));
                if (string.Equals(match.Value, replacement, StringComparison.Ordinal))
                {
                    continue;
                }

                issues.Add(new TextIssue(
                    match.Index,
                    match.Length,
                    match.Value,
                    replacement,
                    rule.Title,
                    rule.DetailedExplanation,
                    rule.IssueCategory,
                    rule.Severity,
                    CanApplyAutomatically: rule.SafeToApply,
                    RuleId: rule.RuleId,
                    LinguisticCategory: rule.Category));
            }
        }

        foreach (Match match in RepeatedWordRegex().Matches(text))
        {
            if (ProtectedTextSpans.Overlaps(match.Index, match.Length, protectedSpans))
            {
                continue;
            }

            var word = match.Groups[1].Value;
            issues.Add(new TextIssue(
                match.Index,
                match.Length,
                match.Value,
                word,
                "Повтор слова",
                "Одно и то же слово написано два раза подряд.",
                IssueCategory.Style,
                IssueSeverity.Warning,
                CanApplyAutomatically: true,
                RuleId: "ru.style.repeated-word",
                LinguisticCategory: LinguisticIssueCategory.RepeatedWord));
        }

        AddSentenceCapitalizationIssues(text, protectedSpans, issues);
        AddMissingTerminalPunctuationHints(text, protectedSpans, issues);
        // Reliable grammar/punctuation (priority over soft heuristics).
        ChtobyAnalyzer.Collect(text, protectedSpans, issues);
        ReflexiveVerbFormAnalyzer.Collect(text, protectedSpans, issues);
        AddMissingCommaAfterIntroductoryWord(text, protectedSpans, issues);
        AddMissingCommaBeforeAdversative(text, protectedSpans, issues);
        _vocative?.Collect(text, protectedSpans, issues);
        _caseGovernment?.Collect(text, protectedSpans, issues);
        _agreement?.Collect(text, protectedSpans, issues);
        _clauseCommas?.Collect(text, protectedSpans, issues);
        _comparative?.Collect(text, protectedSpans, issues);
        _hyphenated.Collect(text, protectedSpans, issues);
        _clauseBoundary.Collect(text, protectedSpans, issues);
        // Soft heuristics — never auto-apply; suppressed when reliable rules already cover zone.
        AddPossibleSubordinateClauseHints(text, protectedSpans, issues);
        AddPossibleMissingCommaAfterIntro(text, protectedSpans, issues);
        AddLongSentenceHints(text, protectedSpans, issues);
        SuppressControversialAndSoftDuplicates(issues);

        // Deduplicate identical ranges from overlapping rules.
        return Deduplicate(issues)
            .Select(ApplyCatalogMetadata)
            .Select(SanitizeIssueOutput)
            .Where(i => i is not null)
            .Select(i => i!)
            .OrderBy(issue => issue.Start)
            .ThenByDescending(issue => issue.Severity)
            .ToList();
    }

    /// <summary>
    /// Resolves an issue against the rule pack, which owns classification and safety.
    /// </summary>
    /// <remarks>
    /// <para>Category, severity and the auto-apply permission always come from the catalog.
    /// That is the integrity property the pack exists for: a rule cannot classify itself
    /// differently at runtime from how it is documented, and it can never grant itself an
    /// auto-apply the pack withholds.</para>
    ///
    /// <para><b>Title and explanation are the exception, and they were not before.</b> This
    /// method used to overwrite both unconditionally, which meant an analyzer that had built a
    /// specific reason — naming the writer's own words — had it replaced by the pack's generic
    /// sentence on the way to the UI. «Обращение «дорогой друг» отделяется запятой» became
    /// «Обращение отделяется запятой». §13 and §24 of the Phase 7 brief ask for the specific
    /// form, so an explanation the analyzer supplied now survives, and the pack fills in only
    /// where the analyzer left it empty — which is every regex rule, since those pass the
    /// pack's own text in to begin with.</para>
    /// </remarks>
    private TextIssue ApplyCatalogMetadata(TextIssue issue)
    {
        var rule = _catalog.GetRequired(issue.RuleId);
        return issue with
        {
            Title = string.IsNullOrWhiteSpace(issue.Title) ? rule.Title : issue.Title,
            Explanation = string.IsNullOrWhiteSpace(issue.Explanation)
                ? rule.DetailedExplanation
                : issue.Explanation,
            Category = rule.IssueCategory,
            Severity = rule.Severity,
            LinguisticCategory = rule.Category,
            CanApplyAutomatically = issue.CanApplyAutomatically && rule.SafeToApply
        };
    }

    private static TextIssue? SanitizeIssueOutput(TextIssue issue)
    {
        var replacement = issue.Replacement;
        if (replacement is not null)
        {
            replacement = TextOutputSanitizer.SanitizeReplacement(issue.Original, replacement);
            if (replacement is null)
            {
                return null;
            }
        }

        var explanation = issue.Explanation ?? string.Empty;
        var safe = issue.CanApplyAutomatically;
        if (safe && TextOutputSanitizer.MessageContradictsSafeApply(explanation, safe))
        {
            // Demote inconsistent soft wording.
            safe = false;
        }

        // Empty original insert display stays empty; UI maps to «[вставка]».
        return issue with
        {
            Replacement = replacement,
            CanApplyAutomatically = safe,
            Explanation = explanation
        };
    }

    /// <summary>
    /// Case-insensitive rules carry a lowercase literal replacement, so a match at
    /// the start of a sentence would otherwise be corrected to lowercase:
    /// "Вообщем, всё готово." became "в общем, всё готово.". If the matched text
    /// starts with a capital and the replacement does not, restore the capital.
    /// </summary>
    private static string PreserveLeadingCase(string original, string replacement)
    {
        if (original.Length == 0 || replacement.Length == 0) return replacement;
        if (!char.IsUpper(original[0]) || !char.IsLower(replacement[0])) return replacement;

        var culture = new System.Globalization.CultureInfo("ru-RU");
        return char.ToUpper(replacement[0], culture) + replacement[1..];
    }

    private static void SuppressControversialAndSoftDuplicates(List<TextIssue> issues)
    {
        // Drop soft subordinate hints that overlap reliable chtoby/comma rules.
        issues.RemoveAll(i =>
            i.RuleId.Contains("subordinate-comma.info", StringComparison.OrdinalIgnoreCase)
            && issues.Any(r =>
                r.RuleId is "ru.punctuation.comma-before-chtoby" or "ru.grammar.chtoby-solid"
                && RangesOverlap(i, r)));

        // «Несмотря на то, что» — do not auto-apply any punctuation toggle; drop auto flags.
        for (var n = 0; n < issues.Count; n++)
        {
            var i = issues[n];
            if (i.Original.Contains("Несмотря на то", StringComparison.OrdinalIgnoreCase)
                || i.Original.Contains("несмотря на то", StringComparison.OrdinalIgnoreCase))
            {
                issues[n] = i with { CanApplyAutomatically = false, Severity = IssueSeverity.Suggestion };
            }
        }

        // Prefer not showing weak "comma may be needed" for «несмотря на то что» variants as apply-all noise.
        issues.RemoveAll(i =>
            !i.CanApplyAutomatically
            && i.RuleId.Contains("info", StringComparison.OrdinalIgnoreCase)
            && i.Original.Contains("несмотря", StringComparison.OrdinalIgnoreCase));
    }

    private static bool RangesOverlap(TextIssue a, TextIssue b)
    {
        var aEnd = a.Start + Math.Max(a.Length, 1);
        var bEnd = b.Start + Math.Max(b.Length, 1);
        return a.Start < bEnd && b.Start < aEnd;
    }

    private static IEnumerable<TextIssue> Deduplicate(List<TextIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var issue in issues)
        {
            var key = $"{issue.Start}|{issue.Length}|{issue.Original}|{issue.Replacement}|{issue.RuleId}";
            if (!seen.Add(key))
            {
                continue;
            }

            yield return issue;
        }
    }

    private static void AddSentenceCapitalizationIssues(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        foreach (Match match in SentenceStartWordRegex().Matches(text))
        {
            var word = match.Groups[1].Value;
            if (word.Length == 0) continue;
            if (ProtectedTextSpans.Overlaps(match.Groups[1].Index, word.Length, protectedSpans)) continue;
            if (ProtectedTextSpans.IsTechnicalToken(word)) continue;

            var ch = word[0];
            if (!char.IsLetter(ch) || char.IsUpper(ch)) continue;

            var fixedWord = char.ToUpper(ch, new System.Globalization.CultureInfo("ru-RU")) + word[1..];
            issues.Add(new TextIssue(
                match.Groups[1].Index,
                word.Length,
                word,
                fixedWord,
                "Регистр в начале предложения",
                "Предложение обычно начинается с заглавной буквы.",
                IssueCategory.Orthography,
                IssueSeverity.Error,
                CanApplyAutomatically: true,
                RuleId: "ru.orthography.sentence-capital",
                LinguisticCategory: LinguisticIssueCategory.EndingError));
        }
    }

    private static void AddMissingTerminalPunctuationHints(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        var trimmed = text.TrimEnd();
        if (trimmed.Length < 20) return;
        if (protectedSpans.Count > 0 && ProtectedTextSpans.Overlaps(0, trimmed.Length, protectedSpans)
            && protectedSpans.Any(s => s.End - s.Start >= trimmed.Length - 2))
        {
            return;
        }

        if (trimmed.Contains('@') || trimmed.Contains("://", StringComparison.Ordinal)
            || trimmed.Contains('\\') || trimmed.Contains('_'))
        {
            return;
        }

        var last = trimmed[^1];
        if (last is '.' or '!' or '?' or '…' or ':' or ';' or '»' or '"' or ')' or '/') return;
        if (!char.IsLetter(last)) return;
        if (!trimmed.Contains(' ') || WordRegex().Matches(trimmed).Count < 4) return;

        // Pure insertion after the last letter — never replace the letter with "letter+dot".
        issues.Add(new TextIssue(
            trimmed.Length,
            0,
            string.Empty,
            ".",
            "Возможно, нет знака в конце",
            "В конце предложения обычно ставится точка, вопрос или восклицательный знак. Нажмите «Добавить точку», чтобы вставить только знак.",
            IssueCategory.Punctuation,
            IssueSeverity.Suggestion,
            CanApplyAutomatically: true,
            RuleId: "ru.punctuation.terminal.insert",
            LinguisticCategory: LinguisticIssueCategory.PunctuationRecommendation,
            Confidence: 0.55));
    }

    /// <summary>
    /// Вводные слова и сочетания в начале предложения: «К сожалению поезд опоздал» →
    /// «К сожалению, поезд опоздал».
    /// </summary>
    /// <remarks>
    /// <para><b>Condition.</b> The sentence opens with one of a closed list of parenthetical
    /// words or phrases, more text follows, and no comma is there already.</para>
    ///
    /// <para><b>Exceptions.</b> The list is restricted to items that are parenthetical
    /// whenever they open a sentence. The ambiguous ones are deliberately absent and must
    /// stay absent:</para>
    /// <list type="bullet">
    /// <item>«Однако» opens a sentence in the sense of «но» — a conjunction, no comma.</item>
    /// <item>«Наконец» is usually adverbial («Наконец мы приехали» — temporal, no comma).</item>
    /// <item>«Таким образом» is adverbial as often as parenthetical («Таким образом мы
    /// получили результат»).</item>
    /// <item>«Правда», «Вообще», «Верно» carry ordinary non-parenthetical senses.</item>
    /// </list>
    ///
    /// <para>Nothing fires unless the intro word is followed by further words, so a
    /// one-word answer — «Конечно.» — is untouched.</para>
    ///
    /// <para>Measured: this cluster is 4 of the 24 punctuation errors the deterministic
    /// pipeline missed on the frozen corpus, and the largest one describable by a closed
    /// list rather than by parsing.</para>
    /// </remarks>
    private static void AddMissingCommaAfterIntroductoryWord(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        foreach (Match match in IntroductoryWordRegex().Matches(text))
        {
            var word = match.Groups["intro"];
            if (ProtectedTextSpans.Overlaps(word.Index, word.Length, protectedSpans))
            {
                continue;
            }

            issues.Add(new TextIssue(
                word.Index,
                word.Length,
                word.Value,
                word.Value + ",",
                "Вводное слово выделяется запятой",
                $"«{word.Value}» — вводное сочетание; оно не является членом предложения и отделяется запятой.",
                IssueCategory.Punctuation,
                IssueSeverity.Warning,
                CanApplyAutomatically: true,
                RuleId: "ru.punctuation.intro-word-comma",
                LinguisticCategory: LinguisticIssueCategory.PunctuationRecommendation,
                Confidence: 0.88));
        }
    }

    /// <summary>
    /// Запятая перед «однако» и «зато» в середине предложения: «Всё было готово однако никто
    /// не пришёл» → «Всё было готово, однако никто не пришёл».
    /// </summary>
    /// <remarks>
    /// <para><b>Condition.</b> «однако» or «зато» appears mid-sentence, directly after a
    /// letter-final word, with no punctuation between them. In that position both are
    /// adversative conjunctions joining clauses or homogeneous parts, and Russian requires a
    /// comma before them.</para>
    ///
    /// <para><b>Exceptions.</b> Sentence-initial «Однако» is the conjunction «но» and takes
    /// no preceding comma — the regex requires a preceding word, so it cannot fire there.
    /// Any existing comma, dash, colon or bracket before the conjunction suppresses it,
    /// which also covers the parenthetical use «Он, однако, не пришёл».</para>
    ///
    /// <para>«поэтому» is deliberately not here despite being the same cluster in the corpus.
    /// It is ambiguous in exactly the position this rule looks at — «Именно поэтому он ушёл»
    /// needs no comma — and separating the two readings needs to know whether a predicate
    /// precedes it, which is parsing, not a list.</para>
    /// </remarks>
    private static void AddMissingCommaBeforeAdversative(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        foreach (Match match in AdversativeConjunctionRegex().Matches(text))
        {
            var conjunction = match.Groups["conj"];
            if (ProtectedTextSpans.Overlaps(match.Index, match.Length, protectedSpans))
            {
                continue;
            }

            // The match opens with the last letter of the preceding word, which is how the
            // rule knows it is mid-sentence — but that letter must not be part of the span.
            // Reported as an insertion at the end of the preceding word, the same shape the
            // word-aware diff gives a missing comma, so it reads as "insert a comma here"
            // rather than as a replacement of half of «готово».
            issues.Add(new TextIssue(
                match.Index + 1,
                0,
                string.Empty,
                ",",
                "Запятая перед противительным союзом",
                $"Перед союзом «{conjunction.Value}» в середине предложения ставится запятая: "
                + "он соединяет части сложного предложения или однородные члены.",
                IssueCategory.Punctuation,
                IssueSeverity.Warning,
                CanApplyAutomatically: true,
                RuleId: "ru.punctuation.adversative-comma",
                LinguisticCategory: LinguisticIssueCategory.PunctuationRecommendation,
                Confidence: 0.86));
        }
    }

    private static void AddPossibleMissingCommaAfterIntro(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        // "Когда … я/он/мы" without comma — informational only; never before the word «Когда» itself.
        foreach (Match match in IntroClauseRegex().Matches(text))
        {
            if (ProtectedTextSpans.Overlaps(match.Index, match.Length, protectedSpans))
            {
                continue;
            }

            var between = match.Groups["mid"].Value;
            if (between.Contains(',')) continue;
            if (between.Length < 4 || between.Length > 80) continue;

            // Highlight the boundary before the second-clause subject, never the intro word.
            var insertAt = match.Groups["mid"].Index + match.Groups["mid"].Length;
            if (insertAt <= 0 || insertAt >= text.Length) continue;

            // Require whitespace before subject.
            if (!char.IsWhiteSpace(text[insertAt])) continue;

            var sliceStart = insertAt;
            // Zero-width informational marker via 1-char whitespace span with no auto-apply.
            var len = 1;
            issues.Add(new TextIssue(
                sliceStart,
                len,
                text.Substring(sliceStart, len),
                null,
                "Возможна пропущенная запятая",
                "После придаточной части (когда, если, хотя…) часто нужна запятая.",
                IssueCategory.Punctuation,
                IssueSeverity.Suggestion,
                CanApplyAutomatically: false,
                RuleId: "ru.punctuation.intro-comma.info",
                LinguisticCategory: LinguisticIssueCategory.PunctuationRecommendation));
        }
    }

    private static void AddPossibleSubordinateClauseHints(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        foreach (Match match in SubordinateConjunctionRegex().Matches(text))
        {
            if (ProtectedTextSpans.Overlaps(match.Index, match.Length, protectedSpans))
            {
                continue;
            }

            // Never suggest a comma *before* a sentence-initial conjunction (esp. «Когда»).
            if (IsClauseOrSentenceStart(text, match.Index))
            {
                continue;
            }

            if (match.Index < 2)
            {
                continue;
            }

            var previous = PreviousNonWhitespace(text, match.Index - 1);
            if (previous is ',' or ';' or ':' or '—' or '-' or '.' or '!' or '?' or '…' or '\0')
            {
                continue;
            }

            // High-confidence auto-apply after a letter (…знаю что → …знаю, что).
            //
            // Phase 7.5 widened this from «что/чтобы» to the «который» family and to «потому
            // что». Both were measured as detected-but-unfixed on the real-world set — the
            // rule already found them and then declined to say what to do, which is the least
            // useful of the three possible outcomes.
            //
            // «который» mid-sentence directly after a word opens a relative clause and takes a
            // preceding comma; there is no reading in which a bare noun is followed by an
            // unpunctuated «который». «потому» is admitted only as the head of «потому что»,
            // and only when «не» does not precede it — «не потому, что устал» puts the comma
            // after «потому», which is a different rule and not one this can settle.
            var word = match.Value;
            var canAuto = word.Equals("что", StringComparison.OrdinalIgnoreCase)
                          || word.Equals("чтобы", StringComparison.OrdinalIgnoreCase)
                          || word.StartsWith("котор", StringComparison.OrdinalIgnoreCase)
                          || (word.StartsWith("потому", StringComparison.OrdinalIgnoreCase)
                              && !PrecededByNegation(text, match.Index));
            string? replacement = null;
            var start = match.Index;
            var length = match.Length;
            var original = match.Value;

            if (canAuto && char.IsLetter(previous))
            {
                var prevIndex = IndexOfPreviousNonWhitespace(text, match.Index - 1);
                if (prevIndex >= 0 && match.Index - prevIndex <= 2)
                {
                    start = prevIndex + 1;
                    length = match.Index + match.Length - start;
                    original = text.Substring(start, length);
                    var spaces = original[..Math.Max(0, original.Length - match.Length)];
                    if (spaces.Length == 0 || spaces.All(char.IsWhiteSpace))
                    {
                        replacement = ", " + match.Value;
                    }
                    else
                    {
                        canAuto = false;
                    }
                }
                else
                {
                    canAuto = false;
                }
            }
            else
            {
                canAuto = false;
            }

            // «когда/который/если/…» mid-sentence: info only (no false auto before «Когда»).
            issues.Add(new TextIssue(
                start,
                length,
                original,
                replacement,
                canAuto ? "Нужна запятая" : "Возможна граница придаточной части",
                canAuto
                    ? $"Перед «{match.Value}» обычно ставится запятая."
                    : $"Перед «{match.Value}» может требоваться запятая — проверьте структуру предложения.",
                IssueCategory.Punctuation,
                canAuto && replacement is not null ? IssueSeverity.Error : IssueSeverity.Suggestion,
                CanApplyAutomatically: canAuto && replacement is not null,
                RuleId: canAuto && replacement is not null
                    ? "ru.punctuation.subordinate-comma"
                    : "ru.punctuation.subordinate-comma.info",
                LinguisticCategory: LinguisticIssueCategory.PunctuationRecommendation));
        }
    }

    /// <summary>
    /// True when «не» immediately precedes the conjunction at <paramref name="index"/>.
    /// </summary>
    /// <remarks>
    /// «Он ушёл не потому, что устал» places the comma after «потому», not before it. The
    /// negation is the whole signal, and it has to be the adjacent word.
    /// </remarks>
    private static bool PrecededByNegation(string text, int index)
    {
        var end = index;
        while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
        var start = end;
        while (start > 0 && char.IsLetter(text[start - 1])) start--;
        return end > start
            && text[start..end].Equals("не", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when index is the start of the text or follows sentence/line boundary whitespace only.
    /// Prevents false "comma before Когда" at the beginning of a sentence.
    /// </summary>
    private static bool IsClauseOrSentenceStart(string text, int index)
    {
        if (index <= 0)
        {
            return true;
        }

        var i = index - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i]))
        {
            if (text[i] is '\n' or '\r')
            {
                return true;
            }

            i--;
        }

        if (i < 0)
        {
            return true;
        }

        return text[i] is '.' or '!' or '?' or '…' or ':' or ';' or '—' or '(' or '«' or '"' or '“';
    }

    private static int IndexOfPreviousNonWhitespace(string text, int index)
    {
        while (index >= 0)
        {
            if (!char.IsWhiteSpace(text[index])) return index;
            index--;
        }

        return -1;
    }

    private static void AddLongSentenceHints(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        foreach (Match match in SentenceRegex().Matches(text))
        {
            var sentence = match.Value.Trim();
            if (sentence.Length == 0) continue;
            if (ProtectedTextSpans.Overlaps(match.Index, match.Length, protectedSpans)) continue;

            var wordCount = WordRegex().Matches(sentence).Count;
            if (wordCount < 32 && sentence.Length < 220) continue;

            var offset = match.Index + (match.Value.Length - match.Value.TrimStart().Length);
            issues.Add(new TextIssue(
                offset,
                sentence.Length,
                sentence,
                null,
                "Перегруженное предложение",
                $"В предложении около {wordCount} слов. Проверьте количество придаточных частей.",
                IssueCategory.Readability,
                IssueSeverity.Warning,
                CanApplyAutomatically: false,
                RuleId: "ru.readability.long-sentence",
                LinguisticCategory: LinguisticIssueCategory.LongSentence));
        }
    }

    private static char PreviousNonWhitespace(string text, int index)
    {
        while (index >= 0)
        {
            if (!char.IsWhiteSpace(text[index])) return text[index];
            index--;
        }

        return '\0';
    }

    [GeneratedRegex(@"\b([\p{L}]{2,})\s+\1\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedWordRegex();

    // Soft mid-clause subordinates only. «чтобы» handled by ChtobyAnalyzer (mandatory rules).
    // Sentence-initial filtered in code via IsClauseOrSentenceStart.
    [GeneratedRegex(@"\b(когда|котор(?:ый|ая|ое|ые|ого|ой|ому|ым|ых)|потому\s+что|если|хотя|поскольку|что)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SubordinateConjunctionRegex();

    [GeneratedRegex(@"(?<=[.!?…]\s+)(\p{L}[\p{L}\-]*)", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceStartWordRegex();

    [GeneratedRegex(@"\b(Когда|Если|Хотя|Пока|Как только)(?<mid>.{4,80}?)(?=\s+(?:я|ты|он|она|оно|мы|вы|они|меня|мне)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntroClauseRegex();

    /// <summary>
    /// Parenthetical openers that are parenthetical <em>whenever</em> they start a sentence.
    /// </summary>
    /// <remarks>
    /// Anchored to the start of the text or to a sentence boundary, and requires a following
    /// word so that a one-word reply is never touched. Multi-word entries come first so the
    /// alternation prefers «Кроме того» over a bare «Кроме». See
    /// <see cref="AddMissingCommaAfterIntroductoryWord"/> for what is excluded and why.
    /// </remarks>
    [GeneratedRegex(
        @"(?:^|(?<=[.!?…]\s))\s*(?<intro>Кроме\s+того|С\s+одной\s+стороны|С\s+другой\s+стороны|"
        + @"К\s+сожалению|К\s+счастью|К\s+удивлению|К\s+несчастью|По-моему|По-твоему|По-нашему|"
        + @"Во-первых|Во-вторых|В-третьих|В-четвёртых|В-пятых|"
        + @"Например|Конечно|Разумеется|Безусловно|Несомненно|Следовательно|Впрочем|Кстати|Итак)"
        + @"(?=\s+\p{L})(?!\s*,)",
        RegexOptions.CultureInvariant)]
    private static partial Regex IntroductoryWordRegex();

    /// <summary>
    /// Adversative «однако»/«зато» directly after a word, with nothing between them but
    /// spaces — the position in which Russian requires a preceding comma.
    /// </summary>
    /// <remarks>
    /// <para>The leading <c>\p{L}</c> is what excludes the sentence-initial conjunction use and
    /// any position already carrying punctuation. The match spans from that letter through the
    /// conjunction so the replacement can insert the comma without a second span.</para>
    ///
    /// <para><b>«а» was added in Phase 7</b> and is the strongest member of the set: a
    /// mid-sentence «а» joining clauses or homogeneous parts takes a preceding comma without
    /// exception, which is more than can be said for «однако». It was missing, and the §9
    /// regression sentence — «…о твоих новых достижениях а теперь я хочу…» — is exactly the
    /// shape it misses.</para>
    ///
    /// <para>The trailing negative lookahead is the one reading that is not a conjunction:
    /// «а» as an enumeration label, as in «пункт а и пункт б». Requiring that the next word is
    /// not another single-letter label separates it without excluding «а я промолчал», where
    /// the following word is also one letter but is a pronoun.</para>
    /// </remarks>
    [GeneratedRegex(
        @"\p{L}\s+(?<conj>однако|зато|а)(?=\s+\p{L})(?!\s+(?:и|б|в|г|д)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AdversativeConjunctionRegex();

    [GeneratedRegex(@"[^.!?\r\n]+(?:[.!?]+|$)", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceRegex();

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}
