using System.Text.RegularExpressions;
using WriteLite.Services;
using WriteLite.Services.Diagnostics;
using WriteLite.Services.Settings;

namespace WriteLite.Tests;

[TestClass]
public sealed class PrivacyScanTests
{
    private static readonly string[] ForbiddenInUserSurfaces =
    [
        "LanguageTool",
        "languagetool",
        "languagetool-server",
        "org.languagetool"
    ];

    [TestMethod]
    public void DiagnosticsReport_HasNoThirdPartyBrandOrPayloads()
    {
        var service = new WriteLiteDiagnosticsService(
            () => null,
            () => null,
            () => new WriteLiteAppSettings { ExtendedChecking = true });
        var report = service.BuildCopyableReport(service.Capture());

        foreach (var token in ForbiddenInUserSurfaces)
        {
            Assert.DoesNotContain(token, report, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("user-dictionary", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"matches\"", report, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void CompatibilityLog_StateEvents_DoNotContainUserTextPatterns()
    {
        CompatibilityLogger.State("privacy-scan-probe");
        var dir = Path.Combine(AppContext.BaseDirectory, "logs");
        var path = Path.Combine(dir, "compatibility.log");
        if (!File.Exists(path))
        {
            Assert.Inconclusive("No compatibility.log yet.");
            return;
        }

        string content;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            content = reader.ReadToEnd();
        }

        var lines = content.Split('\n').TakeLast(200).ToList();
        foreach (var line in lines)
        {
            // Free-form user text should never appear as raw payload fields.
            Assert.IsFalse(
                Regex.IsMatch(line, @"\buserText=", RegexOptions.IgnoreCase),
                "Log must not contain userText=");
            Assert.IsFalse(
                Regex.IsMatch(line, @"\bdocumentText=", RegexOptions.IgnoreCase),
                "Log must not contain documentText=");
            Assert.DoesNotContain("LanguageTool", line, StringComparison.OrdinalIgnoreCase);
            Assert.IsFalse(
                Regex.IsMatch(line, @"\breplacement=", RegexOptions.IgnoreCase),
                "Log must not contain replacement=");
            Assert.IsFalse(
                Regex.IsMatch(line, @"[A-Za-z]:\\Users\\", RegexOptions.IgnoreCase),
                "Log must not contain user profile paths");
        }
    }

    [TestMethod]
    public void Sanitize_DoesNotLeakUserProfilePaths()
    {
        var s = CompatibilityLogger.Sanitize(@"oops C:\Users\USER\Documents\secret.txt LanguageTool");
        Assert.DoesNotContain(@"C:\Users", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LanguageTool", s, StringComparison.OrdinalIgnoreCase);
    }
}
