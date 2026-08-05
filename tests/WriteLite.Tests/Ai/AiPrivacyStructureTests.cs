using System.Text.RegularExpressions;
using WriteLite.Services.Settings;

namespace WriteLite.Tests.Ai;

[TestClass]
public sealed class AiPrivacyStructureTests
{
    private static readonly string[] RetiredCloudSymbols =
    [
        "VercelCloudAiTextProvider",
        "OpenAiCompatibleAiTextProvider",
        "CompositeAiTextProvider",
        "CloudTokenStore",
        "AiSecretsStore",
        "CloudAiEnabled",
        "CloudEndpoint",
        "WRITELITE_CLOUD_TOKEN",
        "WRITELITE_AI_API_KEY",
        "WRITELITE_AI_ENDPOINT",
        "WRITELITE_AI_MODEL",
        "OPENROUTER_API_KEY",
        "openrouter.ai",
        ".vercel.app"
    ];

    [TestMethod]
    public void CloudProjectAndProviders_AreAbsent()
    {
        var root = FindRepositoryRoot();
        var cloudDir = Path.Combine(root, "WriteLite.Cloud");
        var remainingProjectFiles = new[] { "app", "lib", "tests" }
            .Select(area => Path.Combine(cloudDir, area))
            .Where(Directory.Exists)
            .SelectMany(area => Directory.EnumerateFiles(area, "*", SearchOption.AllDirectories))
            .Concat(Directory.Exists(cloudDir)
                ? Directory.EnumerateFiles(cloudDir, "*", SearchOption.TopDirectoryOnly)
                : [])
            .ToArray();
        Assert.IsEmpty(remainingProjectFiles, "WriteLite.Cloud must not contain project/runtime files.");

        foreach (var fileName in new[]
                 {
                     "VercelCloudAiTextProvider.cs",
                     "OpenAiCompatibleAiTextProvider.cs",
                     "AiSecretsStore.cs",
                     "CloudTokenStore.cs"
                 })
        {
            Assert.IsFalse(File.Exists(Path.Combine(root, "src", "WriteLite.App", "Services", "Ai", fileName)));
        }
    }

    [TestMethod]
    public void DesktopRuntime_HasNoRetiredCloudSymbolsOrSecrets()
    {
        var root = FindRepositoryRoot();
        var files = Directory.EnumerateFiles(
                Path.Combine(root, "src", "WriteLite.App", "Services", "Ai"),
                "*.cs",
                SearchOption.AllDirectories)
            .Concat(
            [
                Path.Combine(root, "src", "WriteLite.App", "App.xaml.cs"),
                Path.Combine(root, "src", "WriteLite.App", "Services", "Settings", "WriteLiteAppSettings.cs"),
                Path.Combine(root, "src", "WriteLite.App", "Services", "Settings", "WriteLiteSettingsStore.cs"),
                Path.Combine(root, "src", "WriteLite.App", "Views", "Pages", "SettingsPage.xaml"),
                Path.Combine(root, "src", "WriteLite.App", "Views", "Pages", "SettingsPage.xaml.cs")
            ]);

        var source = string.Join("\n", files.Select(File.ReadAllText));
        foreach (var symbol in RetiredCloudSymbols)
        {
            Assert.DoesNotContain(symbol, source, StringComparison.OrdinalIgnoreCase);
        }

        var settingNames = typeof(WriteLiteAppSettings).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.IsFalse(settingNames.Any(n => n.StartsWith("Cloud", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void AiRuntime_SourceUrlsAreLoopbackOnly()
    {
        var root = FindRepositoryRoot();
        var files = Directory.EnumerateFiles(
                Path.Combine(root, "src", "WriteLite.App", "Services", "Ai"),
                "*.cs",
                SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(
                Path.Combine(root, "src", "WriteLite.AI.Local"),
                "*.cs",
                SearchOption.AllDirectories));

        var urls = files
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"https?://[^\s""'<>]+"))
            .Select(match => match.Value.TrimEnd('.', ',', ';', ')'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var value in urls)
        {
            Assert.IsTrue(Uri.TryCreate(value, UriKind.Absolute, out var uri), $"Invalid URL literal: {value}");
            Assert.AreEqual(Uri.UriSchemeHttp, uri!.Scheme, $"AI endpoint must use local HTTP: {value}");
            Assert.IsTrue(
                uri.Host is "127.0.0.1" or "localhost" or "::1",
                $"Non-loopback AI URL found: {value}");
        }

        Assert.IsTrue(urls.Any(value => value.StartsWith("http://127.0.0.1:8742", StringComparison.Ordinal)));
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "WriteLite.sln")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("WriteLite repository root was not found.");
    }
}
