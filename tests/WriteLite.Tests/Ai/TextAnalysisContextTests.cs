using WriteLite.AI.Local;

namespace WriteLite.Tests.Ai;

[TestClass]
public sealed class TextAnalysisContextTests
{
    [TestMethod]
    public void InteractiveContext_LimitsLargeText_AndPreservesUtf16Offsets()
    {
        var text = new string('a', 700) + ". " + "Привет 😊 как дела сегодня";
        var context = TextAnalysisContext.ExtractInteractive(text, 120);
        Assert.IsLessThanOrEqualTo(120, context.Length);
        Assert.AreEqual(context.Text, text.Substring(context.Start, context.Length));
        Assert.IsTrue(context.Text.Contains("😊", StringComparison.Ordinal));
    }

    [TestMethod]
    public void InteractiveContext_ShortText_IsNotChanged()
    {
        const string text = "Открой https://example.com/a и C:\\Temp\\file.txt";
        var context = TextAnalysisContext.ExtractInteractive(text);
        Assert.AreEqual(0, context.Start);
        Assert.AreEqual(text, context.Text);
    }
}
