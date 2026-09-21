using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using WriteLite.Resources;

namespace WriteLite.Tests.Resources;

/// <summary>
/// Structural checks on Strings.resx.
/// </summary>
/// <remarks>
/// Every UI string moved into the resx in «refactor: consolidation of static data».
/// The move is only checked by the compiler for the <em>name</em> of each key — the
/// generated <see cref="Strings"/> class has a property per resx entry, so a renamed
/// key fails the build. Nothing checked the <em>values</em>, and a value can be wrong
/// in ways that reach the user silently:
///
/// <list type="bullet">
/// <item>a value that lost its <c>{0}</c> makes <c>string.Format</c> drop the argument,
/// which is how the Diagnostics page came to print «Поле» with no field in it;</item>
/// <item>a value that gained a <c>{0}</c> nobody formats prints a literal brace.</item>
/// </list>
///
/// These tests read the compiled resources the application actually ships, so they
/// also catch a resx that failed to embed.
/// </remarks>
[TestClass]
public sealed class StringResourceIntegrityTests
{
    private static readonly Regex Hole = new(@"\{(\d+)(?::[^{}]*)?\}", RegexOptions.Compiled);

    /// <summary>Keys the Diagnostics page formats with exactly one argument.</summary>
    /// <remarks>
    /// Listed explicitly rather than discovered: the point is to pin the rows that
    /// regressed, so adding a row to the page must be a deliberate edit here too.
    /// </remarks>
    private static readonly string[] SingleArgumentKeys =
    [
        "Diag_AppVersion", "Diag_AppBuild",
        "Diag_FieldDetected", "Diag_FieldProcess", "Diag_FieldControl",
        "Diag_FieldRead", "Diag_FieldWrite", "Diag_FieldBounds", "Diag_FieldIssueCount",
        "Diag_EngineStatus", "Diag_EngineLastSuccess", "Diag_EngineDuration",
        "Diag_EngineIssueCount", "Diag_EngineRestarts", "Diag_EngineExtendedChecking",
        "Diag_SystemDpi", "Diag_SystemWorkArea", "Diag_SystemAppMemory",
        "Diag_SystemChildMemory", "Diag_SystemAnalysisQueues",
        "Corr_NoConfidentFixForWord", "Corr_InsertMark",
    ];

    [TestMethod]
    public void EveryFormattedDiagnosticsRowKeepsItsValueHole()
    {
        var manager = new ResourceManager("WriteLite.Resources.Strings", typeof(Strings).Assembly);

        var broken = new List<string>();
        foreach (var key in SingleArgumentKeys)
        {
            var value = manager.GetString(key);
            Assert.IsNotNull(value, $"{key} is missing from the compiled resources");

            var holes = Hole.Matches(value).Select(m => int.Parse(m.Groups[1].Value)).ToArray();
            if (holes.Length == 0 || holes.Max() != 0)
            {
                broken.Add($"{key} = \"{value}\"");
            }
        }

        Assert.IsEmpty(
            broken,
            "these keys are formatted with one argument, so they must contain {0}; "
            + "without it string.Format drops the value: " + string.Join("; ", broken));
    }

    /// <summary>
    /// The generated accessor falls back to the key name when a string is missing, so
    /// a resx that did not embed shows key names instead of throwing. Catch that here.
    /// </summary>
    [TestMethod]
    public void GeneratedAccessorsResolveToRealText_NotKeyNames()
    {
        var properties = typeof(Strings)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string) && p.CanRead && p.GetMethod!.GetParameters().Length == 0)
            .ToArray();

        Assert.IsGreaterThan(500, properties.Length, "the resx should carry the whole UI vocabulary");

        var unresolved = properties
            .Where(p => (string?)p.GetValue(null) == p.Name)
            .Select(p => p.Name)
            .ToArray();

        Assert.IsEmpty(unresolved, "unresolved string keys: " + string.Join(", ", unresolved));
    }

    /// <summary>
    /// The card's primary action is a verb phrase. «запятую» on a button is not one,
    /// and that is exactly what the refactor shipped.
    /// </summary>
    [TestMethod]
    public void PunctuationInsertionLabelsAreVerbPhrases()
    {
        string[] keys =
        [
            "Corr_AddPeriod", "Corr_AddComma", "Corr_AddExclamation",
            "Corr_AddQuestion", "Corr_AddEllipsis", "Corr_AddDash",
        ];

        var manager = new ResourceManager("WriteLite.Resources.Strings", typeof(Strings).Assembly);
        foreach (var key in keys)
        {
            var value = manager.GetString(key);
            Assert.IsNotNull(value, key);
            Assert.StartsWith(
                "Добавить ",
                value,
                $"{key} must name the action, not just its object (was \"{value}\")");
        }
    }
}
