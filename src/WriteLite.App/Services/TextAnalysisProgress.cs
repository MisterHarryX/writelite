namespace WriteLite.Services;

/// <summary>
/// Coverage of one analysis request. Character counts use .NET string indexes
/// (UTF-16 code units), the same coordinate system as <c>TextIssue.Start</c>.
/// </summary>
public sealed record TextAnalysisProgress(
    string Text,
    int CheckedCharacters,
    int TotalCharacters,
    bool IsComplete,
    string? IncompleteReason = null)
{
    public double Fraction => TotalCharacters <= 0
        ? (IsComplete ? 1d : 0d)
        : Math.Clamp((double)CheckedCharacters / TotalCharacters, 0d, 1d);

    public static TextAnalysisProgress Pending(string text)
        => new(text, 0, text?.Length ?? 0, IsComplete: false);

    public static TextAnalysisProgress Complete(string text)
        => new(text, text?.Length ?? 0, text?.Length ?? 0, IsComplete: true);
}

/// <summary>Optional capability for analyzers that can report long-document coverage.</summary>
public interface ITextAnalysisProgressSource
{
    event EventHandler<TextAnalysisProgress>? AnalysisProgressChanged;

    TextAnalysisProgress GetProgress(string text);
}
