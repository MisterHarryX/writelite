using System.Text;
using WriteLite.Documents.Model;

namespace WriteLite.Documents.Import;

/// <summary>
/// Plain text, with the encoding worked out rather than assumed.
/// </summary>
/// <remarks>
/// A Russian-language editor cannot default to UTF-8 and stop there: files written
/// by older Windows tools are CP1251 and files off a mainframe-era pipeline are
/// KOI8-R, and both decode as mojibake under UTF-8 without erroring. The detector
/// only reports a legacy encoding when the byte pattern positively rules UTF-8 out,
/// so a valid UTF-8 file is never second-guessed.
/// </remarks>
public sealed class TxtImporter : IDocumentImporter
{
    public DocumentFormat Format => DocumentFormat.Txt;

    public async Task<DocumentImportResult> ImportAsync(
        Stream stream,
        DocumentImportOptions options,
        IProgress<DocumentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new DocumentProgress("Чтение файла", null));

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var bytes = buffer.ToArray();

        var detection = TextEncodingDetector.Detect(bytes);
        var text = detection.Encoding.GetString(bytes, detection.PreambleLength, bytes.Length - detection.PreambleLength);

        var warnings = new List<DocumentWarning>();
        if (text.Length > options.MaxCharacters)
        {
            text = text[..options.MaxCharacters];
            warnings.Add(new DocumentWarning(
                "txt-truncated",
                "Файл очень большой — открыта первая часть текста."));
        }

        if (detection.IsFallback)
        {
            warnings.Add(new DocumentWarning(
                "txt-encoding-guessed",
                $"Кодировка файла определена как {detection.Name}."));
        }

        progress?.Report(new DocumentProgress("Разбор абзацев", 0.6));
        var document = FromPlainText(text);
        document.Metadata.SourceFormat = DocumentFormat.Txt;
        document.Metadata.SourcePath = options.SourcePath;
        document.Metadata.Title = options.SourcePath is null ? null : Path.GetFileNameWithoutExtension(options.SourcePath);

        progress?.Report(new DocumentProgress("Готово", 1));
        return new DocumentImportResult(document, warnings);
    }

    /// <summary>Splits text into paragraphs on line breaks, keeping blank lines out of the model.</summary>
    public static WlDocument FromPlainText(string text)
    {
        var document = WlDocument.Empty();
        var section = document.CurrentSection();

        // Normalise first: a mixed-ending file must not produce empty paragraphs
        // where a CR happened to be followed by an LF.
        var normalised = text.Replace("\r\n", "\n").Replace('\r', '\n');

        foreach (var line in normalised.Split('\n'))
        {
            section.Blocks.Add(DocumentParagraph.FromText(line.TrimEnd()));
        }

        if (section.Blocks.Count == 0)
        {
            section.Blocks.Add(DocumentParagraph.FromText(string.Empty));
        }

        return document;
    }
}

public readonly record struct EncodingDetection(Encoding Encoding, int PreambleLength, string Name, bool IsFallback);

public static class TextEncodingDetector
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static TextEncodingDetector()
    {
        // CP1251 and KOI8-R live in the code-pages provider, not in the default set.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static EncodingDetection Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return new EncodingDetection(new UTF8Encoding(false), 3, "UTF-8 BOM", IsFallback: false);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return new EncodingDetection(Encoding.Unicode, 2, "UTF-16 LE", IsFallback: false);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return new EncodingDetection(Encoding.BigEndianUnicode, 2, "UTF-16 BE", IsFallback: false);
        }

        if (IsValidUtf8(bytes))
        {
            return new EncodingDetection(new UTF8Encoding(false), 0, "UTF-8", IsFallback: false);
        }

        // Not UTF-8. Pick between the two single-byte Cyrillic encodings that are
        // actually still encountered by scoring which produces more plausible
        // Russian; ties fall to CP1251, which is what Windows tools wrote.
        var koi8Score = ScoreCyrillic(bytes, 20866);
        var cp1251Score = ScoreCyrillic(bytes, 1251);

        return koi8Score > cp1251Score
            ? new EncodingDetection(GetEncodingOrDefault(20866), 0, "KOI8-R", IsFallback: true)
            : new EncodingDetection(GetEncodingOrDefault(1251), 0, "Windows-1251", IsFallback: true);
    }

    private static Encoding GetEncodingOrDefault(int codePage)
    {
        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>How much of the decoded text lands in Cyrillic letters and ordinary punctuation.</summary>
    private static int ScoreCyrillic(ReadOnlySpan<byte> bytes, int codePage)
    {
        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(codePage);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return 0;
        }

        var sampleLength = Math.Min(bytes.Length, 8192);
        var text = encoding.GetString(bytes[..sampleLength]);

        var score = 0;
        var previousWasCyrillic = false;
        foreach (var c in text)
        {
            var isCyrillic = c is >= 'А' and <= 'я' || c is 'ё' or 'Ё';
            if (isCyrillic)
            {
                // Runs of Cyrillic are words; isolated Cyrillic letters among Latin
                // are the signature of a wrong single-byte guess.
                score += previousWasCyrillic ? 2 : 1;
            }
            else if (char.IsLetter(c) && c > 127)
            {
                score -= 2;
            }

            previousWasCyrillic = isCyrillic;
        }

        return score;
    }
}
