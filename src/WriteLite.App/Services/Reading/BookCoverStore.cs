using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WriteLite.Services.Storage;

namespace WriteLite.Services.Reading;

/// <summary>Why a cover could not be set. <see cref="None"/> means it was.</summary>
public enum CoverFailure
{
    None,
    FileMissing,
    FileTooLarge,
    NotAnImage,
    CouldNotWrite
}

/// <summary>The outcome of setting a cover.</summary>
public readonly record struct CoverResult(bool Succeeded, CoverFailure Failure, int Version)
{
    public static CoverResult Ok(int version) => new(true, CoverFailure.None, version);

    public static CoverResult Failed(CoverFailure failure) => new(false, failure, 0);

    /// <summary>What to tell the reader. Plain, and never the exception text.</summary>
    public string Message => Failure switch
    {
        CoverFailure.None => string.Empty,
        CoverFailure.FileMissing => "Файл изображения не найден.",
        CoverFailure.FileTooLarge => "Файл слишком большой. Выберите изображение меньше 64 МБ.",
        CoverFailure.NotAnImage => "Не удалось прочитать изображение. Поддерживаются PNG и JPEG.",
        CoverFailure.CouldNotWrite => "Не удалось сохранить обложку.",
        _ => "Не удалось установить обложку."
    };
}

/// <summary>
/// The cover images readers choose for their books.
/// </summary>
/// <remarks>
/// <para><b>The picture is copied, not referenced.</b> Storing the path someone picked would
/// make the shelf depend on a file WriteLite does not own: covers would vanish when the image
/// was moved out of Downloads, renamed, or deleted after being used — and the reader would
/// have no idea why, because nothing they did was to the book. A copy under the application's
/// own data directory is a few hundred kilobytes and belongs to the library for as long as
/// the library does.</para>
///
/// <para><b>Everything is re-encoded to PNG at a bounded size.</b> The input is decoded,
/// scaled so its longest side is at most <see cref="MaximumEdge"/>, and written out fresh.
/// That normalises three problems at once: a 40-megapixel photograph does not become a
/// 40-megapixel decode every time the shelf is drawn; a file whose extension disagrees with
/// its content is caught by the decode rather than by whatever reads it later; and
/// transparency survives, which JPEG would have flattened.</para>
///
/// <para><b>A failure is a message, never an exception reaching the reader.</b> A truncated
/// download, a file that is not an image, a directory that cannot be written — each returns a
/// <see cref="CoverResult"/> saying which, and the book keeps whatever cover it had.</para>
/// </remarks>
public sealed class BookCoverStore
{
    /// <summary>Longest side of a stored cover, in pixels.</summary>
    /// <remarks>
    /// A shelf card shows a cover about 300 px wide, so this is comfortable at 200% display
    /// scaling and on a future larger card, without keeping a print-resolution scan of a
    /// dust jacket in the data directory.
    /// </remarks>
    private const int MaximumEdge = 1200;

    /// <summary>
    /// Largest input file that will be opened at all, before anything decodes it.
    /// </summary>
    /// <remarks>
    /// A decoder is a parser reading a file the application did not produce. Refusing an
    /// implausible one by its size costs nothing and is checked before the bytes are touched.
    /// </remarks>
    private const long MaximumSourceBytes = 64L * 1024 * 1024;

    private readonly string _root;

    public BookCoverStore(string? root = null)
        => _root = root ?? Path.Combine(WriteLiteDataPaths.ReadingRoot, "covers");

    /// <summary>Where a project's cover lives, whether or not one has been set.</summary>
    public string PathFor(string projectId) => Path.Combine(_root, $"{projectId}.png");

    /// <summary>True when this project has a cover.</summary>
    public bool Has(string projectId) =>
        !string.IsNullOrWhiteSpace(projectId) && File.Exists(PathFor(projectId));

    /// <summary>
    /// Copies an image in as the project's cover, replacing any previous one.
    /// </summary>
    /// <param name="previousVersion">
    /// The project's current cover version; the returned version is one higher. It exists
    /// because WPF caches decoded images by URI, and a replacement cover written to the same
    /// path would otherwise keep showing the old picture until the application restarted.
    /// </param>
    public CoverResult Set(string projectId, string sourcePath, int previousVersion)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return CoverResult.Failed(CoverFailure.CouldNotWrite);
        }

        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return CoverResult.Failed(CoverFailure.FileMissing);
        }

        long length;
        try
        {
            length = new FileInfo(sourcePath).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return CoverResult.Failed(CoverFailure.FileMissing);
        }

        if (length == 0)
        {
            return CoverResult.Failed(CoverFailure.NotAnImage);
        }

        if (length > MaximumSourceBytes)
        {
            return CoverResult.Failed(CoverFailure.FileTooLarge);
        }

        BitmapSource decoded;
        try
        {
            decoded = Decode(sourcePath);
        }
        catch (Exception exception) when (exception is NotSupportedException
                                              or FileFormatException
                                              or ArgumentException
                                              or OverflowException
                                              or IOException)
        {
            // Every one of these is "this file is not a picture WriteLite can read", which is
            // the reader's answer regardless of which of them the decoder chose to throw.
            CompatibilityLogger.Technical("book-cover-decode-failed", $"type={exception.GetType().Name}");
            return CoverResult.Failed(CoverFailure.NotAnImage);
        }

        try
        {
            Directory.CreateDirectory(_root);
            WriteAtomically(PathFor(projectId), decoded);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CompatibilityLogger.Technical("book-cover-save-failed", $"type={exception.GetType().Name}");
            return CoverResult.Failed(CoverFailure.CouldNotWrite);
        }

        return CoverResult.Ok(Math.Max(1, previousVersion + 1));
    }

    /// <summary>Removes a project's cover. Absent is success: the outcome asked for holds.</summary>
    public bool Remove(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return false;
        }

        try
        {
            var path = PathFor(projectId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CompatibilityLogger.Technical("book-cover-delete-failed", $"type={exception.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// The cover ready to draw, or null when there is none or it will not load.
    /// </summary>
    /// <remarks>
    /// Loaded with <see cref="BitmapCacheOption.OnLoad"/> and frozen: the file handle is
    /// closed before this returns, so replacing or deleting the cover is never blocked by the
    /// shelf still having it open, and the image can be handed to the UI thread from anywhere.
    /// </remarks>
    public BitmapImage? Load(string projectId)
    {
        var path = PathFor(projectId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is NotSupportedException
                                              or FileFormatException
                                              or IOException
                                              or UriFormatException)
        {
            // A cover file that has become unreadable costs this book its picture, not the
            // shelf: every other card still draws.
            CompatibilityLogger.Technical("book-cover-load-failed", $"type={exception.GetType().Name}");
            return null;
        }
    }

    private static BitmapSource Decode(string sourcePath)
    {
        using var stream = File.OpenRead(sourcePath);
        var frame = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad)
            .Frames[0];

        var longest = Math.Max(frame.PixelWidth, frame.PixelHeight);
        if (longest <= MaximumEdge || longest == 0)
        {
            frame.Freeze();
            return frame;
        }

        // One scale factor for both axes: an unusual aspect ratio is the reader's choice and
        // is preserved here. How it is fitted into a card is the card's business.
        var scale = (double)MaximumEdge / longest;
        var scaled = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    /// <summary>
    /// Writes the cover beside its destination and moves it into place.
    /// </summary>
    /// <remarks>
    /// The same reasoning as <c>AtomicJsonFile</c>: encoding straight over the existing file
    /// means a crash or a full disk halfway through leaves a truncated PNG where a valid one
    /// was, and the reader loses the cover they already had in exchange for the one that
    /// failed.
    /// </remarks>
    private static void WriteAtomically(string path, BitmapSource image)
    {
        var temporary = path + ".tmp";

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));

        using (var output = File.Create(temporary))
        {
            encoder.Save(output);
        }

        File.Move(temporary, path, overwrite: true);
    }
}
