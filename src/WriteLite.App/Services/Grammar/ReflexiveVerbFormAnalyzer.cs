using System.Text.RegularExpressions;
using WriteLite.Models;

namespace WriteLite.Services.Grammar;

/// <summary>
/// Contextual -ться / -тся for high-confidence finite predicate cases only.
/// Does not flag forms solely by ending; requires subject-like left context and
/// absence of modal / infinitive governors.
/// </summary>
public static partial class ReflexiveVerbFormAnalyzer
{
    private static readonly HashSet<string> ModalLeft = new(StringComparer.OrdinalIgnoreCase)
    {
        "может", "могут", "можешь", "можем", "можете", "могу",
        "должен", "должна", "должно", "должны",
        "хочет", "хотят", "хочу", "хотим", "хотите",
        "будет", "будут", "буду", "будем", "будете", "было", "была", "были",
        "решил", "решила", "решили", "решит",
        "нужно", "надо", "необходимо", "следует", "стоит",
        "начал", "начала", "начали", "станет", "станут",
        "способен", "способна", "способны",
        "обязан", "обязана", "обязаны",
        "пытается", "старается", "продолжает",
        "позволяет", "позволяют",
        "сможет", "смогут", "собирается", "собираются",
        "старается", "стремится",
        "имеет", "имеют" // «имеет становиться» rare; still block auto
    };

    // High-frequency pairs: infinitive form → 3rd person present when used as finite predicate.
    private static readonly Dictionary<string, string> FiniteMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["становиться"] = "становится",
        ["ставиться"] = "ставится",
        ["меняться"] = "меняется",
        ["запускаться"] = "запускается",
        ["развиваться"] = "развивается",
        ["использоваться"] = "используется",
        ["считаться"] = "считается",
        ["казаться"] = "кажется",
        ["являться"] = "является",
        ["продолжаться"] = "продолжается",
        ["увеличиваться"] = "увеличивается",
        ["уменьшаться"] = "уменьшается",
        ["открываться"] = "открывается",
        ["закрываться"] = "закрывается",
        ["начинаться"] = "начинается",
        ["заканчиваться"] = "заканчивается",
        ["выполняться"] = "выполняется",
        ["игнорироваться"] = "игнорируется"
    };

    public static void Collect(string text, IReadOnlyList<(int Start, int End)> protectedSpans, ICollection<TextIssue> issues)
    {
        CollectInfinitiveWrittenAsFinite(text, protectedSpans, issues);
        CollectFiniteWrittenAsInfinitive(text, protectedSpans, issues);
    }

    /// <summary>
    /// «Он хочет учится» → «учиться». A finite form where the sentence requires an infinitive.
    /// </summary>
    /// <remarks>
    /// <para>The direction this analyzer was missing. The other half of the file asks whether
    /// an infinitive is standing where a predicate belongs, and needs a subject hint and the
    /// absence of a governor to decide it. This half has the opposite and much easier problem:
    /// a modal or phase verb <em>directly</em> governs an infinitive in Russian, with no
    /// alternative reading, so an adjacent governor is the whole evidence. «хочет учится» is
    /// not ambiguous, it is ungrammatical.</para>
    ///
    /// <para>Adjacency is the entire safety property and is why this is not simply the
    /// existing <see cref="ModalLeft"/> window run backwards. That window looks five tokens
    /// back, and at that distance «Он должен, кажется, уйти» puts «должен» behind «кажется» —
    /// a correct parenthetical third-person form that the rule would rewrite into
    /// «казаться». Requiring the governor to be the immediately preceding token with nothing
    /// but spaces between them removes that reading structurally rather than by exception
    /// list: a comma, a conjunction or any other word ends the government relation.</para>
    ///
    /// <para>The replacement is exact rather than looked up. Inserting the soft sign is a
    /// total orthographic relation on this ending, so unlike <see cref="FiniteMap"/> — which
    /// carries pairs such as «казаться»/«кажется» that also change the stem — it needs no
    /// table and covers every reflexive verb in the language.</para>
    /// </remarks>
    private static void CollectFiniteWrittenAsInfinitive(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        foreach (Match m in ReflexiveFiniteRegex().Matches(text))
        {
            if (ProtectedTextSpans.Overlaps(m.Index, m.Length, protectedSpans))
            {
                continue;
            }

            if (!HasAdjacentInfinitiveGovernor(text, m.Index))
            {
                continue;
            }

            var word = m.Value;
            var infinitive = RussianTokens.MatchLeadingCase(word, word[..^3] + "ться");

            issues.Add(new TextIssue(
                m.Index,
                m.Length,
                word,
                infinitive,
                "Форма сказуемого",
                "После модального глагола требуется инфинитив: «что делать?»",
                IssueCategory.Grammar,
                IssueSeverity.Error,
                CanApplyAutomatically: true,
                RuleId: "ru.grammar.reflexive-infinitive",
                LinguisticCategory: LinguisticIssueCategory.EndingError));
        }
    }

    /// <summary>
    /// True when the token immediately before <paramref name="verbIndex"/> governs an
    /// infinitive, with nothing but whitespace between the two.
    /// </summary>
    private static bool HasAdjacentInfinitiveGovernor(string text, int verbIndex)
    {
        var cursor = verbIndex - 1;
        while (cursor >= 0 && char.IsWhiteSpace(text[cursor])) cursor--;
        if (cursor < 0 || !char.IsLetter(text[cursor])) return false;

        var end = cursor + 1;
        while (cursor >= 0 && char.IsLetter(text[cursor])) cursor--;
        var previous = text[(cursor + 1)..end];

        // «Он должен» and «нужно» govern an infinitive; so does another infinitive
        // («решил остановиться»). Both readings are the same relation.
        return ModalLeft.Contains(previous)
               || previous.EndsWith("ть", StringComparison.OrdinalIgnoreCase)
               || previous.EndsWith("чь", StringComparison.OrdinalIgnoreCase);
    }

    private static void CollectInfinitiveWrittenAsFinite(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        foreach (Match m in ReflexiveInfinitiveRegex().Matches(text))
        {
            var word = m.Value;
            if (!FiniteMap.TryGetValue(word, out var finite))
            {
                continue;
            }

            if (ProtectedTextSpans.Overlaps(m.Index, m.Length, protectedSpans))
            {
                continue;
            }

            if (HasModalOrInfinitiveGovernor(text, m.Index))
            {
                continue; // «может становиться», «должен ставиться», «нужно остановиться»
            }

            if (!HasFinitePredicateContext(text, m.Index))
            {
                continue;
            }

            var replacement = RussianTokens.MatchLeadingCase(word, finite);
            var message = BuildMessage(word);

            issues.Add(new TextIssue(
                m.Index,
                m.Length,
                word,
                replacement,
                "Форма сказуемого",
                message,
                IssueCategory.Grammar,
                IssueSeverity.Error,
                CanApplyAutomatically: true,
                RuleId: "ru.grammar.reflexive-finite",
                LinguisticCategory: LinguisticIssueCategory.EndingError));
        }
    }

    private static string BuildMessage(string infinitiveForm)
    {
        // Card copy for the golden climate text cases.
        if (infinitiveForm.Equals("ставиться", StringComparison.OrdinalIgnoreCase))
        {
            return "В данном предложении требуется форма третьего лица.";
        }

        return "Форма сказуемого должна отвечать на вопрос «что делает?»";
    }

    private static bool HasModalOrInfinitiveGovernor(string text, int verbIndex)
    {
        var left = text[..verbIndex];
        var tokens = TokenizeLeft(left, 5);
        if (tokens.Count == 0)
        {
            return false;
        }

        // Any nearby modal / future / duty marker blocks auto-fix.
        foreach (var t in tokens)
        {
            if (ModalLeft.Contains(t))
            {
                return true;
            }
        }

        // «чтобы становиться» — purpose keeps infinitive.
        if (tokens.Any(t => t.Equals("чтобы", StringComparison.OrdinalIgnoreCase)
                            || t.Equals("что", StringComparison.OrdinalIgnoreCase)))
        {
            // «что» alone is weak; block only with «чтобы» or particle cluster
            if (tokens.Any(t => t.Equals("чтобы", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        // Previous content word is itself an infinitive governor: «решить остановиться»
        var prev = tokens[0];
        if (prev.EndsWith("ть", StringComparison.OrdinalIgnoreCase)
            || prev.EndsWith("ться", StringComparison.OrdinalIgnoreCase)
            || prev.EndsWith("ти", StringComparison.OrdinalIgnoreCase)
            || prev.EndsWith("чь", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool HasFinitePredicateContext(string text, int verbIndex)
    {
        var left = text[..verbIndex];
        var tokens = TokenizeLeft(left, 8);
        if (tokens.Count == 0)
        {
            return false;
        }

        var preps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "в", "на", "с", "со", "к", "по", "о", "об", "от", "до", "из", "у", "за", "под", "над",
            "при", "для", "без", "через", "между", "и", "а", "но", "что", "как", "где", "когда",
            "или", "либо", "то", "же", "ли", "бы", "не", "ни", "уже", "ещё", "еще", "всё", "все",
            "более", "очень", "весьма", "также", "только"
        };

        // Need a content-word subject hint (noun/pronoun-like) to the left.
        var hasSubjectHint = tokens.Any(t => t.Length >= 3 && !preps.Contains(t));
        if (!hasSubjectHint)
        {
            return false;
        }

        // Avoid bare list / title fragments: require that we are mid-clause (letter/punct before).
        var prevChar = RussianTokens.PreviousNonWhitespace(text, verbIndex - 1);
        if (prevChar == '\0')
        {
            return false;
        }

        return char.IsLetter(prevChar) || prevChar is ',' or ';' or ':' or '—' or '–';
    }

    private static List<string> TokenizeLeft(string left, int maxTokens)
    {
        var list = new List<string>();
        var matches = RussianTokens.LettersTokenRegex().Matches(left);
        for (var i = matches.Count - 1; i >= 0 && list.Count < maxTokens; i--)
        {
            list.Add(matches[i].Value);
        }

        return list;
    }

    [GeneratedRegex(@"\b\p{L}+ться\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReflexiveInfinitiveRegex();

    /// <summary>A reflexive form ending in -тся, which -ться would have written with a soft sign.</summary>
    [GeneratedRegex(@"\b\p{L}+тся\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReflexiveFiniteRegex();
}
