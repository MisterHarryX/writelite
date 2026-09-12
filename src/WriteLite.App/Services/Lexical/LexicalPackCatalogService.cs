using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using WriteLite.Language.Russian;

namespace WriteLite.Services.Lexical;

public sealed record LexicalPackDescriptor(
    string Id,
    string Name,
    string Language,
    string DataType,
    string Source,
    string License,
    string Version,
    int? EntryCount,
    long SizeBytes,
    DateTimeOffset? InstalledAt,
    bool IsBuiltIn,
    bool IsEnabled,
    bool IsValid,
    string Status,
    string Path)
{
    public string SizeDisplay => SizeBytes < 1024 * 1024
        ? $"{Math.Max(1, SizeBytes / 1024d):F0} КБ"
        : $"{SizeBytes / 1024d / 1024d:F1} МБ";
    public string EntryCountDisplay => EntryCount is int count ? count.ToString("N0") : "—";
}

/// <summary>
/// Local pack catalogue. Bundled packs are read-only; imported packs live in
/// LocalAppData and are installed through validated atomic replacement.
/// </summary>
public sealed class LexicalPackCatalogService
{
    private readonly string _bundledRoot;
    private readonly string _userRoot;
    private readonly string _languageEngineRoot;

    public LexicalPackCatalogService(string bundledRoot, string userRoot, string languageEngineRoot)
    {
        _bundledRoot = Path.GetFullPath(bundledRoot);
        _userRoot = Path.GetFullPath(userRoot);
        _languageEngineRoot = Path.GetFullPath(languageEngineRoot);
    }

    public static LexicalPackCatalogService CreateDefault() => new(
        Path.Combine(AppContext.BaseDirectory, "resources", "lexical"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WriteLite", "dictionaries", "packs"),
        Path.Combine(AppContext.BaseDirectory, "ThirdParty", "LanguageEngine", "6.4"));

    public IReadOnlyList<LexicalPackDescriptor> Snapshot()
    {
        var packs = new List<LexicalPackDescriptor>();
        AddDocumentPack(packs, Path.Combine(_bundledRoot, "writelight-lexical-open.json"), builtIn: true);
        AddDocumentPack(packs, Path.Combine(_bundledRoot, "writelight-lexical-core.json"), builtIn: true);
        AddHistoricalPack(packs);

        if (Directory.Exists(_userRoot))
        {
            foreach (var path in Directory.EnumerateFiles(_userRoot, "*.json", SearchOption.TopDirectoryOnly))
                AddDocumentPack(packs, path, builtIn: false);
        }

        if (Directory.Exists(_languageEngineRoot))
        {
            var files = Directory.EnumerateFiles(_languageEngineRoot, "*", SearchOption.AllDirectories).ToList();
            packs.Add(new LexicalPackDescriptor(
                "language-engine-6.4", "LanguageTool 6.4", "Русский", "Дополнительные правила, орфография и морфология",
                "LanguageTool distribution", "LGPL-2.1+; данные — согласно third-party notices", "6.4", null,
                files.Sum(path => SafeLength(path)), Directory.GetLastWriteTimeUtc(_languageEngineRoot), true, true,
                files.Count > 0, files.Count > 0 ? "Установлен" : "Повреждён", _languageEngineRoot));
        }

        return packs.OrderByDescending(p => p.IsBuiltIn).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Only the dictionaries the user installed themselves.
    /// </summary>
    /// <remarks>
    /// The normal interface shows this, never <see cref="Snapshot"/>. WriteLite's own
    /// packs — the morphological data, the explanatory dictionary, the language engine,
    /// the historical sample — are application infrastructure: the user did not choose
    /// them, cannot meaningfully evaluate them, and gains nothing from a list of file
    /// sizes and licence identifiers they are not allowed to act on. Every action the
    /// old catalogue offered on a built-in pack was refused by the service anyway, so
    /// the screen could only ever produce an error dialog.
    ///
    /// It is also far cheaper. <see cref="Snapshot"/> fully parses the 55 MB open pack
    /// and walks the entire language-engine tree to render rows nobody can use; this
    /// reads one directory.
    ///
    /// <see cref="Snapshot"/> remains for diagnostics, where the full picture is the
    /// point.
    /// </remarks>
    public IReadOnlyList<LexicalPackDescriptor> UserPacks()
    {
        if (!Directory.Exists(_userRoot))
        {
            return [];
        }

        var packs = new List<LexicalPackDescriptor>();
        foreach (var path in Directory.EnumerateFiles(_userRoot, "*.json", SearchOption.TopDirectoryOnly))
        {
            AddDocumentPack(packs, path, builtIn: false);
        }

        return packs
            .OrderBy(pack => pack.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<LexicalPackDescriptor> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source) || !string.Equals(Path.GetExtension(source), ".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Поддерживаются только JSON-пакеты WriteLite.");

        Directory.CreateDirectory(_userRoot);
        var safeName = Path.GetFileName(source);
        var destination = Path.GetFullPath(Path.Combine(_userRoot, safeName));
        EnsureUnderUserRoot(destination);
        var temp = destination + ".download-" + Guid.NewGuid().ToString("N") + ".tmp";
        var backup = destination + ".previous";

        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            var validation = LexicalPackLoader.LoadFromFile(temp);
            if (!validation.Success || validation.Manifest is null)
                throw new InvalidDataException(validation.Error ?? "Пакет не прошёл проверку.");

            if (File.Exists(destination)) File.Replace(temp, destination, backup, ignoreMetadataErrors: true);
            else File.Move(temp, destination);

            return DescribeDocumentPack(destination, builtIn: false);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public Task<LexicalPackDescriptor> VerifyAsync(LexicalPackDescriptor pack, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pack.Id == "language-engine-6.4")
            return Task.FromResult(Snapshot().First(p => p.Id == pack.Id));
        return Task.FromResult(DescribeDocumentPack(pack.Path, pack.IsBuiltIn));
    }

    public void SetEnabled(LexicalPackDescriptor pack, bool enabled)
    {
        if (pack.IsBuiltIn) throw new InvalidOperationException("Встроенный пакет нельзя отключить из менеджера.");
        EnsureUnderUserRoot(pack.Path);
        var marker = pack.Path + ".disabled";
        if (enabled)
        {
            if (File.Exists(marker)) File.Delete(marker);
        }
        else
        {
            File.WriteAllText(marker, "disabled");
        }
    }

    public void Remove(LexicalPackDescriptor pack)
    {
        if (pack.IsBuiltIn) throw new InvalidOperationException("Встроенный пакет нельзя удалить.");
        EnsureUnderUserRoot(pack.Path);
        if (File.Exists(pack.Path)) File.Delete(pack.Path);
        if (File.Exists(pack.Path + ".disabled")) File.Delete(pack.Path + ".disabled");
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private void AddDocumentPack(List<LexicalPackDescriptor> packs, string path, bool builtIn)
    {
        if (!File.Exists(path)) return;
        try { packs.Add(DescribeDocumentPack(path, builtIn)); }
        catch (Exception ex)
        {
            packs.Add(new LexicalPackDescriptor(Path.GetFileNameWithoutExtension(path), Path.GetFileName(path), "—", "Лексика",
                "local", "—", "—", null, SafeLength(path), File.GetLastWriteTimeUtc(path), builtIn, true, false,
                $"Ошибка: {ex.GetType().Name}", path));
        }
    }

    private LexicalPackDescriptor DescribeDocumentPack(string path, bool builtIn)
    {
        var loaded = LexicalPackLoader.LoadFromFile(path);
        if (!loaded.Success || loaded.Manifest is null)
            throw new InvalidDataException(loaded.Error ?? "Invalid lexical pack");
        var manifest = loaded.Manifest;
        var enabled = builtIn || !File.Exists(path + ".disabled");
        return new LexicalPackDescriptor(
            manifest.PackId,
            manifest.DisplayName,
            "Русский",
            manifest.PackId.Contains("core", StringComparison.OrdinalIgnoreCase)
                ? "Толкования, примеры, синонимы и антонимы"
                : "Леммы, словоформы и локальные справочные статьи",
            manifest.Source ?? "local", manifest.License, manifest.PackVersion,
            manifest.EntryCount > 0
                ? manifest.EntryCount
                : loaded.Index?.Values.Select(entry => entry.Lemma).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            SafeLength(path), File.GetLastWriteTimeUtc(path), builtIn, enabled, true,
            enabled ? (manifest.IsDemo ? "Ограниченный пакет" : "Установлен") : "Отключён", path);
    }

    private void AddHistoricalPack(List<LexicalPackDescriptor> packs)
    {
        var entries = Path.Combine(_bundledRoot, "historical-sample.entries.jsonl");
        var manifestPath = Path.Combine(_bundledRoot, "historical-sample.manifest.json");
        if (!File.Exists(entries) || !File.Exists(manifestPath)) return;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = json.RootElement;
            var count = File.ReadLines(entries).Count(line => !string.IsNullOrWhiteSpace(line));
            packs.Add(new LexicalPackDescriptor(
                root.TryGetProperty("packId", out var id) ? id.GetString() ?? "historical-sample" : "historical-sample",
                root.TryGetProperty("displayName", out var name) ? name.GetString() ?? "Historical sample" : "Historical sample",
                RussianLanguageProfile.IsoCode, "Исторические толкования", "WriteLite authors",
                root.TryGetProperty("license", out var license) ? license.GetString() ?? "CC0-1.0" : "CC0-1.0",
                root.TryGetProperty("version", out var version) ? version.GetString() ?? "1.0" : "1.0",
                count, SafeLength(entries) + SafeLength(manifestPath), File.GetLastWriteTimeUtc(entries), true, true, true,
                "Исторический sample", entries));
        }
        catch (Exception ex)
        {
            CompatibilityLogger.Technical("dictionary-catalog-error", ex);
        }
    }

    private void EnsureUnderUserRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var root = _userRoot.EndsWith(Path.DirectorySeparatorChar) ? _userRoot : _userRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Pack path находится вне пользовательского каталога.");
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; } // file may be deleted or locked between listing and this read
    }
}
