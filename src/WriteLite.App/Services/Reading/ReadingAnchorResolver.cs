namespace WriteLite.Services.Reading;

/// <summary>Where an anchor ended up when the reader tried to place it.</summary>
public enum AnchorStatus
{
    /// <summary>The offset still holds the quote. Nothing moved.</summary>
    Exact,

    /// <summary>The quote was found elsewhere; the offset has been corrected.</summary>
    Shifted,

    /// <summary>The quote is gone. The mark is kept but cannot be pointed at.</summary>
    Detached
}

public readonly record struct AnchorResolution(AnchorStatus Status, int Start, int Length)
{
    public bool IsPlaced => Status != AnchorStatus.Detached;
}

/// <summary>
/// Puts a saved mark back on the page after the document has been reloaded.
/// </summary>
/// <remarks>
/// The offset is checked first because it is right almost every time — the same file
/// flattens to the same text. It stops being right when the source is replaced by a
/// new edition, re-exported by a different tool, or an earlier chapter is edited, and
/// then every mark after the change is off by the same amount. Searching for the
/// stored quote outwards from the old offset finds them all, and finds them at the
/// nearest of several identical passages rather than at the first one in the book.
///
/// A quote that genuinely no longer exists yields <see cref="AnchorStatus.Detached"/>.
/// The mark is still shown in its panel, still carries the reader's note, and simply
/// cannot be jumped to — which is honest, and is the opposite of deleting it.
/// </remarks>
public static class ReadingAnchorResolver
{
    /// <summary>
    /// How far either side of the old offset the quote is looked for before giving up
    /// on locality and scanning the whole document.
    /// </summary>
    private static readonly int NearWindow = WriteLiteDefaults.TextLimits.ReadingAnchorNearWindow;

    public static AnchorResolution Resolve(string text, ReadingAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);

        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(anchor.Quote))
        {
            return new AnchorResolution(AnchorStatus.Detached, 0, 0);
        }

        var quote = anchor.Quote;

        if (anchor.Start >= 0
            && anchor.Start + quote.Length <= text.Length
            && string.CompareOrdinal(text, anchor.Start, quote, 0, quote.Length) == 0)
        {
            return new AnchorResolution(AnchorStatus.Exact, anchor.Start, quote.Length);
        }

        // Local search first, so a repeated sentence resolves to the copy the reader
        // actually marked rather than to the earliest one in the book.
        var windowStart = Math.Clamp(anchor.Start - NearWindow, 0, Math.Max(0, text.Length - 1));
        var windowEnd = Math.Clamp(anchor.Start + NearWindow, 0, text.Length);
        var windowLength = Math.Max(0, windowEnd - windowStart);

        if (windowLength >= quote.Length)
        {
            var found = NearestOccurrence(text, quote, windowStart, windowLength, anchor.Start);
            if (found >= 0)
            {
                return new AnchorResolution(AnchorStatus.Shifted, found, quote.Length);
            }
        }

        var anywhere = text.IndexOf(quote, StringComparison.Ordinal);
        return anywhere >= 0
            ? new AnchorResolution(AnchorStatus.Shifted, anywhere, quote.Length)
            : new AnchorResolution(AnchorStatus.Detached, 0, 0);
    }

    /// <summary>The occurrence inside the window whose start is closest to <paramref name="target"/>.</summary>
    private static int NearestOccurrence(string text, string quote, int windowStart, int windowLength, int target)
    {
        var best = -1;
        var bestDistance = int.MaxValue;
        var cursor = windowStart;
        var limit = windowStart + windowLength;

        while (cursor < limit)
        {
            var index = text.IndexOf(quote, cursor, limit - cursor, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            var distance = Math.Abs(index - target);
            if (distance < bestDistance)
            {
                best = index;
                bestDistance = distance;
            }

            cursor = index + 1;
        }

        return best;
    }

    /// <summary>
    /// Clamps a remembered reading position onto a document that may have changed length.
    /// </summary>
    /// <remarks>
    /// A position past the end is not an error worth reporting; it means the book got
    /// shorter, and the honest answer is the end of it.
    /// </remarks>
    public static int ClampPosition(int position, int textLength) =>
        textLength <= 0 ? 0 : Math.Clamp(position, 0, Math.Max(0, textLength - 1));

    /// <summary>How far through a document an offset is, as 0–1.</summary>
    public static double Fraction(int position, int textLength) =>
        textLength <= 0 ? 0 : Math.Clamp(position / (double)textLength, 0, 1);
}
