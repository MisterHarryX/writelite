namespace WriteLite.Services.Reading;

/// <summary>
/// Divides a book into numbered pages the reader can navigate by.
/// </summary>
/// <remarks>
/// <para><b>Why pages are virtual.</b> WriteLite reads a book as one continuously scrolling
/// flow, not as a stack of laid-out sheets, so there is no rendered page to count. Neither is
/// there a physical one to borrow: TXT and DOCX have no pages at all, EPUB has them only where
/// a publisher chose to mark them, and a PDF's pages belong to the file's own layout rather
/// than to the text extracted from it. Deriving a number from the viewport instead would
/// produce a page count that changes when the window is resized, the type size is changed or
/// the measure is narrowed — that is, a number that means nothing between two sessions and
/// nothing at all between two people reading the same book.</para>
///
/// <para><b>What a page is here.</b> A fixed span of the document's own text, about
/// <see cref="TargetCharacters"/> characters long, ending at a paragraph boundary. The
/// classic manuscript page — thirty lines of sixty characters — is the size, because it is
/// what the phrase "page 124 of 367" has always meant when a text has no typeset pages of its
/// own, and because it puts a book of ordinary length into a few hundred pages rather than
/// tens or tens of thousands.</para>
///
/// <para><b>What this buys.</b> The division depends on the text and nothing else. Resizing
/// the window, changing the font size, the line spacing or the measure cannot change the page
/// count or move the reader between pages, because none of those change the text. Two people
/// reading the same file are on the same page. Re-opening the book after a restart lands on
/// the page it was left on, since the stored reading position is a character offset and this
/// is a function of character offsets.</para>
///
/// <para><b>What it does not claim.</b> These are not the publisher's page numbers, and where
/// a source has its own they will not agree. That is stated in the reader rather than hidden:
/// the alternative is a number that looks authoritative and is quietly wrong.</para>
/// </remarks>
public sealed class ReadingPagination
{
    /// <summary>
    /// Characters per page before snapping to a paragraph boundary — a standard page.
    /// </summary>
    public const int TargetCharacters = 1_800;

    /// <summary>
    /// How far past the target a page may run to reach the end of a paragraph.
    /// </summary>
    /// <remarks>
    /// Beyond this the paragraph is broken rather than swallowed, so one enormous block —
    /// a whole chapter exported without paragraph marks, which happens — cannot collapse the
    /// book into a single page.
    /// </remarks>
    private const int MaximumOverrun = TargetCharacters;

    private readonly int[] _pageStarts;

    private ReadingPagination(int[] pageStarts, int textLength)
    {
        _pageStarts = pageStarts;
        TextLength = textLength;
    }

    /// <summary>Characters in the document this division was built for.</summary>
    public int TextLength { get; }

    /// <summary>Pages in the book. Always at least one, including for an empty document.</summary>
    public int PageCount => _pageStarts.Length;

    /// <summary>The empty division: one page, no text. What an unopened reader shows.</summary>
    public static ReadingPagination Empty { get; } = new([0], 0);

    /// <summary>Divides a loaded document, using its paragraph starts as the break points.</summary>
    public static ReadingPagination For(ReadingDocument? document) =>
        document is null ? Empty : Build(document.Text, document.BlockStarts);

    /// <summary>
    /// Divides <paramref name="text"/>, breaking pages at the given paragraph offsets.
    /// </summary>
    /// <param name="blockStarts">
    /// Ascending character offsets where paragraphs begin. May be empty, in which case pages
    /// are cut at exactly <see cref="TargetCharacters"/>.
    /// </param>
    public static ReadingPagination Build(string? text, IReadOnlyList<int>? blockStarts)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Empty;
        }

        var breaks = NormalizedBreaks(blockStarts, text.Length);
        var starts = new List<int> { 0 };
        var cursor = 0;

        while (cursor < text.Length)
        {
            var target = cursor + TargetCharacters;
            if (target >= text.Length)
            {
                break;
            }

            var next = NextBreakAtOrAfter(breaks, target);
            if (next is null || next.Value > target + MaximumOverrun)
            {
                // No paragraph ends within reach. Cut at the target rather than let one
                // unbroken block become the whole book.
                next = target;
            }

            if (next.Value <= cursor)
            {
                // Defensive: a break list that does not advance would loop forever.
                next = cursor + TargetCharacters;
            }

            if (next.Value >= text.Length)
            {
                break;
            }

            starts.Add(next.Value);
            cursor = next.Value;
        }

        return new ReadingPagination([.. starts], text.Length);
    }

    /// <summary>The 1-based page containing a character offset.</summary>
    public int PageAt(int offset)
    {
        if (_pageStarts.Length <= 1)
        {
            return 1;
        }

        var clamped = Math.Clamp(offset, 0, Math.Max(0, TextLength));
        var index = Array.BinarySearch(_pageStarts, clamped);

        // BinarySearch returns the complement of the next larger element when there is no
        // exact hit; the page is the one before it.
        if (index < 0)
        {
            index = ~index - 1;
        }

        return Math.Clamp(index, 0, _pageStarts.Length - 1) + 1;
    }

    /// <summary>The character offset a 1-based page begins at. Out-of-range pages clamp.</summary>
    public int OffsetOfPage(int page)
    {
        if (_pageStarts.Length == 0)
        {
            return 0;
        }

        var index = Math.Clamp(page, 1, _pageStarts.Length) - 1;
        return _pageStarts[index];
    }

    /// <summary>True when <paramref name="page"/> names a page of this book.</summary>
    public bool IsValidPage(int page) => page >= 1 && page <= PageCount;

    private static int[] NormalizedBreaks(IReadOnlyList<int>? blockStarts, int textLength)
    {
        if (blockStarts is null || blockStarts.Count == 0)
        {
            return [];
        }

        // Sorted and bounded rather than trusted: the break list comes from a document build
        // and a malformed one must produce odd page sizes, never an infinite loop.
        var breaks = blockStarts
            .Where(start => start > 0 && start < textLength)
            .Distinct()
            .ToArray();

        Array.Sort(breaks);
        return breaks;
    }

    private static int? NextBreakAtOrAfter(int[] breaks, int offset)
    {
        if (breaks.Length == 0)
        {
            return null;
        }

        var index = Array.BinarySearch(breaks, offset);
        if (index < 0)
        {
            index = ~index;
        }

        return index < breaks.Length ? breaks[index] : null;
    }
}
