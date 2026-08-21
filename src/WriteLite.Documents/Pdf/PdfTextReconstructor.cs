using System.Text;
using UglyToad.PdfPig.Content;
using WriteLite.Documents.Model;

namespace WriteLite.Documents.Pdf;

/// <summary>
/// Turns positioned words back into paragraphs.
/// </summary>
/// <remarks>
/// Every threshold here is relative — to the page's own median line height, its own
/// body type size, its own text column width — never absolute. A 9 pt legal document
/// and a 24 pt slide handout both have to segment correctly, and a fixed "12 points
/// means a new paragraph" rule gets one of them wrong.
/// </remarks>
public static class PdfTextReconstructor
{
    /// <summary>Bullet glyphs that mark a list item when they open a line.</summary>
    private static readonly char[] BulletGlyphs = ['•', '·', '‣', '▪', '▫', '◦', '–', '—', '-', '*'];

    public static IReadOnlyList<BlockElement> Reconstruct(IReadOnlyList<Word> words, double pageWidth, double pageHeight)
    {
        if (words.Count == 0)
        {
            return [];
        }

        var lines = BuildLines(words);
        if (lines.Count == 0)
        {
            return [];
        }

        var bodySize = Median(lines.Select(line => line.FontSize).ToArray());
        var blocks = new List<BlockElement>();

        foreach (var column in SplitIntoColumns(lines, pageWidth))
        {
            blocks.AddRange(BuildParagraphs(column, bodySize));
        }

        return blocks;
    }

    // ── Lines ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Groups words into visual lines by baseline.
    /// </summary>
    /// <remarks>
    /// Baseline rather than bounding-box top: a line containing a capital, a
    /// parenthesis and a lowercase "o" has three different tops but one baseline,
    /// and grouping by top splits it into three.
    /// </remarks>
    private static List<TextLine> BuildLines(IReadOnlyList<Word> words)
    {
        var ordered = words
            .Select(word => new
            {
                Word = word,
                Baseline = word.Letters.Count > 0
                    ? Median(word.Letters.Select(letter => letter.StartBaseLine.Y).ToArray())
                    : word.BoundingBox.Bottom,
                Size = word.Letters.Count > 0
                    ? Median(word.Letters.Select(letter => letter.PointSize).ToArray())
                    : word.BoundingBox.Height
            })
            .OrderByDescending(entry => entry.Baseline)
            .ThenBy(entry => entry.Word.BoundingBox.Left)
            .ToArray();

        var medianSize = Median(ordered.Select(entry => entry.Size).ToArray());
        var tolerance = Math.Max(1.0, medianSize * 0.35);

        var lines = new List<TextLine>();
        var current = new List<Word>();
        var currentBaseline = double.NaN;

        foreach (var entry in ordered)
        {
            if (current.Count == 0 || Math.Abs(entry.Baseline - currentBaseline) <= tolerance)
            {
                if (current.Count == 0)
                {
                    currentBaseline = entry.Baseline;
                }

                current.Add(entry.Word);
                continue;
            }

            lines.Add(TextLine.From(current, currentBaseline));
            current = [entry.Word];
            currentBaseline = entry.Baseline;
        }

        if (current.Count > 0)
        {
            lines.Add(TextLine.From(current, currentBaseline));
        }

        return lines;
    }

    // ── Columns ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Splits a page into text columns when a clear vertical gutter runs through it.
    /// </summary>
    /// <remarks>
    /// Without this, a two-column page reads as alternating fragments of both columns,
    /// because grouping by baseline puts the left and right column's first lines into
    /// the same visual line. Detection is deliberately conservative: an ambiguous page
    /// stays single-column, which degrades to ordinary reading order rather than to
    /// scrambled text.
    /// </remarks>
    private static List<List<TextLine>> SplitIntoColumns(List<TextLine> lines, double pageWidth)
    {
        if (lines.Count < 8 || pageWidth <= 0)
        {
            return [lines];
        }

        const int Buckets = 100;
        var occupancy = new int[Buckets];

        foreach (var line in lines)
        {
            foreach (var word in line.Words)
            {
                var from = (int)Math.Clamp(word.BoundingBox.Left / pageWidth * Buckets, 0, Buckets - 1);
                var to = (int)Math.Clamp(word.BoundingBox.Right / pageWidth * Buckets, 0, Buckets - 1);
                for (var index = from; index <= to; index++)
                {
                    occupancy[index]++;
                }
            }
        }

        // Look for an empty band in the middle two-thirds of the page, wide enough
        // to be a gutter rather than the ragged inner edge of justified text.
        var bestStart = -1;
        var bestLength = 0;
        var runStart = -1;

        for (var index = Buckets / 6; index < Buckets * 5 / 6; index++)
        {
            if (occupancy[index] == 0)
            {
                if (runStart < 0)
                {
                    runStart = index;
                }

                var length = index - runStart + 1;
                if (length > bestLength)
                {
                    bestLength = length;
                    bestStart = runStart;
                }
            }
            else
            {
                runStart = -1;
            }
        }

        if (bestLength < 4)
        {
            return [lines];
        }

        var gutter = (bestStart + bestLength / 2.0) / Buckets * pageWidth;

        var left = new List<TextLine>();
        var right = new List<TextLine>();

        foreach (var line in lines)
        {
            // A line crossing the gutter is a heading or a rule spanning both columns;
            // its presence means this is not a clean two-column page.
            if (line.Left < gutter && line.Right > gutter)
            {
                return [lines];
            }

            (line.Right <= gutter ? left : right).Add(line);
        }

        var total = (double)lines.Count;
        if (left.Count / total < 0.2 || right.Count / total < 0.2)
        {
            return [lines];
        }

        return [left, right];
    }

    // ── Paragraphs ───────────────────────────────────────────────────────────

    private static IEnumerable<BlockElement> BuildParagraphs(List<TextLine> lines, double bodySize)
    {
        if (lines.Count == 0)
        {
            yield break;
        }

        var gaps = new List<double>();
        for (var index = 1; index < lines.Count; index++)
        {
            var gap = lines[index - 1].Baseline - lines[index].Baseline;
            if (gap > 0)
            {
                gaps.Add(gap);
            }
        }

        var medianGap = gaps.Count > 0 ? Median(gaps.ToArray()) : bodySize * 1.2;
        var columnLeft = lines.Min(line => line.Left);
        var columnRight = lines.Max(line => line.Right);
        var columnWidth = Math.Max(1, columnRight - columnLeft);

        var group = new List<TextLine> { lines[0] };

        for (var index = 1; index < lines.Count; index++)
        {
            var previous = lines[index - 1];
            var line = lines[index];

            if (StartsNewParagraph(previous, line, medianGap, columnLeft, columnWidth))
            {
                yield return BuildParagraph(group, bodySize, columnLeft, columnWidth);
                group = [line];
                continue;
            }

            group.Add(line);
        }

        yield return BuildParagraph(group, bodySize, columnLeft, columnWidth);
    }

    private static bool StartsNewParagraph(
        TextLine previous,
        TextLine line,
        double medianGap,
        double columnLeft,
        double columnWidth)
    {
        var gap = previous.Baseline - line.Baseline;

        // Extra leading between lines is the single most reliable signal.
        if (gap > medianGap * 1.45)
        {
            return true;
        }

        // A change of type size means a different kind of block entirely.
        if (Math.Abs(line.FontSize - previous.FontSize) > Math.Max(0.75, previous.FontSize * 0.12))
        {
            return true;
        }

        // A first-line indent starts a paragraph.
        if (line.Left > columnLeft + Math.Max(6, columnWidth * 0.02)
            && Math.Abs(previous.Left - columnLeft) < 2)
        {
            return true;
        }

        // A short previous line that is not the ragged end of justified text ends a
        // paragraph — but only when the next line begins at the margin, otherwise a
        // wrapped, indented continuation would be split.
        if (previous.Right < columnLeft + columnWidth * 0.82
            && line.Left <= columnLeft + Math.Max(6, columnWidth * 0.02))
        {
            return true;
        }

        // An explicit list marker always begins its own item.
        return LooksLikeListItem(line.Text) is not null;
    }

    private static DocumentParagraph BuildParagraph(
        List<TextLine> lines,
        double bodySize,
        double columnLeft,
        double columnWidth)
    {
        var builder = new StringBuilder();

        for (var index = 0; index < lines.Count; index++)
        {
            var text = lines[index].Text;

            if (index > 0)
            {
                // A trailing hyphen before a lowercase continuation is a word broken
                // across lines, not a compound; joining without the hyphen restores it.
                if (builder.Length > 0
                    && builder[^1] is '-' or '­'
                    && text.Length > 0
                    && char.IsLower(text[0]))
                {
                    builder.Length -= 1;
                }
                else
                {
                    builder.Append(' ');
                }
            }

            builder.Append(text);
        }

        var content = builder.ToString().Trim();
        var first = lines[0];
        var size = Median(lines.Select(line => line.FontSize).ToArray());

        var list = LooksLikeListItem(content);
        if (list is not null)
        {
            content = content[list.Value.MarkerLength..].TrimStart();
        }

        var format = new ParagraphFormatting(
            DetectAlignment(lines, columnLeft, columnWidth),
            IndentLeftPt: Math.Round(Math.Max(0, first.Left - columnLeft), 1));

        var run = new RunFormatting(
            Bold: lines.All(line => line.IsBold),
            Italic: lines.All(line => line.IsItalic),
            FontFamily: NormaliseFontName(first.FontName),
            FontSizePt: Math.Round(size, 1));

        var paragraph = new DocumentParagraph { Format = format };

        if (content.Length > 0)
        {
            paragraph.Inlines.Add(new TextRun(content, run));
        }

        if (list is not null)
        {
            paragraph.List = new ListInfo(list.Value.Kind, 0);
            // A list marker is layout, not emphasis; the indent it produced would
            // otherwise be applied twice.
            paragraph.Format = paragraph.Format with { IndentLeftPt = 0 };
        }
        else if (size > bodySize * 1.15 && lines.Count <= 2 && content.Length is > 0 and < 200)
        {
            paragraph.OutlineLevel = size switch
            {
                _ when size >= bodySize * 1.8 => 1,
                _ when size >= bodySize * 1.45 => 2,
                _ => 3
            };
        }

        return paragraph;
    }

    private static TextAlign DetectAlignment(List<TextLine> lines, double columnLeft, double columnWidth)
    {
        var columnCentre = columnLeft + columnWidth / 2;
        var tolerance = Math.Max(4, columnWidth * 0.03);

        var centred = lines.All(line =>
            Math.Abs((line.Left + line.Right) / 2 - columnCentre) < tolerance
            && line.Left > columnLeft + tolerance);

        if (centred)
        {
            return TextAlign.Center;
        }

        var rightAligned = lines.All(line =>
            Math.Abs(line.Right - (columnLeft + columnWidth)) < tolerance
            && line.Left > columnLeft + tolerance);

        if (rightAligned)
        {
            return TextAlign.Right;
        }

        // Justified text is recognisable by every line but the last reaching the
        // right margin exactly.
        if (lines.Count >= 3)
        {
            var flush = lines.Take(lines.Count - 1)
                .Count(line => Math.Abs(line.Right - (columnLeft + columnWidth)) < tolerance);
            if (flush == lines.Count - 1)
            {
                return TextAlign.Justify;
            }
        }

        return TextAlign.Left;
    }

    private static (ListKind Kind, int MarkerLength)? LooksLikeListItem(string text)
    {
        if (text.Length < 2)
        {
            return null;
        }

        if (BulletGlyphs.Contains(text[0]) && char.IsWhiteSpace(text[1]))
        {
            return (ListKind.Bullet, 2);
        }

        // "1." / "12)" / "a)" — a marker is at most three characters plus punctuation.
        var index = 0;
        while (index < text.Length && index < 3 && char.IsLetterOrDigit(text[index]))
        {
            index++;
        }

        if (index is > 0 and < 4
            && index + 1 < text.Length
            && text[index] is '.' or ')'
            && char.IsWhiteSpace(text[index + 1]))
        {
            var isNumber = char.IsDigit(text[0]);
            return (isNumber ? ListKind.Numbered : ListKind.Bullet, index + 2);
        }

        return null;
    }

    /// <summary>Strips the subset tag PDF prefixes onto embedded font names.</summary>
    private static string? NormaliseFontName(string? fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName))
        {
            return null;
        }

        var name = fontName;

        // Subset fonts are named "ABCDEF+Family"; the six-letter tag is not part of
        // the family and would otherwise show up in the editor's font box.
        if (name.Length > 7 && name[6] == '+')
        {
            name = name[7..];
        }

        foreach (var suffix in new[] { "-BoldItalic", "-BoldOblique", "-Bold", "-Italic", "-Oblique", "-Regular", "MT", "PS" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];
            }
        }

        name = name.Replace(",Bold", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(",Italic", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        return name.Length == 0 ? null : name;
    }

    private static double Median(double[] values)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private sealed class TextLine
    {
        public required IReadOnlyList<Word> Words { get; init; }
        public required string Text { get; init; }
        public required double Baseline { get; init; }
        public required double Left { get; init; }
        public required double Right { get; init; }
        public required double FontSize { get; init; }
        public required bool IsBold { get; init; }
        public required bool IsItalic { get; init; }
        public required string? FontName { get; init; }

        /// <summary>
        /// Joins a line's words, inserting a space only where the page actually has one.
        /// </summary>
        /// <remarks>
        /// A word extractor splits on more than spaces: a change of font, a kerning
        /// pair or a colour change all end a word. Joining unconditionally with a
        /// space turns "Georgia." into "Georgia ." and "эффект" into "эф фект"
        /// wherever the producer switched fonts mid-word. Measuring the gap against
        /// the type size is what tells the two cases apart.
        /// </remarks>
        private static string JoinWords(IReadOnlyList<Word> words)
        {
            var builder = new StringBuilder(words[0].Text);

            for (var index = 1; index < words.Count; index++)
            {
                var previous = words[index - 1];
                var word = words[index];
                var gap = word.BoundingBox.Left - previous.BoundingBox.Right;

                var size = word.Letters.Count > 0
                    ? Median(word.Letters.Select(letter => letter.PointSize).ToArray())
                    : word.BoundingBox.Height;

                if (gap > Math.Max(0.6, size * 0.18))
                {
                    builder.Append(' ');
                }

                builder.Append(word.Text);
            }

            return builder.ToString();
        }

        public static TextLine From(List<Word> words, double baseline)
        {
            var ordered = words.OrderBy(word => word.BoundingBox.Left).ToArray();
            var fontName = ordered[0].FontName;

            return new TextLine
            {
                Words = ordered,
                Text = JoinWords(ordered),
                Baseline = baseline,
                Left = ordered.Min(word => word.BoundingBox.Left),
                Right = ordered.Max(word => word.BoundingBox.Right),
                FontSize = Median(ordered
                    .SelectMany(word => word.Letters)
                    .Select(letter => letter.PointSize)
                    .DefaultIfEmpty(ordered[0].BoundingBox.Height)
                    .ToArray()),
                IsBold = fontName?.Contains("Bold", StringComparison.OrdinalIgnoreCase) == true,
                IsItalic = fontName?.Contains("Italic", StringComparison.OrdinalIgnoreCase) == true
                           || fontName?.Contains("Oblique", StringComparison.OrdinalIgnoreCase) == true,
                FontName = fontName
            };
        }
    }
}
