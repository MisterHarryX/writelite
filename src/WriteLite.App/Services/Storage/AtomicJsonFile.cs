using System.IO;
using System.Text.Json;

namespace WriteLite.Services.Storage;

/// <summary>
/// Reads and writes a JSON file in a way that survives being interrupted.
/// </summary>
/// <remarks>
/// Notes and reading projects are the only data in WriteLite the user cannot get
/// back from somewhere else, so a save is never allowed to be half a save. The
/// sequence is write-elsewhere, flush to the platter, then swap:
///
/// <list type="number">
/// <item>serialise into <c>name.json.tmp</c>;</item>
/// <item><c>FileStream.Flush(flushToDisk: true)</c>, so the bytes are on the device
/// rather than in the cache — without it a power cut leaves a correctly renamed file
/// full of zeroes;</item>
/// <item><c>File.Replace</c>, which swaps the two directory entries in one operation
/// and keeps the previous contents as <c>name.json.bak</c>.</item>
/// </list>
///
/// Whatever moment a crash lands on, one of the three files is a complete document:
/// the original if the temporary was never finished, the temporary if the swap did
/// not happen, the backup if the swap was interrupted. <see cref="Read{T}"/> tries
/// them in that order, so a corrupt current file costs the last save rather than
/// everything.
/// </remarks>
public static class AtomicJsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // A note or an annotation is Russian text; escaping every Cyrillic letter
        // would quadruple the file and make it unreadable to anyone inspecting it.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Loads a document, falling back to the backup when the current file is unreadable.
    /// </summary>
    /// <returns>Null when nothing readable exists, which is also the first-run case.</returns>
    public static T? Read<T>(string path) where T : class
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(candidate);
                if (string.IsNullOrWhiteSpace(json))
                {
                    continue;
                }

                var value = JsonSerializer.Deserialize<T>(json, Options);
                if (value is not null)
                {
                    if (!ReferenceEquals(candidate, path))
                    {
                        CompatibilityLogger.Technical("store-recovered-from-backup", Path.GetFileName(path));
                    }

                    return value;
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                CompatibilityLogger.Technical(
                    "store-read-failed",
                    $"file={Path.GetFileName(candidate)} type={exception.GetType().Name}");
            }
        }

        return null;
    }

    /// <summary>Writes a document so that an interrupted save cannot destroy the previous one.</summary>
    public static void Write<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".tmp";
        var backup = path + ".bak";

        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, Options);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            // ignoreMetadataErrors: the swap must not fail because the destination's
            // ACL or compression attribute could not be carried across, which happens
            // on redirected folders.
            File.Replace(temporary, path, backup, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporary, path);
        }
    }

    /// <summary>Removes a document and the files that shadow it.</summary>
    public static void Delete(string path)
    {
        foreach (var candidate in new[] { path, path + ".bak", path + ".tmp" })
        {
            try
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                CompatibilityLogger.Technical("store-delete-failed", Path.GetFileName(candidate));
            }
        }
    }
}
