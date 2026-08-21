using System.Globalization;
using System.Text;

namespace WriteLite.Services.Lexical;

/// <summary>
/// Resolves a Russian word range from a UTF-16 index or selection, handling Cyrillic,
/// hyphenated words, apostrophes, punctuation adjacency, emoji, and surrogates.
/// </summary>
public sealed class WordRangeResolver(ILexicalLanguageDetector? languageDetector = null) : IWordRangeResolver
{
    private readonly ILexicalLanguageDetector _languages = languageDetector ?? new LexicalLanguageDetector();

    public WordRange? ResolveFromText(string text, int caretOrClickIndex)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (caretOrClickIndex < 0) caretOrClickIndex = 0;
        if (caretOrClickIndex > text.Length) caretOrClickIndex = text.Length;

        var index = caretOrClickIndex;
        if (index == text.Length) index = Math.Max(0, index - 1);
        if (index < text.Length && !IsWordChar(text, index) && index > 0 && IsWordChar(text, index - 1))
            index--;

        if (index >= text.Length || !IsWordChar(text, index))
        {
            // Search nearby for a word (double-click near punctuation).
            var found = FindNearestWordIndex(text, index);
            if (found < 0) return null;
            index = found;
        }

        var start = index;
        while (start > 0 && IsWordChar(text, start - 1))
            start = MoveLeft(text, start);

        var end = index;
        while (end < text.Length && IsWordChar(text, end))
            end = MoveRight(text, end);

        if (end <= start) return null;
        var word = text.Substring(start, end - start);
        if (string.IsNullOrWhiteSpace(word)) return null;

        var sentence = ExtractSentence(text, start, end - start);
        var language = _languages.DetectWord(word);
        if (!IsLookupCandidate(word, language)) return null;
        // "release-v1" stops the word scan at the digit, leaving "release-v";
        // a token touching a digit or underscore is part of an identifier.
        if (language == LexicalLanguage.English && TouchesIdentifierChar(text, start, end))
            return null;

        return new WordRange(start, end - start, word, sentence, language);
    }

    /// <summary>
    /// Russian words are always looked up. English words are looked up only when
    /// they read as prose rather than code: identifiers such as "release-v1" or
    /// "API_TOKEN" must not open a dictionary card.
    /// </summary>
    private static bool IsLookupCandidate(string word, LexicalLanguage language)
    {
        if (language == LexicalLanguage.Russian) return true;
        if (language != LexicalLanguage.English) return false;

        foreach (var ch in word)
        {
            if (char.IsDigit(ch) || ch == '_') return false;
        }

        return word.Length > 1;
    }

    private static bool TouchesIdentifierChar(string text, int start, int end)
    {
        if (start > 0 && (char.IsDigit(text[start - 1]) || text[start - 1] == '_')) return true;
        if (end < text.Length && (char.IsDigit(text[end]) || text[end] == '_')) return true;
        return false;
    }

    public WordRange? ResolveFromSelection(string text, int selectionStart, int selectionLength)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (selectionStart < 0 || selectionLength <= 0 || selectionStart + selectionLength > text.Length)
            return ResolveFromText(text, selectionStart);

        var selected = text.Substring(selectionStart, selectionLength);
        // If selection is a single word (possibly with trailing space trimmed), use it.
        var trimmed = selected.Trim();
        if (trimmed.Length == 0) return ResolveFromText(text, selectionStart);

        var trimStart = selected.IndexOf(trimmed, StringComparison.Ordinal);
        var start = selectionStart + Math.Max(0, trimStart);
        // Expand to full word boundaries if selection is partial.
        var expanded = ResolveFromText(text, start);
        if (expanded is null) return null;

        // Prefer exact selection when it is fully inside one word or equals the word.
        if (string.Equals(expanded.Word, trimmed, StringComparison.Ordinal)
            || (start >= expanded.Start && start + trimmed.Length <= expanded.End))
        {
            return expanded;
        }

        var language = _languages.DetectWord(trimmed);
        return IsLookupCandidate(trimmed, language)
            ? new WordRange(start, trimmed.Length, trimmed, ExtractSentence(text, start, trimmed.Length), language)
            : null;
    }

    public static string ExtractSentence(string text, int wordStart, int wordLength)
    {
        if (string.IsNullOrEmpty(text) || wordStart < 0 || wordStart >= text.Length) return text ?? string.Empty;

        var start = wordStart;
        while (start > 0)
        {
            var ch = text[start - 1];
            if (ch is '.' or '!' or '?' or '\n' or '\r') break;
            start--;
        }

        var end = Math.Min(text.Length, wordStart + Math.Max(wordLength, 0));
        while (end < text.Length)
        {
            var ch = text[end];
            if (ch is '.' or '!' or '?' or '\n' or '\r')
            {
                end++;
                break;
            }

            end++;
        }

        return text.Substring(start, end - start).Trim();
    }

    private static int FindNearestWordIndex(string text, int index)
    {
        for (var radius = 0; radius < 8; radius++)
        {
            var left = index - radius;
            var right = index + radius;
            if (left >= 0 && left < text.Length && IsWordChar(text, left)) return left;
            if (right >= 0 && right < text.Length && IsWordChar(text, right)) return right;
        }

        return -1;
    }

    private static bool IsWordChar(string text, int index)
    {
        if (index < 0 || index >= text.Length) return false;
        var ch = text[index];
        if (char.IsHighSurrogate(ch))
        {
            // Emoji / non-BMP are not word letters for dictionary lookup.
            return false;
        }

        if (char.IsLetter(ch) || ch is '_' or '\u00B4') return true;

        // Internal hyphen or apostrophe: only if surrounded by letters.
        if (ch is '-' or '\'' or '\u2019' or '\u02BC')
        {
            return HasLetterBefore(text, index) && HasLetterAfter(text, index);
        }

        return false;
    }

    private static bool HasLetterBefore(string text, int index)
    {
        var i = index - 1;
        while (i >= 0)
        {
            if (char.IsHighSurrogate(text[i])) return false;
            if (char.IsLetter(text[i])) return true;
            if (!char.IsWhiteSpace(text[i])) return false;
            i--;
        }

        return false;
    }

    private static bool HasLetterAfter(string text, int index)
    {
        var i = index + 1;
        while (i < text.Length)
        {
            if (char.IsHighSurrogate(text[i])) return false;
            if (char.IsLetter(text[i])) return true;
            if (!char.IsWhiteSpace(text[i])) return false;
            i++;
        }

        return false;
    }

    private static int MoveLeft(string text, int index)
    {
        if (index <= 0) return 0;
        var prev = index - 1;
        if (prev > 0 && char.IsLowSurrogate(text[prev]) && char.IsHighSurrogate(text[prev - 1]))
            return prev - 1;
        return prev;
    }

    private static int MoveRight(string text, int index)
    {
        if (index >= text.Length) return text.Length;
        if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            return index + 2;
        return index + 1;
    }
}
