using WriteLite.Services;

namespace WriteLite.Tests.Corrections;

/// <summary>
/// §6 and §21: analyzer offsets are not automatically the offsets a window selects with.
/// </summary>
[TestClass]
public sealed class Win32SelectionOffsetTests
{
    private const string TwoLines = "первая строка\r\nвторая строка";

    [TestMethod]
    public void AWindowThatCountsCrLfAsTwo_NeedsNoMapping()
    {
        var convention = Win32SelectionOffsets.DetectConvention(TwoLines, TwoLines.Length);
        Assert.AreEqual(Win32SelectionConvention.MatchesWindowText, convention);

        Assert.IsTrue(Win32SelectionOffsets.TryMap(TwoLines, 20, convention, out var mapped));
        Assert.AreEqual(20, mapped);
    }

    [TestMethod]
    public void AWindowThatCountsALineBreakOnce_ShiftsEveryOffsetAfterIt()
    {
        // RichEdit: WM_GETTEXT gives 28 characters, the selection model counts 27.
        var convention = Win32SelectionOffsets.DetectConvention(TwoLines, TwoLines.Length - 1);
        Assert.AreEqual(Win32SelectionConvention.LineBreaksCountOnce, convention);

        // Before the break, nothing moves.
        Assert.IsTrue(Win32SelectionOffsets.TryMap(TwoLines, 7, convention, out var early));
        Assert.AreEqual(7, early);

        // After it, one character per collapsed break. This is the miss that put the
        // replacement inside the neighbouring word on every multiline RichEdit.
        Assert.IsTrue(Win32SelectionOffsets.TryMap(TwoLines, 15, convention, out var late));
        Assert.AreEqual(14, late);

        // «вторая» starts at 15 in the text and at 14 in the selection model.
        Assert.AreEqual("вторая", TwoLines.Substring(15, 6));
    }

    [TestMethod]
    public void ManyLinesAccumulate()
    {
        const string text = "a\r\nb\r\nc\r\nроботает";
        var convention = Win32SelectionOffsets.DetectConvention(text, text.Length - 3);
        Assert.AreEqual(Win32SelectionConvention.LineBreaksCountOnce, convention);

        var wordStart = text.IndexOf("роботает", StringComparison.Ordinal);
        Assert.IsTrue(Win32SelectionOffsets.TryMap(text, wordStart, convention, out var mapped));
        Assert.AreEqual(wordStart - 3, mapped);
    }

    [TestMethod]
    public void ALoneCarriageReturnIsAlreadyOneCharacterInBothModels()
    {
        const string text = "a\rb";
        Assert.AreEqual(
            Win32SelectionConvention.MatchesWindowText,
            Win32SelectionOffsets.DetectConvention(text, text.Length));

        Assert.IsTrue(Win32SelectionOffsets.TryMap(
            text, 2, Win32SelectionConvention.LineBreaksCountOnce, out var mapped));
        Assert.AreEqual(2, mapped);
    }

    [TestMethod]
    public void AWindowWhoseAnswerFitsNeitherModel_IsRefused()
    {
        // Refusing produces no write and hands the correction to the next strategy;
        // guessing would put the right word in the wrong place.
        var convention = Win32SelectionOffsets.DetectConvention(TwoLines, 3);
        Assert.AreEqual(Win32SelectionConvention.Unknown, convention);
        Assert.IsFalse(Win32SelectionOffsets.TryMap(TwoLines, 5, convention, out _));
    }

    [TestMethod]
    public void AnOffsetOutsideTheTextIsRefused()
    {
        Assert.IsFalse(Win32SelectionOffsets.TryMap(
            TwoLines, TwoLines.Length + 1, Win32SelectionConvention.MatchesWindowText, out _));
        Assert.IsFalse(Win32SelectionOffsets.TryMap(
            TwoLines, -1, Win32SelectionConvention.MatchesWindowText, out _));
    }
}
