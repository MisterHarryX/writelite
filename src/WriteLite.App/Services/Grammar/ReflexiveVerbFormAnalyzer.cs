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

            var replacement = PreserveCase(word, finite);
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
        var prevChar = PreviousNonWhitespace(text, verbIndex - 1);
        if (prevChar == '\0')
        {
            return false;
        }

        return char.IsLetter(prevChar) || prevChar is ',' or ';' or ':' or '—' or '–';
    }

    private static char PreviousNonWhitespace(string text, int index)
    {
        while (index >= 0)
        {
            if (!char.IsWhiteSpace(text[index]))
            {
                return text[index];
            }

            index--;
        }

        return '\0';
    }

    private static List<string> TokenizeLeft(string left, int maxTokens)
    {
        var list = new List<string>();
        var matches = LeftTokenRegex().Matches(left);
        for (var i = matches.Count - 1; i >= 0 && list.Count < maxTokens; i--)
        {
            list.Add(matches[i].Value);
        }

        return list;
    }

    private static string PreserveCase(string original, string replacement)
    {
        if (original.Length == 0)
        {
            return replacement;
        }

        if (char.IsUpper(original[0]))
        {
            return char.ToUpper(replacement[0], new System.Globalization.CultureInfo("ru-RU")) + replacement[1..];
        }

        return replacement;
    }

    [GeneratedRegex(@"\b\p{L}+ться\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReflexiveInfinitiveRegex();

    [GeneratedRegex(@"\p{L}+", RegexOptions.CultureInvariant)]
    private static partial Regex LeftTokenRegex();
}
