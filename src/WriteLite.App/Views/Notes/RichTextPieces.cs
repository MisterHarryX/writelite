using System.Text;
using System.Windows.Documents;
using WriteLite.Services.Ai;

namespace WriteLite.Views.Notes;

/// <summary>A stretch of one paragraph that an AI action works on, by text offset.</summary>
public sealed record RichTextPiece(Paragraph Paragraph, int Start, int Length, string Text);

/// <summary>
/// Reads text out of a <see cref="FlowDocument"/> paragraph by paragraph and writes AI
/// results back without flattening the formatting around them.
/// </summary>
/// <remarks>
/// <para>The obvious write-back — set <see cref="TextRange.Text"/> over the whole selection —
/// turns a note into plain text: headings lose their size, list lines merge, the bold word
/// in the middle of a fixed sentence stops being bold. So results go back paragraph by
/// paragraph, and within a paragraph only the words that changed are replaced: the word
/// diff the preview already shows becomes a list of small edits, applied right to left so
/// one edit never moves the offsets of the next.</para>
///
/// <para>Offsets count characters of runs, one per line break, and nothing for embedded
/// elements — a checklist line's tick box is not text. Reading and writing use the same
/// walk, which is what keeps them in agreement.</para>
/// </remarks>
public static class RichTextPieces
{
    /// <summary>Every paragraph of the document as a whole piece.</summary>
    public static List<RichTextPiece> WholeDocument(FlowDocument document) =>
        NoteDocumentMapper.Paragraphs(document.Blocks)
            .Select(paragraph =>
            {
                var text = TextOf(paragraph);
                return new RichTextPiece(paragraph, 0, text.Length, text);
            })
            .ToList();

    /// <summary>The parts of each paragraph that the selection covers.</summary>
    public static List<RichTextPiece> FromSelection(FlowDocument document, TextPointer start, TextPointer end)
    {
        var pieces = new List<RichTextPiece>();
        foreach (var paragraph in NoteDocumentMapper.Paragraphs(document.Blocks))
        {
            if (paragraph.ContentEnd.CompareTo(start) <= 0 || paragraph.ContentStart.CompareTo(end) >= 0)
            {
                continue;
            }

            var text = TextOf(paragraph);
            var from = start.CompareTo(paragraph.ContentStart) <= 0 ? 0 : OffsetOf(paragraph, start);
            var to = end.CompareTo(paragraph.ContentEnd) >= 0 ? text.Length : OffsetOf(paragraph, end);

            from = Math.Clamp(from, 0, text.Length);
            to = Math.Clamp(to, from, text.Length);
            pieces.Add(new RichTextPiece(paragraph, from, to - from, text.Substring(from, to - from)));
        }

        return pieces;
    }

    /// <summary>The paragraph's text, by the same walk the offsets use.</summary>
    public static string TextOf(Paragraph paragraph)
    {
        var builder = new StringBuilder();
        Walk(paragraph, (_, text) =>
        {
            builder.Append(text);
            return true;
        });
        return builder.ToString();
    }

    /// <summary>The text offset of a position inside a paragraph.</summary>
    public static int OffsetOf(Paragraph paragraph, TextPointer position)
    {
        var offset = 0;
        Walk(paragraph, (at, text) =>
        {
            if (at.CompareTo(position) >= 0)
            {
                return false;
            }

            if (text.Length > 1)
            {
                var end = at.GetPositionAtOffset(text.Length);
                if (end is not null && end.CompareTo(position) > 0)
                {
                    offset += at.GetOffsetToPosition(position);
                    return false;
                }
            }

            offset += text.Length;
            return true;
        });
        return offset;
    }

    /// <summary>The position at a text offset inside a paragraph.</summary>
    public static TextPointer PositionAt(Paragraph paragraph, int offset)
    {
        var remaining = offset;
        TextPointer? found = null;
        Walk(paragraph, (at, text) =>
        {
            if (remaining <= 0)
            {
                found = at;
                return false;
            }

            if (remaining < text.Length || (remaining == text.Length && text != "\n"))
            {
                found = at.GetPositionAtOffset(remaining) ?? at;
                return false;
            }

            remaining -= text.Length;
            return true;
        });

        return found ?? paragraph.ContentEnd;
    }

    /// <summary>
    /// Visits the text of a paragraph in order: each call gets the position where a stretch of
    /// text begins and the text itself. Returning false stops the walk.
    /// </summary>
    private static void Walk(Paragraph paragraph, Func<TextPointer, string, bool> visit)
    {
        var navigator = NoteDocumentMapper.TextStart(paragraph);
        var end = paragraph.ContentEnd;

        while (navigator is not null && navigator.CompareTo(end) < 0)
        {
            switch (navigator.GetPointerContext(LogicalDirection.Forward))
            {
                case TextPointerContext.Text:
                    var text = navigator.GetTextInRun(LogicalDirection.Forward);
                    if (!visit(navigator, text))
                    {
                        return;
                    }

                    navigator = navigator.GetPositionAtOffset(text.Length, LogicalDirection.Forward);
                    break;

                case TextPointerContext.ElementStart when navigator.GetAdjacentElement(LogicalDirection.Forward) is LineBreak lineBreak:
                    if (!visit(navigator, "\n"))
                    {
                        return;
                    }

                    navigator = lineBreak.ElementEnd;
                    break;

                default:
                    navigator = navigator.GetNextContextPosition(LogicalDirection.Forward);
                    break;
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="replacement"/> over a piece, touching only the words that differ.
    /// </summary>
    /// <returns>False when the paragraph no longer holds the text the piece was read from.</returns>
    public static bool Apply(RichTextPiece piece, string replacement)
    {
        var current = TextOf(piece.Paragraph);
        if (piece.Start + piece.Length > current.Length
            || !string.Equals(current.Substring(piece.Start, piece.Length), piece.Text, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(piece.Text, replacement, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var edit in Edits(piece.Text, replacement).OrderByDescending(edit => edit.Offset))
        {
            var from = PositionAt(piece.Paragraph, piece.Start + edit.Offset);
            var to = PositionAt(piece.Paragraph, piece.Start + edit.Offset + edit.RemovedLength);
            var range = new TextRange(from, to);

            if (edit.Inserted.Length == 0)
            {
                range.Text = string.Empty;
                continue;
            }

            // Inserted next to the text it replaces, so it takes that text's formatting.
            range.Text = edit.Inserted.Replace("\n", " ", StringComparison.Ordinal);
        }

        return true;
    }

    /// <summary>One replacement inside a piece: at an offset of the original, remove so many characters, insert this.</summary>
    public readonly record struct TextEdit(int Offset, int RemovedLength, string Inserted);

    /// <summary>The word diff as a list of edits against the original's offsets.</summary>
    public static List<TextEdit> Edits(string original, string replacement)
    {
        var edits = new List<TextEdit>();
        var offset = 0;
        var editStart = -1;
        var removed = 0;
        var inserted = new StringBuilder();

        void Close()
        {
            if (editStart >= 0)
            {
                edits.Add(new TextEdit(editStart, removed, inserted.ToString()));
            }

            editStart = -1;
            removed = 0;
            inserted.Clear();
        }

        foreach (var segment in RewriteDiff.Compute(original, replacement))
        {
            switch (segment.Kind)
            {
                case DiffKind.Unchanged:
                    Close();
                    offset += segment.Text.Length;
                    break;

                case DiffKind.Removed:
                    if (editStart < 0)
                    {
                        editStart = offset;
                    }

                    removed += segment.Text.Length;
                    offset += segment.Text.Length;
                    break;

                case DiffKind.Added:
                    if (editStart < 0)
                    {
                        editStart = offset;
                    }

                    inserted.Append(segment.Text);
                    break;
            }
        }

        Close();

        // The diff works on whole tokens with their trailing space; if the edits do not
        // reproduce the replacement exactly, one edit over the whole piece is the safe answer.
        return Reproduces(original, edits, replacement) ? edits : [new TextEdit(0, original.Length, replacement)];
    }

    private static bool Reproduces(string original, IEnumerable<TextEdit> edits, string replacement)
    {
        var builder = new StringBuilder(original);
        foreach (var edit in edits.OrderByDescending(edit => edit.Offset))
        {
            if (edit.Offset + edit.RemovedLength > builder.Length)
            {
                return false;
            }

            builder.Remove(edit.Offset, edit.RemovedLength);
            builder.Insert(edit.Offset, edit.Inserted);
        }

        return string.Equals(builder.ToString(), replacement, StringComparison.Ordinal);
    }
}
