using System.Text.Json.Serialization;

namespace WriteLite.Services.Reading;

/// <summary>
/// Where something is in a book.
/// </summary>
/// <remarks>
/// Two coordinates, on purpose. <see cref="Start"/> is a character offset into the
/// document's flattened plain text, which is exact and cheap to jump to.
/// <see cref="Quote"/> is the text that was there, which is what lets a mark survive
/// the source file being re-exported, re-paginated or lightly edited: on open the
/// reader checks the offset first and, when the text no longer matches, searches
/// outwards from it for the quote. A mark that cannot be re-found is kept and shown
/// as detached rather than deleted — losing someone's annotation because a publisher
/// shipped a new PDF is not an acceptable outcome.
/// </remarks>
public sealed class ReadingAnchor
{
    public int Start { get; set; }

    public int Length { get; set; }

    /// <summary>The exact passage, as it read when the mark was made.</summary>
    public string Quote { get; set; } = string.Empty;

    /// <summary>Index of the paragraph the mark starts in, for a coarse fallback.</summary>
    public int BlockIndex { get; set; }

    [JsonIgnore]
    public int End => Start + Length;

    public ReadingAnchor Copy() => new()
    {
        Start = Start,
        Length = Length,
        Quote = Quote,
        BlockIndex = BlockIndex
    };
}

/// <summary>
/// The four marker tints.
/// </summary>
/// <remarks>
/// Named rather than stored as hex so they follow the theme, and restrained to the
/// palette WriteLite already uses — the amber accent, the sage and coral of the
/// status colours, and a neutral. A highlighter yellow would be the one saturated
/// thing in the entire product.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<HighlightTint>))]
public enum HighlightTint
{
    Amber,
    Sage,
    Coral,
    Neutral
}

public sealed class Highlight
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public ReadingAnchor Anchor { get; set; } = new();

    public HighlightTint Tint { get; set; } = HighlightTint.Amber;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>A passage plus what the reader thought about it.</summary>
public sealed class Annotation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public ReadingAnchor Anchor { get; set; } = new();

    /// <summary>The reader's own words. The reason the annotation exists.</summary>
    public string Note { get; set; } = string.Empty;

    public HighlightTint Tint { get; set; } = HighlightTint.Amber;

    public string? Tag { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class Bookmark
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Character offset of the reading position that was saved.</summary>
    public int Offset { get; set; }

    /// <summary>0–1 through the document, kept so the list can show progress without reloading it.</summary>
    public double Fraction { get; set; }

    /// <summary>A line of the text at that position, so the list reads as places rather than as numbers.</summary>
    public string Preview { get; set; } = string.Empty;

    public string? Title { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore]
    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(Title) ? (Preview.Length > 0 ? Preview : "Закладка") : Title!;
}

/// <summary>A two-sided card made from a passage.</summary>
public sealed class StudyCard
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Front { get; set; } = string.Empty;

    public string Back { get; set; } = string.Empty;

    /// <summary>Where in the book the card came from. Null for a card typed from scratch.</summary>
    public ReadingAnchor? Source { get; set; }

    /// <summary>True when the local model drafted it, so the list can say so.</summary>
    public bool DraftedByAi { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>A word the reader marked while reading, so the dictionary walk is retraceable.</summary>
public sealed class InterestingWord
{
    public string Word { get; set; } = string.Empty;

    public int Offset { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>How the reader is set up for this book. Per project, because a PDF and a novel do not want the same measure.</summary>
public sealed class ReaderTypography
{
    public double FontSize { get; set; } = 17;

    public double LineSpacing { get; set; } = 1.7;

    /// <summary>Content measure in device-independent pixels. Around 34 em is the readable band.</summary>
    public double ContentWidth { get; set; } = 720;

    public static ReaderTypography Default => new();

    public ReaderTypography Clamped() => new()
    {
        FontSize = Math.Clamp(FontSize, 12, 32),
        LineSpacing = Math.Clamp(LineSpacing, 1.2, 2.6),
        ContentWidth = Math.Clamp(ContentWidth, 480, 1400)
    };
}

/// <summary>
/// One book and everything the reader has done with it.
/// </summary>
/// <remarks>
/// Identity is the fingerprint, not the path. Opening the same file from a different
/// folder, or after it has been renamed, has to land in the project that already has
/// the highlights in it — creating a second empty project every time a book moves is
/// the failure this design exists to prevent.
/// </remarks>
public sealed class ReadingProject
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = string.Empty;

    /// <summary>Last known location of the source file. Updated whenever the book is found somewhere new.</summary>
    public string SourcePath { get; set; } = string.Empty;

    public string SourceFileName { get; set; } = string.Empty;

    /// <summary>Content hash. Two files with this value are the same book.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    public long SourceLength { get; set; }

    public string SourceFormat { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset LastOpenedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Where reading stopped, kept whether or not a bookmark was ever made.</summary>
    public int Position { get; set; }

    /// <summary>0–1 through the document at <see cref="Position"/>.</summary>
    public double Progress { get; set; }

    /// <summary>Characters in the flattened text, so progress can be recomputed after a reload.</summary>
    public int TextLength { get; set; }

    public List<Highlight> Highlights { get; set; } = [];

    public List<Annotation> Annotations { get; set; } = [];

    public List<Bookmark> Bookmarks { get; set; } = [];

    public List<StudyCard> Cards { get; set; } = [];

    public List<InterestingWord> Words { get; set; } = [];

    public ReaderTypography Typography { get; set; } = ReaderTypography.Default;

    /// <summary>
    /// How many times a cover has been chosen for this book. Zero means none ever was.
    /// </summary>
    /// <remarks>
    /// A counter rather than a flag, because the cover always lives at the same path and WPF
    /// caches decoded images by URI: without something that changes, replacing a cover would
    /// go on showing the previous picture until the application was restarted. Absent from an
    /// older project file, where it reads as zero — which is correct, since no cover was ever
    /// set. See <see cref="BookCoverStore"/>.
    /// </remarks>
    public int CoverVersion { get; set; }

    /// <summary>The line under the project title: «12 пометок · 4 закладки · 18 карточек · 37% прочитано».</summary>
    public string Summary()
    {
        var parts = new List<string>
        {
            Plural(Annotations.Count, "пометка", "пометки", "пометок"),
            Plural(Highlights.Count, "выделение", "выделения", "выделений"),
            Plural(Bookmarks.Count, "закладка", "закладки", "закладок"),
            Plural(Cards.Count, "карточка", "карточки", "карточек"),
            $"{(int)Math.Round(Math.Clamp(Progress, 0, 1) * 100)}% прочитано"
        };

        return string.Join(" · ", parts);
    }

    private static string Plural(int count, string one, string few, string many) =>
        $"{count} {RussianPlural.Form(count, one, few, many)}";
}

/// <summary>The library index row for one project.</summary>
/// <remarks>
/// A separate, small record so the reading home screen can list twenty books without
/// deserialising twenty documents of highlights.
/// </remarks>
public sealed class ReadingProjectSummary
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public string SourceFileName { get; set; } = string.Empty;

    public string Fingerprint { get; set; } = string.Empty;

    public string SourceFormat { get; set; } = string.Empty;

    public DateTimeOffset LastOpenedAt { get; set; } = DateTimeOffset.Now;

    public double Progress { get; set; }

    public int HighlightCount { get; set; }

    public int AnnotationCount { get; set; }

    public int BookmarkCount { get; set; }

    public int CardCount { get; set; }

    /// <inheritdoc cref="ReadingProject.CoverVersion"/>
    public int CoverVersion { get; set; }

    /// <summary>The counts line shown on the library card.</summary>
    public string Details()
    {
        var parts = new List<string>
        {
            $"{AnnotationCount} {RussianPlural.Form(AnnotationCount, "пометка", "пометки", "пометок")}",
            $"{BookmarkCount} {RussianPlural.Form(BookmarkCount, "закладка", "закладки", "закладок")}",
            $"{CardCount} {RussianPlural.Form(CardCount, "карточка", "карточки", "карточек")}",
            $"{(int)Math.Round(Math.Clamp(Progress, 0, 1) * 100)}% прочитано"
        };

        return string.Join(" · ", parts);
    }

    public static ReadingProjectSummary From(ReadingProject project) => new()
    {
        Id = project.Id,
        Title = project.Title,
        SourcePath = project.SourcePath,
        SourceFileName = project.SourceFileName,
        Fingerprint = project.Fingerprint,
        SourceFormat = project.SourceFormat,
        LastOpenedAt = project.LastOpenedAt,
        Progress = project.Progress,
        HighlightCount = project.Highlights.Count,
        AnnotationCount = project.Annotations.Count,
        BookmarkCount = project.Bookmarks.Count,
        CardCount = project.Cards.Count,
        CoverVersion = project.CoverVersion
    };
}

/// <summary>The library index as it sits on disk.</summary>
public sealed class ReadingLibraryDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<ReadingProjectSummary> Projects { get; set; } = [];
}
