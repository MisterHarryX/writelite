namespace WriteLite.Documents;

public enum DocumentFormat
{
    Unknown = 0,
    Txt,
    Docx,
    Odt,
    Pdf
}

public static class DocumentFormats
{
    public static readonly IReadOnlyList<DocumentFormat> All =
    [
        DocumentFormat.Docx,
        DocumentFormat.Pdf,
        DocumentFormat.Txt,
        DocumentFormat.Odt
    ];

    /// <summary>Formats WriteLite can open.</summary>
    public static readonly IReadOnlyList<DocumentFormat> Importable = All;

    /// <summary>Formats WriteLite can write.</summary>
    public static readonly IReadOnlyList<DocumentFormat> Exportable =
    [
        DocumentFormat.Docx,
        DocumentFormat.Odt,
        DocumentFormat.Txt,
        DocumentFormat.Pdf
    ];

    /// <summary>
    /// PDF is a page description, not a document structure: writing one back is
    /// always a fresh render, never an edit of the original.
    /// </summary>
    public static bool IsLossyTarget(DocumentFormat format) =>
        format is DocumentFormat.Pdf or DocumentFormat.Txt;

    public static DocumentFormat FromPath(string path)
    {
        var extension = Path.GetExtension(path);
        return FromExtension(extension);
    }

    public static DocumentFormat FromExtension(string? extension) =>
        (extension ?? string.Empty).TrimStart('.').ToLowerInvariant() switch
        {
            "docx" => DocumentFormat.Docx,
            "odt" => DocumentFormat.Odt,
            "pdf" => DocumentFormat.Pdf,
            "txt" or "text" or "log" or "md" => DocumentFormat.Txt,
            _ => DocumentFormat.Unknown
        };

    public static string Extension(DocumentFormat format) => format switch
    {
        DocumentFormat.Docx => ".docx",
        DocumentFormat.Odt => ".odt",
        DocumentFormat.Pdf => ".pdf",
        DocumentFormat.Txt => ".txt",
        _ => string.Empty
    };

    public static string DisplayName(DocumentFormat format) => format switch
    {
        DocumentFormat.Docx => "DOCX",
        DocumentFormat.Odt => "ODT",
        DocumentFormat.Pdf => "PDF",
        DocumentFormat.Txt => "TXT",
        _ => "—"
    };

    /// <summary>Win32 common-dialog filter covering every importable format.</summary>
    public static string OpenFilter =>
        "Документы (*.docx;*.odt;*.pdf;*.txt)|*.docx;*.odt;*.pdf;*.txt|" +
        "Word (*.docx)|*.docx|" +
        "OpenDocument (*.odt)|*.odt|" +
        "PDF (*.pdf)|*.pdf|" +
        "Текст (*.txt)|*.txt|" +
        "Все файлы (*.*)|*.*";

    public static string SaveFilter =>
        "Word (*.docx)|*.docx|" +
        "OpenDocument (*.odt)|*.odt|" +
        "Текст (*.txt)|*.txt|" +
        "PDF (*.pdf)|*.pdf";
}
