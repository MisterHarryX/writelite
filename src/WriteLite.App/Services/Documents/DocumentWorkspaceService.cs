using System.IO;
using WriteLite.Documents;
using WriteLite.Documents.Model;

namespace WriteLite.Services.Documents;

/// <summary>
/// Owns the document the editor currently has open.
/// </summary>
/// <remarks>
/// The editor asks this service to open, save and convert; it never touches an
/// importer, an exporter or a file path directly. That is what keeps the rules
/// about where a document may be written in one place — in particular the rule
/// that editing a PDF never writes back over the PDF.
///
/// Nothing here touches the dispatcher, so every method is safe to await from the
/// UI without it becoming a UI-thread operation.
/// </remarks>
public sealed class DocumentWorkspaceService(
    DocumentSerializer? serializer = null,
    RecentDocumentsStore? recent = null)
{
    private readonly DocumentSerializer _serializer = serializer ?? new DocumentSerializer();

    public RecentDocumentsStore Recent { get; } = recent ?? new RecentDocumentsStore();

    /// <summary>The file backing the open document, or null for a document never saved.</summary>
    public string? CurrentPath { get; private set; }

    public DocumentFormat CurrentFormat { get; private set; } = DocumentFormat.Unknown;

    public DocumentMetadata CurrentMetadata { get; private set; } = new();

    /// <summary>
    /// Whether plain Save can write back to where the document came from.
    /// </summary>
    /// <remarks>
    /// False for PDF. A PDF was reconstructed from glyph positions, so writing the
    /// edited model back over it would replace the user's original with WriteLite's
    /// interpretation of it — a silent, unrecoverable downgrade. Save on a document
    /// opened from PDF asks where to put the result instead.
    /// </remarks>
    public bool CanSaveInPlace =>
        CurrentPath is not null && CurrentFormat is DocumentFormat.Docx or DocumentFormat.Odt or DocumentFormat.Txt;

    public string DisplayName =>
        CurrentPath is not null
            ? Path.GetFileName(CurrentPath)
            : string.IsNullOrWhiteSpace(CurrentMetadata.Title)
                ? "Без названия"
                : CurrentMetadata.Title!;

    public async Task<DocumentImportResult> OpenAsync(
        string path,
        IProgress<DocumentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _serializer.LoadAsync(path, progress, cancellationToken).ConfigureAwait(false);

        CurrentPath = path;
        CurrentFormat = DocumentFormats.FromPath(path);
        CurrentMetadata = result.Document.Metadata;
        Recent.Add(path);

        CompatibilityLogger.Technical(
            "document-opened",
            $"format={CurrentFormat} blocks={result.Document.Blocks.Count()} warnings={result.Warnings.Count}");

        return result;
    }

    /// <summary>Starts a new empty document, forgetting the previous file association.</summary>
    public void Reset(string? title = null)
    {
        CurrentPath = null;
        CurrentFormat = DocumentFormat.Unknown;
        CurrentMetadata = new DocumentMetadata { Title = title };
    }

    /// <summary>Adopts a document that arrived from somewhere other than a file.</summary>
    public void AdoptImported(WlDocument document, string path)
    {
        CurrentPath = path;
        CurrentFormat = DocumentFormats.FromPath(path);
        CurrentMetadata = document.Metadata;
    }

    public async Task<DocumentExportResult> SaveAsync(
        WlDocument document,
        string path,
        DocumentFormat format,
        IProgress<DocumentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        document.Metadata.Modified = DateTimeOffset.Now;

        var result = await _serializer
            .SaveAsync(document, path, format, progress, cancellationToken)
            .ConfigureAwait(false);

        // Exporting a copy is not the same as saving: writing a PDF beside a DOCX
        // must not make the PDF the document the editor is now editing.
        if (format is DocumentFormat.Docx or DocumentFormat.Odt or DocumentFormat.Txt)
        {
            CurrentPath = path;
            CurrentFormat = format;
            CurrentMetadata = document.Metadata;
            Recent.Add(path);
        }

        CompatibilityLogger.Technical(
            "document-saved",
            $"format={format} warnings={result.Warnings.Count}");

        return result;
    }

    /// <summary>Writes a copy in another format without changing what is being edited.</summary>
    public Task<DocumentExportResult> ExportAsync(
        WlDocument document,
        string path,
        DocumentFormat format,
        IProgress<DocumentProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => _serializer.SaveAsync(document, path, format, progress, cancellationToken);

    /// <summary>A default file name for Save As, derived from the document's own title.</summary>
    public string SuggestFileName(DocumentFormat format)
    {
        var baseName = CurrentPath is not null
            ? Path.GetFileNameWithoutExtension(CurrentPath)
            : string.IsNullOrWhiteSpace(CurrentMetadata.Title)
                ? "Документ"
                : CurrentMetadata.Title!;

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            baseName = baseName.Replace(invalid, '_');
        }

        return baseName + DocumentFormats.Extension(format);
    }
}
