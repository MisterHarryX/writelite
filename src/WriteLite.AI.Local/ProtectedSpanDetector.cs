using System.Text.RegularExpressions;

namespace WriteLite.AI.Local;

/// <summary>
/// Detects spans that must not be rewritten (URLs, emails, paths, code-ish tokens, numbers).
/// </summary>
public static partial class ProtectedSpanDetector
{
    public static IReadOnlyList<(int Start, int Length)> FindProtectedSpans(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var spans = new List<(int, int)>();
        void AddMatches(Regex re)
        {
            foreach (Match m in re.Matches(text))
            {
                if (m.Success && m.Length > 0)
                {
                    spans.Add((m.Index, m.Length));
                }
            }
        }

        AddMatches(UrlRegex());
        AddMatches(EmailRegex());
        AddMatches(WindowsPathRegex());
        AddMatches(UnixPathRegex());
        AddMatches(CodeFenceRegex());
        AddMatches(JsonRegex());
        AddMatches(CommandRegex());
        AddMatches(IpRegex());
        AddMatches(DateRegex());
        AddMatches(VersionTokenRegex());
        AddMatches(LatinTokenRegex());
        AddMatches(NumberRegex());

        return MergeOverlapping(spans);
    }

    public static bool OverlapsProtected(int start, int length, IReadOnlyList<(int Start, int Length)> protectedSpans)
    {
        var end = start + length;
        foreach (var (ps, pl) in protectedSpans)
        {
            var pe = ps + pl;
            if (start < pe && end > ps)
            {
                return true;
            }
        }

        return false;
    }

    public static string MaskProtected(string text, out IReadOnlyList<(int Start, int Length, string Token)> map)
    {
        var spans = FindProtectedSpans(text);
        if (spans.Count == 0)
        {
            map = [];
            return text;
        }

        var ordered = spans.OrderByDescending(s => s.Start).ToList();
        var list = new List<(int, int, string)>();
        var sb = new System.Text.StringBuilder(text);
        var i = 0;
        foreach (var (start, length) in ordered)
        {
            var token = $"⟦P{i}⟧";
            var original = text.Substring(start, length);
            list.Add((start, length, original));
            sb.Remove(start, length);
            sb.Insert(start, token);
            i++;
        }

        // Keep placeholder assignment order. Sorting by source position here
        // would make P0 restore the text belonging to another protected span.
        map = list;
        return sb.ToString();
    }

    public static string UnmaskProtected(string text, IReadOnlyList<(int Start, int Length, string Token)> map)
    {
        // Tokens were inserted left-to-right after reverse replace; restore by token content.
        var result = text;
        // Rebuild using sequential placeholders P0..Pn that may appear in corrected text.
        // The bound is map.Count, not a fixed cap: a technical text can easily produce more
        // protected spans than any fixed placeholder budget, and a fixed 64 silently left the
        // unstrict 65th span's ⟦P{n}⟧ token in place — corrupting the corrected text.
        for (var i = 0; i < map.Count; i++)
        {
            var ph = $"⟦P{i}⟧";
            if (!result.Contains(ph, StringComparison.Ordinal))
            {
                continue;
            }

            result = result.Replace(ph, map[i].Token, StringComparison.Ordinal);
        }

        return result;
    }

    private static List<(int Start, int Length)> MergeOverlapping(List<(int Start, int Length)> spans)
    {
        if (spans.Count == 0)
        {
            return spans;
        }

        var ordered = spans.OrderBy(s => s.Start).ThenByDescending(s => s.Length).ToList();
        var merged = new List<(int, int)> { ordered[0] };
        for (var i = 1; i < ordered.Count; i++)
        {
            var (s, l) = ordered[i];
            var last = merged[^1];
            var lastEnd = last.Item1 + last.Item2;
            if (s <= lastEnd)
            {
                var newEnd = Math.Max(lastEnd, s + l);
                merged[^1] = (last.Item1, newEnd - last.Item1);
            }
            else
            {
                merged.Add((s, l));
            }
        }

        return merged;
    }

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"[A-Za-z]:\\(?:[^\s<>""'|]+\\)*[^\s<>""'|]*", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(@"(?:/(?:[\w.\-]+))+", RegexOptions.CultureInvariant)]
    private static partial Regex UnixPathRegex();

    [GeneratedRegex(@"`[^`]+`", RegexOptions.CultureInvariant)]
    private static partial Regex CodeFenceRegex();

    [GeneratedRegex(@"\{[^{}\r\n]{1,2000}\}", RegexOptions.CultureInvariant)]
    private static partial Regex JsonRegex();

    [GeneratedRegex(@"(?m)^\s*(?:dotnet|git|npm|pnpm|yarn|python|powershell|cmd)\s+[^\r\n]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommandRegex();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex IpRegex();

    [GeneratedRegex(@"\b\d{1,4}[-/.]\d{1,2}[-/.]\d{1,4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"\b[A-Za-z]+Lite(?:\s+\d+(?:\.\d+)*)?\b|\b\d+\.\d+(?:\.\d+)+\b", RegexOptions.CultureInvariant)]
    private static partial Regex VersionTokenRegex();

    [GeneratedRegex(@"\b[A-Za-z][A-Za-z0-9_.+\-]*\b", RegexOptions.CultureInvariant)]
    private static partial Regex LatinTokenRegex();

    [GeneratedRegex(@"\b\d+(?:[.,]\d+)?\b", RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();
}
