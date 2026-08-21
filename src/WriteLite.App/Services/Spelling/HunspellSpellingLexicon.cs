using System.Diagnostics;
using System.IO;
using System.Text;
using WeCantSpell.Hunspell;
using WriteLite.Language.Core;

namespace WriteLite.Services.Spelling;

/// <summary>
/// Offline Russian Hunspell lexicon from LibreOffice dictionaries.
/// </summary>
public sealed class HunspellSpellingLexicon : ISpellingLexicon, IDisposable
{
    private readonly WordList? _wordList;
    private readonly object _gate = new();
    private bool _disposed;

    private HunspellSpellingLexicon(
        WordList? wordList,
        LanguageCode language,
        string name,
        int approximateWordCount,
        TimeSpan loadTime,
        string source,
        string license,
        bool ready)
    {
        _wordList = wordList;
        Language = language;
        Name = name;
        ApproximateWordCount = approximateWordCount;
        LoadTime = loadTime;
        Source = source;
        License = license;
        IsReady = ready && wordList is not null;
    }

    public string Name { get; }
    public LanguageCode Language { get; }
    public bool IsReady { get; }
    public int ApproximateWordCount { get; }
    public TimeSpan LoadTime { get; }
    public string Source { get; }
    public string License { get; }

    public static HunspellSpellingLexicon LoadRussian(string? baseDirectory = null)
        => Load(
            LanguageCode.Russian,
            "ru_RU",
            "LibreOffice ru_RU Hunspell; copyright 1997-2008 Alexander I. Lebedev",
            "BSD-like ru_RU license with notice retention and modified-version marking",
            baseDirectory);

    private static HunspellSpellingLexicon Load(
        LanguageCode language,
        string filePrefix,
        string source,
        string license,
        string? baseDirectory)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var dir = ResolveDictionaryDirectory(baseDirectory);
            var dic = Path.Combine(dir, filePrefix + ".dic");
            var aff = Path.Combine(dir, filePrefix + ".aff");
            if (!File.Exists(dic) || !File.Exists(aff))
            {
                CompatibilityLogger.Technical(
                    "hunspell-missing",
                    $"lang={filePrefix} dir={(Directory.Exists(dir) ? "ok" : "missing")}");
                return new HunspellSpellingLexicon(null, language, filePrefix, 0, sw.Elapsed, source, license, false);
            }

            // Detect encoding from AFF SET line; default UTF-8.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var wordList = WordList.CreateFromFiles(dic, aff);
            var count = TryReadDicHeaderCount(dic);
            sw.Stop();
            CompatibilityLogger.Technical(
                "hunspell-loaded",
                $"lang={filePrefix} words~{count} ms={sw.Elapsed.TotalMilliseconds:F0}");
            return new HunspellSpellingLexicon(wordList, language, filePrefix, count, sw.Elapsed, source, license, true);
        }
        catch (Exception ex)
        {
            CompatibilityLogger.AccessError("hunspell-load", null, ex);
            return new HunspellSpellingLexicon(null, language, filePrefix, 0, sw.Elapsed, source, license, false);
        }
    }

    public bool ContainsExact(string word)
    {
        if (!IsReady || _wordList is null || string.IsNullOrEmpty(word)) return false;
        lock (_gate)
        {
            return _wordList.Check(word);
        }
    }

    public IReadOnlyList<string> Suggest(string word, int maxSuggestions = 5)
    {
        if (!IsReady || _wordList is null || string.IsNullOrEmpty(word)) return [];
        lock (_gate)
        {
            try
            {
                var suggestions = _wordList.Suggest(word);
                // Hunspell's suggester offers word splits («ро ботает» for «роботает»)
                // indistinguishably from ordinary candidates. Its own word list is the
                // evidence that decides which of them are real.
                return CorrectionCandidateValidityPolicy.FilterSuggestions(
                    word, suggestions, maxSuggestions, isKnownWord: ContainsExact);
            }
            catch
            {
                return [];
            }
        }
    }

    private static string ResolveDictionaryDirectory(string? baseDirectory)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(baseDirectory)) roots.Add(baseDirectory);
        roots.Add(AppContext.BaseDirectory);
        roots.Add(Path.Combine(AppContext.BaseDirectory, "resources", "spelling", "hunspell"));
        roots.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "resources", "spelling", "hunspell")));
        roots.Add(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "resources", "spelling", "hunspell")));

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = root;
            if (File.Exists(Path.Combine(candidate, "ru_RU.dic")))
            {
                return candidate;
            }

            var nested = Path.Combine(candidate, "resources", "spelling", "hunspell");
            if (File.Exists(Path.Combine(nested, "ru_RU.dic")))
            {
                return nested;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "resources", "spelling", "hunspell");
    }

    private static int TryReadDicHeaderCount(string dicPath)
    {
        try
        {
            using var reader = new StreamReader(dicPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var line = reader.ReadLine();
            return int.TryParse(line?.Trim(), out var n) ? n : 0;
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // WordList has no IDisposable in WeCantSpell 5.x
    }
}
