using WriteLite.Models;
using WriteLite.Services;

namespace WriteLite.Tests;

[TestClass]
public sealed class CorrectionApplicationServiceTests
{
    [TestMethod]
    public void ResolveRange_ExactMatch()
    {
        var issue = new TextIssue(5, 4, "тест", "тест!", "t", "e", IssueCategory.Orthography, IssueSeverity.Error);
        var (ok, status, start, length) = CorrectionApplicationService.ResolveRange("xxxx тест yyyy", issue);
        Assert.IsTrue(ok);
        Assert.AreEqual(CorrectionApplicationStatus.CorrectionApplied, status);
        Assert.AreEqual(5, start);
        Assert.AreEqual(4, length);
    }

    [TestMethod]
    public void ResolveRange_DoesNotRelocateStaleOriginal()
    {
        const string live = "начало ошибка конец";
        var issue = new TextIssue(0, 6, "ошибка", "правка", "t", "e", IssueCategory.Orthography, IssueSeverity.Error);
        var (ok, status, start, length) = CorrectionApplicationService.ResolveRange(live, issue);
        Assert.IsFalse(ok);
        Assert.AreEqual(CorrectionApplicationStatus.OriginalMismatch, status);
        Assert.AreEqual(-1, start);
        Assert.AreEqual(0, length);
    }

    [TestMethod]
    public void ResolveRange_StaleDuplicateIsRejectedWithoutAmbiguousRelocation()
    {
        const string live = "ошибка и ошибка";
        var issue = new TextIssue(0, 6, "ошибка", "правка", "t", "e", IssueCategory.Orthography, IssueSeverity.Error);
        // First match at 0 is exact — should use exact without ambiguous.
        var exact = CorrectionApplicationService.ResolveRange(live, issue);
        Assert.IsTrue(exact.Ok);
        Assert.AreEqual(0, exact.Start);

        // A stale range is rejected; WriteLite never guesses between duplicates.
        var stale = new TextIssue(3, 6, "ошибка", "правка", "t", "e", IssueCategory.Orthography, IssueSeverity.Error);
        var relocated = CorrectionApplicationService.ResolveRange(live, stale);
        Assert.IsFalse(relocated.Ok);
        Assert.AreEqual(CorrectionApplicationStatus.OriginalMismatch, relocated.Status);
    }

    [TestMethod]
    public void SnapshotValidation_RejectsStaleTargetGenerationVersionAndText()
    {
        Assert.AreEqual(
            CorrectionApplicationStatus.CorrectionApplied,
            Validate("target", 7, 12, "text", "target", 7, 12, "text"));
        Assert.AreEqual(
            CorrectionApplicationStatus.CorrectionStale,
            Validate("target", 7, 12, "text", "other", 7, 12, "text"));
        Assert.AreEqual(
            CorrectionApplicationStatus.CorrectionStale,
            Validate("target", 7, 12, "text", "target", 8, 12, "text"));
        Assert.AreEqual(
            CorrectionApplicationStatus.CorrectionStale,
            Validate("target", 7, 12, "text", "target", 7, 13, "text"));
        Assert.AreEqual(
            CorrectionApplicationStatus.CorrectionStale,
            Validate("target", 7, 12, "text", "target", 7, 12, "changed"));
    }

    [TestMethod]
    public void ResolveRange_RejectsInvalidRangeAndNonEmptyInsertOriginal()
    {
        var invalid = new TextIssue(50, 3, "abc", "x", "t", "e", IssueCategory.Orthography, IssueSeverity.Error);
        var invalidResult = CorrectionApplicationService.ResolveRange("short", invalid);
        Assert.IsFalse(invalidResult.Ok);
        Assert.AreEqual(CorrectionApplicationStatus.RangeUnavailable, invalidResult.Status);

        var malformedInsert = invalid with { Start = 5, Length = 0, Original = "abc", Replacement = "." };
        var insertResult = CorrectionApplicationService.ResolveRange("short", malformedInsert);
        Assert.IsFalse(insertResult.Ok);
        Assert.AreEqual(CorrectionApplicationStatus.OriginalMismatch, insertResult.Status);
    }

    [TestMethod]
    public void NormalizePunctuation_LetterPlusDot_BecomesInsert()
    {
        var issue = new TextIssue(10, 1, "а", "а.", "Точка", "e", IssueCategory.Punctuation, IssueSeverity.Suggestion);
        var n = CorrectionPresentation.NormalizeForApply(issue);
        Assert.AreEqual(0, n.Length);
        Assert.AreEqual(11, n.Start);
        Assert.AreEqual(".", n.Replacement);
        Assert.AreEqual("Добавить точку", CorrectionPresentation.FormatChange(n));
    }

    [TestMethod]
    public void TerminalInsert_CanApplyManual_AtEnd()
    {
        const string text = "Это достаточно длинное тестовое предложение";
        var issue = new TextIssue(text.Length, 0, "", ".", "Точка", "e", IssueCategory.Punctuation, IssueSeverity.Suggestion, CanApplyAutomatically: true);
        Assert.IsTrue(TextCorrectionService.CanApplyManual(issue, text, supportsDirectWrite: true));
        Assert.IsTrue(TextCorrectionService.TryApplyManual(text, issue, true, out var newText, out var caret));
        Assert.AreEqual(text + ".", newText);
        Assert.AreEqual(text.Length + 1, caret);
    }

    [TestMethod]
    public void RuleBased_TerminalPunctuation_IsZeroLengthInsert()
    {
        var analyzer = new RuleBasedAnalyzer();
        var text = "Это достаточно длинное тестовое предложение без точки в конце";
        var issues = analyzer.Analyze(text);
        var terminal = issues.FirstOrDefault(i => i.RuleId.Contains("terminal", StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(terminal);
        Assert.AreEqual(0, terminal!.Length);
        Assert.AreEqual(".", terminal.Replacement);
        Assert.IsFalse(terminal.Original.Contains('.'));
        Assert.DoesNotContain(terminal.Original + " →", CorrectionPresentation.FormatChange(terminal));
        Assert.Contains("точку", CorrectionPresentation.FormatChange(terminal), StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void SingleInstance_MutexName_IsStable()
    {
        Assert.AreEqual(@"Global\WriteLite.Application.SingleInstance", SingleInstanceService.MutexName);
        Assert.AreEqual("SHOW_MAIN_WINDOW", SingleInstanceService.ShowMainWindowCommand);
    }

    [TestMethod]
    public void CircuitBreaker_OpensAndResets()
    {
        var b = new UiaCircuitBreaker();
        Assert.IsFalse(b.IsOpen("t1"));
        b.Trip("t1", TimeSpan.FromMilliseconds(50));
        Assert.IsTrue(b.IsOpen("t1"));
        Thread.Sleep(80);
        Assert.IsFalse(b.IsOpen("t1"));
    }

    [TestMethod]
    public void Presentation_DoesNotShowLetterArrowLetterDot()
    {
        var issue = new TextIssue(0, 1, "а", "а.", "t", "e", IssueCategory.Punctuation, IssueSeverity.Suggestion);
        var label = CorrectionPresentation.FormatChange(issue);
        Assert.DoesNotContain("а → а.", label);
        Assert.Contains("точк", label, StringComparison.OrdinalIgnoreCase);
    }

    private static CorrectionApplicationStatus Validate(
        string snapshotTarget,
        int snapshotGeneration,
        long snapshotVersion,
        string snapshotText,
        string? currentTarget,
        int currentGeneration,
        long currentVersion,
        string? currentText)
        => CorrectionApplicationService.ValidateSnapshotState(
            snapshotTarget,
            snapshotGeneration,
            snapshotVersion,
            snapshotText,
            currentTarget,
            currentGeneration,
            currentVersion,
            currentText);
}
