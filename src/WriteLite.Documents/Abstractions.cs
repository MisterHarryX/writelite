using WriteLite.Documents.Model;

namespace WriteLite.Documents;

/// <summary>
/// Progress from a long document operation.
/// </summary>
/// <param name="Fraction">0–1 where the total work is known, otherwise null.</param>
public readonly record struct DocumentProgress(string Stage, double? Fraction)
{
    public static DocumentProgress Of(string stage, int done, int total) =>
        new(stage, total <= 0 ? null : Math.Clamp(done / (double)total, 0, 1));
}

/// <summary>
/// One thing that could not be preserved, reported rather than swallowed.
/// </summary>
/// <remarks>
/// Import never fails because of a warning: the contract is that readable text
/// always survives, and anything richer that did not is named here so the status
/// bar can say so instead of the user discovering it later.
/// </remarks>
public sealed record DocumentWarning(string Code, string Message);

public sealed record DocumentImportResult(WlDocument Document, IReadOnlyList<DocumentWarning> Warnings)
{
    public static DocumentImportResult Clean(WlDocument document) => new(document, []);

    public bool HasWarnings => Warnings.Count > 0;
}

public sealed record DocumentExportResult(string Path, IReadOnlyList<DocumentWarning> Warnings)
{
    public bool HasWarnings => Warnings.Count > 0;
}

public interface IDocumentImporter
{
    DocumentFormat Format { get; }

    /// <summary>Reads a document from an already-open stream.</summary>
    Task<DocumentImportResult> ImportAsync(
        Stream stream,
        DocumentImportOptions options,
        IProgress<DocumentProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record DocumentImportOptions(string? SourcePath = null)
{
    public static readonly DocumentImportOptions Default = new();

    /// <summary>Hard ceiling on characters imported, so a pathological file cannot exhaust memory.</summary>
    public int MaxCharacters { get; init; } = 8_000_000;
}

public interface IDocumentExporter
{
    DocumentFormat Format { get; }

    Task<IReadOnlyList<DocumentWarning>> ExportAsync(
        WlDocument document,
        Stream stream,
        IProgress<DocumentProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IDocumentSerializer
{
    Task<DocumentImportResult> LoadAsync(
        string path,
        IProgress<DocumentProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<DocumentExportResult> SaveAsync(
        WlDocument document,
        string path,
        DocumentFormat format,
        IProgress<DocumentProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IDocumentConverter
{
    bool CanConvert(DocumentFormat from, DocumentFormat to);

    Task<DocumentConversionResult> ConvertAsync(
        string inputPath,
        string outputPath,
        DocumentFormat targetFormat,
        IProgress<DocumentProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record DocumentConversionResult(
    string OutputPath,
    DocumentFormat From,
    DocumentFormat To,
    IReadOnlyList<DocumentWarning> Warnings,
    TimeSpan Duration);

/// <summary>
/// A file WriteLite understands but could not fully read.
/// </summary>
/// <remarks>
/// Thrown only when there is genuinely nothing to show. Anything recoverable —
/// a damaged style table, an unreadable table, a page that would not decode —
/// comes back as a <see cref="DocumentWarning"/> alongside the text that did
/// survive, because a partly readable document is worth more to a writer than
/// an error dialog.
/// </remarks>
public sealed class DocumentFormatException(string message, string userMessage, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>What to show a person. Never contains a stack trace or a library name.</summary>
    public string UserMessage { get; } = userMessage;
}
