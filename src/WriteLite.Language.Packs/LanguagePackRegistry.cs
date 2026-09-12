using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WriteLite.Language.Packs;

public sealed class LanguagePackDescriptor
{
    public required string PackId { get; init; }
    public required string Path { get; init; }
    public required string Kind { get; init; } // spelling|lexical|morphology|punctuation
    public string? Version { get; init; }
    public string? License { get; init; }
    public long Bytes { get; init; }
    public string? Sha256 { get; init; }
    public bool Exists { get; init; }
}

/// <summary>
/// Discovers local language packs under resources/ without requiring network.
/// </summary>
public sealed class LanguagePackRegistry
{
    public IReadOnlyList<LanguagePackDescriptor> Discover(string? appBaseDirectory = null)
    {
        var root = appBaseDirectory ?? AppContext.BaseDirectory;
        var list = new List<LanguagePackDescriptor>();

        void Add(string kind, string relative)
        {
            var full = Path.Combine(root, relative);
            var exists = File.Exists(full);
            string? sha = null;
            long bytes = 0;
            if (exists)
            {
                bytes = new FileInfo(full).Length;
                try
                {
                    using var fs = File.OpenRead(full);
                    sha = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
                }
                catch { /* ignore */ } // hash is best-effort metadata; a locked/deleted pack keeps Exists/Length answers
            }

            list.Add(new LanguagePackDescriptor
            {
                PackId = Path.GetFileNameWithoutExtension(full),
                Path = full,
                Kind = kind,
                Bytes = bytes,
                Sha256 = sha,
                Exists = exists
            });
        }

        Add("spelling", Path.Combine("resources", "spelling", "hunspell", "ru_RU.dic"));
        Add("lexical", Path.Combine("resources", "lexical", "writelight-lexical-core.json"));
        Add("morphology", Path.Combine("resources", "lexical", "morphology-index.json"));
        Add("punctuation", Path.Combine("resources", "rules", "ru", "punctuation.json"));

        return list;
    }

    public string ToDiagnosticSummary(IReadOnlyList<LanguagePackDescriptor> packs)
    {
        var sb = new StringBuilder();
        foreach (var p in packs)
        {
            sb.Append(p.Kind).Append('=').Append(p.Exists ? "ok" : "missing")
              .Append(" bytes=").Append(p.Bytes).Append(';');
        }
        return sb.ToString();
    }
}
