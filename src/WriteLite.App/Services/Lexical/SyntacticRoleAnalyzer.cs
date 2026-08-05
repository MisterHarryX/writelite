namespace WriteLite.Services.Lexical;

/// <summary>
/// Lightweight sentence-role heuristics (not a full parser).
/// Good enough for UI explanations: subject / predicate / object / attribute / adverbial.
/// </summary>
public sealed class SyntacticRoleAnalyzer : ISyntacticRoleAnalyzer
{
    private static readonly HashSet<string> RuPrep = new(StringComparer.OrdinalIgnoreCase)
    {
        "в", "на", "с", "со", "к", "ко", "у", "о", "об", "от", "до", "из", "за", "под", "над", "при", "про", "без", "для", "по"
    };

    private static readonly HashSet<string> RuConj = new(StringComparer.OrdinalIgnoreCase)
    {
        "и", "а", "но", "или", "что", "чтобы", "если", "когда", "потому", "хотя"
    };

    public SyntacticAnalysis Analyze(
        string word,
        int start,
        int length,
        string sentence,
        LexicalPartOfSpeech pos,
        LexicalLanguage language)
    {
        var tokens = Tokenize(sentence);
        if (tokens.Count == 0)
        {
            return new SyntacticAnalysis(SyntacticRole.Unknown, null, null,
                "Недостаточно контекста для определения роли.", []);
        }

        var idx = FindTokenIndex(tokens, word, start, sentence);
        if (idx < 0) idx = 0;

        var role = InferRole(tokens, idx, pos, language);
        var head = FindHead(tokens, idx, role, language);
        var relations = BuildRelations(tokens, idx, head, role, language);
        var explanation = Explain(role, word, head, language);

        return new SyntacticAnalysis(role, head, role.ToString(), explanation, relations);
    }

    private static SyntacticRole InferRole(
        IReadOnlyList<Token> tokens,
        int idx,
        LexicalPartOfSpeech pos,
        LexicalLanguage language)
    {
        var t = tokens[idx];
        var lower = t.Text.ToLowerInvariant();

        if (RuConj.Contains(lower) || pos == LexicalPartOfSpeech.Conjunction)
            return SyntacticRole.ConjunctionRole;
        if (RuPrep.Contains(lower) || pos == LexicalPartOfSpeech.Preposition)
            return SyntacticRole.ParticleRole;
        if (pos == LexicalPartOfSpeech.Particle)
            return SyntacticRole.ParticleRole;

        if (pos == LexicalPartOfSpeech.Verb || (pos == LexicalPartOfSpeech.Unknown && LooksLikeFiniteVerb(lower, language)))
            return SyntacticRole.Predicate;

        // Trust explicit POS from the lexicon over surface heuristics.
        if (pos == LexicalPartOfSpeech.Adjective || (pos == LexicalPartOfSpeech.Unknown && LooksLikeAdjective(lower, language)))
            return SyntacticRole.Attribute;

        if (pos == LexicalPartOfSpeech.Adverb || (pos == LexicalPartOfSpeech.Unknown && LooksLikeAdverb(lower, language)))
            return SyntacticRole.Adverbial;

        // Subject: first content noun/pronoun before the main verb
        var verbIdx = FindMainVerbIndex(tokens, language);
        if (verbIdx >= 0)
        {
            if (idx < verbIdx && (pos is LexicalPartOfSpeech.Noun or LexicalPartOfSpeech.Pronoun or LexicalPartOfSpeech.Unknown))
            {
                // if immediately after preposition → object of prep
                if (idx > 0 && IsPrep(tokens[idx - 1].Text, language))
                    return SyntacticRole.PrepositionalObject;
                return SyntacticRole.Subject;
            }

            if (idx > verbIdx && (pos is LexicalPartOfSpeech.Noun or LexicalPartOfSpeech.Pronoun or LexicalPartOfSpeech.Unknown))
            {
                if (idx > 0 && IsPrep(tokens[idx - 1].Text, language))
                    return SyntacticRole.PrepositionalObject;
                return SyntacticRole.Object;
            }
        }

        if (idx > 0 && IsPrep(tokens[idx - 1].Text, language))
            return SyntacticRole.PrepositionalObject;

        if (idx == 0 && pos is LexicalPartOfSpeech.Noun or LexicalPartOfSpeech.Pronoun or LexicalPartOfSpeech.Unknown)
            return SyntacticRole.Subject;

        return pos switch
        {
            LexicalPartOfSpeech.Noun => SyntacticRole.Object,
            LexicalPartOfSpeech.Pronoun => SyntacticRole.Subject,
            _ => SyntacticRole.Unknown
        };
    }

    private static string? FindHead(IReadOnlyList<Token> tokens, int idx, SyntacticRole role, LexicalLanguage language)
    {
        return role switch
        {
            SyntacticRole.Attribute => FindNextNoun(tokens, idx) ?? FindPrevNoun(tokens, idx),
            SyntacticRole.Object or SyntacticRole.PrepositionalObject => FindPrevVerb(tokens, idx, language),
            SyntacticRole.Subject => FindNextVerb(tokens, idx, language),
            SyntacticRole.Adverbial => FindPrevVerb(tokens, idx, language) ?? FindNextVerb(tokens, idx, language),
            SyntacticRole.Predicate => FindPrevNoun(tokens, idx),
            _ => null
        };
    }

    private static IReadOnlyList<WordRelation> BuildRelations(
        IReadOnlyList<Token> tokens,
        int idx,
        string? head,
        SyntacticRole role,
        LexicalLanguage language)
    {
        var list = new List<WordRelation>();
        if (!string.IsNullOrEmpty(head))
            list.Add(new WordRelation(head, "head", RoleLabel(role)));

        if (idx > 0)
            list.Add(new WordRelation(tokens[idx - 1].Text, "left", "сосед слева"));
        if (idx + 1 < tokens.Count)
            list.Add(new WordRelation(tokens[idx + 1].Text, "right", "сосед справа"));

        if (idx > 0 && IsPrep(tokens[idx - 1].Text, language))
            list.Add(new WordRelation(tokens[idx - 1].Text, "prep", "управление предлогом"));

        return list;
    }

    private static string Explain(SyntacticRole role, string word, string? head, LexicalLanguage language)
    {
        var w = $"«{word}»";
        return role switch
        {
            SyntacticRole.Subject => head is null
                ? $"{w} вероятно выполняет роль подлежащего."
                : $"{w} — подлежащее при сказуемом «{head}».",
            SyntacticRole.Predicate => head is null
                ? $"{w} — сказуемое (глагольный центр предложения)."
                : $"{w} — сказуемое; связано с «{head}».",
            SyntacticRole.Object => head is null
                ? $"{w} вероятно дополнение."
                : $"{w} — дополнение при «{head}».",
            SyntacticRole.Attribute => head is null
                ? $"{w} — определение."
                : $"{w} — определение к «{head}».",
            SyntacticRole.Adverbial => head is null
                ? $"{w} — обстоятельство."
                : $"{w} — обстоятельство при «{head}».",
            SyntacticRole.PrepositionalObject => head is null
                ? $"{w} входит в предложную группу."
                : $"{w} — зависимое в предложной группе «{head}».",
            SyntacticRole.ConjunctionRole => $"{w} — союз, связывает части предложения.",
            SyntacticRole.ParticleRole => $"{w} — служебное слово (предлог/частица).",
            _ => $"{w}: роль в предложении не определена однозначно."
        };
    }

    private static int FindMainVerbIndex(IReadOnlyList<Token> tokens, LexicalLanguage language)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (LooksLikeFiniteVerb(tokens[i].Text.ToLowerInvariant(), language))
                return i;
        }

        return -1;
    }

    private static bool LooksLikeFiniteVerb(string lower, LexicalLanguage language)
    {
        return lower.EndsWith("ет") || lower.EndsWith("ёт") || lower.EndsWith("ит")
               || lower.EndsWith("ут") || lower.EndsWith("ют") || lower.EndsWith("ат") || lower.EndsWith("ят")
               || lower.EndsWith("л") || lower.EndsWith("ла") || lower.EndsWith("ло") || lower.EndsWith("ли")
               || lower.EndsWith("ешь") || lower.EndsWith("ишь") || lower.EndsWith("ем") || lower.EndsWith("им");
    }

    private static bool LooksLikeAdjective(string lower, LexicalLanguage language)
    {
        return lower.EndsWith("ый") || lower.EndsWith("ий") || lower.EndsWith("ой")
               || lower.EndsWith("ая") || lower.EndsWith("ое") || lower.EndsWith("ые");
    }

    private static bool LooksLikeAdverb(string lower, LexicalLanguage language)
    {
        return lower.EndsWith("о") && lower.Length > 3;
    }

    private static bool IsPrep(string text, LexicalLanguage language)
        => RuPrep.Contains(text);

    private static string? FindNextNoun(IReadOnlyList<Token> tokens, int idx)
    {
        for (var i = idx + 1; i < tokens.Count; i++)
            if (!IsPrep(tokens[i].Text, LexicalLanguage.Russian))
                return tokens[i].Text;
        return null;
    }

    private static string? FindPrevNoun(IReadOnlyList<Token> tokens, int idx)
    {
        for (var i = idx - 1; i >= 0; i--)
            if (!IsPrep(tokens[i].Text, LexicalLanguage.Russian))
                return tokens[i].Text;
        return null;
    }

    private static string? FindPrevVerb(IReadOnlyList<Token> tokens, int idx, LexicalLanguage language)
    {
        for (var i = idx - 1; i >= 0; i--)
            if (LooksLikeFiniteVerb(tokens[i].Text.ToLowerInvariant(), language))
                return tokens[i].Text;
        return null;
    }

    private static string? FindNextVerb(IReadOnlyList<Token> tokens, int idx, LexicalLanguage language)
    {
        for (var i = idx + 1; i < tokens.Count; i++)
            if (LooksLikeFiniteVerb(tokens[i].Text.ToLowerInvariant(), language))
                return tokens[i].Text;
        return null;
    }

    private static string RoleLabel(SyntacticRole role) => role switch
    {
        SyntacticRole.Subject => "подлежащее",
        SyntacticRole.Predicate => "сказуемое",
        SyntacticRole.Object => "дополнение",
        SyntacticRole.Attribute => "определение",
        SyntacticRole.Adverbial => "обстоятельство",
        SyntacticRole.PrepositionalObject => "предложное дополнение",
        _ => role.ToString()
    };

    private static int FindTokenIndex(IReadOnlyList<Token> tokens, string word, int start, string sentence)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (string.Equals(tokens[i].Text, word, StringComparison.OrdinalIgnoreCase)
                && Math.Abs(tokens[i].Start - start) <= Math.Max(2, word.Length))
                return i;
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            if (string.Equals(tokens[i].Text, word, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static List<Token> Tokenize(string sentence)
    {
        var list = new List<Token>();
        if (string.IsNullOrEmpty(sentence)) return list;
        var i = 0;
        while (i < sentence.Length)
        {
            if (char.IsLetter(sentence[i]) || sentence[i] is '-' or '\'' or '\u2019')
            {
                var start = i;
                while (i < sentence.Length && (char.IsLetter(sentence[i]) || sentence[i] is '-' or '\'' or '\u2019'))
                    i++;
                list.Add(new Token(sentence[start..i], start));
            }
            else i++;
        }

        return list;
    }

    private sealed record Token(string Text, int Start);
}
