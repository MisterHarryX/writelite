using WriteLite.Services.Reading;

namespace WriteLite.Tests.Reading;

/// <summary>
/// Putting a saved mark back on the page.
/// </summary>
/// <remarks>
/// The offset alone is right almost every time and wrong in exactly the case that
/// matters: a source file replaced by a new edition, where every mark past the change
/// is shifted by the same amount. These tests pin the three outcomes — found where it
/// was, found somewhere else, genuinely gone — and pin that "gone" never means
/// "deleted".
/// </remarks>
[TestClass]
public sealed class ReadingAnchorResolverTests
{
    private static ReadingAnchor Anchor(int start, string quote) =>
        new() { Start = start, Length = quote.Length, Quote = quote };

    [TestMethod]
    public void An_unchanged_document_resolves_exactly()
    {
        const string text = "Первое предложение. Второе предложение.";

        var result = ReadingAnchorResolver.Resolve(text, Anchor(20, "Второе"));

        Assert.AreEqual(AnchorStatus.Exact, result.Status);
        Assert.AreEqual(20, result.Start);
        Assert.AreEqual(6, result.Length);
    }

    [TestMethod]
    public void Text_inserted_earlier_shifts_the_mark_rather_than_losing_it()
    {
        const string original = "Начало. Важная мысль здесь.";
        var anchor = Anchor(original.IndexOf("Важная мысль", StringComparison.Ordinal), "Важная мысль");

        var edited = "Новое предисловие. " + original;

        var result = ReadingAnchorResolver.Resolve(edited, anchor);

        Assert.AreEqual(AnchorStatus.Shifted, result.Status);
        Assert.AreEqual("Важная мысль", edited.Substring(result.Start, result.Length));
    }

    [TestMethod]
    public void Text_removed_earlier_shifts_the_mark_backwards()
    {
        const string original = "Длинное предисловие, которое потом удалили. Важная мысль здесь.";
        var anchor = Anchor(original.IndexOf("Важная мысль", StringComparison.Ordinal), "Важная мысль");

        const string edited = "Важная мысль здесь.";

        var result = ReadingAnchorResolver.Resolve(edited, anchor);

        Assert.AreEqual(AnchorStatus.Shifted, result.Status);
        Assert.AreEqual(0, result.Start);
    }

    [TestMethod]
    public void A_repeated_passage_resolves_to_the_nearest_copy()
    {
        // The same sentence three times. A naive IndexOf would send every mark to the
        // first one, and a reader who highlighted the third would be thrown to the top
        // of the chapter.
        var filler = new string('.', 400);
        var text = "Повтор. " + filler + "Повтор. " + filler + "Повтор.";

        var third = text.LastIndexOf("Повтор.", StringComparison.Ordinal);
        var anchor = Anchor(third + 2, "Повтор.");

        var result = ReadingAnchorResolver.Resolve(text, anchor);

        Assert.AreEqual(AnchorStatus.Shifted, result.Status);
        Assert.AreEqual(third, result.Start, "The nearest occurrence to the stored offset wins.");
    }

    [TestMethod]
    public void A_passage_that_no_longer_exists_detaches_rather_than_pointing_somewhere_wrong()
    {
        var result = ReadingAnchorResolver.Resolve("Совершенно другой текст.", Anchor(0, "Пропавшая цитата"));

        Assert.AreEqual(AnchorStatus.Detached, result.Status);
        Assert.IsFalse(result.IsPlaced);
    }

    [TestMethod]
    public void An_empty_document_detaches_every_mark()
    {
        var result = ReadingAnchorResolver.Resolve(string.Empty, Anchor(0, "Что угодно"));

        Assert.AreEqual(AnchorStatus.Detached, result.Status);
    }

    [TestMethod]
    public void An_offset_past_the_end_does_not_throw()
    {
        const string text = "Короткий текст.";

        var result = ReadingAnchorResolver.Resolve(text, Anchor(9_000, "текст"));

        Assert.AreEqual(AnchorStatus.Shifted, result.Status);
        Assert.AreEqual(text.IndexOf("текст", StringComparison.Ordinal), result.Start);
    }

    // ── Position ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void A_position_past_the_end_of_a_shortened_book_lands_at_its_end()
    {
        Assert.AreEqual(99, ReadingAnchorResolver.ClampPosition(5_000, 100));
        Assert.AreEqual(0, ReadingAnchorResolver.ClampPosition(-20, 100));
        Assert.AreEqual(0, ReadingAnchorResolver.ClampPosition(10, 0));
    }

    [TestMethod]
    public void Progress_is_the_fraction_of_the_way_through()
    {
        Assert.AreEqual(0.0, ReadingAnchorResolver.Fraction(0, 200), 0.0001);
        Assert.AreEqual(0.5, ReadingAnchorResolver.Fraction(100, 200), 0.0001);
        Assert.AreEqual(1.0, ReadingAnchorResolver.Fraction(200, 200), 0.0001);
        Assert.AreEqual(0.0, ReadingAnchorResolver.Fraction(10, 0), 0.0001, "An empty book is not half read.");
    }
}
