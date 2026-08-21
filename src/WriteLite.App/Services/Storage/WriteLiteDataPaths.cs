using System.IO;

namespace WriteLite.Services.Storage;

/// <summary>
/// Where WriteLite keeps the things a person made.
/// </summary>
/// <remarks>
/// Everything sits under <c>%LOCALAPPDATA%\WriteLite</c>, beside the settings and
/// recent-documents files that were already there. Never the installation directory:
/// it is read-only for a standard user, it is wiped by an upgrade, and it is the
/// wrong place for data that outlives the version that created it.
///
/// Roaming is deliberately not used. A reading project points at a book by absolute
/// path and a note can be long; neither belongs in a profile that syncs over a
/// network share.
/// </remarks>
public static class WriteLiteDataPaths
{
    /// <summary>The application data root, created on demand.</summary>
    public static string Root { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WriteLite");

    /// <summary>Redirects every store to another root. Tests only.</summary>
    internal static void OverrideRoot(string root) => Root = root;

    public static string NotesFile => Path.Combine(Root, "notes.json");

    public static string ReadingRoot => Path.Combine(Root, "reading");

    public static string ReadingLibraryFile => Path.Combine(ReadingRoot, "library.json");

    public static string ReadingProjectsRoot => Path.Combine(ReadingRoot, "projects");

    public static string ReadingProjectFile(string projectId) =>
        Path.Combine(ReadingProjectsRoot, $"{projectId}.json");
}
