using WriteLite.Services;

namespace WriteLite.Tests;

/// <summary>
/// A tool that corrects Russian has to agree its own numerals.
/// </summary>
/// <remarks>
/// The editor status bar previously read "23 слов" because the rule tested
/// <c>count is >= 2 and &lt;= 4</c> instead of the last two digits.
/// </remarks>
[TestClass]
public sealed class RussianPluralTests
{
    [TestMethod]
    [DataRow(0, "0 слов")]
    [DataRow(1, "1 слово")]
    [DataRow(2, "2 слова")]
    [DataRow(4, "4 слова")]
    [DataRow(5, "5 слов")]
    [DataRow(11, "11 слов")]
    [DataRow(12, "12 слов")]
    [DataRow(14, "14 слов")]
    [DataRow(15, "15 слов")]
    [DataRow(21, "21 слово")]
    [DataRow(22, "22 слова")]
    [DataRow(23, "23 слова")]
    [DataRow(25, "25 слов")]
    [DataRow(100, "100 слов")]
    [DataRow(101, "101 слово")]
    [DataRow(111, "111 слов")]
    [DataRow(1002, "1002 слова")]
    public void Words_agree_with_the_count(int count, string expected) =>
        Assert.AreEqual(expected, RussianPlural.Words(count));

    [TestMethod]
    [DataRow(1, "1 правило")]
    [DataRow(3, "3 правила")]
    [DataRow(13, "13 правил")]
    [DataRow(22, "22 правила")]
    public void Rules_agree_with_the_count(int count, string expected) =>
        Assert.AreEqual(expected, RussianPlural.Rules(count));

    [TestMethod]
    [DataRow(1, "1 замечание")]
    [DataRow(2, "2 замечания")]
    [DataRow(5, "5 замечаний")]
    [DataRow(21, "21 замечание")]
    public void Issues_agree_with_the_count(int count, string expected) =>
        Assert.AreEqual(expected, RussianPlural.Issues(count));
}
