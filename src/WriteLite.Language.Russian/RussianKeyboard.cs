namespace WriteLite.Language.Russian;

/// <summary>
/// Physical-keyboard knowledge for Russian typing: ЙЦУКЕН key adjacency, the
/// ЙЦУКЕН/QWERTY layout mapping, and Cyrillic/Latin homoglyphs.
/// </summary>
/// <remarks>
/// Edit distance alone treats every substitution as equally likely, which is
/// wrong for typing: "привет" -> "принет" (н and в are not neighbours) is a much
/// rarer slip than "привет" -> "поивет" (р and о are adjacent). Scoring uses
/// this to prefer candidates that a hand actually produces.
/// </remarks>
public static class RussianKeyboard
{
    // Rows as physically laid out on a standard Russian keyboard. Adjacency is
    // derived from these, including the vertical/diagonal offsets between rows.
    private static readonly string[] Rows =
    [
        "ё1234567890-=",
        "йцукенгшщзхъ",
        "фывапролджэ",
        "ячсмитьбю.",
    ];

    // Horizontal offset of each row relative to the one above, in half-keys.
    private static readonly double[] RowOffsets = [0.0, 0.5, 0.9, 1.4];

    private static readonly Dictionary<char, string> Adjacency = BuildAdjacency();

    private static readonly Dictionary<char, char> LatinToCyrillic = new()
    {
        ['q'] = 'й', ['w'] = 'ц', ['e'] = 'у', ['r'] = 'к', ['t'] = 'е', ['y'] = 'н',
        ['u'] = 'г', ['i'] = 'ш', ['o'] = 'щ', ['p'] = 'з', ['['] = 'х', [']'] = 'ъ',
        ['a'] = 'ф', ['s'] = 'ы', ['d'] = 'в', ['f'] = 'а', ['g'] = 'п', ['h'] = 'р',
        ['j'] = 'о', ['k'] = 'л', ['l'] = 'д', [';'] = 'ж', ['\''] = 'э',
        ['z'] = 'я', ['x'] = 'ч', ['c'] = 'с', ['v'] = 'м', ['b'] = 'и', ['n'] = 'т',
        ['m'] = 'ь', [','] = 'б', ['.'] = 'ю', ['`'] = 'ё', ['/'] = '.',
    };

    private static readonly Dictionary<char, char> CyrillicToLatin = BuildReverseLayout();

    /// <summary>
    /// Latin characters that are visually identical (or near enough) to a
    /// Cyrillic letter. Mixed-script words are usually the result of a layout
    /// switch mid-word or a paste from a rendered document.
    /// </summary>
    private static readonly Dictionary<char, char> LatinHomoglyphs = new()
    {
        ['a'] = 'а', ['b'] = 'в', ['c'] = 'с', ['e'] = 'е', ['h'] = 'н', ['k'] = 'к',
        ['m'] = 'м', ['o'] = 'о', ['p'] = 'р', ['t'] = 'т', ['x'] = 'х', ['y'] = 'у',
        ['A'] = 'А', ['B'] = 'В', ['C'] = 'С', ['E'] = 'Е', ['H'] = 'Н', ['K'] = 'К',
        ['M'] = 'М', ['O'] = 'О', ['P'] = 'Р', ['T'] = 'Т', ['X'] = 'Х', ['Y'] = 'У',
    };

    private static Dictionary<char, string> BuildAdjacency()
    {
        var positions = new Dictionary<char, (int Row, double Column)>();
        for (var row = 0; row < Rows.Length; row++)
        {
            for (var column = 0; column < Rows[row].Length; column++)
            {
                positions[Rows[row][column]] = (row, column + RowOffsets[row]);
            }
        }

        var result = new Dictionary<char, string>();
        foreach (var (key, position) in positions)
        {
            var neighbours = positions
                .Where(other => other.Key != key
                    && Math.Abs(other.Value.Row - position.Row) <= 1
                    && Math.Abs(other.Value.Column - position.Column) <= 1.0)
                .Select(other => other.Key)
                .OrderBy(c => c)
                .ToArray();
            result[key] = new string(neighbours);
        }

        return result;
    }

    private static Dictionary<char, char> BuildReverseLayout()
    {
        var reverse = new Dictionary<char, char>();
        foreach (var (latin, cyrillic) in LatinToCyrillic)
        {
            reverse.TryAdd(cyrillic, latin);
        }

        return reverse;
    }

    /// <summary>Keys physically adjacent to <paramref name="key"/> on ЙЦУКЕН.</summary>
    public static string Neighbours(char key)
        => Adjacency.TryGetValue(char.ToLowerInvariant(key), out var value) ? value : string.Empty;

    public static bool AreNeighbours(char a, char b)
        => Neighbours(a).Contains(char.ToLowerInvariant(b));

    /// <summary>True when the word is entirely characters produced by a Latin keyboard.</summary>
    public static bool IsLatinLayoutText(string word)
    {
        if (string.IsNullOrEmpty(word)) return false;
        foreach (var ch in word)
        {
            var lower = char.ToLowerInvariant(ch);
            if (!LatinToCyrillic.ContainsKey(lower) && !(lower >= 'a' && lower <= 'z')) return false;
        }

        return true;
    }

    /// <summary>
    /// True when every character is a Cyrillic letter — the precondition for asking whether
    /// the token is an English word typed with the Russian layout active.
    /// </summary>
    /// <remarks>
    /// Deliberately strict about digits and punctuation as well as about Latin characters: a
    /// token like «файл2» or «привет-мир» is not a layout accident, and a token that mixes
    /// scripts is a homoglyph case with its own path.
    /// </remarks>
    public static bool IsCyrillicText(string word)
    {
        if (string.IsNullOrEmpty(word)) return false;
        foreach (var ch in word)
        {
            if (!RussianLanguageProfile.IsCyrillicLetter(ch)) return false;
        }

        return true;
    }

    /// <summary>Reinterprets Latin keystrokes as the Cyrillic letters on the same keys.</summary>
    public static string? LatinToRussianLayout(string word)
    {
        if (string.IsNullOrEmpty(word)) return null;
        var buffer = new char[word.Length];
        for (var i = 0; i < word.Length; i++)
        {
            var lower = char.ToLowerInvariant(word[i]);
            if (!LatinToCyrillic.TryGetValue(lower, out var mapped)) return null;
            buffer[i] = char.IsUpper(word[i]) ? char.ToUpperInvariant(mapped) : mapped;
        }

        return new string(buffer);
    }

    /// <summary>Reinterprets Cyrillic keystrokes as the Latin letters on the same keys.</summary>
    public static string? RussianToLatinLayout(string word)
    {
        if (string.IsNullOrEmpty(word)) return null;
        var buffer = new char[word.Length];
        for (var i = 0; i < word.Length; i++)
        {
            var lower = char.ToLowerInvariant(word[i]);
            if (!CyrillicToLatin.TryGetValue(lower, out var mapped)) return null;
            buffer[i] = char.IsUpper(word[i]) ? char.ToUpperInvariant(mapped) : mapped;
        }

        return new string(buffer);
    }

    /// <summary>True when the word mixes Cyrillic letters with Latin lookalikes.</summary>
    public static bool HasMixedScript(string word)
    {
        var cyrillic = false;
        var latin = false;
        foreach (var ch in word)
        {
            if (RussianLanguageProfile.IsCyrillicLetter(ch)) cyrillic = true;
            else if (LatinHomoglyphs.ContainsKey(ch)) latin = true;
            else if (char.IsLetter(ch)) latin = true;
        }

        return cyrillic && latin;
    }

    /// <summary>Rewrites Latin lookalikes as their Cyrillic twins, or null when nothing changed.</summary>
    public static string? NormalizeHomoglyphs(string word)
    {
        if (string.IsNullOrEmpty(word)) return null;
        char[]? buffer = null;
        for (var i = 0; i < word.Length; i++)
        {
            if (!LatinHomoglyphs.TryGetValue(word[i], out var cyrillic)) continue;
            buffer ??= word.ToCharArray();
            buffer[i] = cyrillic;
        }

        return buffer is null ? null : new string(buffer);
    }

    /// <summary>
    /// Fraction of substitutions between two equal-length-ish words that are
    /// explained by adjacent keys. Higher means "more likely a typing slip".
    /// </summary>
    public static double AdjacencyScore(string original, string candidate)
    {
        var length = Math.Min(original.Length, candidate.Length);
        if (length == 0) return 0.0;
        var differing = 0;
        var adjacent = 0;
        for (var i = 0; i < length; i++)
        {
            var a = RussianFormIndex.Fold(original[i]);
            var b = RussianFormIndex.Fold(candidate[i]);
            if (a == b) continue;
            differing++;
            if (AreNeighbours(a, b)) adjacent++;
        }

        return differing == 0 ? 0.0 : (double)adjacent / differing;
    }
}
