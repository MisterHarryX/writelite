using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using PdfSharp.Fonts;

namespace WriteLite.Documents.Pdf;

/// <summary>
/// Supplies real TrueType files to PDFsharp so exported PDFs embed their fonts.
/// </summary>
/// <remarks>
/// This is the whole reason Cyrillic survives export. Without an embedded font,
/// PDFsharp falls back to a standard base-14 face with WinAnsi encoding, and every
/// Russian letter becomes a box or a question mark — the exact failure the product
/// brief calls out. Embedding the actual face, with Unicode encoding, makes the
/// output correct for Russian, English and anything else the face covers.
///
/// Families are resolved from the font files themselves rather than from the
/// registry, so the resolver has no Windows-only dependency and one code path
/// covers the machine font directory and per-user installs alike.
/// </remarks>
public sealed class SystemFontResolver : IFontResolver
{
    /// <summary>
    /// Fonts that ship with Windows and are known to carry a Cyrillic block.
    /// </summary>
    /// <remarks>
    /// Order is the fallback chain: a document asking for a font the machine does
    /// not have gets the first of these that it does, rather than a face that would
    /// render Russian as empty boxes.
    /// </remarks>
    private static readonly string[] FallbackFamilies =
    [
        "Segoe UI",
        "Arial",
        "Calibri",
        "Times New Roman",
        "Tahoma",
        "Verdana",
        "DejaVu Sans",
        "Liberation Sans"
    ];

    public static SystemFontResolver Instance { get; } = new();

    private readonly Lazy<IReadOnlyDictionary<string, string>> _faces;
    private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.OrdinalIgnoreCase);

    private SystemFontResolver()
    {
        _faces = new Lazy<IReadOnlyDictionary<string, string>>(BuildIndex, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Installs this resolver once per process. Safe to call repeatedly.</summary>
    public static void Install()
    {
        if (GlobalFontSettings.FontResolver is SystemFontResolver)
        {
            return;
        }

        // PDFsharp rejects a resolver swap after the font cache has been touched,
        // and a failure here must not take the export down with it.
        try
        {
            GlobalFontSettings.FontResolver = Instance;
        }
        catch (InvalidOperationException)
        {
            // A resolver is already in place; the existing one keeps serving.
        }
    }

    /// <summary>The family actually used for a request, so callers can measure with it.</summary>
    public string ResolveFamilyName(string? requested)
    {
        var faces = _faces.Value;

        if (!string.IsNullOrWhiteSpace(requested))
        {
            var family = requested.Trim();
            if (faces.ContainsKey(Key(family, false, false)) || faces.Keys.Any(key => key.StartsWith(family + "|", StringComparison.OrdinalIgnoreCase)))
            {
                return family;
            }
        }

        foreach (var fallback in FallbackFamilies)
        {
            if (faces.Keys.Any(key => key.StartsWith(fallback + "|", StringComparison.OrdinalIgnoreCase)))
            {
                return fallback;
            }
        }

        return faces.Count > 0 ? faces.Keys.First().Split('|')[0] : "Arial";
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        var faces = _faces.Value;
        var family = ResolveFamilyName(familyName);

        // Exact style, then the same family with the style simulated, then the
        // fallback chain. Simulation is preferable to switching family: a bold run
        // in a document set in one face should not silently change typeface.
        if (faces.ContainsKey(Key(family, bold, italic)))
        {
            return new FontResolverInfo(Key(family, bold, italic));
        }

        if (faces.ContainsKey(Key(family, false, false)))
        {
            return new FontResolverInfo(Key(family, false, false), bold, italic);
        }

        foreach (var fallback in FallbackFamilies)
        {
            if (faces.ContainsKey(Key(fallback, bold, italic)))
            {
                return new FontResolverInfo(Key(fallback, bold, italic));
            }

            if (faces.ContainsKey(Key(fallback, false, false)))
            {
                return new FontResolverInfo(Key(fallback, false, false), bold, italic);
            }
        }

        var any = faces.Keys.FirstOrDefault();
        return any is null ? null : new FontResolverInfo(any, bold, italic);
    }

    public byte[]? GetFont(string faceName)
    {
        if (_cache.TryGetValue(faceName, out var cached))
        {
            return cached;
        }

        if (!_faces.Value.TryGetValue(faceName, out var path))
        {
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            _cache[faceName] = bytes;
            return bytes;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Key(string family, bool bold, bool italic) =>
        $"{family}|{(bold ? "b" : string.Empty)}{(italic ? "i" : string.Empty)}";

    // ── Index ────────────────────────────────────────────────────────────────

    private static IReadOnlyDictionary<string, string> BuildIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in FontDirectories())
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*.ttf", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.EnumerateFiles(directory, "*.otf", SearchOption.TopDirectoryOnly));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (TrueTypeNames.TryRead(file) is not { } names)
                {
                    continue;
                }

                var key = Key(names.Family, names.Bold, names.Italic);

                // First writer wins: the machine font directory is enumerated before
                // per-user installs, and a user-installed clone should not displace
                // the system face a document most likely means.
                index.TryAdd(key, file);
            }
        }

        return index;
    }

    private static IEnumerable<string> FontDirectories()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows))
        {
            yield return Path.Combine(windows, "Fonts");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            yield return Path.Combine(localAppData, "Microsoft", "Windows", "Fonts");
        }

        // Non-Windows hosts, which the test suite can run on.
        foreach (var path in new[] { "/usr/share/fonts", "/usr/local/share/fonts" })
        {
            if (Directory.Exists(path))
            {
                yield return path;
            }
        }
    }
}

/// <summary>
/// Reads family and style out of a TrueType/OpenType <c>name</c> table.
/// </summary>
/// <remarks>
/// Deliberately minimal: enough of the format to answer "what family is this file
/// and is it bold or italic", and nothing else. The alternative was the Windows
/// registry, which would have tied the document layer to one platform for a
/// question the file itself already answers.
/// </remarks>
internal static class TrueTypeNames
{
    private const ushort NameIdFamily = 1;
    private const ushort NameIdSubfamily = 2;
    private const ushort NameIdTypographicFamily = 16;

    public static (string Family, bool Bold, bool Italic)? TryRead(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            var tag = ReadUInt32(reader);

            // 'ttcf' is a font collection; skipping them costs a handful of exotic
            // faces and avoids a second container format in this reader.
            if (tag is not (0x00010000 or 0x4F54544F))
            {
                return null;
            }

            var tableCount = ReadUInt16(reader);
            reader.BaseStream.Seek(6, SeekOrigin.Current);

            uint nameOffset = 0;
            for (var index = 0; index < tableCount; index++)
            {
                var tableTag = reader.ReadBytes(4);
                ReadUInt32(reader);
                var offset = ReadUInt32(reader);
                ReadUInt32(reader);

                if (tableTag.Length == 4 && Encoding.ASCII.GetString(tableTag) == "name")
                {
                    nameOffset = offset;
                    break;
                }
            }

            if (nameOffset == 0 || nameOffset >= stream.Length)
            {
                return null;
            }

            stream.Seek(nameOffset, SeekOrigin.Begin);
            ReadUInt16(reader);
            var recordCount = ReadUInt16(reader);
            var stringOffset = ReadUInt16(reader);

            string? family = null;
            string? typographicFamily = null;
            string? subfamily = null;

            for (var index = 0; index < recordCount; index++)
            {
                var platformId = ReadUInt16(reader);
                var encodingId = ReadUInt16(reader);
                ReadUInt16(reader);
                var nameId = ReadUInt16(reader);
                var length = ReadUInt16(reader);
                var offset = ReadUInt16(reader);

                if (nameId is not (NameIdFamily or NameIdSubfamily or NameIdTypographicFamily))
                {
                    continue;
                }

                var position = stream.Position;
                var absolute = nameOffset + stringOffset + offset;
                if (absolute + length > stream.Length)
                {
                    continue;
                }

                stream.Seek(absolute, SeekOrigin.Begin);
                var bytes = reader.ReadBytes(length);
                stream.Seek(position, SeekOrigin.Begin);

                // Platform 3 (Windows) and platform 0 (Unicode) store UTF-16BE;
                // platform 1 (Macintosh) stores single-byte Roman.
                var value = platformId is 3 or 0
                    ? Encoding.BigEndianUnicode.GetString(bytes)
                    : Encoding.ASCII.GetString(bytes);

                value = value.Trim('\0', ' ');
                if (value.Length == 0)
                {
                    continue;
                }

                switch (nameId)
                {
                    case NameIdFamily when family is null || platformId == 3:
                        family = value;
                        break;
                    case NameIdTypographicFamily when typographicFamily is null || platformId == 3:
                        typographicFamily = value;
                        break;
                    case NameIdSubfamily when subfamily is null || platformId == 3:
                        subfamily = value;
                        break;
                }

                _ = encodingId;
            }

            var resolved = typographicFamily ?? family;
            if (string.IsNullOrWhiteSpace(resolved))
            {
                return null;
            }

            var style = subfamily ?? "Regular";
            return (
                resolved,
                style.Contains("Bold", StringComparison.OrdinalIgnoreCase),
                style.Contains("Italic", StringComparison.OrdinalIgnoreCase)
                || style.Contains("Oblique", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return null;
        }
    }

    private static ushort ReadUInt16(BinaryReader reader) =>
        BinaryPrimitives.ReadUInt16BigEndian(reader.ReadBytes(2));

    private static uint ReadUInt32(BinaryReader reader) =>
        BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4));
}
