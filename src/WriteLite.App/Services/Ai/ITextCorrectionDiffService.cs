using WriteLite.Models;

namespace WriteLite.Services.Ai;

public interface ITextCorrectionDiffService
{
    /// <summary>
    /// Builds discrete TextIssue items from original vs corrected full text.
    /// Prefer local diff over trusting remote offsets for punctuation restoration.
    /// </summary>
    IReadOnlyList<TextIssue> BuildIssues(string original, string corrected);
}
