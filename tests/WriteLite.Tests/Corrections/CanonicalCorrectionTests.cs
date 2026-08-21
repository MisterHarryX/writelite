using WriteLite.Language.Core;

namespace WriteLite.Tests.Corrections;

/// <summary>
/// §5 and §21: a correction may only be applied when its span still holds its original, and
/// offsets are UTF-16 code units that never land inside a character.
/// </summary>
[TestClass]
public sealed class CanonicalCorrectionTests
{
    [TestMethod]
    public void Binds_WhenSpanHoldsTheOriginal()
    {
        var status = CanonicalCorrection.TryBind(
            "Сечас программа роботает", 16, 8, "роботает", "работает", out var correction);

        Assert.AreEqual(CorrectionBindingStatus.Bound, status);
        Assert.IsNotNull(correction);
        Assert.AreEqual(16, correction.Start);
        Assert.AreEqual(8, correction.Length);
        Assert.AreEqual("роботает", correction.Original);
        Assert.AreEqual("работает", correction.Replacement);
        Assert.AreEqual("Сечас программа работает", correction.ApplyTo("Сечас программа роботает"));
    }

    [TestMethod]
    public void Refuses_WhenTheTextChangedUnderneath()
    {
        // §19: the user fixed the word themselves while the popup was open.
        var status = CanonicalCorrection.TryBind(
            "работает сегодня", 0, 8, "роботает", "работает", out var correction);

        Assert.AreEqual(CorrectionBindingStatus.OriginalMismatch, status);
        Assert.IsNull(correction);
    }

    [TestMethod]
    public void Refuses_WhenTheSpanRunsPastTheEnd()
    {
        Assert.AreEqual(
            CorrectionBindingStatus.OutOfRange,
            CanonicalCorrection.TryBind("короткий", 4, 40, "х", "у", out _));

        Assert.AreEqual(
            CorrectionBindingStatus.OutOfRange,
            CanonicalCorrection.TryBind("короткий", -1, 2, "ко", "КО", out _));
    }

    [TestMethod]
    public void Refuses_AZeroLengthSpanThatClaimsAnOriginal()
    {
        Assert.AreEqual(
            CorrectionBindingStatus.Malformed,
            CanonicalCorrection.TryBind("текст", 2, 0, "кс", ".", out _));
    }

    [TestMethod]
    public void Accepts_APureInsertion()
    {
        var status = CanonicalCorrection.TryBind("Привет", 6, 0, "", ",", out var correction);

        Assert.AreEqual(CorrectionBindingStatus.Bound, status);
        Assert.IsNotNull(correction);
        Assert.IsTrue(correction.IsInsertion);
        Assert.AreEqual("Привет,", correction.ApplyTo("Привет"));
    }

    [TestMethod]
    public void Refuses_AnOffsetInsideASurrogatePair()
    {
        // 😀 is two UTF-16 code units; offset 1 names neither character.
        const string text = "😀роботает";
        Assert.AreEqual(
            CorrectionBindingStatus.OutOfRange,
            CanonicalCorrection.TryBind(text, 1, 8, "роботает", "работает", out _));

        Assert.AreEqual(
            CorrectionBindingStatus.Bound,
            CanonicalCorrection.TryBind(text, 2, 8, "роботает", "работает", out _));
    }

    [DataTestMethod]
    [DataRow("роботает", "работает")]
    [DataRow("Сечас", "Сейчас")]
    [DataRow("превет", "привет")]
    [DataRow("пожалуста", "пожалуйста")]
    [DataRow("интиресный", "интересный")]
    [DataRow("сделаный", "сделанный")]
    public void AppliesTheWholeReplacement_InEveryPosition(string original, string replacement)
    {
        // §20 and §21: start of text, end of text, and surrounded by material that has
        // historically confused offset arithmetic.
        string[] hosts =
        [
            "{0}",
            "{0} дальше",
            "начало {0}",
            "начало {0} дальше",
            "😀 {0} 😀",
            "«{0}»",
            "первая строка\r\nвторая {0} строка",
            "два  пробела {0} рядом",
            "тире — {0} и ещё",
            "ёлка {0} ёж",
            "неразрывный пробел {0} тут",
        ];

        foreach (var host in hosts)
        {
            var text = string.Format(host, original);
            var start = text.IndexOf(original, StringComparison.Ordinal);
            var status = CanonicalCorrection.TryBind(
                text, start, original.Length, original, replacement, out var correction);

            Assert.AreEqual(CorrectionBindingStatus.Bound, status, host);
            Assert.IsNotNull(correction, host);
            Assert.AreEqual(string.Format(host, replacement), correction.ApplyTo(text), host);
        }
    }
}
