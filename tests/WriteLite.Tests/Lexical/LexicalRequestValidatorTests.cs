using WriteLite.Services.Lexical;

namespace WriteLite.Tests.Lexical;

[TestClass]
public sealed class LexicalRequestValidatorTests
{
    [TestMethod]
    public void IsCurrent_RequiresTargetGenerationAndTextVersionToAllMatch()
    {
        Assert.IsTrue(LexicalRequestValidator.IsCurrent("target-a", 4, 9, "target-a", 4, 9));
        Assert.IsFalse(LexicalRequestValidator.IsCurrent("target-a", 4, 9, "target-b", 4, 9));
        Assert.IsFalse(LexicalRequestValidator.IsCurrent("target-a", 4, 9, "target-a", 5, 9));
        Assert.IsFalse(LexicalRequestValidator.IsCurrent("target-a", 4, 9, "target-a", 4, 10));
    }

    [TestMethod]
    [TestCategory("Stress")]
    [TestProperty("Category", "Stress")]
    public void IsCurrent_RejectsEveryStaleVersionDuringRapidTargetChanges()
    {
        const string target = "target-active";
        const int generation = 31;
        const long version = 90;

        for (var i = 0; i < 1_000; i++)
        {
            Assert.IsFalse(LexicalRequestValidator.IsCurrent(target, generation, version, target, generation, version - 1));
            Assert.IsFalse(LexicalRequestValidator.IsCurrent(target, generation, version, "target-previous", generation, version));
            Assert.IsFalse(LexicalRequestValidator.IsCurrent(target, generation, version, target, generation - 1, version));
        }

        Assert.IsTrue(LexicalRequestValidator.IsCurrent(target, generation, version, target, generation, version));
    }
}
