using System.Text;

namespace WriteLite.Services.Lexical;

/// <summary>
/// The one place a piece of selected text becomes a dictionary query.
/// </summary>
/// <remarks>
/// Before this existed there were three ways to get a word into a dictionary and none
/// of them met: the editor's panel took <c>Editor.Selection.Text</c> verbatim and only
/// accepted it if every character was a letter, the double-click card resolved a word
/// through <see cref="WordRangeResolver"/> from a raw text index, and the dictionary
/// page could only be reached by typing into its own search box. A selection made by
/// double-clicking a word at the end of a sentence carries the full stop with it, so
/// «слова.» failed the editor's all-letters test and the menu entry did nothing —
/// which is the "select a word, nothing happens" bug.
///
/// Normalisation is deliberately generous about what surrounds a word and strict about
/// what a word is. Quotation marks, brackets, dashes and terminal punctuation are the
/// debris of selecting text; they are stripped. Hyphens and apostrophes inside a word
/// are part of it — «кто-то» and «don't» are single entries — so they survive.
/// </remarks>
public static class WordNavigation
{
    /// <summary>Longest selection still treated as a word. Beyond this it is prose, not a lookup.</summary>
    private const int MaxWordLength = 48;

    /// <summary>
    /// Turns raw selected text into the word to look up, or null when there is none.
    /// </summary>
    /// <remarks>
    /// A multi-word selection resolves to its first word rather than being rejected:
    /// someone who selects a phrase and asks to open the dictionary wants an article,
    /// and refusing to answer would send them back to retype it — the exact thing this
    /// flow exists to avoid.
    /// </remarks>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Invisible characters travel with text copied out of PDFs and web pages, and
        // a soft hyphen in the middle of a word would otherwise split it in two.
        var cleaned = new StringBuilder(raw.Length);
        foreach (var character in raw)
        {
            if (character is '­' or '​' or '‌' or '‍' or '﻿')
            {
                continue;
            }

            cleaned.Append(character switch
            {
                ' ' or ' ' or ' ' => ' ',
                '’' or '‘' or 'ʼ' => '\'',
                _ => character
            });
        }

        var text = cleaned.ToString();

        // The first run of word characters. Everything before it is an opening quote
        // or bracket, everything after is punctuation or the rest of the sentence.
        // A leading hyphen or apostrophe is skipped too: it only counts as part of a
        // word when it sits between letters.
        var start = 0;
        while (start < text.Length && !char.IsLetter(text[start]))
        {
            start++;
        }

        if (start >= text.Length)
        {
            return null;
        }

        var end = start;
        while (end < text.Length && IsWordChar(text[end]))
        {
            end++;
        }

        // A hyphen or apostrophe only belongs to the word when a letter follows it:
        // "кто-то" keeps its hyphen, "слово-" (a line-wrap remnant) loses it.
        while (end > start && text[end - 1] is '-' or '\'')
        {
            end--;
        }

        if (end <= start)
        {
            return null;
        }

        var word = text[start..end];
        return word.Length is > 0 and <= MaxWordLength ? word : null;
    }

    /// <summary>True when a selection would produce a dictionary query.</summary>
    public static bool CanOpen(string? raw) => Normalize(raw) is not null;

    /// <summary>
    /// Word characters: letters plus the two marks that live inside words.
    /// </summary>
    /// <remarks>
    /// Digits are excluded on purpose. "COVID-19" and "release-v1" are identifiers,
    /// not entries, and a dictionary has nothing to say about them.
    /// </remarks>
    private static bool IsWordChar(char character) =>
        char.IsLetter(character) || character is '-' or '\'';
}
