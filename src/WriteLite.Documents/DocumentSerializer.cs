using System.Diagnostics;
using WriteLite.Documents.Export;
using WriteLite.Documents.Import;
using WriteLite.Documents.Model;

namespace WriteLite.Documents;

/// <summary>
/// The one place that maps a format to its importer and exporter.
/// </summary>
/// <remarks>
/// Adding a format means adding a pair here and nothing else: the editor, the
/// converter and the save flow all go through this type and none of them names a
/// concrete importer.
///
/// Streams are opened with explicit buffering and closed the moment the work is
/// done. A document editor that leaves a handle on the file the user is editing
/// makes their next save fail from another application.
/// </remarks>
public sealed class DocumentSerializer : IDocumentSerializer
{
    private readonly Dictionary<DocumentFormat, IDocumentImporter> _importers;
    private readonly Dictionary<DocumentFormat, IDocumentExporter> _exporters;

    public DocumentSerializer()
        : this(
            [new TxtImporter(), new DocxImporter(), new OdtImporter(), new PdfImporter()],
            [new TxtExporter(), new DocxExporter(), new OdtExporter(), new PdfExporter()])
    {
    }

    public DocumentSerializer(IEnumerable<IDocumentImporter> importers, IEnumerable<IDocumentExporter> exporters)
    {
        _importers = importers.ToDictionary(importer => importer.Format);
        _exporters = exporters.ToDictionary(exporter => exporter.Format);
    }

    public IReadOnlyCollection<DocumentFormat> SupportedImports => _importers.Keys;

    public IReadOnlyCollection<DocumentFormat> SupportedExports => _exporters.Keys;

    public bool CanImport(DocumentFormat format) => _importers.ContainsKey(format);

    public bool CanExport(DocumentFormat format) => _exporters.ContainsKey(format);

    public async Task<DocumentImportResult> LoadAsync(
        string path,
        IProgress<DocumentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var format = DocumentFormats.FromPath(path);

        if (!_importers.TryGetValue(format, out var importer))
        {
            throw new DocumentFormatException(
                $"unsupported-import: {Path.GetExtension(path)}",
                "WriteLite пока не умеет открывать файлы этого типа.");
        }

        if (!File.Exists(path))
        {
            throw new DocumentFormatException(
                "file-missing",
                "Файл не найден. Возможно, он был перемещён или удалён.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            // Shared read: another application having the file open must not stop
            // WriteLite from opening a copy of it for editing.
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            useAsync: true);

        return await importer
            .ImportAsync(stream, new DocumentImportOptions(path), progress, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DocumentExportResult> SaveAsync(
        WlDocument document,
        string path,
        DocumentFormat format,
        IProgress<DocumentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_exporters.TryGetValue(format, out var exporter))
        {
            throw new DocumentFormatException(
                $"unsupported-export: {format}",
                "WriteLite пока не умеет сохранять в этот формат.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Written beside the target and moved into place, so a cancelled or failed
        // export never leaves a half-written file where the user's document was.
        var temporary = path + ".writelite-tmp";
        IReadOnlyList<DocumentWarning> warnings;

        try
        {
            // ReadWrite, not Write: the OPC layer behind DOCX seeks back over the
            // package it is building to fix up its central directory, and a
            // write-only stream fails the save outright.
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.Create,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             useAsync: true))
            {
                warnings = await exporter
                    .ExportAsync(document, stream, progress, cancellationToken)
                    .ConfigureAwait(false);

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(path))
            {
                File.Replace(temporary, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }

        return new DocumentExportResult(path, warnings);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is not worth masking the real failure.
        }
    }
}

/// <summary>
/// File-to-file conversion between the supported formats.
/// </summary>
/// <remarks>
/// Every conversion goes through the internal model — there is no direct
/// DOCX-to-ODT path — so the matrix is every importable format crossed with every
/// exportable one, and a new format joins both axes at once.
/// </remarks>
public sealed class DocumentConversionService(IDocumentSerializer? serializer = null) : IDocumentConverter
{
    private readonly DocumentSerializer _serializer = serializer as DocumentSerializer ?? new DocumentSerializer();

    public bool CanConvert(DocumentFormat from, DocumentFormat to) =>
        from != DocumentFormat.Unknown
        && to != DocumentFormat.Unknown
        && _serializer.CanImport(from)
        && _serializer.CanExport(to);

    public async Task<DocumentConversionResult> ConvertAsync(
        string inputPath,
        string outputPath,
        DocumentFormat targetFormat,
        IProgress<DocumentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var sourceFormat = DocumentFormats.FromPath(inputPath);

        if (!CanConvert(sourceFormat, targetFormat))
        {
            throw new DocumentFormatException(
                $"unsupported-conversion: {sourceFormat}->{targetFormat}",
                "Это преобразование пока не поддерживается.");
        }

        if (string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new DocumentFormatException(
                "conversion-would-overwrite-source",
                "Исходный файл нельзя перезаписать результатом преобразования.");
        }

        // Import and export each report 0–1; halving them makes one continuous bar
        // rather than two that each run to full and restart.
        var importProgress = progress is null
            ? null
            : new Progress<DocumentProgress>(report => progress.Report(
                report with { Fraction = report.Fraction is null ? null : report.Fraction * 0.5 }));

        var exportProgress = progress is null
            ? null
            : new Progress<DocumentProgress>(report => progress.Report(
                report with { Fraction = report.Fraction is null ? null : 0.5 + report.Fraction * 0.5 }));

        var imported = await _serializer.LoadAsync(inputPath, importProgress, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var exported = await _serializer
            .SaveAsync(imported.Document, outputPath, targetFormat, exportProgress, cancellationToken)
            .ConfigureAwait(false);

        stopwatch.Stop();

        return new DocumentConversionResult(
            exported.Path,
            sourceFormat,
            targetFormat,
            [.. imported.Warnings, .. exported.Warnings],
            stopwatch.Elapsed);
    }
}
