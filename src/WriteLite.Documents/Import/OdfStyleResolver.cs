using System.Xml.Linq;
using WriteLite.Documents.Model;
using WriteLite.Documents.OpenDocument;

namespace WriteLite.Documents.Import;

public readonly record struct ResolvedOdfStyle(
    ParagraphFormatting Format,
    RunFormatting RunFormat,
    int? OutlineLevel,
    bool PageBreakBefore);

/// <summary>
/// Resolves ODF style names against both style tables, following parent chains.
/// </summary>
/// <remarks>
/// Automatic styles from <c>content.xml</c> take precedence over the named styles in
/// <c>styles.xml</c>, because that is where an editor records the formatting a user
/// applied by hand. Both tables can name a parent, and the chains cross between the
/// two files, so lookup is over the union rather than over either one.
/// </remarks>
public sealed class OdfStyleResolver
{
    private readonly Dictionary<string, XElement> _styles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> _listStyles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResolvedOdfStyle> _paragraphCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RunFormatting> _runCache = new(StringComparer.Ordinal);

    public OdfStyleResolver(XDocument? styles, XDocument? content)
    {
        // Named styles first, then automatic styles: a name collision means the
        // automatic style is the one the document actually applied.
        Collect(styles?.Root?.Element(Odf.Office + "styles"));
        Collect(styles?.Root?.Element(Odf.Office + "automatic-styles"));
        Collect(content?.Root?.Element(Odf.Office + "automatic-styles"));
    }

    private void Collect(XElement? container)
    {
        if (container is null)
        {
            return;
        }

        foreach (var style in container.Elements(Odf.Style + "style"))
        {
            if (style.Attribute(Odf.Style + "name")?.Value is { Length: > 0 } name)
            {
                _styles[name] = style;
            }
        }

        foreach (var listStyle in container.Elements(Odf.Text + "list-style"))
        {
            if (listStyle.Attribute(Odf.Style + "name")?.Value is { Length: > 0 } name)
            {
                _listStyles[name] = listStyle;
            }
        }
    }

    public ResolvedOdfStyle ResolveParagraph(string? styleName)
    {
        if (styleName is null)
        {
            return new ResolvedOdfStyle(ParagraphFormatting.Default, RunFormatting.Default, null, false);
        }

        if (_paragraphCache.TryGetValue(styleName, out var cached))
        {
            return cached;
        }

        var format = ParagraphFormatting.Default;
        var runFormat = RunFormatting.Default;
        int? outline = null;
        var pageBreak = false;

        foreach (var style in BuildChain(styleName))
        {
            var paragraphProperties = style.Element(Odf.Style + "paragraph-properties");
            format = ReadParagraphProperties(paragraphProperties, format);
            runFormat = ReadTextProperties(style.Element(Odf.Style + "text-properties"), runFormat);

            // ODF has no standalone page-break element: a break is a paragraph whose
            // style says fo:break-before, so it can only be recovered from here.
            if (paragraphProperties?.Attribute(Odf.Fo + "break-before")?.Value is { } breakBefore)
            {
                pageBreak = breakBefore.Equals("page", StringComparison.OrdinalIgnoreCase);
            }

            if (style.Attribute(Odf.Style + "default-outline-level")?.Value is { Length: > 0 } raw
                && int.TryParse(raw, out var level)
                && level is >= 1 and <= 6)
            {
                outline = level;
            }
        }

        var resolved = new ResolvedOdfStyle(format, runFormat, outline, pageBreak);
        _paragraphCache[styleName] = resolved;
        return resolved;
    }

    public RunFormatting ResolveRun(string? styleName, RunFormatting inherited)
    {
        if (styleName is null)
        {
            return inherited;
        }

        if (!_runCache.TryGetValue(styleName, out var own))
        {
            own = RunFormatting.Default;
            foreach (var style in BuildChain(styleName))
            {
                own = ReadTextProperties(style.Element(Odf.Style + "text-properties"), own);
            }

            _runCache[styleName] = own;
        }

        return own.InheritFrom(inherited);
    }

    /// <summary>Whether a list at this level is numbered or bulleted.</summary>
    public ListKind? ResolveListKind(string? listStyleName, int level)
    {
        if (listStyleName is null || !_listStyles.TryGetValue(listStyleName, out var style))
        {
            return null;
        }

        var wanted = Math.Clamp(level + 1, 1, 10);
        foreach (var definition in style.Elements())
        {
            var definedLevel = (int?)definition.Attribute(Odf.Text + "level") ?? 1;
            if (definedLevel != wanted)
            {
                continue;
            }

            if (definition.Name == Odf.Text + "list-level-style-number")
            {
                return ListKind.Numbered;
            }

            if (definition.Name == Odf.Text + "list-level-style-bullet"
                || definition.Name == Odf.Text + "list-level-style-image")
            {
                return ListKind.Bullet;
            }
        }

        // A style that defines only its first level applies it to the rest.
        return style.Elements(Odf.Text + "list-level-style-number").Any()
            ? ListKind.Numbered
            : ListKind.Bullet;
    }

    /// <summary>The style's ancestry, root first.</summary>
    private List<XElement> BuildChain(string styleName)
    {
        var chain = new List<XElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = styleName;

        while (current is not null && seen.Add(current) && _styles.TryGetValue(current, out var style))
        {
            chain.Insert(0, style);
            current = style.Attribute(Odf.Style + "parent-style-name")?.Value;
        }

        return chain;
    }

    private static ParagraphFormatting ReadParagraphProperties(XElement? properties, ParagraphFormatting inherited)
    {
        if (properties is null)
        {
            return inherited;
        }

        var alignment = properties.Attribute(Odf.Fo + "text-align")?.Value switch
        {
            "center" => TextAlign.Center,
            // "start"/"end" are writing-direction relative; every document WriteLite
            // handles is left-to-right, so they map to left and right.
            "end" or "right" => TextAlign.Right,
            "justify" => TextAlign.Justify,
            "start" or "left" => TextAlign.Left,
            _ => inherited.Alignment
        };

        return new ParagraphFormatting(
            alignment,
            Math.Clamp(
                Odf.LineHeightToMultiplier(properties.Attribute(Odf.Fo + "line-height")?.Value) ?? inherited.LineSpacing,
                0.5,
                5),
            Odf.LengthToPoints(properties.Attribute(Odf.Fo + "margin-top")?.Value) ?? inherited.SpaceBeforePt,
            Odf.LengthToPoints(properties.Attribute(Odf.Fo + "margin-bottom")?.Value) ?? inherited.SpaceAfterPt,
            Odf.LengthToPoints(properties.Attribute(Odf.Fo + "margin-left")?.Value) ?? inherited.IndentLeftPt,
            Odf.LengthToPoints(properties.Attribute(Odf.Fo + "margin-right")?.Value) ?? inherited.IndentRightPt,
            Odf.LengthToPoints(properties.Attribute(Odf.Fo + "text-indent")?.Value) ?? inherited.FirstLineIndentPt);
    }

    private static RunFormatting ReadTextProperties(XElement? properties, RunFormatting inherited)
    {
        if (properties is null)
        {
            return inherited;
        }

        var bold = properties.Attribute(Odf.Fo + "font-weight")?.Value switch
        {
            null => inherited.Bold,
            "normal" => false,
            var value => value is "bold" || (int.TryParse(value, out var weight) && weight >= 600)
        };

        var italic = properties.Attribute(Odf.Fo + "font-style")?.Value switch
        {
            null => inherited.Italic,
            "normal" => false,
            _ => true
        };

        var underline = properties.Attribute(Odf.Style + "text-underline-style")?.Value switch
        {
            null => inherited.Underline,
            "none" => false,
            _ => true
        };

        var strike = properties.Attribute(Odf.Style + "text-line-through-style")?.Value switch
        {
            null => inherited.Strikethrough,
            "none" => false,
            _ => true
        };

        var family = properties.Attribute(Odf.Fo + "font-family")?.Value
                     ?? properties.Attribute(Odf.Style + "font-name")?.Value
                     ?? inherited.FontFamily;

        return new RunFormatting(
            bold,
            italic,
            underline,
            strike,
            family?.Trim('\''),
            Odf.LengthToPoints(properties.Attribute(Odf.Fo + "font-size")?.Value) ?? inherited.FontSizePt,
            Odf.NormaliseColor(properties.Attribute(Odf.Fo + "color")?.Value) ?? inherited.ColorHex,
            Odf.NormaliseColor(properties.Attribute(Odf.Fo + "background-color")?.Value) ?? inherited.HighlightHex);
    }
}
