namespace WriteLite.Services.Stylistics;

/// <summary>
/// The register a text is being written in, which decides what counts as a style problem.
/// </summary>
/// <remarks>
/// §18: «заюзать библиотеку» is unremarkable in a technical chat and worth a note in a
/// thesis. §19: the profile changes which suggestions appear — it never changes whether the
/// text is treated as erroneous, and no profile bans slang outright.
/// </remarks>
public enum StyleProfile
{
    General,
    Academic,
    Business,
    Formal,
    Technical,
    Creative,
    Social,
    Student,
}

public static class StyleProfiles
{
    public static StyleProfile Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "academic" or "академический" => StyleProfile.Academic,
        "business" or "деловой" => StyleProfile.Business,
        "formal" or "официальный" => StyleProfile.Formal,
        "technical" or "технический" => StyleProfile.Technical,
        "creative" or "художественный" => StyleProfile.Creative,
        "social" or "неформальный" => StyleProfile.Social,
        "student" or "учебный" => StyleProfile.Student,
        _ => StyleProfile.General,
    };

    /// <summary>
    /// Profiles that expect a neutral written register, and so are the only ones where a
    /// colloquial word is worth mentioning.
    /// </summary>
    public static bool ExpectsNeutralRegister(this StyleProfile profile)
        => profile is StyleProfile.Academic or StyleProfile.Business
            or StyleProfile.Formal or StyleProfile.Student;

    /// <summary>
    /// Profiles where the writer's voice is the point and rhythm outranks concision.
    /// </summary>
    /// <remarks>
    /// §19: creative and casual text must retain individuality. Repetition is a device in
    /// prose and a defect in a report, and a run of short sentences is a style, so the
    /// document-level observations stand down here rather than being tuned differently.
    /// </remarks>
    public static bool ProtectsAuthorVoice(this StyleProfile profile)
        => profile is StyleProfile.Creative or StyleProfile.Social;
}
