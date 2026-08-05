using WriteLite.Services;

namespace WriteLite.Tests;

[TestClass]
public sealed class IndicatorPresentationTests
{
    [DataTestMethod]
    [DataRow(0, "")]
    [DataRow(1, "1")]
    [DataRow(9, "9")]
    [DataRow(10, "10")]
    [DataRow(99, "99")]
    [DataRow(100, "99+")]
    public void BadgeText_UsesReservedFixedPresentation(int count, string expected)
        => Assert.AreEqual(expected, IndicatorPresentation.BadgeText(count));

    [TestMethod]
    public void OnePixelBoundsJitter_DoesNotMoveIndicator()
        => Assert.IsFalse(IndicatorPresentation.ShouldUpdatePosition(100, 200, 101, 199));

    [TestMethod]
    public void ActualBoundsMove_RepositionsIndicator()
        => Assert.IsTrue(IndicatorPresentation.ShouldUpdatePosition(100, 200, 102, 200));
}
