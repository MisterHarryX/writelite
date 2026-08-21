using System.Globalization;
using System.Xml.Linq;

namespace WriteLite.Documents.OpenDocument;

/// <summary>
/// The OpenDocument namespaces and the unit parsing every ODF attribute needs.
/// </summary>
/// <remarks>
/// Shared by the importer and the exporter so a name is declared once. ODF spreads
/// one logical property across several namespaces — a paragraph's alignment is
/// <c>fo:text-align</c> while its line height may be <c>fo:line-height</c> or
/// <c>style:line-height-at-least</c> — so guessing the namespace is a real source
/// of silently-dropped formatting.
/// </remarks>
public static class Odf
{
    public static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    public static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    public static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    public static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    public static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    public static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    public static readonly XNamespace Svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
    public static readonly XNamespace Meta = "urn:oasis:names:tc:opendocument:xmlns:meta:1.0";
    public static readonly XNamespace Xlink = "http://www.w3.org/1999/xlink";
    public static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    public const string TextMimeType = "application/vnd.oasis.opendocument.text";

    /// <summary>
    /// Converts an ODF length to points.
    /// </summary>
    /// <remarks>
    /// ODF permits any CSS absolute unit and real files use all of them: LibreOffice
    /// writes centimetres, Word's ODF filter writes inches and web-origin documents
    /// write points or pixels.
    /// </remarks>
    public static double? LengthToPoints(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        var digits = text.AsSpan();
        var unitStart = digits.Length;
        for (var index = 0; index < digits.Length; index++)
        {
            var c = digits[index];
            if (!char.IsDigit(c) && c != '.' && c != '-' && c != '+' && c != ',')
            {
                unitStart = index;
                break;
            }
        }

        var numberText = text[..unitStart].Replace(',', '.');
        if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        return text[unitStart..].Trim().ToLowerInvariant() switch
        {
            "pt" or "" => number,
            "cm" => number * 72.0 / 2.54,
            "mm" => number * 72.0 / 25.4,
            "in" => number * 72.0,
            "pc" => number * 12.0,
            "px" => number * 72.0 / 96.0,
            _ => null
        };
    }

    public static string PointsToCentimetres(double points) =>
        (points * 2.54 / 72.0).ToString("0.###", CultureInfo.InvariantCulture) + "cm";

    public static string PointsToPoints(double points) =>
        points.ToString("0.##", CultureInfo.InvariantCulture) + "pt";

    /// <summary>Reads <c>fo:line-height</c>, which is a percentage for a multiplier.</summary>
    public static double? LineHeightToMultiplier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.EndsWith('%')
            && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return percent / 100.0;
        }

        // An absolute line height only becomes a multiplier against a font size; a
        // 12 pt line is the assumption everywhere else in the model too.
        var points = LengthToPoints(text);
        return points is null ? null : points.Value / 12.0;
    }

    public static string? NormaliseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        text = text.TrimStart('#');
        if (text.Length != 6 || !text.All(Uri.IsHexDigit))
        {
            return null;
        }

        return "#" + text.ToUpperInvariant();
    }
}
