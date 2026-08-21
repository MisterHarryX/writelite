namespace WriteLite.Services.Ai;

public enum DiffKind
{
    Unchanged,
    Added,
    Removed
}

public sealed record DiffSegment(string Text, DiffKind Kind);

/// <summary>
/// Word-level difference between the original text and an AI suggestion.
/// </summary>
/// <remarks>
/// The preview exists so nobody accepts a rewrite without seeing what it does, and
/// a wall of two paragraphs does not show that — the changed words do. Diffing by
/// word rather than by character keeps the highlighting readable: character diffs
/// on Russian text fragment inside inflected endings and produce speckle.
///
/// Tokens keep their trailing whitespace so reassembling the segments reproduces
/// the text exactly, which is what lets the preview render from the diff instead of
/// rendering the text twice and hoping the two agree.
/// </remarks>
public static class RewriteDiff
{
    /// <summary>
    /// Above this many words the diff is skipped.
    /// </summary>
    /// <remarks>
    /// The table below is O(n·m); at the service's 4000-character selection limit
    /// that stays small, but a pathological input should degrade to "here is the
    /// old text, here is the new one" rather than stall the dispatcher.
    /// </remarks>
    private const int MaxTokens = 2000;

    public static IReadOnlyList<DiffSegment> Compute(string original, string suggestion)
    {
        var left = Tokenize(original);
        var right = Tokenize(suggestion);

        if (left.Length > MaxTokens || right.Length > MaxTokens)
        {
            return
            [
                new DiffSegment(original, DiffKind.Removed),
                new DiffSegment(suggestion, DiffKind.Added)
            ];
        }

        var lengths = BuildLcsTable(left, right);
        var segments = new List<DiffSegment>();

        var i = 0;
        var j = 0;

        while (i < left.Length && j < right.Length)
        {
            if (Same(left[i], right[j]))
            {
                Append(segments, left[i], DiffKind.Unchanged);
                i++;
                j++;
            }
            else if (lengths[i + 1, j] >= lengths[i, j + 1])
            {
                Append(segments, left[i], DiffKind.Removed);
                i++;
            }
            else
            {
                Append(segments, right[j], DiffKind.Added);
                j++;
            }
        }

        while (i < left.Length)
        {
            Append(segments, left[i++], DiffKind.Removed);
        }

        while (j < right.Length)
        {
            Append(segments, right[j++], DiffKind.Added);
        }

        return segments;
    }

    private static int[,] BuildLcsTable(string[] left, string[] right)
    {
        var lengths = new int[left.Length + 1, right.Length + 1];

        for (var i = left.Length - 1; i >= 0; i--)
        {
            for (var j = right.Length - 1; j >= 0; j--)
            {
                lengths[i, j] = Same(left[i], right[j])
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        return lengths;
    }

    /// <summary>Merges adjacent segments of the same kind so the preview has fewer runs.</summary>
    private static void Append(List<DiffSegment> segments, string token, DiffKind kind)
    {
        if (segments.Count > 0 && segments[^1].Kind == kind)
        {
            segments[^1] = segments[^1] with { Text = segments[^1].Text + token };
            return;
        }

        segments.Add(new DiffSegment(token, kind));
    }

    /// <summary>
    /// Splits into words, each carrying the whitespace that followed it.
    /// </summary>
    private static string[] Tokenize(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var tokens = new List<string>();
        var index = 0;

        while (index < text.Length)
        {
            var start = index;

            while (index < text.Length && !char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            tokens.Add(text[start..index]);
        }

        return [.. tokens];
    }

    /// <summary>
    /// Compares tokens ignoring the whitespace they carry.
    /// </summary>
    /// <remarks>
    /// Case-sensitive on purpose: a rewrite that changes "текст" to "Текст" has
    /// changed something the writer should see.
    /// </remarks>
    private static bool Same(string left, string right) =>
        string.Equals(left.TrimEnd(), right.TrimEnd(), StringComparison.Ordinal);
}
