namespace WriteLite.Services.Morphology;

public interface IMorphologyService
{
    MorphologyAnalysis Analyze(string word);
    string? GetLemma(string word);
    string? GetPartOfSpeech(string word);
    IReadOnlyDictionary<string, string> GetGrammaticalFeatures(string word);
    bool IsKnownForm(string word);
}

public sealed record MorphologyAnalysis(
    string Word,
    bool IsKnownForm,
    string? Lemma,
    string? PartOfSpeech,
    IReadOnlyDictionary<string, string> Features);
