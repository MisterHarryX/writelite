using System.Collections.Concurrent;
using System.IO;

namespace WriteLite.Services.Lexical;

/// <summary>
/// Offline Russian lexical lookup: immutable JSON index + morphology + syntax + optional licensed/historical layers.
/// Never calls Qwen. Never fabricates dictionary articles for unknown words.
/// </summary>
public sealed class OfflineLexicalKnowledgeService : ILexicalKnowledgeService, IDisposable
{
    private const int MaxCacheEntries = 384;

    private readonly IContextualLexicalRanker _ranker;
    private readonly IWordMorphologyService _morphology;
    private readonly ISyntacticRoleAnalyzer _syntax;
    private readonly ILexicalLanguageDetector _languages;
    private readonly IOzhegovDictionaryProvider _ozhegov;
    private readonly IHistoricalDictionaryProvider _historical;
    private readonly ConcurrentDictionary<string, LexicalLookupResult> _cache = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private IReadOnlyDictionary<string, LexicalEntry> _index =
        new Dictionary<string, LexicalEntry>(StringComparer.OrdinalIgnoreCase);
    private LexicalPackManifest? _manifest;

    public OfflineLexicalKnowledgeService(
        IContextualLexicalRanker? ranker = null,
        IWordMorphologyService? morphology = null,
        ILexicalLanguageDetector? languages = null,
        ISyntacticRoleAnalyzer? syntax = null,
        IOzhegovDictionaryProvider? ozhegov = null,
        IHistoricalDictionaryProvider? historical = null)
    {
        _ranker = ranker ?? new ContextualLexicalRanker();
        _languages = languages ?? new LexicalLanguageDetector();
        _morphology = morphology ?? new WordMorphologyService(LookupEntry);
        _syntax = syntax ?? new SyntacticRoleAnalyzer();
        _ozhegov = ozhegov ?? new LicensedOzhegovDictionaryProvider();
        _historical = historical ?? new HistoricalDictionaryProvider();
    }

    public string? PackVersion => _manifest?.PackVersion;
    public bool IsPackLoaded => _manifest is not null && _index.Count > 0;
    public bool IsDemoPack => _manifest?.IsDemo == true;
    public string? License => _manifest?.License;
    public string? LoadError { get; private set; }
    public bool OzhegovAvailable => _ozhegov.IsAvailable;
    public bool HistoricalAvailable => _historical.IsAvailable;

    public LexicalEntry? LookupEntry(string word, LexicalLanguage language)
    {
        if (string.IsNullOrWhiteSpace(word)) return null;

        if (_index.Count == 0) return null;
        if (_index.TryGetValue(word.Trim(), out var entry)
            || _index.TryGetValue(FoldYo(word.Trim()), out entry))
        {
            if (language == LexicalLanguage.Unknown || entry.Language == language || entry.Language == LexicalLanguage.Unknown)
                return entry;
        }

        var lemma = word.Trim().ToLowerInvariant();
        if (_index.TryGetValue(lemma, out entry)
            || _index.TryGetValue(FoldYo(lemma), out entry)) return entry;
        return null;
    }

    public void LoadPack(string path)
    {
        var result = LexicalPackLoader.LoadFromFile(path);
        ApplyLoadResult(result);
    }

    public void LoadPackBytes(byte[] bytes)
    {
        var result = LexicalPackLoader.LoadFromBytes(bytes);
        ApplyLoadResult(result);
    }

    public void LoadFromDirectory(string? directory = null)
    {
        directory ??= Path.Combine(AppContext.BaseDirectory, "resources", "lexical");

        var preferred = new[]
        {
            Path.Combine(directory, "writelight-lexical-open.json"),
            Path.Combine(directory, "writelight-lexical-core.json"),
            Path.Combine(directory, "pack.json")
        };

        foreach (var path in preferred)
        {
            if (!File.Exists(path)) continue;
            LoadPack(path);
            if (_index.Count > 0)
            {
                CompatibilityLogger.Technical("lexical-json-loaded", $"path={Path.GetFileName(path)} entries={_index.Count}");
                break;
            }
        }

        if (!IsPackLoaded)
        {
            LoadError ??= "lexical pack not found";
            CompatibilityLogger.Technical("lexical-pack-missing", "dir=resources/lexical");
        }
    }

    /// <summary>
    /// Loads the small authored core synchronously so the first double-click has
    /// an immediate local result. The large open pack can then replace it in the
    /// background through <see cref="LoadFromDirectory"/>.
    /// </summary>
    public void LoadBootstrapFromDirectory(string? directory = null)
    {
        directory ??= Path.Combine(AppContext.BaseDirectory, "resources", "lexical");
        foreach (var fileName in new[] { "writelight-lexical-core.json" })
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path)) continue;
            LoadPack(path);
            if (_index.Count > 0)
            {
                CompatibilityLogger.Technical(
                    "lexical-bootstrap-loaded",
                    $"path={fileName} entries={_index.Count}");
                return;
            }
        }
    }

    private void ApplyLoadResult(LexicalPackLoadResult result)
    {
        if (!result.Success || result.Index is null || result.Manifest is null)
        {
            if (!IsPackLoaded)
            {
                LoadError = result.Error ?? "load failed";
                CompatibilityLogger.Technical("lexical-pack-load-failed", $"reason={Sanitize(LoadError)}");
            }

            return;
        }

        lock (_gate)
        {
            _index = result.Index;
            _manifest = result.Manifest;
            _cache.Clear();
            LoadError = null;
        }
    }

    public Task<LexicalLookupResult> LookupAsync(LexicalLookupRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.Word))
            return Task.FromResult(LexicalLookupResult.Empty(request.Word ?? "", request.RequestId, "Пустой запрос."));

        if (!IsPackLoaded && !_ozhegov.IsAvailable && !_historical.IsAvailable)
        {
            return Task.FromResult(LexicalLookupResult.Empty(
                request.Word,
                request.RequestId,
                "Локальный словарный пакет не загружен. Проверка текста продолжает работать."));
        }

        var lang = _languages.DetectWord(request.Word);
        if (lang != LexicalLanguage.Russian)
        {
            return Task.FromResult(LexicalLookupResult.Empty(
                request.Word,
                request.RequestId,
                "Справочный словарь WriteLite поддерживает только русские слова."));
        }

        var cacheKey = BuildCacheKey(request.Word, lang, request.Sentence, request.Start);
        if (_cache.TryGetValue(cacheKey, out var cached))
            return Task.FromResult(cached with { RequestId = request.RequestId });

        cancellationToken.ThrowIfCancellationRequested();

        var entry = LookupEntry(request.Word, lang);
        if (entry is null)
        {
            var lemmaGuess = _morphology.GetLemma(request.Word, lang);
            entry = LookupEntry(lemmaGuess, lang);
        }

        var morphology = _morphology.AnalyzeFeatures(request.Word, lang, request.Sentence, request.Start);
        var pos = entry?.PartOfSpeech
                  ?? _morphology.GetPartOfSpeech(request.Word, lang);
        if (pos == LexicalPartOfSpeech.Unknown && entry is not null)
            pos = entry.PartOfSpeech;

        var syntax = _syntax.Analyze(request.Word, request.Start, request.Length, request.Sentence, pos, lang);

        var definitions = new List<LexicalDefinition>();
        var synonyms = Array.Empty<LexicalSuggestion>();
        var antonyms = Array.Empty<LexicalSuggestion>();
        var examples = Array.Empty<LexicalExample>();
        var lemma = entry?.Lemma ?? _morphology.GetLemma(request.Word, lang);

        if (entry is not null)
        {
            definitions.AddRange(entry.Definitions);
            synonyms = _ranker.RankSynonyms(entry.Synonyms, request.Word, request.Sentence, pos)
                .Select(s => s with
                {
                    CanReplace = true,
                    Value = _morphology.InflectLike(request.Word, s.Value, lang) ?? s.Value
                })
                .ToArray();
            antonyms = _ranker.RankSynonyms(entry.Antonyms, request.Word, request.Sentence, pos)
                .Select(s => s with
                {
                    CanReplace = true,
                    Value = _morphology.InflectLike(request.Word, s.Value, lang) ?? s.Value
                })
                .ToArray();
            examples = entry.Examples.Select(e => e with { HighlightWord = request.Word }).ToArray();
            lang = entry.Language != LexicalLanguage.Unknown ? entry.Language : lang;
        }

        // Licensed Ozhegov layer (only if user installed pack)
        foreach (var d in _ozhegov.LookupDefinitions(lemma))
            definitions.Add(d);
        if (!string.Equals(lemma, request.Word, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var d in _ozhegov.LookupDefinitions(request.Word))
                definitions.Add(d);
        }

        // Historical / Dal PD layer
        foreach (var d in _historical.LookupHistoricalDefinitions(lemma))
            definitions.Add(d);

        string? status = null;
        if (entry is null && definitions.Count == 0)
        {
            status = IsDemoPack
                ? "Слово отсутствует в локальном словаре WriteLite. Это не ошибка модели: статья не выдумывается."
                : "Слово не найдено в установленных словарных пакетах.";
            var empty = new LexicalLookupResult(
                request.Word, lemma, lang, pos, [], [], [],
                request.RequestId, IsEmpty: true, StatusMessage: status, PackVersion: PackVersion,
                Morphology: morphology, Syntax: syntax, Antonyms: [], Relations: syntax.Relations,
                SurfaceFormNote: BuildSurfaceNote(request.Word, lemma, morphology),
                PackLicense: License, PackSource: _manifest?.Source);
            Remember(cacheKey, empty);
            return Task.FromResult(empty);
        }

        if (IsDemoPack)
            status = "Ограниченный локальный пакет WriteLite (CC0). Не полный академический словарь.";
        if (!_ozhegov.IsAvailable && definitions.Count == 0)
            status = (status is null ? "" : status + " ") + "Дополнительный толковый пакет не установлен.";

        var result = new LexicalLookupResult(
            request.Word,
            lemma,
            lang,
            pos,
            synonyms,
            definitions,
            examples,
            request.RequestId,
            IsEmpty: definitions.Count == 0 && synonyms.Length == 0,
            StatusMessage: status?.Trim(),
            PackVersion: PackVersion,
            Morphology: morphology,
            Syntax: syntax,
            Antonyms: antonyms,
            Relations: syntax.Relations,
            SurfaceFormNote: BuildSurfaceNote(request.Word, lemma, morphology),
            PackLicense: License,
            PackSource: _manifest?.Source);

        Remember(cacheKey, result);
        CompatibilityLogger.Technical(
            "lexical-lookup",
            $"request={request.RequestId} lang={lang} found={(entry is null ? 0 : 1)} " +
            $"syn={synonyms.Length} def={definitions.Count} role={syntax.Role}");
        return Task.FromResult(result);
    }

    private static string BuildSurfaceNote(string word, string lemma, MorphologicalFeatures morphology)
    {
        var feats = string.Join(", ", morphology.ToDisplayList());
        if (string.Equals(word, lemma, StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrEmpty(feats) ? "Начальная форма." : $"Начальная форма. {feats}";
        return string.IsNullOrEmpty(feats)
            ? $"Форма слова; лемма: {lemma}."
            : $"Форма слова; лемма: {lemma}. {feats}";
    }

    private void Remember(string key, LexicalLookupResult result)
    {
        if (_cache.Count >= MaxCacheEntries)
        {
            foreach (var k in _cache.Keys.Take(MaxCacheEntries / 2).ToArray())
                _cache.TryRemove(k, out _);
        }

        _cache[key] = result;
    }

    private static string BuildCacheKey(string word, LexicalLanguage lang, string sentence, int start)
    {
        var sentenceHash = (sentence ?? string.Empty).GetHashCode(StringComparison.Ordinal);
        return $"{lang}|{word.Trim().ToLowerInvariant()}|{start}|{sentenceHash:X8}";
    }

    private static string FoldYo(string value)
        => value.Replace('ё', 'е').Replace('Ё', 'Е');

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= 48 ? value : value[..48];
    }

    public void Dispose()
    {
        // The immutable JSON index is managed memory and needs no external cleanup.
    }
}
