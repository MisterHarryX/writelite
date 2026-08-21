using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace FieldMatrix;

/// <summary>
/// Shows how a provider's character-unit movement lines up with the text it reports.
/// </summary>
/// <remarks>
/// Exists because "the range I built does not contain the word I asked for" is the one
/// failure a write strategy must never guess its way past, and the only way to fix it is to
/// see what the provider actually does.
/// </remarks>
internal static class RangeDiagnostic
{
    public static void Run(AutomationElement element, string marker)
    {
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject)
            || patternObject is not TextPattern textPattern)
        {
            Console.WriteLine("  no TextPattern");
            return;
        }

        var document = textPattern.DocumentRange.GetText(-1) ?? string.Empty;
        Console.WriteLine($"  DocumentRange.GetText length={document.Length}");
        Console.WriteLine($"    «{Escape(document)}»");

        var index = document.IndexOf(marker, StringComparison.Ordinal);
        Console.WriteLine($"  marker index in document text = {index}");
        if (index < 0) return;

        for (var offset = Math.Max(0, index - 3); offset <= index + 3; offset++)
        {
            var built = textPattern.DocumentRange.Clone();
            built.MoveEndpointByRange(TextPatternRangeEndpoint.End, built, TextPatternRangeEndpoint.Start);
            var moved = built.Move(TextUnit.Character, offset);
            var expanded = built.MoveEndpointByUnit(
                TextPatternRangeEndpoint.End, TextUnit.Character, marker.Length);
            var text = built.GetText(-1) ?? string.Empty;
            var hit = string.Equals(text, marker, StringComparison.Ordinal) ? "  <<< MATCH" : string.Empty;
            Console.WriteLine(
                $"    offset={offset,4} moved={moved,4} expanded={expanded,3} text=«{Escape(text)}»{hit}");
        }

        // Does the degenerate start range even sit where Move claims?
        var probe = textPattern.DocumentRange.Clone();
        probe.MoveEndpointByRange(TextPatternRangeEndpoint.End, probe, TextPatternRangeEndpoint.Start);
        probe.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, index);
        Console.WriteLine($"  prefix by MoveEndpointByUnit: «{Escape(probe.GetText(-1) ?? string.Empty)}»");
    }

    private static string Escape(string value)
        => value.Replace("\r", "\\r").Replace("\n", "\\n");
}
