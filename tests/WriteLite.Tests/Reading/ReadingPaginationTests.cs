using WriteLite.Services.Reading;

namespace WriteLite.Tests.Reading;

/// <summary>
/// The reader's page numbers, and the properties that make them worth showing.
/// </summary>
/// <remarks>
/// The reader scrolls continuously, so its pages are a division of the text rather than a
/// count of anything drawn. The point of these tests is the guarantee that follows from
/// that: because the division reads only the document's characters, nothing about how the
/// book is displayed can change it. A page number that moved when someone resized the
/// window would be worse than no page number at all.
/// </remarks>
[TestClass]
public sealed class ReadingPaginationTests
{
    private static string Book(int paragraphs, int paragraphLength = 700)
        => string.Join("\n", Enumerable.Range(0, paragraphs)
            .Select(i => new string((char)('a' + (i % 26)), paragraphLength)));

    private static IReadOnlyList<int> BlockStartsOf(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' && i + 1 < text.Length)
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    [TestMethod]
    public void AnEmptyDocumentIsOnePage()
    {
        // Not zero: "страница 0 из 0" is not a position, and the reader always has to be
        // somewhere.
        Assert.AreEqual(1, ReadingPagination.Build("", []).PageCount);
        Assert.AreEqual(1, ReadingPagination.Empty.PageCount);
        Assert.AreEqual(1, ReadingPagination.Empty.PageAt(0));
    }

    [TestMethod]
    public void AShortDocumentIsOnePage()
    {
        var text = Book(1, 200);
        Assert.AreEqual(1, ReadingPagination.Build(text, BlockStartsOf(text)).PageCount);
    }

    [TestMethod]
    public void PageCountGrowsWithTheText()
    {
        var text = Book(40);
        var pagination = ReadingPagination.Build(text, BlockStartsOf(text));

        // ~28 000 characters at 1 800 per page.
        Assert.IsGreaterThan(10, pagination.PageCount);
        Assert.IsLessThan(25, pagination.PageCount);
    }

    [TestMethod]
    public void EveryPageStartsWhereItSaysItDoes()
    {
        var text = Book(40);
        var pagination = ReadingPagination.Build(text, BlockStartsOf(text));

        for (var page = 1; page <= pagination.PageCount; page++)
        {
            Assert.AreEqual(
                page,
                pagination.PageAt(pagination.OffsetOfPage(page)),
                $"page {page} does not contain its own first character");
        }
    }

    [TestMethod]
    public void PagesRunForwardAndCoverTheWholeBook()
    {
        var text = Book(40);
        var pagination = ReadingPagination.Build(text, BlockStartsOf(text));

        Assert.AreEqual(0, pagination.OffsetOfPage(1));

        var previous = -1;
        for (var page = 1; page <= pagination.PageCount; page++)
        {
            var start = pagination.OffsetOfPage(page);
            Assert.IsGreaterThan(previous, start, "page starts must strictly increase");
            previous = start;
        }

        Assert.AreEqual(pagination.PageCount, pagination.PageAt(text.Length));
    }

    [TestMethod]
    public void PagesBreakAtParagraphBoundaries()
    {
        var text = Book(40);
        var starts = BlockStartsOf(text);
        var pagination = ReadingPagination.Build(text, starts);

        for (var page = 2; page <= pagination.PageCount; page++)
        {
            Assert.Contains(
                pagination.OffsetOfPage(page),
                starts,
                $"page {page} begins mid-paragraph");
        }
    }

    [TestMethod]
    public void OneUnbrokenBlockIsStillPaginated()
    {
        // A chapter exported with no paragraph marks. Snapping to the next boundary would
        // make the entire book one page; the division falls back to cutting at the target.
        var text = new string('x', 50_000);
        var pagination = ReadingPagination.Build(text, [0]);

        Assert.IsGreaterThan(20, pagination.PageCount);
    }

    [TestMethod]
    public void DisplaySettingsCannotChangeThePages()
    {
        // The property the whole model exists for. Typography is not an input here, so this
        // is a statement about the type rather than a simulation — and it is the statement
        // that has to keep being true as the reader gains settings.
        var text = Book(40);
        var starts = BlockStartsOf(text);

        var a = ReadingPagination.Build(text, starts);
        var b = ReadingPagination.Build(text, starts);

        Assert.AreEqual(a.PageCount, b.PageCount);
        for (var page = 1; page <= a.PageCount; page++)
        {
            Assert.AreEqual(a.OffsetOfPage(page), b.OffsetOfPage(page));
        }
    }

    [TestMethod]
    public void AnOffsetPastTheEndIsTheLastPageAndNotACrash()
    {
        var text = Book(10);
        var pagination = ReadingPagination.Build(text, BlockStartsOf(text));

        Assert.AreEqual(pagination.PageCount, pagination.PageAt(text.Length * 4));
        Assert.AreEqual(1, pagination.PageAt(-500));
    }

    [TestMethod]
    public void APageNumberOutsideTheBookClampsInsteadOfThrowing()
    {
        var text = Book(10);
        var pagination = ReadingPagination.Build(text, BlockStartsOf(text));

        Assert.AreEqual(pagination.OffsetOfPage(1), pagination.OffsetOfPage(0));
        Assert.AreEqual(pagination.OffsetOfPage(1), pagination.OffsetOfPage(-7));
        Assert.AreEqual(
            pagination.OffsetOfPage(pagination.PageCount),
            pagination.OffsetOfPage(pagination.PageCount + 100));

        Assert.IsFalse(pagination.IsValidPage(0));
        Assert.IsFalse(pagination.IsValidPage(pagination.PageCount + 1));
        Assert.IsTrue(pagination.IsValidPage(pagination.PageCount));
    }

    [TestMethod]
    public void AMalformedBreakListDoesNotHangOrCorruptThePages()
    {
        // Unsorted, duplicated, negative and out-of-range offsets: a document build that
        // went wrong must cost odd page sizes, never a loop that never ends.
        var text = Book(20);
        var pagination = ReadingPagination.Build(text, [900, 12, -4, 900, int.MaxValue, 3000, 12]);

        Assert.IsGreaterThan(1, pagination.PageCount);
        var previous = -1;
        for (var page = 1; page <= pagination.PageCount; page++)
        {
            var start = pagination.OffsetOfPage(page);
            Assert.IsGreaterThan(previous, start);
            previous = start;
        }
    }
}
