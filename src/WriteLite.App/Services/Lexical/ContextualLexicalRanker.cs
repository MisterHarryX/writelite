namespace WriteLite.Services.Lexical;

/// <summary>
/// Deterministic contextual ranker. Optional Qwen path is not required —
/// uses sentence token overlap, POS match, and pack relevance seeds.
/// </summary>
public sealed class ContextualLexicalRanker : IContextualLexicalRanker
{
    public IReadOnlyList<LexicalSuggestion> RankSynonyms(
        IReadOnlyList<LexicalSuggestion> candidates,
        string word,
        string sentence,
        LexicalPartOfSpeech partOfSpeech)
    {
        if (candidates.Count == 0) return candidates;

        var tokens = Tokenize(sentence);
        var wordLower = word.ToLowerInvariant();

        return candidates
            .Select(c =>
            {
                var score = c.Relevance;
                if (c.PartOfSpeech == partOfSpeech && partOfSpeech != LexicalPartOfSpeech.Unknown)
                    score += 0.25;
                if (tokens.Contains(c.Value.ToLowerInvariant())) score -= 0.4; // already in sentence
                if (string.Equals(c.Value, word, StringComparison.OrdinalIgnoreCase)) score -= 1.0;
                if (c.Label is not null && tokens.Any(t => c.Label.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    score += 0.1;
                if (c.Value.StartsWith(wordLower.AsSpan(0, Math.Min(2, wordLower.Length)), StringComparison.OrdinalIgnoreCase))
                    score += 0.05;
                return c with { Relevance = Math.Clamp(score, 0, 1) };
            })
            .Where(c => !string.Equals(c.Value, word, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Relevance)
            .ThenBy(c => c.Value, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
    }

    private static HashSet<string> Tokenize(string sentence)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(sentence)) return set;
        var current = new System.Text.StringBuilder();
        foreach (var ch in sentence)
        {
            if (char.IsLetter(ch) || ch is '-' or '\'')
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                set.Add(current.ToString().ToLowerInvariant());
                current.Clear();
            }
        }

        if (current.Length > 0) set.Add(current.ToString().ToLowerInvariant());
        return set;
    }
}
