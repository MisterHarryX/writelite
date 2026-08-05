namespace WriteLite.Services.Spelling;

public interface ISpellDictionary
{
    bool IsFullDictionaryLoaded { get; }

    string Language { get; }

    DictionaryMetadata Metadata { get; }

    bool Check(string word);

    IReadOnlyList<string> Suggest(string word);
}

public sealed record DictionaryMetadata(
    string Source,
    string Version,
    string License,
    string LicenseFile,
    int WordCount,
    DateTimeOffset? VersionDate,
    int SchemaVersion = 1,
    string Language = "ru",
    string? Sha256 = null);
