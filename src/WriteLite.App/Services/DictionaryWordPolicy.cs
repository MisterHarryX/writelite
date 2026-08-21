using WriteLite.Models;

namespace WriteLite.Services;

/// <summary>
/// What «В словарь» is allowed to add.
/// </summary>
/// <remarks>
/// <para>§18. The user's dictionary is permanent and it silences every future finding for
/// whatever goes into it, so the one thing that must never happen is a fragment landing in
/// it. The button sits on a card whose <c>Original</c> is normally exactly the token the
/// spelling pass matched — but not always: a punctuation finding's original is the letter
/// before the mark, a phrase-level finding's original is several words, and an insertion's
/// original is empty. None of those are words, and adding «робота» because the card happened
/// to show part of «роботает» would quietly stop WriteLite from ever flagging that typo
/// again.</para>
///
/// <para>The test is deliberately narrow: one token, letters and interior hyphens or
/// apostrophes only, at least two characters. Anything else is refused rather than trimmed
/// into shape, because a guess about which part of a span the user meant is exactly the
/// mistake this exists to prevent.</para>
/// </remarks>
public static class DictionaryWordPolicy
{
    /// <summary>The lexical token this finding is about, or null when there is not exactly one.</summary>
    public static string? ResolveWord(TextIssue issue)
        => ResolveWord(issue.Original);

    /// <summary>The lexical token in <paramref name="original"/>, or null.</summary>
    public static string? ResolveWord(string? original)
    {
        var candidate = (original ?? string.Empty).Trim();
        if (candidate.Length < 2) return null;

        for (var i = 0; i < candidate.Length; i++)
        {
            var character = candidate[i];
            if (char.IsLetter(character)) continue;

            // A hyphen or apostrophe belongs to the word only between two letters:
            // «кто-то», «п'ять». At an edge it is punctuation the span picked up.
            var isInteriorJoiner = character is '-' or '\'' or '’'
                                   && i > 0
                                   && i < candidate.Length - 1
                                   && char.IsLetter(candidate[i - 1])
                                   && char.IsLetter(candidate[i + 1]);
            if (!isInteriorJoiner) return null;
        }

        return candidate;
    }
}
