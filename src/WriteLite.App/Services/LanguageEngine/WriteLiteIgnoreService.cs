using System.IO;
using System.Text.Json;
using WriteLite.Models;

namespace WriteLite.Services.LanguageEngine;

/// <summary>
/// Local ignore lists for words, rules (WL-id), and ephemeral issue keys.
/// Does not log user text.
/// </summary>
public sealed class WriteLiteIgnoreService
{
    private readonly object _gate = new();
    private readonly HashSet<string> _words = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _rules = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sessionIssues = new(StringComparer.Ordinal);
    private string _textFingerprint = string.Empty;

    public WriteLiteIgnoreService()
    {
    }

    public static WriteLiteIgnoreService LoadDefault()
    {
        var service = new WriteLiteIgnoreService();
        try
        {
            var path = GetStorePath();
            if (!File.Exists(path))
            {
                return service;
            }

            var dto = JsonSerializer.Deserialize<StoreDto>(File.ReadAllText(path));
            if (dto is null)
            {
                return service;
            }

            foreach (var w in dto.Words ?? [])
            {
                if (!string.IsNullOrWhiteSpace(w))
                {
                    service._words.Add(w.Trim());
                }
            }

            foreach (var r in dto.Rules ?? [])
            {
                if (!string.IsNullOrWhiteSpace(r))
                {
                    service._rules.Add(r.Trim());
                }
            }
        }
        catch
        {
            // ignore corrupt store
        }

        return service;
    }

    public void NotifyText(string text)
    {
        var fp = Fingerprint(text);
        lock (_gate)
        {
            if (!string.Equals(fp, _textFingerprint, StringComparison.Ordinal))
            {
                _textFingerprint = fp;
                _sessionIssues.Clear();
            }
        }
    }

    public void IgnoreWord(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return;
        }

        lock (_gate)
        {
            if (_words.Add(word.Trim()))
            {
                PersistUnlocked();
            }
        }

        CompatibilityLogger.Technical("language-engine-ignore", "kind=word");
    }

    public void IgnoreRule(string ruleId)
    {
        if (string.IsNullOrWhiteSpace(ruleId))
        {
            return;
        }

        lock (_gate)
        {
            if (_rules.Add(ruleId.Trim()))
            {
                PersistUnlocked();
            }
        }

        CompatibilityLogger.Technical("language-engine-ignore", "kind=rule");
    }

    public void IgnoreIssueUntilTextChanges(TextIssue issue)
    {
        lock (_gate)
        {
            _sessionIssues.Add(IssueKey(issue));
        }

        CompatibilityLogger.Technical("language-engine-ignore", "kind=issue");
    }

    public bool IsIgnored(TextIssue issue)
    {
        lock (_gate)
        {
            if (_rules.Contains(issue.RuleId))
            {
                return true;
            }

            if (_sessionIssues.Contains(IssueKey(issue)))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(issue.Original) && _words.Contains(issue.Original.Trim()))
            {
                return true;
            }

            // Also ignore orthography of dictionary words when original is a single token.
            var token = issue.Original.Trim();
            if (token.Length > 0 && !token.Contains(' ') && _words.Contains(token))
            {
                return true;
            }

            return false;
        }
    }

    public IReadOnlyList<TextIssue> Filter(IEnumerable<TextIssue> issues)
    {
        return issues.Where(i => !IsIgnored(i)).ToList();
    }

    public bool IsWordIgnored(string word)
    {
        lock (_gate)
        {
            return _words.Contains(word);
        }
    }

    public IReadOnlyList<string> GetIgnoredWords()
    {
        lock (_gate)
        {
            return _words.Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public IReadOnlyList<string> GetIgnoredRules()
    {
        lock (_gate)
        {
            return _rules.Order(StringComparer.Ordinal).ToList();
        }
    }

    public void UnignoreWord(string word)
    {
        lock (_gate)
        {
            if (_words.Remove(word.Trim()))
            {
                PersistUnlocked();
            }
        }
    }

    public void UnignoreRule(string ruleId)
    {
        lock (_gate)
        {
            if (_rules.Remove(ruleId.Trim()))
            {
                PersistUnlocked();
            }
        }
    }

    public void ClearPersistent()
    {
        lock (_gate)
        {
            _words.Clear();
            _rules.Clear();
            PersistUnlocked();
        }
    }

    private void PersistUnlocked()
    {
        try
        {
            var path = GetStorePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var dto = new StoreDto
            {
                Words = _words.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                Rules = _rules.Order(StringComparer.Ordinal).ToArray()
            };
            var json = JsonSerializer.Serialize(dto);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch
        {
            // non-fatal
        }
    }

    private static string IssueKey(TextIssue issue)
        => $"{issue.Start}|{issue.Length}|{issue.RuleId}|{issue.Original.Length}";

    private static string Fingerprint(string text)
    {
        // Length + hash of content for change detection without logging text.
        var hash = text.GetHashCode(StringComparison.Ordinal);
        return $"{text.Length}:{hash}";
    }

    private static string GetStorePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WriteLite",
            "ignore-store.json");

    private sealed class StoreDto
    {
        public string[]? Words { get; set; }
        public string[]? Rules { get; set; }
    }
}
