using WriteLite.Documents.Model;
using WriteLite.Services.Reading;

namespace WriteLite.Tests.Reading;

/// <summary>
/// The map between stored offsets and positions in the rendered book.
/// </summary>
/// <remarks>
/// This is the join every reading feature depends on: a highlight is an offset, and
/// painting it means turning that offset back into a range in a <c>FlowDocument</c>
/// that did not exist when the highlight was made. If the two disagree by even a
/// character, marks land a word to the left and nobody can say why.
///
/// Runs on the shared UI thread because a <c>FlowDocument</c> is a DispatcherObject.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class ReadingDocumentTests
{
    private static WlDocument Book(params string[] paragraphs)
    {
        var document = WlDocument.Empty();
        foreach (var text in paragraphs)
        {
            document.CurrentSection().Blocks.Add(DocumentParagraph.FromText(text));
        }

        return document;
    }

    [TestMethod]
    public void Paragraphs_are_joined_by_one_newline_each()
    {
        WpfTestHost.Run(() =>
        {
            var reading = ReadingDocument.Build(Book("Первый", "Второй", "Третий"), ReaderTypography.Default);

            Assert.AreEqual("Первый\nВторой\nТретий", reading.Text);
            Assert.AreEqual(3, reading.BlockCount);
            CollectionAssert.AreEqual(new[] { 0, 7, 14 }, reading.BlockStarts.ToArray());
        });
    }

    [TestMethod]
    public void An_offset_round_trips_through_a_pointer()
    {
        WpfTestHost.Run(() =>
        {
            var reading = ReadingDocument.Build(
                Book("Первый абзац книги.", "Второй абзац книги.", "Третий абзац книги."),
                ReaderTypography.Default);

            foreach (var offset in new[] { 0, 3, 18, 20, 25, 40, reading.Length - 1 })
            {
                var pointer = reading.PointerAt(offset);
                Assert.AreEqual(offset, reading.OffsetOf(pointer), $"Offset {offset} did not round-trip.");
            }
        });
    }

    [TestMethod]
    public void A_range_covers_exactly_the_stored_text()
    {
        WpfTestHost.Run(() =>
        {
            var reading = ReadingDocument.Build(
                Book("Выделенная фраза находится здесь."),
                ReaderTypography.Default);

            var start = reading.Text.IndexOf("фраза", StringComparison.Ordinal);
            var range = reading.RangeFor(start, "фраза".Length);

            Assert.IsNotNull(range);
            Assert.AreEqual("фраза", range.Text);
        });
    }

    [TestMethod]
    public void A_range_across_a_paragraph_break_still_resolves()
    {
        WpfTestHost.Run(() =>
        {
            var reading = ReadingDocument.Build(Book("Конец первого", "начало второго"), ReaderTypography.Default);

            var start = reading.Text.IndexOf("первого", StringComparison.Ordinal);
            var length = reading.Text.IndexOf("начало", StringComparison.Ordinal) + "начало".Length - start;

            var range = reading.RangeFor(start, length);

            Assert.IsNotNull(range);
            StringAssert.Contains(range.Text, "первого");
            StringAssert.Contains(range.Text, "начало");
        });
    }

    [TestMethod]
    public void Headings_keep_their_place_in_the_text()
    {
        WpfTestHost.Run(() =>
        {
            var document = WlDocument.Empty();
            var heading = DocumentParagraph.FromText("Глава первая");
            heading.OutlineLevel = 1;
            document.CurrentSection().Blocks.Add(heading);
            document.CurrentSection().Blocks.Add(DocumentParagraph.FromText("Тело главы."));

            var reading = ReadingDocument.Build(document, ReaderTypography.Default);

            Assert.AreEqual("Глава первая\nТело главы.", reading.Text);
            Assert.AreEqual(0, reading.OffsetOf(reading.PointerAt(0)));
        });
    }

    [TestMethod]
    public void List_markers_are_part_of_the_text_so_offsets_stay_contiguous()
    {
        WpfTestHost.Run(() =>
        {
            var document = WlDocument.Empty();
            var item = DocumentParagraph.FromText("Первый пункт");
            item.List = ListInfo.Bullet();
            document.CurrentSection().Blocks.Add(item);

            var reading = ReadingDocument.Build(document, ReaderTypography.Default);

            Assert.AreEqual("• Первый пункт", reading.Text);

            var start = reading.Text.IndexOf("Первый", StringComparison.Ordinal);
            Assert.AreEqual("Первый", reading.RangeFor(start, 6)!.Text);
        });
    }

    [TestMethod]
    public void A_preview_is_one_clean_line()
    {
        WpfTestHost.Run(() =>
        {
            var reading = ReadingDocument.Build(
                Book("Первая строка книги", "вторая строка книги", "третья строка книги"),
                ReaderTypography.Default);

            var preview = reading.Preview(20, 30);

            Assert.IsFalse(preview.Contains('\n'), "A preview is a line, not a paragraph.");
            Assert.IsTrue(preview.Length > 0);
        });
    }

    [TestMethod]
    public void An_empty_book_does_not_throw()
    {
        WpfTestHost.Run(() =>
        {
            var reading = ReadingDocument.Build(WlDocument.Empty(), ReaderTypography.Default);

            Assert.AreEqual(0, reading.Length);
            Assert.AreEqual(0, reading.OffsetOf(reading.PointerAt(0)));
            Assert.IsNull(reading.RangeFor(0, 5));
            Assert.AreEqual(string.Empty, reading.Preview(0));
        });
    }

    [TestMethod]
    public void Changing_the_type_size_does_not_change_a_single_offset()
    {
        WpfTestHost.Run(() =>
        {
            var book = Book("Первый абзац.", "Второй абзац.");

            var small = ReadingDocument.Build(book, new ReaderTypography { FontSize = 13 });
            var large = ReadingDocument.Build(book, new ReaderTypography { FontSize = 28 });

            // This is why a reading position can be a number: the layout changes, the
            // text does not, so the place someone stopped is still the same place.
            Assert.AreEqual(small.Text, large.Text);
            CollectionAssert.AreEqual(small.BlockStarts.ToArray(), large.BlockStarts.ToArray());
        });
    }

    [TestMethod]
    public void A_saved_mark_repaints_on_a_freshly_built_document()
    {
        WpfTestHost.Run(() =>
        {
            var book = Book("Первый абзац.", "Здесь выделенная фраза.", "Третий абзац.");

            var first = ReadingDocument.Build(book, ReaderTypography.Default);
            var start = first.Text.IndexOf("выделенная фраза", StringComparison.Ordinal);
            var anchor = new ReadingAnchor
            {
                Start = start,
                Length = "выделенная фраза".Length,
                Quote = "выделенная фраза"
            };

            // The document the mark was made on is gone; this is the one built at the
            // next launch.
            var second = ReadingDocument.Build(book, new ReaderTypography { FontSize = 24 });
            var resolution = ReadingAnchorResolver.Resolve(second.Text, anchor);

            Assert.AreEqual(AnchorStatus.Exact, resolution.Status);
            Assert.AreEqual("выделенная фраза", second.RangeFor(resolution.Start, resolution.Length)!.Text);
        });
    }
}
