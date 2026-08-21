using System.Globalization;
using System.Text;

namespace WriteLite.AI.Local;

/// <summary>
/// BERT WordPiece tokenizer, enough of it to feed a Russian encoder at runtime.
/// </summary>
/// <remarks>
/// The reranker is a fine-tuned BERT-family encoder, so its input has to be
/// tokenized exactly the way it was during training — a different segmentation
/// produces different embeddings and silently degrades the scores rather than
/// failing. This implements the same pipeline HuggingFace's `BertTokenizer`
/// uses with the settings rubert-tiny2 ships: NFD accent stripping off, lower
/// casing on, punctuation split out, then greedy longest-match-first subword
/// segmentation against the vocabulary.
///
/// `ai/scripts/export_reranker_onnx.py` writes the vocabulary next to the model
/// and asserts that this implementation and the Python tokenizer agree on a
/// sample of the training data.
/// </remarks>
public sealed class WordPieceTokenizer
{
    private const string ContinuationPrefix = "##";
    private const int MaxCharsPerWord = 100;

    private readonly Dictionary<string, int> _vocabulary;
    private readonly bool _lowercase;

    public WordPieceTokenizer(IReadOnlyDictionary<string, int> vocabulary, bool lowercase = true)
    {
        _vocabulary = new Dictionary<string, int>(vocabulary, StringComparer.Ordinal);
        _lowercase = lowercase;
        UnknownId = Lookup("[UNK]", 0);
        ClsId = Lookup("[CLS]", 1);
        SepId = Lookup("[SEP]", 2);
        PadId = Lookup("[PAD]", 3);
    }

    public int UnknownId { get; }
    public int ClsId { get; }
    public int SepId { get; }
    public int PadId { get; }
    public int VocabularySize => _vocabulary.Count;

    private int Lookup(string token, int fallback)
        => _vocabulary.TryGetValue(token, out var id) ? id : fallback;

    /// <summary>Loads a `vocab.txt` — one token per line, id equal to line index.</summary>
    public static WordPieceTokenizer? LoadFromVocabFile(string path, bool lowercase = true)
    {
        if (!File.Exists(path)) return null;
        var vocabulary = new Dictionary<string, int>(StringComparer.Ordinal);
        var index = 0;
        foreach (var line in File.ReadLines(path))
        {
            // Trailing \r on a CRLF file would become part of the token.
            vocabulary[line.TrimEnd('\r', '\n')] = index++;
        }

        return vocabulary.Count == 0 ? null : new WordPieceTokenizer(vocabulary, lowercase);
    }

    public sealed record Encoding(int[] InputIds, int[] AttentionMask, int[] TokenTypeIds);

    /// <summary>Encodes one sequence, truncating and padding to <paramref name="maxLength"/>.</summary>
    public Encoding Encode(string text, int maxLength = 64)
    {
        var ids = new List<int>(maxLength) { ClsId };
        foreach (var token in Tokenize(text))
        {
            if (ids.Count >= maxLength - 1) break;
            ids.Add(token);
        }

        ids.Add(SepId);

        var inputIds = new int[maxLength];
        var attention = new int[maxLength];
        for (var i = 0; i < maxLength; i++)
        {
            if (i < ids.Count)
            {
                inputIds[i] = ids[i];
                attention[i] = 1;
            }
            else
            {
                inputIds[i] = PadId;
            }
        }

        return new Encoding(inputIds, attention, new int[maxLength]);
    }

    /// <summary>
    /// Encodes a sequence <em>pair</em> as <c>[CLS] a [SEP] b [SEP]</c>, with segment ids 0
    /// and 1 — the shape HuggingFace produces for <c>tokenizer(text, text_pair)</c>.
    /// </summary>
    /// <remarks>
    /// <para>Needed because a punctuation decision is a question about a boundary, and
    /// splitting the sentence at that boundary lets BERT's segment embeddings mark it exactly,
    /// with no marker token and no vocabulary change.</para>
    ///
    /// <para>Truncation takes from whichever side is currently longer rather than from the
    /// tail. Truncating only the second segment would mean that in a long sentence the model
    /// sees all of the left context and none of the right, and the right context is half the
    /// evidence for a comma.</para>
    /// </remarks>
    public Encoding EncodePair(string first, string second, int maxLength = 64)
    {
        var left = Tokenize(first).ToList();
        var right = Tokenize(second).ToList();

        // [CLS] + left + [SEP] + right + [SEP]
        var budget = maxLength - 3;
        while (left.Count + right.Count > budget && (left.Count > 0 || right.Count > 0))
        {
            if (left.Count >= right.Count) left.RemoveAt(0);
            else right.RemoveAt(right.Count - 1);
        }

        var inputIds = new int[maxLength];
        var attention = new int[maxLength];
        var segments = new int[maxLength];

        var cursor = 0;
        void Emit(int id, int segment)
        {
            if (cursor >= maxLength) return;
            inputIds[cursor] = id;
            attention[cursor] = 1;
            segments[cursor] = segment;
            cursor++;
        }

        Emit(ClsId, 0);
        foreach (var id in left) Emit(id, 0);
        Emit(SepId, 0);
        foreach (var id in right) Emit(id, 1);
        Emit(SepId, 1);

        for (var i = cursor; i < maxLength; i++) inputIds[i] = PadId;
        return new Encoding(inputIds, attention, segments);
    }

    /// <summary>Token ids for the text, without the special tokens.</summary>
    public IEnumerable<int> Tokenize(string text)
    {
        foreach (var word in SplitOnWhitespaceAndPunctuation(text))
        {
            foreach (var id in TokenizeWord(word))
            {
                yield return id;
            }
        }
    }

    private IEnumerable<string> SplitOnWhitespaceAndPunctuation(string text)
    {
        var buffer = new StringBuilder();
        foreach (var raw in text)
        {
            var ch = _lowercase ? char.ToLowerInvariant(raw) : raw;
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                if (buffer.Length > 0) { yield return buffer.ToString(); buffer.Clear(); }
                continue;
            }

            if (IsPunctuation(ch))
            {
                if (buffer.Length > 0) { yield return buffer.ToString(); buffer.Clear(); }
                yield return ch.ToString();
                continue;
            }

            buffer.Append(ch);
        }

        if (buffer.Length > 0) yield return buffer.ToString();
    }

    // BERT treats every ASCII non-alphanumeric character as punctuation, not just
    // the Unicode punctuation categories; matching that exactly matters because
    // it changes where words are split.
    private static bool IsPunctuation(char ch)
    {
        if (ch is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~') return true;
        var category = CharUnicodeInfo.GetUnicodeCategory(ch);
        return category is UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation;
    }

    private IEnumerable<int> TokenizeWord(string word)
    {
        if (word.Length > MaxCharsPerWord)
        {
            yield return UnknownId;
            yield break;
        }

        // Greedy longest-match-first: take the longest prefix present in the
        // vocabulary, then keep matching the remainder as "##" continuations.
        var subwords = new List<int>();
        var start = 0;
        while (start < word.Length)
        {
            var end = word.Length;
            var matched = -1;
            while (start < end)
            {
                var piece = start == 0 ? word[start..end] : ContinuationPrefix + word[start..end];
                if (_vocabulary.TryGetValue(piece, out var id))
                {
                    matched = id;
                    break;
                }

                end--;
            }

            if (matched < 0)
            {
                // An unmatched fragment invalidates the whole word, as in BERT.
                yield return UnknownId;
                yield break;
            }

            subwords.Add(matched);
            start = end;
        }

        foreach (var id in subwords) yield return id;
    }
}
