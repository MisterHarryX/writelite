using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace WriteLite.Language.Russian;

/// <summary>
/// Memory-resident index over every known Russian surface form.
/// </summary>
/// <remarks>
/// Backed by the minimal DAFSA produced by <c>ai/scripts/build_ru_form_index.py</c>.
/// Three million forms compress to a few megabytes of arcs, which is what makes
/// it practical to answer "is this a word" and "what real words are within two
/// edits of this" from the same structure, without the tens of millions of
/// deletion keys a SymSpell index would need at this scale.
///
/// Word ids are the lexicographic rank of the form, recovered during traversal
/// from the per-arc subtree counts, so the metadata array is a plain indexed
/// block with no keys of its own and can stay memory-mapped.
/// </remarks>
public sealed class RussianFormIndex : IDisposable
{
    private const int ArcSize = 12;
    private const int MetaSize = 12;
    private const uint NoTarget = 0xFFFFFFFFu;
    private const ushort FlagLastArc = 1 << 0;
    private const ushort FlagWordEnd = 1 << 1;
    private const int HeaderSize = 32;

    private readonly byte[] _arcs;
    private readonly char[] _alphabet;
    private readonly int _rootArc;
    private readonly MemoryMappedFile? _metaFile;
    private readonly MemoryMappedViewAccessor? _meta;
    private readonly string[] _lemmas;
    private readonly string[] _tags;
    private bool _disposed;

    private RussianFormIndex(
        byte[] arcs,
        char[] alphabet,
        int rootArc,
        int wordCount,
        MemoryMappedFile? metaFile,
        MemoryMappedViewAccessor? meta,
        string[] lemmas,
        string[] tags,
        TimeSpan loadTime,
        string directory)
    {
        _arcs = arcs;
        _alphabet = alphabet;
        _rootArc = rootArc;
        _metaFile = metaFile;
        _meta = meta;
        _lemmas = lemmas;
        _tags = tags;
        WordCount = wordCount;
        LoadTime = loadTime;
        SourceDirectory = directory;
    }

    public int WordCount { get; }
    public TimeSpan LoadTime { get; }
    public string SourceDirectory { get; }
    public bool HasMetadata => _meta is not null;

    public const string Source = "OpenCorpora 0.92 (rev. 417150) via pymorphy3-dicts-ru, plus the WriteLite curated modern-vocabulary pack";
    public const string License = "CC BY-SA (OpenCorpora data); CC0-1.0 (WriteLite curated pack)";

    /// <summary>Loads the index, or returns null when the artefacts are absent.</summary>
    public static RussianFormIndex? Load(string? directory = null, string prefix = "ru-forms")
    {
        var sw = Stopwatch.StartNew();
        var dir = ResolveDirectory(directory, prefix);
        if (dir is null) return null;

        var graphPath = Path.Combine(dir, prefix + ".wldawg");
        MemoryMappedFile? metaFile = null;
        MemoryMappedViewAccessor? meta = null;
        try
        {
            var bytes = File.ReadAllBytes(graphPath);
            if (bytes.Length < HeaderSize) return null;
            if (Encoding.ASCII.GetString(bytes, 0, 6) != "WLDAWG" || bytes[6] != 1) return null;

            var alphabetCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
            var arcCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
            var wordCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));
            var rootArc = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20));

            var alphabet = new char[alphabetCount];
            for (var i = 0; i < alphabetCount; i++)
            {
                alphabet[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(HeaderSize + i * 2));
            }

            var arcsStart = HeaderSize + alphabetCount * 2;
            if (bytes.Length < arcsStart + arcCount * ArcSize) return null;
            var arcs = new byte[arcCount * ArcSize];
            Buffer.BlockCopy(bytes, arcsStart, arcs, 0, arcs.Length);

            // Metadata is one 12-byte record per form (36 MB for three million
            // forms) and is only touched when ranking a handful of candidates,
            // so it stays mapped rather than resident.
            var metaPath = Path.Combine(dir, prefix + ".meta.bin");
            if (File.Exists(metaPath) && new FileInfo(metaPath).Length >= (long)wordCount * MetaSize)
            {
                metaFile = MemoryMappedFile.CreateFromFile(
                    new FileStream(metaPath, FileMode.Open, FileAccess.Read, FileShare.Read),
                    mapName: null,
                    capacity: 0,
                    MemoryMappedFileAccess.Read,
                    HandleInheritability.None,
                    leaveOpen: false);
                meta = metaFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            }

            var (lemmas, tags) = ReadStringTables(Path.Combine(dir, prefix + ".strings"));
            sw.Stop();
            return new RussianFormIndex(arcs, alphabet, rootArc, wordCount, metaFile, meta, lemmas, tags, sw.Elapsed, dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            meta?.Dispose();
            metaFile?.Dispose();
            return null;
        }
    }

    private static (string[] Lemmas, string[] Tags) ReadStringTables(string path)
    {
        if (!File.Exists(path)) return ([], []);
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 8 || Encoding.ASCII.GetString(bytes, 0, 5) != "WLSTR") return ([], []);
            var offset = 8;
            var first = ReadTable(bytes, ref offset);
            var second = ReadTable(bytes, ref offset);
            return (first, second);
        }
        catch (IOException)
        {
            return ([], []);
        }

        static string[] ReadTable(byte[] bytes, ref int offset)
        {
            if (offset + 8 > bytes.Length) return [];
            var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
            var blobLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4));
            offset += 8;
            if (offset + blobLength > bytes.Length) return [];
            var blob = Encoding.UTF8.GetString(bytes, offset, blobLength);
            offset += blobLength;
            var parts = blob.Split('\n');
            return parts.Length >= count ? parts : parts;
        }
    }

    private static string? ResolveDirectory(string? directory, string prefix)
    {
        foreach (var candidate in EnumerateRoots(directory))
        {
            if (candidate is null) continue;
            if (File.Exists(Path.Combine(candidate, prefix + ".wldawg"))) return candidate;
            var nested = Path.Combine(candidate, "resources", "lexical");
            if (File.Exists(Path.Combine(nested, prefix + ".wldawg"))) return nested;
        }

        return null;

        static IEnumerable<string?> EnumerateRoots(string? directory)
        {
            yield return directory;
            yield return AppContext.BaseDirectory;
            yield return Path.Combine(AppContext.BaseDirectory, "resources", "lexical");
            yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "resources", "lexical"));
            yield return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "resources", "lexical"));
        }
    }

    // ---- arc accessors ---------------------------------------------------

    private char ArcLabel(int arc)
    {
        var index = BinaryPrimitives.ReadUInt16LittleEndian(_arcs.AsSpan(arc * ArcSize));
        return _alphabet[index];
    }

    private ushort ArcFlags(int arc) => BinaryPrimitives.ReadUInt16LittleEndian(_arcs.AsSpan(arc * ArcSize + 2));

    private uint ArcTarget(int arc) => BinaryPrimitives.ReadUInt32LittleEndian(_arcs.AsSpan(arc * ArcSize + 4));

    private uint ArcSubtree(int arc) => BinaryPrimitives.ReadUInt32LittleEndian(_arcs.AsSpan(arc * ArcSize + 8));

    /// <summary>Folds case and ё/е, the two distinctions the index does not store separately.</summary>
    public static char Fold(char value)
    {
        var lower = char.ToLowerInvariant(value);
        return lower == 'ё' ? 'е' : lower;
    }

    // ---- exact lookup ----------------------------------------------------

    /// <summary>True when the word is a known Russian form, ignoring case and ё/е.</summary>
    public bool Contains(string word) => GetId(word) >= 0;

    /// <summary>
    /// Lexicographic rank of the form, or -1. Matching folds case and ё/е, so a
    /// query may follow more than one path (both "ежик" and "ёжик" may exist);
    /// the first accepting path wins, which is stable because arcs are sorted.
    /// </summary>
    public int GetId(string word)
    {
        if (string.IsNullOrEmpty(word) || _arcs.Length == 0) return -1;
        return Walk(_rootArc, word, 0, 0);
    }

    private int Walk(int nodeArc, string word, int position, int idSoFar)
    {
        if (nodeArc < 0) return -1;
        var target = Fold(word[position]);
        var id = idSoFar;

        for (var arc = nodeArc; ; arc++)
        {
            var flags = ArcFlags(arc);
            var label = Fold(ArcLabel(arc));
            if (label == target)
            {
                if (position == word.Length - 1)
                {
                    if ((flags & FlagWordEnd) != 0) return id;
                }
                else
                {
                    var next = ArcTarget(arc);
                    if (next != NoTarget)
                    {
                        var childId = id + ((flags & FlagWordEnd) != 0 ? 1 : 0);
                        var found = Walk((int)next, word, position + 1, childId);
                        if (found >= 0) return found;
                    }
                }
            }

            id += (int)ArcSubtree(arc);
            if ((flags & FlagLastArc) != 0) break;
        }

        return -1;
    }

    // ---- metadata --------------------------------------------------------

    public readonly record struct FormInfo(
        RussianPartOfSpeech PartOfSpeech,
        RussianFormFlags Flags,
        int FrequencyRank,
        string Lemma,
        string Tag)
    {
        /// <summary>Rank 1 is the most frequent word; 0 means "not in the frequency list".</summary>
        public bool HasFrequency => FrequencyRank > 0;
    }

    public FormInfo GetInfo(int id)
    {
        if (_meta is null || id < 0 || id >= WordCount) return default;
        var offset = (long)id * MetaSize;
        var pos = _meta.ReadByte(offset);
        var flags = _meta.ReadByte(offset + 1);
        var rank = _meta.ReadUInt16(offset + 2);
        var lemmaId = _meta.ReadUInt32(offset + 4);
        var tagId = _meta.ReadUInt16(offset + 8);
        return new FormInfo(
            (RussianPartOfSpeech)pos,
            (RussianFormFlags)flags,
            rank,
            lemmaId < (uint)_lemmas.Length ? _lemmas[lemmaId] : string.Empty,
            tagId < (uint)_tags.Length ? _tags[tagId] : string.Empty);
    }

    public FormInfo GetInfo(string word)
    {
        var id = GetId(word);
        return id < 0 ? default : GetInfo(id);
    }

    // ---- approximate search ---------------------------------------------

    public readonly record struct Match(string Word, int Id, int Distance);

    /// <summary>
    /// Every known form within <paramref name="maxDistance"/> Damerau-Levenshtein
    /// edits of the query, folded on case and ё/е.
    /// </summary>
    /// <remarks>
    /// A Levenshtein automaton over the graph: one DP row per prefix, pruned as
    /// soon as its minimum exceeds the budget. Because the graph shares suffixes,
    /// most of the three million forms are eliminated within the first two arcs.
    /// </remarks>
    public IReadOnlyList<Match> FindWithin(
        string word,
        int maxDistance = 2,
        int maxResults = 64,
        CancellationToken cancellationToken = default)
    {
        var results = new List<Match>();
        if (string.IsNullOrEmpty(word) || _arcs.Length == 0 || maxDistance < 0) return results;

        var folded = new char[word.Length];
        for (var i = 0; i < word.Length; i++) folded[i] = Fold(word[i]);

        var row = new int[word.Length + 1];
        for (var i = 0; i <= word.Length; i++) row[i] = i;

        var prefix = new StringBuilder(word.Length + maxDistance + 2);
        Search(_rootArc, 0, prefix, row, previousRow: null, previousChar: '\0',
            folded, maxDistance, maxResults, results, cancellationToken);

        results.Sort(static (a, b) => a.Distance != b.Distance
            ? a.Distance.CompareTo(b.Distance)
            : string.CompareOrdinal(a.Word, b.Word));
        return results;
    }

    private void Search(
        int nodeArc,
        int idSoFar,
        StringBuilder prefix,
        int[] row,
        int[]? previousRow,
        char previousChar,
        char[] query,
        int maxDistance,
        int maxResults,
        List<Match> results,
        CancellationToken cancellationToken)
    {
        if (nodeArc < 0 || results.Count >= maxResults) return;
        cancellationToken.ThrowIfCancellationRequested();

        var width = query.Length + 1;
        var id = idSoFar;

        for (var arc = nodeArc; ; arc++)
        {
            var flags = ArcFlags(arc);
            var rawLabel = ArcLabel(arc);
            var label = Fold(rawLabel);

            var next = new int[width];
            next[0] = row[0] + 1;
            var best = next[0];
            for (var i = 1; i < width; i++)
                {
                    var cost = query[i - 1] == label ? 0 : 1;
                    var value = Math.Min(Math.Min(next[i - 1] + 1, row[i] + 1), row[i - 1] + cost);

                    // Damerau transposition: the previous query character equals
                    // this label and the previous label equals this query
                    // character, so one swap explains both mismatches.
                    if (previousRow is not null && i > 1
                        && query[i - 1] == previousChar && query[i - 2] == label)
                    {
                        value = Math.Min(value, previousRow[i - 2] + 1);
                    }

                    next[i] = value;
                    if (value < best) best = value;
                }

            if (best <= maxDistance)
            {
                prefix.Append(rawLabel);
                // A word ending on this arc is the first of the arc's subtree in
                // lexicographic order, so its id is exactly the running count.
                if ((flags & FlagWordEnd) != 0 && next[query.Length] <= maxDistance)
                {
                    results.Add(new Match(prefix.ToString(), id, next[query.Length]));
                }

                var target = ArcTarget(arc);
                if (target != NoTarget && results.Count < maxResults)
                {
                    var childId = id + ((flags & FlagWordEnd) != 0 ? 1 : 0);
                    Search((int)target, childId, prefix, next, row, label,
                        query, maxDistance, maxResults, results, cancellationToken);
                }

                prefix.Length -= 1;
            }

            id += (int)ArcSubtree(arc);
            if ((flags & FlagLastArc) != 0) break;
            if (results.Count >= maxResults) break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _meta?.Dispose();
        _metaFile?.Dispose();
    }
}

public enum RussianPartOfSpeech : byte
{
    Unknown = 0,
    Noun = 1,
    AdjectiveFull = 2,
    AdjectiveShort = 3,
    Comparative = 4,
    Verb = 5,
    Infinitive = 6,
    ParticipleFull = 7,
    ParticipleShort = 8,
    Gerund = 9,
    Numeral = 10,
    Adverb = 11,
    Pronoun = 12,
    Predicative = 13,
    Preposition = 14,
    Conjunction = 15,
    Particle = 16,
    Interjection = 17,
}

[Flags]
public enum RussianFormFlags : byte
{
    None = 0,
    ProperName = 1 << 0,
    Abbreviation = 1 << 1,
    Slang = 1 << 2,
    Obscene = 1 << 3,
    Borrowing = 1 << 4,
    Informal = 1 << 5,
    Archaic = 1 << 6,
    NonCyrillic = 1 << 7,
}
