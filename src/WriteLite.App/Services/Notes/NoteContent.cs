using System.Text;
using System.Text.Json.Serialization;

namespace WriteLite.Services.Notes;

/// <summary>What one block of a note's body is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NoteBlockKind>))]
public enum NoteBlockKind
{
    Paragraph,
    Heading1,
    Heading2,
    Heading3,
    Bullet,
    Numbered,

    /// <summary>A line with its own tick box, inside the body.</summary>
    Check,
    Quote,

    /// <summary>A picture stored beside the note; <see cref="NoteBlock.AssetId"/> names the file.</summary>
    Image,

    /// <summary>A hand drawing; the strokes travel in <see cref="NoteBlock.Ink"/>.</summary>
    Sketch,
    Divider
}

/// <summary>A run of text with one set of character formatting.</summary>
public sealed class NoteSpan
{
    public string Text { get; set; } = string.Empty;

    public bool Bold { get; set; }

    public bool Italic { get; set; }

    public bool Underline { get; set; }

    public bool Strike { get; set; }

    public bool Highlight { get; set; }

    public NoteSpan Copy() => new()
    {
        Text = Text,
        Bold = Bold,
        Italic = Italic,
        Underline = Underline,
        Strike = Strike,
        Highlight = Highlight
    };

    /// <summary>True when two spans can be merged into one without losing formatting.</summary>
    public bool SameFormatAs(NoteSpan other) =>
        Bold == other.Bold && Italic == other.Italic && Underline == other.Underline
        && Strike == other.Strike && Highlight == other.Highlight;
}

/// <summary>
/// One block of a note's body: a paragraph, a heading, a list line, a picture, a drawing.
/// </summary>
/// <remarks>
/// <para>The body is stored as this flat list rather than as XAML. XAML written by
/// <c>XamlWriter</c> embeds absolute image paths and drops anything hosted in a UI
/// container — tick boxes and drawings are exactly that — and a format the application
/// owns is one it can migrate, search and hand to the local model without a WPF thread.</para>
///
/// <para>One shape for every kind, for the same reason <see cref="Note"/> is one shape:
/// turning a paragraph into a heading or a list line must not lose what was typed.</para>
/// </remarks>
public sealed class NoteBlock
{
    public NoteBlockKind Kind { get; set; } = NoteBlockKind.Paragraph;

    public List<NoteSpan> Spans { get; set; } = [];

    /// <summary>Tick state of a <see cref="NoteBlockKind.Check"/> line.</summary>
    public bool IsChecked { get; set; }

    /// <summary>Nesting depth of a list line, from zero.</summary>
    public int Indent { get; set; }

    /// <summary>File name of a picture inside the note's asset folder.</summary>
    public string? AssetId { get; set; }

    /// <summary>Display width of a picture in device-independent pixels; null fits the column.</summary>
    public double? Width { get; set; }

    /// <summary>Strokes of a drawing, as base64 Ink Serialized Format.</summary>
    public string? Ink { get; set; }

    /// <summary>Height of a drawing surface.</summary>
    public double? Height { get; set; }

    /// <summary>Set when the reader bookmarked this block; stable across edits of its text.</summary>
    public string? BookmarkId { get; set; }

    public string? BookmarkName { get; set; }

    [JsonIgnore]
    public string Text => string.Concat(Spans.Select(span => span.Text));

    [JsonIgnore]
    public bool IsTextual => Kind is not (NoteBlockKind.Image or NoteBlockKind.Sketch or NoteBlockKind.Divider);

    public NoteBlock Copy() => new()
    {
        Kind = Kind,
        Spans = Spans.Select(span => span.Copy()).ToList(),
        IsChecked = IsChecked,
        Indent = Indent,
        AssetId = AssetId,
        Width = Width,
        Ink = Ink,
        Height = Height,
        BookmarkId = BookmarkId,
        BookmarkName = BookmarkName
    };

    public static NoteBlock Paragraph(string text, NoteBlockKind kind = NoteBlockKind.Paragraph) => new()
    {
        Kind = kind,
        Spans = text.Length == 0 ? [] : [new NoteSpan { Text = text }]
    };
}

/// <summary>A bookmark set inside a note, as listed in the note and in its project.</summary>
public sealed record NoteBookmark(string NoteId, string BookmarkId, string Name, string NoteTitle);

/// <summary>Pure operations over a note body. No WPF, so the tests and the model can use them.</summary>
public static class NoteContent
{
    /// <summary>Longest bookmark name kept; the rest of a long line is only noise in a list.</summary>
    public const int MaxBookmarkName = 60;

    /// <summary>
    /// The body as plain text: what search matches, what the card shows, what the model reads.
    /// </summary>
    /// <remarks>
    /// List markers are written out so the text still reads as a list on a card and in a
    /// prompt. Pictures and drawings contribute nothing — a placeholder word would be found
    /// by search and quoted by the model as if the author had written it.
    /// </remarks>
    public static string ToPlainText(IReadOnlyList<NoteBlock> blocks)
    {
        var builder = new StringBuilder();
        var number = 0;

        foreach (var block in blocks)
        {
            if (!block.IsTextual)
            {
                number = 0;
                continue;
            }

            number = block.Kind == NoteBlockKind.Numbered ? number + 1 : 0;

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(' ', Math.Clamp(block.Indent, 0, 6) * 2);
            builder.Append(block.Kind switch
            {
                NoteBlockKind.Bullet => "• ",
                NoteBlockKind.Numbered => $"{number}. ",
                NoteBlockKind.Check => block.IsChecked ? "☑ " : "☐ ",
                _ => string.Empty
            });
            builder.Append(block.Text);
        }

        return builder.ToString();
    }

    /// <summary>The body a note had before it was rich: one paragraph per line.</summary>
    public static List<NoteBlock> FromPlainText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [NoteBlock.Paragraph(string.Empty)];
        }

        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => NoteBlock.Paragraph(line))
            .ToList();
    }

    /// <summary>The body to edit: the stored blocks, or the legacy text turned into blocks.</summary>
    public static List<NoteBlock> BlocksOf(Note note) =>
        note.Content is { Count: > 0 } content
            ? content.Select(block => block.Copy()).ToList()
            : FromPlainText(note.Text);

    public static IEnumerable<NoteBookmark> Bookmarks(Note note) =>
        (note.Content ?? [])
        .Where(block => !string.IsNullOrEmpty(block.BookmarkId))
        .Select(block => new NoteBookmark(
            note.Id,
            block.BookmarkId!,
            string.IsNullOrWhiteSpace(block.BookmarkName) ? DefaultBookmarkName(block) : block.BookmarkName!,
            note.DisplayTitle));

    /// <summary>A bookmark is named after the start of the line it marks, unless renamed.</summary>
    public static string DefaultBookmarkName(NoteBlock block)
    {
        var text = block.Text.Trim();
        if (text.Length == 0)
        {
            return block.Kind switch
            {
                NoteBlockKind.Image => "Изображение",
                NoteBlockKind.Sketch => "Рисунок",
                _ => "Закладка"
            };
        }

        return text.Length <= MaxBookmarkName ? text : text[..MaxBookmarkName].TrimEnd() + "…";
    }

    public static IEnumerable<string> ReferencedAssets(IEnumerable<NoteBlock>? blocks) =>
        (blocks ?? [])
        .Where(block => block.Kind == NoteBlockKind.Image && !string.IsNullOrEmpty(block.AssetId))
        .Select(block => block.AssetId!);

    public static int CountOf(Note note, NoteBlockKind kind) =>
        note.Content?.Count(block => block.Kind == kind) ?? 0;

    /// <summary>
    /// Merges neighbouring spans that carry the same formatting.
    /// </summary>
    /// <remarks>
    /// The editor splits runs whenever the caret passes through them; without this a
    /// paragraph typed in one go could come back as dozens of one-letter spans and grow
    /// the file for nothing.
    /// </remarks>
    public static List<NoteSpan> Normalize(IEnumerable<NoteSpan> spans)
    {
        var result = new List<NoteSpan>();
        foreach (var span in spans)
        {
            if (span.Text.Length == 0)
            {
                continue;
            }

            if (result.Count > 0 && result[^1].SameFormatAs(span))
            {
                result[^1].Text += span.Text;
            }
            else
            {
                result.Add(span.Copy());
            }
        }

        return result;
    }
}
