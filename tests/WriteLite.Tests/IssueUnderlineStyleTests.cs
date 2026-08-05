using System.Windows;
using System.Windows.Media;
using WriteLite.Models;
using WriteLite.Services;

namespace WriteLite.Tests;

[TestClass]
public sealed class IssueUnderlineStyleTests
{
    [TestMethod]
    public void CategoryMapsToDistinctSoftColors()
    {
        var orth = IssueUnderlineTheme.ForCategory(IssueCategory.Orthography);
        var grammar = IssueUnderlineTheme.ForCategory(IssueCategory.Grammar);
        var punct = IssueUnderlineTheme.ForCategory(IssueCategory.Punctuation);
        var style = IssueUnderlineTheme.ForCategory(IssueCategory.Style);

        Assert.AreEqual(IssueUnderlineTheme.Orthography, orth.Color);
        Assert.AreEqual(IssueUnderlineTheme.Grammar, grammar.Color);
        Assert.AreEqual(IssueUnderlineTheme.Punctuation, punct.Color);
        Assert.AreEqual(IssueUnderlineTheme.Style, style.Color);

        Assert.AreNotEqual(orth.Color, grammar.Color);
        Assert.AreNotEqual(grammar.Color, punct.Color);
        Assert.AreNotEqual(punct.Color, style.Color);
    }

    [TestMethod]
    public void UnderlineIsThinnerThanPreviousAggressiveStyle()
    {
        var style = IssueUnderlineTheme.ForCategory(IssueCategory.Orthography);
        Assert.IsLessThanOrEqualTo(1.3, style.Thickness);
        Assert.IsLessThanOrEqualTo(1.6, style.WaveHeight);
        Assert.IsGreaterThan(0.5, style.Opacity);
        Assert.IsLessThanOrEqualTo(1.0, style.Opacity);
    }

    [TestMethod]
    public void SuggestionSeverityLowersOpacity()
    {
        var error = IssueUnderlineTheme.ForCategory(IssueCategory.Orthography, IssueSeverity.Error);
        var suggestion = IssueUnderlineTheme.ForCategory(IssueCategory.Orthography, IssueSeverity.Suggestion);
        Assert.IsLessThan(error.Opacity, suggestion.Opacity);
    }

    [TestMethod]
    public void MinimizeDuplicationSoftensStroke()
    {
        IssueUnderlineTheme.MinimizeDuplication = false;
        var normal = IssueUnderlineTheme.ForCategory(IssueCategory.Grammar);
        IssueUnderlineTheme.MinimizeDuplication = true;
        var soft = IssueUnderlineTheme.ForCategory(IssueCategory.Grammar);
        IssueUnderlineTheme.MinimizeDuplication = false;

        Assert.IsLessThanOrEqualTo(normal.Thickness, soft.Thickness);
        Assert.IsLessThan(normal.Opacity, soft.Opacity);
    }

    [TestMethod]
    public void GeometryBuilderRejectsEmptyRectangles()
    {
        var issues = new[]
        {
            new TextIssue(0, 3, "bad", "good", "t", "e", IssueCategory.Orthography, IssueSeverity.Error)
        };
        var geometry = InlineIssueGeometryBuilder.Build(issues, new IReadOnlyList<Rect>[] { [] });
        Assert.AreEqual(0, geometry.Count);
    }

    [TestMethod]
    public void GeometryBuilderRejectsFieldWideFalseGeometry()
    {
        var field = new Rect(0, 0, 400, 80);
        var issues = new[]
        {
            new TextIssue(0, 3, "bad", "good", "t", "e", IssueCategory.Orthography, IssueSeverity.Error)
        };
        var fieldWide = new Rect(0, 0, 400, 20);
        var geometry = InlineIssueGeometryBuilder.Build(
            issues,
            new IReadOnlyList<Rect>[] { [fieldWide] },
            field);

        Assert.AreEqual(0, geometry.Count);
        Assert.IsFalse(InlineIssueGeometryBuilder.IsPreciseRangeRectangle(fieldWide, field));
    }

    [TestMethod]
    public void GeometryBuilderKeepsPreciseWordRect()
    {
        var field = new Rect(0, 0, 400, 80);
        var issues = new[]
        {
            new TextIssue(0, 3, "bad", "good", "t", "e", IssueCategory.Orthography, IssueSeverity.Error)
        };
        var word = new Rect(40, 12, 28, 16);
        var geometry = InlineIssueGeometryBuilder.Build(
            issues,
            new IReadOnlyList<Rect>[] { [word] },
            field);

        Assert.AreEqual(1, geometry.Count);
        Assert.AreEqual(28d, geometry[0].Rectangles[0].Width);
    }

    [TestMethod]
    public void NaNAndInfiniteRectanglesAreRejected()
    {
        Assert.IsFalse(InlineIssueGeometryBuilder.IsPreciseRangeRectangle(
            new Rect(double.NaN, 0, 10, 10)));
        Assert.IsFalse(InlineIssueGeometryBuilder.IsPreciseRangeRectangle(
            new Rect(0, 0, double.PositiveInfinity, 10)));
        Assert.IsFalse(InlineIssueGeometryBuilder.IsPreciseRangeRectangle(Rect.Empty));
    }

    [TestMethod]
    public void ProviderMatchesThemeCategory()
    {
        var issue = new TextIssue(0, 1, "a", "b", "t", "e", IssueCategory.Punctuation, IssueSeverity.Warning);
        var style = UnderlineStyleProvider.GetStyle(issue);
        Assert.AreEqual(IssueUnderlineTheme.Punctuation, style.Color);
        Assert.AreEqual(style.Color, UnderlineStyleProvider.GetUnderlineColor(issue));
    }
}
