using WriteLite.Models;
using WriteLite.Services;

namespace WriteLite.Tests;

[TestClass]
public sealed class IssueCountSummaryTests
{
    [TestMethod]
    public void CategoriesAreMutuallyExclusiveAndSumToAll()
    {
        var issues = Enum.GetValues<IssueCategory>()
            .Select((category, index) => new TextIssue(
                index, 1, "x", "y", "title", "explanation", category,
                IssueSeverity.Warning, RuleId: $"r{index}"))
            .ToArray();

        var counts = IssueCountSummary.From(issues);

        Assert.AreEqual(issues.Length, counts.All);
        Assert.AreEqual(counts.All, counts.CategoryTotal);
        Assert.AreEqual(2, counts.Style, "Readability belongs to the user-facing Style tab");
    }
}
