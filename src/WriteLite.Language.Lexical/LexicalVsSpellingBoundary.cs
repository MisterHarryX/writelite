namespace WriteLite.Language.Lexical;

/// <summary>
/// Lexical lookup is definition/synonym oriented and must never
/// be used as the sole source of orthography truth.
/// </summary>
public static class LexicalVsSpellingBoundary
{
    public const string Rule =
        "Absence from a lexical definition pack is not an orthographic error.";
}
