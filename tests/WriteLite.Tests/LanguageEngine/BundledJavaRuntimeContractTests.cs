using WriteLite.Services.LanguageEngine;

namespace WriteLite.Tests.LanguageEngine;

/// <summary>
/// The contract between the installer and <see cref="WriteLiteJavaResolver"/>.
/// </summary>
/// <remarks>
/// LanguageTool is Java. Nothing in the repository ships a JVM, and the 1.0.0
/// installer shipped none either, so on any machine without a system Java the engine
/// failed with <c>java-not-found</c> and the product quietly fell back to its own
/// basic checking. A developer box with a JDK on PATH cannot reproduce that, which is
/// why it went out.
///
/// The resolver already reserves a slot for a bundled runtime, ahead of JAVA_HOME and
/// PATH. These tests pin the exact path the packaging step has to fill, so a rename on
/// either side fails here instead of in the field.
/// </remarks>
[TestClass]
public sealed class BundledJavaRuntimeContractTests
{
    /// <summary>
    /// The path build/build-release.ps1 writes the jlink image to, relative to the
    /// installed application directory.
    /// </summary>
    private const string BundledRelativePath = @"Runtime\Java\bin";

    [TestMethod]
    public void BundledRuntimeIsPreferredOverJavaHomeAndPath()
    {
        var candidates = WriteLiteJavaResolver
            .EnumerateCandidates(new WriteLiteLanguageOptions { PreferJavaw = true })
            .ToList();

        Assert.IsNotEmpty(candidates);

        var bundled = candidates.FindIndex(
            c => c.Contains(BundledRelativePath, StringComparison.OrdinalIgnoreCase));
        Assert.IsGreaterThanOrEqualTo(
            0,
            bundled,
            "the resolver must look inside the application directory for a bundled runtime; "
            + "candidates were: " + string.Join(" | ", candidates));

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var home = candidates.FindIndex(
                c => c.StartsWith(javaHome.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase));
            if (home >= 0)
            {
                Assert.IsLessThan(
                    home,
                    bundled,
                    "a runtime shipped with the application must win over JAVA_HOME");
            }
        }
    }

    /// <summary>
    /// An explicitly configured path still outranks the bundled runtime, so a user who
    /// points WriteLite at their own JVM keeps that.
    /// </summary>
    [TestMethod]
    public void ConfiguredPathStillWins()
    {
        var options = new WriteLiteLanguageOptions
        {
            ConfiguredJavaPath = @"C:\Custom\jdk\bin\java.exe",
            PreferJavaw = true,
        };

        var first = WriteLiteJavaResolver.EnumerateCandidates(options).First();

        Assert.AreEqual(@"C:\Custom\jdk\bin\java.exe", first);
    }

    /// <summary>
    /// Both javaw.exe and java.exe are offered for the bundled slot: jlink images are
    /// built with both, and javaw keeps a console window from flashing at startup.
    /// </summary>
    [TestMethod]
    public void BundledSlotOffersBothLaunchers()
    {
        var candidates = WriteLiteJavaResolver
            .EnumerateCandidates(new WriteLiteLanguageOptions())
            .Where(c => c.Contains(BundledRelativePath, StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToArray();

        CollectionAssert.AreEquivalent(new[] { "javaw.exe", "java.exe" }, candidates);
    }
}
