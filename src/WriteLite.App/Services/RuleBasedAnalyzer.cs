using System.Text.RegularExpressions;
using WriteLite.Models;
using WriteLite.Services.Grammar;
using WriteLite.Services.Rules;

namespace WriteLite.Services;

public sealed partial class RuleBasedAnalyzer : ITextAnalyzer
{
    private readonly RuleCatalog _catalog;

    public RuleBasedAnalyzer()
        : this(RuleCatalog.LoadDefault())
    {
    }

    public RuleBasedAnalyzer(RuleCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
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

                var replacement = match.Result(rule.Implementation.Replacement!);
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

    private TextIssue ApplyCatalogMetadata(TextIssue issue)
    {
        var rule = _catalog.GetRequired(issue.RuleId);
        return issue with
        {
            Title = rule.Title,
            Explanation = rule.DetailedExplanation,
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

            // Only high-confidence auto-apply for «что/чтобы» after a letter (…знаю что → …знаю, что).
            var canAuto = match.Value.Equals("что", StringComparison.OrdinalIgnoreCase)
                          || match.Value.Equals("чтобы", StringComparison.OrdinalIgnoreCase);
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

    [GeneratedRegex(@"[^.!?\r\n]+(?:[.!?]+|$)", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceRegex();

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}
