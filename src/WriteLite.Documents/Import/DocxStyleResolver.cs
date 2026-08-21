using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WriteLite.Documents.Model;

namespace WriteLite.Documents.Import;

/// <summary>
/// Resolved formatting for one paragraph, after the style chain has been walked.
/// </summary>
public readonly record struct ResolvedParagraphStyle(
    ParagraphFormatting Format,
    RunFormatting RunFormat,
    int? OutlineLevel,
    string? StyleName,
    ListInfo? List);

/// <summary>
/// Walks WordprocessingML's inheritance chain so imported text keeps its look.
/// </summary>
/// <remarks>
/// The order is fixed by the specification: document defaults, then the style's
/// own <c>basedOn</c> ancestry from the root down, then the style itself, then
/// direct formatting on the element. Each layer only overrides what it sets, so
/// a run that specifies nothing but bold still inherits the family and size the
/// style gave it.
///
/// Results are memoised per style id: a long document asks about the same handful
/// of styles thousands of times, and the ancestry walk is not free.
/// </remarks>
public sealed class DocxStyleResolver
{
    private const double DefaultFontSizePt = 11;

    private readonly Dictionary<string, Style> _styles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResolvedParagraphStyle> _paragraphCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ParagraphFormatting _defaultParagraph;
    private readonly RunFormatting _defaultRun;
    private readonly string? _defaultStyleId;

    public DocxStyleResolver(MainDocumentPart? part)
    {
        var definitions = part?.StyleDefinitionsPart?.Styles;

        if (definitions is not null)
        {
            foreach (var style in definitions.Elements<Style>())
            {
                var id = style.StyleId?.Value;
                if (id is not null)
                {
                    _styles[id] = style;
                }

                if (style.Type?.Value == StyleValues.Paragraph && style.Default?.Value == true)
                {
                    _defaultStyleId = id;
                }
            }
        }

        var docDefaults = definitions?.DocDefaults;
        _defaultRun = ReadRunFormatting(
            docDefaults?.RunPropertiesDefault?.RunPropertiesBaseStyle,
            new RunFormatting(FontSizePt: DefaultFontSizePt));
        _defaultParagraph = ReadParagraphFormatting(
            docDefaults?.ParagraphPropertiesDefault?.ParagraphPropertiesBaseStyle,
            ParagraphFormatting.Default);
    }

    public ResolvedParagraphStyle ResolveParagraph(string? styleId, ParagraphProperties? direct)
    {
        var baseline = ResolveStyleChain(styleId ?? _defaultStyleId);

        var format = ReadParagraphFormatting(direct, baseline.Format);
        var runFormat = ReadRunFormatting(direct?.ParagraphMarkRunProperties, baseline.RunFormat);

        return baseline with { Format = format, RunFormat = runFormat };
    }

    public RunFormatting ResolveRun(RunProperties? properties, RunFormatting inherited)
    {
        var styleId = properties?.RunStyle?.Val?.Value;
        var baseline = styleId is null ? inherited : ReadRunStyleChain(styleId, inherited);
        return ReadRunFormatting(properties, baseline);
    }

    // ── Style chains ─────────────────────────────────────────────────────────

    private ResolvedParagraphStyle ResolveStyleChain(string? styleId)
    {
        if (styleId is null)
        {
            return new ResolvedParagraphStyle(_defaultParagraph, _defaultRun, null, null, null);
        }

        if (_paragraphCache.TryGetValue(styleId, out var cached))
        {
            return cached;
        }

        var chain = BuildChain(styleId);

        var format = _defaultParagraph;
        var runFormat = _defaultRun;
        ListInfo? list = null;

        foreach (var style in chain)
        {
            format = ReadParagraphFormatting(style.StyleParagraphProperties, format);
            runFormat = ReadRunFormatting(style.StyleRunProperties, runFormat);

            if (style.StyleParagraphProperties?.NumberingProperties is not null)
            {
                var level = style.StyleParagraphProperties.NumberingProperties.NumberingLevelReference?.Val?.Value ?? 0;
                list = ListInfo.Bullet(level);
            }
        }

        var last = chain.Count > 0 ? chain[^1] : null;
        var name = last?.StyleName?.Val?.Value ?? styleId;
        var outline = ResolveOutlineLevel(chain, name, styleId);

        var resolved = new ResolvedParagraphStyle(format, runFormat, outline, name, list);
        _paragraphCache[styleId] = resolved;
        return resolved;
    }

    private RunFormatting ReadRunStyleChain(string styleId, RunFormatting inherited)
    {
        var format = inherited;
        foreach (var style in BuildChain(styleId))
        {
            format = ReadRunFormatting(style.StyleRunProperties, format);
        }

        return format;
    }

    /// <summary>The style's ancestry, root first, so later entries override earlier ones.</summary>
    private List<Style> BuildChain(string styleId)
    {
        var chain = new List<Style>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = styleId;

        // A malformed file can point basedOn at a cycle; the seen-set is what keeps
        // this from becoming an infinite loop on an otherwise readable document.
        while (current is not null && seen.Add(current) && _styles.TryGetValue(current, out var style))
        {
            chain.Insert(0, style);
            current = style.BasedOn?.Val?.Value;
        }

        return chain;
    }

    private static int? ResolveOutlineLevel(List<Style> chain, string name, string styleId)
    {
        for (var index = chain.Count - 1; index >= 0; index--)
        {
            var outline = chain[index].StyleParagraphProperties?.OutlineLevel?.Val?.Value;
            if (outline is >= 0 and <= 5)
            {
                return outline.Value + 1;
            }
        }

        // Documents written by non-Word tools frequently carry the heading only in
        // the style name, with no outlineLvl anywhere in the chain.
        foreach (var candidate in new[] { name, styleId })
        {
            var level = HeadingLevelFromName(candidate);
            if (level is not null)
            {
                return level;
            }
        }

        return null;
    }

    private static int? HeadingLevelFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        foreach (var prefix in new[] { "heading", "Заголовок", "Titre", "Überschrift" })
        {
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suffix = trimmed[prefix.Length..].Trim(' ', '-', '_');
            if (int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level)
                && level is >= 1 and <= 6)
            {
                return level;
            }
        }

        if (trimmed.Equals("Title", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Название", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return null;
    }

    // ── Property readers ─────────────────────────────────────────────────────

    private static ParagraphFormatting ReadParagraphFormatting(OpenXmlElement? properties, ParagraphFormatting inherited)
    {
        if (properties is null)
        {
            return inherited;
        }

        var alignment = inherited.Alignment;
        if (properties.GetFirstChild<Justification>()?.Val?.Value is { } justification)
        {
            alignment = justification switch
            {
                var value when value == JustificationValues.Center => TextAlign.Center,
                var value when value == JustificationValues.Right => TextAlign.Right,
                var value when value == JustificationValues.Both => TextAlign.Justify,
                var value when value == JustificationValues.Distribute => TextAlign.Justify,
                _ => TextAlign.Left
            };
        }

        var lineSpacing = inherited.LineSpacing;
        var spaceBefore = inherited.SpaceBeforePt;
        var spaceAfter = inherited.SpaceAfterPt;

        if (properties.GetFirstChild<SpacingBetweenLines>() is { } spacing)
        {
            if (spacing.Before?.Value is { } before && int.TryParse(before, out var beforeTwips))
            {
                spaceBefore = beforeTwips / 20.0;
            }

            if (spacing.After?.Value is { } after && int.TryParse(after, out var afterTwips))
            {
                spaceAfter = afterTwips / 20.0;
            }

            if (spacing.Line?.Value is { } line && int.TryParse(line, out var lineValue))
            {
                var rule = spacing.LineRule?.Value;
                lineSpacing = rule switch
                {
                    // "auto" stores 240ths of a line; the other two store twips, which
                    // only become a multiplier once a font size is known — approximated
                    // here against a 12 pt line, which is what Word's own UI does.
                    var value when value == LineSpacingRuleValues.Auto => lineValue / 240.0,
                    var value when value == LineSpacingRuleValues.Exact
                                   || value == LineSpacingRuleValues.AtLeast => lineValue / 20.0 / 12.0,
                    _ => lineValue / 240.0
                };
            }
        }

        var indentLeft = inherited.IndentLeftPt;
        var indentRight = inherited.IndentRightPt;
        var firstLine = inherited.FirstLineIndentPt;

        if (properties.GetFirstChild<Indentation>() is { } indentation)
        {
            if (TryTwips(indentation.Left?.Value, out var left))
            {
                indentLeft = left;
            }
            else if (TryTwips(indentation.Start?.Value, out var start))
            {
                indentLeft = start;
            }

            if (TryTwips(indentation.Right?.Value, out var right))
            {
                indentRight = right;
            }
            else if (TryTwips(indentation.End?.Value, out var end))
            {
                indentRight = end;
            }

            if (TryTwips(indentation.FirstLine?.Value, out var first))
            {
                firstLine = first;
            }
            else if (TryTwips(indentation.Hanging?.Value, out var hanging))
            {
                firstLine = -hanging;
            }
        }

        return new ParagraphFormatting(
            alignment,
            Math.Clamp(lineSpacing <= 0 ? 1 : lineSpacing, 0.5, 5),
            Math.Max(0, spaceBefore),
            Math.Max(0, spaceAfter),
            indentLeft,
            indentRight,
            firstLine);
    }

    private static bool TryTwips(string? raw, out double points)
    {
        points = 0;
        if (raw is null || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var twips))
        {
            return false;
        }

        points = twips / 20.0;
        return true;
    }

    private static RunFormatting ReadRunFormatting(OpenXmlElement? properties, RunFormatting inherited)
    {
        if (properties is null)
        {
            return inherited;
        }

        var bold = ReadOnOff(properties.GetFirstChild<Bold>(), inherited.Bold);
        var italic = ReadOnOff(properties.GetFirstChild<Italic>(), inherited.Italic);
        var strike = ReadOnOff(properties.GetFirstChild<Strike>(), inherited.Strikethrough);

        var underline = inherited.Underline;
        if (properties.GetFirstChild<Underline>()?.Val?.Value is { } underlineValue)
        {
            underline = underlineValue != UnderlineValues.None;
        }

        var family = inherited.FontFamily;
        if (properties.GetFirstChild<RunFonts>() is { } fonts)
        {
            family = FirstNonEmpty(fonts.Ascii?.Value, fonts.HighAnsi?.Value, fonts.ComplexScript?.Value, fonts.EastAsia?.Value)
                     ?? family;
        }

        var size = inherited.FontSizePt;
        if (properties.GetFirstChild<FontSize>()?.Val?.Value is { } halfPoints
            && double.TryParse(halfPoints, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            size = parsed / 2.0;
        }

        var color = inherited.ColorHex;
        if (properties.GetFirstChild<Color>()?.Val?.Value is { } colorValue
            && !colorValue.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            color = NormaliseHex(colorValue) ?? color;
        }

        var highlight = inherited.HighlightHex;
        if (properties.GetFirstChild<Shading>()?.Fill?.Value is { } fill
            && !fill.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            highlight = NormaliseHex(fill) ?? highlight;
        }

        return new RunFormatting(bold, italic, underline, strike, family, size, color, highlight);
    }

    /// <summary>
    /// Reads a WordprocessingML on/off property.
    /// </summary>
    /// <remarks>
    /// The element's absence means "inherit", but its presence without a
    /// <c>w:val</c> attribute means "on" — and bare <c>&lt;w:b/&gt;</c> is how Word
    /// and every other writer actually emit bold. Reading only the attribute loses
    /// every bold and italic run in the document.
    /// </remarks>
    private static bool ReadOnOff(OnOffType? element, bool inherited) =>
        element is null ? inherited : element.Val is null || element.Val.Value;

    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));

    internal static string? NormaliseHex(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim().TrimStart('#');
        if (value.Length == 8)
        {
            value = value[2..];
        }

        if (value.Length != 6 || !value.All(Uri.IsHexDigit))
        {
            return null;
        }

        return "#" + value.ToUpperInvariant();
    }
}

/// <summary>
/// Maps a numbering id and level to the kind of list a reader actually sees.
/// </summary>
/// <remarks>
/// WordprocessingML routes every list through an indirection — the paragraph names
/// a <c>numId</c>, which names an abstract numbering definition, which finally
/// carries the format per level. Following it is the difference between importing
/// a numbered list as numbered and importing every list as bullets.
/// </remarks>
public sealed class DocxNumberingResolver
{
    private readonly Dictionary<int, int> _numberingToAbstract = [];
    private readonly Dictionary<(int Abstract, int Level), ListKind> _levels = [];

    public DocxNumberingResolver(MainDocumentPart? part)
    {
        var numbering = part?.NumberingDefinitionsPart?.Numbering;
        if (numbering is null)
        {
            return;
        }

        foreach (var instance in numbering.Elements<NumberingInstance>())
        {
            if (instance.NumberID?.Value is { } id && instance.AbstractNumId?.Val?.Value is { } abstractId)
            {
                _numberingToAbstract[id] = abstractId;
            }
        }

        foreach (var definition in numbering.Elements<AbstractNum>())
        {
            var abstractId = definition.AbstractNumberId?.Value;
            if (abstractId is null)
            {
                continue;
            }

            foreach (var level in definition.Elements<Level>())
            {
                var index = level.LevelIndex?.Value ?? 0;
                var format = level.NumberingFormat?.Val?.Value;
                var kind = format is not null && (format == NumberFormatValues.Bullet || format == NumberFormatValues.None)
                    ? ListKind.Bullet
                    : ListKind.Numbered;
                _levels[(abstractId.Value, index)] = kind;
            }
        }
    }

    public ListInfo? Resolve(int? numberingId, int level)
    {
        if (numberingId is null)
        {
            return null;
        }

        // numId 0 is the documented way to say "this paragraph is not in a list".
        if (numberingId.Value == 0)
        {
            return null;
        }

        var clampedLevel = Math.Clamp(level, 0, 8);

        if (_numberingToAbstract.TryGetValue(numberingId.Value, out var abstractId)
            && _levels.TryGetValue((abstractId, clampedLevel), out var kind))
        {
            return new ListInfo(kind, clampedLevel);
        }

        return ListInfo.Bullet(clampedLevel);
    }
}
