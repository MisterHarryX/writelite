using System.IO;
using System.Text.Json;
using WriteLite.Services.Storage;

namespace WriteLite.Services.Audio;

/// <summary>
/// What the user added to «Музыка»: folders (scanned with their subfolders) and single files,
/// plus favourites. Stored in %LOCALAPPDATA%\WriteLite\music-library.json.
/// </summary>
/// <remarks>
/// Paths only — no copies of the music. A folder is rescanned on every start, so songs added to
/// it in Explorer appear on their own; a file that no longer exists simply drops out.
/// </remarks>
public sealed class MusicLibrary
{
    public List<string> Folders { get; set; } = [];

    public List<string> Files { get; set; } = [];

    public List<string> Favorites { get; set; } = [];

    public string? LastTrack { get; set; }

    public double LastPositionSeconds { get; set; }

    public bool Shuffle { get; set; }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WriteLite", "music-library.json");

    public static MusicLibrary Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<MusicLibrary>(File.ReadAllText(path)) ?? new MusicLibrary();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            CompatibilityLogger.Technical("music-library-load-failed", $"type={ex.GetType().Name}");
        }

        return new MusicLibrary();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicJsonFile.WriteSerialized(path, JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CompatibilityLogger.Technical("music-library-save-failed", $"type={ex.GetType().Name}");
        }
    }
}
