using System.IO;
using System.Text.RegularExpressions;

namespace WriteLite.Services.Notes;

/// <summary>
/// The pictures a note carries, one folder per note beside <c>notes.json</c>.
/// </summary>
/// <remarks>
/// <para>Pictures live in files rather than in the JSON. A pasted screenshot is a megabyte
/// or more; base64 inside the notes file would make every autosave rewrite every picture
/// the user ever pasted, and one corrupt write would take them all.</para>
///
/// <para>The store belongs to <see cref="NotesService"/> and is reached only through it,
/// so one object decides when a note's folder is created, copied and removed. Nothing is
/// deleted when a block is removed in the editor — the editor's undo can bring the block
/// back — only when the note itself goes, or at load time, when no undo history exists
/// any more and an unreferenced file can only be garbage.</para>
/// </remarks>
public sealed partial class NoteAssetStore
{
    /// <summary>Formats a picture block can hold; anything else is converted to PNG first.</summary>
    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp" };

    /// <summary>Largest picture accepted. Bigger files are almost always a mistaken drop.</summary>
    public const long MaxAssetBytes = 40L * 1024 * 1024;

    private readonly string _root;

    public NoteAssetStore(string root)
    {
        _root = root;
    }

    public string Root => _root;

    /// <summary>
    /// Copies picture bytes into the note's folder and returns the new asset's id.
    /// </summary>
    /// <exception cref="ArgumentException">The format is not one a picture block can show.</exception>
    /// <exception cref="InvalidOperationException">The picture is larger than <see cref="MaxAssetBytes"/>.</exception>
    public string Import(string noteId, ReadOnlySpan<byte> bytes, string extension)
    {
        ValidateNoteId(noteId);

        var normalized = extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
        if (normalized == ".jpeg")
        {
            normalized = ".jpg";
        }

        if (!SupportedExtensions.Contains(normalized))
        {
            throw new ArgumentException($"Формат {extension} не поддерживается.", nameof(extension));
        }

        if (bytes.Length == 0 || bytes.Length > MaxAssetBytes)
        {
            throw new InvalidOperationException(
                bytes.Length == 0 ? "Файл изображения пуст." : "Изображение больше 40 МБ — такое в заметку не вставить.");
        }

        var id = Guid.NewGuid().ToString("N") + normalized;
        var folder = FolderOf(noteId);
        Directory.CreateDirectory(folder);

        var target = Path.Combine(folder, id);
        var temporary = target + ".tmp";
        File.WriteAllBytes(temporary, bytes.ToArray());
        File.Move(temporary, target);
        return id;
    }

    /// <summary>The file behind an asset, or null when the id is malformed or the file is gone.</summary>
    public string? PathOf(string noteId, string? assetId)
    {
        if (!IsValidAssetId(assetId) || !IsValidNoteId(noteId))
        {
            return null;
        }

        var path = Path.Combine(FolderOf(noteId), assetId!);
        return File.Exists(path) ? path : null;
    }

    /// <summary>The ids stored for a note, whether or not its body still references them.</summary>
    public IReadOnlyList<string> List(string noteId)
    {
        if (!IsValidNoteId(noteId))
        {
            return [];
        }

        var folder = FolderOf(noteId);
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).Select(Path.GetFileName).Where(IsValidAssetId).Cast<string>().ToList()
            : [];
    }

    /// <summary>Copies every picture of one note to another, keeping the ids, for a duplicated note.</summary>
    public void CopyAll(string fromNoteId, string toNoteId)
    {
        ValidateNoteId(fromNoteId);
        ValidateNoteId(toNoteId);

        var source = FolderOf(fromNoteId);
        if (!Directory.Exists(source))
        {
            return;
        }

        var target = FolderOf(toNoteId);
        Directory.CreateDirectory(target);
        foreach (var id in List(fromNoteId))
        {
            File.Copy(Path.Combine(source, id), Path.Combine(target, id), overwrite: true);
        }
    }

    public void DeleteAll(string noteId)
    {
        if (!IsValidNoteId(noteId))
        {
            return;
        }

        var folder = FolderOf(noteId);
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A picture held open by a viewer is retried at the next load's sweep.
            CompatibilityLogger.Technical("notes-assets-delete-failed", exception.GetType().Name);
        }
    }

    /// <summary>
    /// Removes files no note references any more. Load time only; see the class remarks.
    /// </summary>
    /// <returns>How many files were removed.</returns>
    public int Sweep(IReadOnlyDictionary<string, HashSet<string>> referencedByNote)
    {
        if (!Directory.Exists(_root))
        {
            return 0;
        }

        var removed = 0;
        foreach (var folder in Directory.EnumerateDirectories(_root))
        {
            var noteId = Path.GetFileName(folder);
            try
            {
                if (!referencedByNote.TryGetValue(noteId, out var keep))
                {
                    Directory.Delete(folder, recursive: true);
                    removed++;
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    if (!keep.Contains(Path.GetFileName(file)))
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                CompatibilityLogger.Technical("notes-assets-sweep-failed", exception.GetType().Name);
            }
        }

        return removed;
    }

    private string FolderOf(string noteId) => Path.Combine(_root, noteId);

    public static bool IsValidAssetId(string? assetId) => assetId is not null && AssetIdPattern().IsMatch(assetId);

    // Note ids come from the JSON file, which a user can edit by hand; anything that is
    // not a plain id must never be combined into a path that is then deleted recursively.
    private static bool IsValidNoteId(string? noteId) => noteId is not null && NoteIdPattern().IsMatch(noteId);

    private static void ValidateNoteId(string noteId)
    {
        if (!IsValidNoteId(noteId))
        {
            throw new ArgumentException("Недопустимый идентификатор заметки.", nameof(noteId));
        }
    }

    [GeneratedRegex("^[0-9a-f]{32}\\.(png|jpg|gif|bmp)$")]
    private static partial Regex AssetIdPattern();

    [GeneratedRegex("^[0-9A-Za-z_-]{1,64}$")]
    private static partial Regex NoteIdPattern();
}
