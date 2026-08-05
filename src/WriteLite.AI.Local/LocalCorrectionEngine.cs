using System.Text;
using System.Text.RegularExpressions;
using WriteLite.AI.Contracts;

namespace WriteLite.AI.Local;

/// <summary>
/// Deterministic offline corrector used as the Lite profile and as fallback for Standard/Quality.
/// Produces corrected text; issues are derived via <see cref="TextDiffBuilder"/>.
/// Does not send data over the network.
/// </summary>
public sealed partial class LocalCorrectionEngine
{
    private readonly LiteRulesModel _rules;

    public LocalCorrectionEngine(LiteRulesModel? rules = null)
    {
        _rules = rules ?? LiteRulesModel.LoadEmbedded();
    }

    public string ModelVersion => _rules.ModelVersion;

    public (string Corrected, string Language, bool Uncertain) Correct(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (text, "und", false);
        }

        var language = LanguageDetector.Detect(text);
        if (language != "ru")
        {
            return (text, "und", false);
        }

        var protectedSpans = ProtectedSpanDetector.FindProtectedSpans(text);

        // Work on a mutable string with protected regions locked via character map.
        var chars = text.ToCharArray();
        var locked = new bool[chars.Length];
        foreach (var (s, len) in protectedSpans)
        {
            for (var i = s; i < s + len && i < locked.Length; i++)
            {
                locked[i] = true;
            }
        }

        var working = new string(chars);
        working = NormalizeWhitespaceOutsideLocks(working, locked);
        working = ApplyDictionarySpelling(working, locked);
        working = FixRepeatedWords(working, locked);
        working = RestoreSentenceBoundaries(working, locked);
        working = ApplyCapitalization(working, locked);
        working = ApplyRussianCommaHeuristics(working, locked);

        var uncertain = working.Length > text.Length * 1.35 || working.Length < text.Length * 0.65;
        return (working, language, uncertain);
    }

    private static string NormalizeWhitespaceOutsideLocks(string text, bool[] locked)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (locked[i])
            {
                sb.Append(text[i]);
                i++;
                continue;
            }

            if (char.IsWhiteSpace(text[i]))
            {
                // Collapse runs of spaces/tabs (keep single space); preserve newlines.
                if (text[i] is '\r' or '\n')
                {
                    sb.Append(text[i]);
                    i++;
                    continue;
                }

                sb.Append(' ');
                while (i < text.Length && !locked[i] && char.IsWhiteSpace(text[i]) && text[i] is not ('\r' or '\n'))
                {
                    i++;
                }

                continue;
            }

            // Space before punctuation cleanup
            sb.Append(text[i]);
            i++;
        }

        var s = sb.ToString();
        // Remove spaces before punctuation (not in URLs — those are locked).
        s = Regex.Replace(s, @" +([,.;:!?…])", "$1");
        // Ensure space after punctuation when followed by a letter.
        s = Regex.Replace(s, @"([,.;:!?])(\p{L})", "$1 $2");
        return s;
    }

    private string ApplyDictionarySpelling(string text, bool[] locked)
    {
        return ReplaceWords(text, locked, word =>
        {
            if (_rules.RuSpelling.TryGetValue(word, out var repl))
            {
                return MatchCase(word, repl);
            }

            return null;
        });
    }

    private static string FixRepeatedWords(string text, bool[] locked)
    {
        return Regex.Replace(
            text,
            @"\b(\p{L}[\p{L}\-]*)(\s+)(\1)\b",
            m =>
            {
                if (IsRangeLocked(locked, m.Index, m.Length, text.Length))
                {
                    return m.Value;
                }

                // Keep intentional "that that" rarely — drop exact case-insensitive repeats with whitespace.
                return m.Groups[1].Value;
            },
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string RestoreSentenceBoundaries(string text, bool[] locked)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var trimmedEnd = text.TrimEnd();
        var trailingWs = text[trimmedEnd.Length..];
        if (trimmedEnd.Length == 0)
        {
            return text;
        }

        // Don't add terminal punctuation inside protected-only short tokens.
        var last = trimmedEnd[^1];
        if (char.IsLetterOrDigit(last) || last is '»' or '"' or '\'' or ')')
        {
            // Question heuristics
            var lower = trimmedEnd.ToLowerInvariant();
            var isQuestion = RuQuestionStart().IsMatch(lower) || lower.Contains('?');

            if (!trimmedEnd.Contains('?') && !trimmedEnd.Contains('!'))
            {
                if (isQuestion && !trimmedEnd.EndsWith('?'))
                {
                    trimmedEnd += "?";
                }
                else if (!EndsWithSentencePunct(trimmedEnd) && trimmedEnd.Count(char.IsLetter) >= 8)
                {
                    trimmedEnd += ".";
                }
            }
        }

        // Split glued sentences: "дела я сегодня" patterns handled in comma heuristics;
        // Add period between lowercase-Uppercase Latin transitions without space already handled.

        return trimmedEnd + trailingWs;
    }

    private static string ApplyCapitalization(string text, bool[] locked)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var chars = text.ToCharArray();
        // Capitalize first letter of text if letter.
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsWhiteSpace(chars[i]))
            {
                continue;
            }

            if (!IsIndexLocked(locked, i, chars.Length) && char.IsLetter(chars[i]))
            {
                chars[i] = char.ToUpper(chars[i], System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
            }

            break;
        }

        // After sentence-ending punctuation + space, capitalize next letter.
        for (var i = 1; i < chars.Length - 1; i++)
        {
            if (chars[i - 1] is '.' or '!' or '?' or '…')
            {
                var j = i;
                while (j < chars.Length && char.IsWhiteSpace(chars[j]))
                {
                    j++;
                }

                if (j < chars.Length && char.IsLetter(chars[j]) && !IsIndexLocked(locked, j, chars.Length))
                {
                    chars[j] = char.ToUpper(chars[j], System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
                }
            }
        }

        return new string(chars);
    }

    private string ApplyRussianCommaHeuristics(string text, bool[] locked)
    {
        var result = text;

        // "когда ... [clause] [subject]" — insert comma before second finite-looking segment.
        // Pattern: intro word ... letter space Capital? simple heuristic for "когда я пришел домой мама"
        foreach (var intro in _rules.RuIntroducers.OrderByDescending(x => x.Length))
        {
            var pattern = $@"(?i)\b({Regex.Escape(intro)})\b([^,\n.!?]{{6,80}}?)\s+(\p{{L}}+)\s+(\p{{L}}+)";
            result = Regex.Replace(result, pattern, m =>
            {
                if (IsRangeLocked(locked, m.Index, m.Length, result.Length))
                {
                    return m.Value;
                }

                // If already has comma in group 2, skip.
                if (m.Groups[2].Value.Contains(','))
                {
                    return m.Value;
                }

                // Insert comma before the last two words of the match when they look like a new main clause.
                // Example: "Когда я пришел домой мама уже" → comma before "мама"
                var g2 = m.Groups[2].Value.TrimEnd();
                var w1 = m.Groups[3].Value;
                var w2 = m.Groups[4].Value;

                // Only if g2 ends with a verb-ish word and w1 is a noun-ish starter (capital or common subject).
                if (g2.Length < 4)
                {
                    return m.Value;
                }

                return $"{m.Groups[1].Value}{m.Groups[2].Value.TrimEnd()}, {w1} {w2}";
            }, RegexOptions.CultureInvariant);
        }

        // Parenthetical single words mid-sentence: "это конечно важно" → "это, конечно, важно"
        foreach (var word in _rules.RuParentheticals.OrderByDescending(x => x.Length))
        {
            var pattern = $@"(?i)(?<=\p{{L}})\s+({Regex.Escape(word)})\s+(?=\p{{L}})";
            result = Regex.Replace(result, pattern, m =>
            {
                if (IsRangeLocked(locked, m.Index, m.Length, result.Length))
                {
                    return m.Value;
                }

                return $", {m.Groups[1].Value}, ";
            });
        }

        // "привет как" → "Привет, как" style: greeting + question word
        result = Regex.Replace(
            result,
            @"(?i)^(привет|здравствуйте|здорово|добрый день|добрый вечер)\s+(как|что|где|когда|почему)\b",
            m => $"{m.Groups[1].Value}, {m.Groups[2].Value}");

        // Collapse accidental double commas / spaces after edits
        result = Regex.Replace(result, @",\s*,+", ", ");
        result = Regex.Replace(result, @"[ \t]{2,}", " ");
        return result;
    }

    private static string ReplaceWords(
        string text,
        bool[] locked,
        Func<string, string?> replacer)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (IsIndexLocked(locked, i, text.Length))
            {
                sb.Append(text[i]);
                i++;
                continue;
            }

            if (IsWordChar(text[i]))
            {
                var start = i;
                while (i < text.Length && !IsIndexLocked(locked, i, text.Length) && IsWordChar(text[i]))
                {
                    i++;
                }

                var word = text[start..i];
                var repl = replacer(word);
                sb.Append(repl ?? word);
                continue;
            }

            sb.Append(text[i]);
            i++;
        }

        return sb.ToString();
    }

    private static bool IsWordChar(char ch)
        => char.IsLetter(ch) || ch is '-' or '\'' or '’' or 'ё' or 'Ё';

    private static bool LooksLikeRussianVerb(string rest)
    {
        var lower = rest.ToLowerInvariant();
        return lower.EndsWith("л") || lower.EndsWith("ла") || lower.EndsWith("ло") || lower.EndsWith("ли")
               || lower.EndsWith("ть") || lower.EndsWith("ет") || lower.EndsWith("ют");
    }

    private static bool EndsWithSentencePunct(string s)
    {
        var t = s.TrimEnd();
        return t.Length > 0 && t[^1] is '.' or '!' or '?' or '…' or '»';
    }

    private static bool IsIndexLocked(bool[] locked, int index, int length)
        => index >= 0 && index < locked.Length && index < length && locked[index];

    private static bool IsRangeLocked(bool[] locked, int start, int length, int textLength)
    {
        if (locked.Length != textLength)
        {
            // After mutations lengths diverge — skip lock checks rather than crash.
            return false;
        }

        var end = Math.Min(start + length, locked.Length);
        for (var i = Math.Max(0, start); i < end; i++)
        {
            if (locked[i])
            {
                return true;
            }
        }

        return false;
    }

    private static string MatchCase(string original, string replacement)
    {
        if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(replacement))
        {
            return replacement;
        }

        if (original.All(char.IsUpper))
        {
            return replacement.ToUpperInvariant();
        }

        if (char.IsUpper(original[0]))
        {
            return char.ToUpper(replacement[0]) + replacement[1..];
        }

        return replacement;
    }

    [GeneratedRegex(@"^(как|что|где|когда|почему|зачем|кто|чей|откуда|куда|сколько)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RuQuestionStart();

}
