using WriteLite.Models;
using WriteLite.Services;

namespace WriteLite.Tests.Corrections;

/// <summary>
/// What an open card does as the monitor reports on the field — §4 and §5.
/// </summary>
/// <remarks>
/// Stated against <see cref="CorrectionCardContext"/> rather than a live snapshot, so the
/// decisions can be exercised without a real out-of-process control.
/// <see cref="ExternalFieldCorrectionCardTests"/> then runs the same coordinator against one.
/// </remarks>
[TestClass]
public sealed class CorrectionCardCoordinatorTests
{
    [TestMethod]
    public void ARepublishOfTheSameTextChangesNothing()
    {
        var coordinator = Opened(out var issue);

        Assert.AreEqual(CorrectionCardActionKind.None, coordinator.Observe(Context("он роботает", [issue])).Kind);
        Assert.AreEqual(
            CorrectionCardActionKind.None,
            coordinator.Observe(Context("он роботает", [issue])).Kind,
            "fast and deep passes republish the same text; the card must not flicker");
    }

    [TestMethod]
    public void LosingTheTargetDoesNotDisturbTheCard()
    {
        // The monitor drops its target whenever focus leaves an editable field — which the
        // card's own popup causes. Treating that as staleness is what produced «Текст
        // изменился» for corrections that were perfectly valid.
        var coordinator = Opened(out _);

        Assert.AreEqual(CorrectionCardActionKind.None, coordinator.Observe((TextSnapshot?)null).Kind);
    }

    /// <summary>§4: the announcement shows progress; the finished pass replaces it.</summary>
    [TestMethod]
    public void ATextChangeChecksFirstAndThenRendersTheNewFinding()
    {
        var coordinator = Opened(out _);

        // The monitor sees the edit: no issues yet, request not yet numbered.
        Assert.AreEqual(
            CorrectionCardActionKind.Recheck,
            coordinator.Observe(Context("а он роботает", [], analysisInFlight: true)).Kind);
        Assert.AreEqual("он роботает", coordinator.PinnedText,
            "the pinned text stays put so the finished pass can still map the span forward");

        // The pass finishes and reports the same word at its new offset.
        var action = coordinator.Observe(Context("а он роботает", [Spelling(5, "роботает", "работает")]));

        Assert.AreEqual(CorrectionCardActionKind.Render, action.Kind);
        Assert.AreEqual(5, action.Issue?.Start);
        Assert.AreEqual("а он роботает", coordinator.PinnedText);
    }

    [TestMethod]
    public void TheRenderedFindingIsTheOneTheNewPassProduced()
    {
        var coordinator = Opened(out _);

        var action = coordinator.Observe(Context("а он роботает", [Spelling(5, "роботает", "работал")]));

        Assert.AreEqual("работал", action.Issue?.Replacement,
            "the card shows what is true of the text now, not the old candidate at a new offset");
    }

    [TestMethod]
    public void AFinishedPassWithoutTheFindingClosesTheCard()
    {
        var coordinator = Opened(out _);

        Assert.AreEqual(CorrectionCardActionKind.Dismiss, coordinator.Observe(Context("он работает", [])).Kind);
    }

    [TestMethod]
    public void AnotherFieldClosesTheCard()
    {
        var coordinator = Opened(out var issue);

        Assert.AreEqual(
            CorrectionCardActionKind.Dismiss,
            coordinator.Observe(Context("он роботает", [issue], targetId: "another-field")).Kind);
    }

    /// <summary>§9: a run of edits always leaves the card on the newest text.</summary>
    [TestMethod]
    public void ARunOfEditsLeavesTheCardOnTheNewestText()
    {
        var coordinator = new CorrectionCardCoordinator();
        coordinator.Open(
            Context("роботает", [Spelling(0, "роботает", "работает")]),
            Spelling(0, "роботает", "работает"));

        foreach (var prefix in new[] { "а ", "аб ", "абв ", "абвг " })
        {
            var text = prefix + "роботает";
            coordinator.Observe(Context(text, [], analysisInFlight: true));
            coordinator.Observe(Context(text, [Spelling(prefix.Length, "роботает", "работает")]));
        }

        Assert.AreEqual("абвг роботает", coordinator.PinnedText);
        Assert.AreEqual(5, coordinator.PinnedIssue?.Start);
    }

    /// <summary>
    /// The announcement must be recognised from the request number, not the indicator.
    /// </summary>
    /// <remarks>
    /// The monitor rewrites <see cref="TextSnapshot.IndicatorState"/> to
    /// <see cref="AnalysisIndicatorState.Hidden"/> whenever the main UI is suppressed, so the
    /// indicator alone would make an announcement look like a finished, clean pass — and the
    /// card would close instead of refreshing.
    /// </remarks>
    [TestMethod]
    public void AnUnnumberedPublishIsAnAnnouncementWhateverItsIndicatorSays()
    {
        Assert.IsTrue(CorrectionCardCoordinator.IsAnalysisInFlight(
            Snapshot(requestId: 0, AnalysisIndicatorState.Hidden)));
        Assert.IsTrue(CorrectionCardCoordinator.IsAnalysisInFlight(
            Snapshot(requestId: 0, AnalysisIndicatorState.FastAnalyzing)));
        Assert.IsTrue(CorrectionCardCoordinator.IsAnalysisInFlight(
            Snapshot(requestId: 4, AnalysisIndicatorState.DeepAnalyzing)));

        Assert.IsFalse(CorrectionCardCoordinator.IsAnalysisInFlight(
            Snapshot(requestId: 4, AnalysisIndicatorState.NoErrors)),
            "a numbered pass over clean text is a finished result, not an announcement");
    }

    [TestMethod]
    public void AClosedCardObservesNothing()
    {
        var coordinator = Opened(out _);
        coordinator.Close();

        Assert.IsFalse(coordinator.IsOpen);
        Assert.AreEqual(CorrectionCardActionKind.None, coordinator.Observe(Context("совсем другое", [])).Kind);
    }

    private static CorrectionCardCoordinator Opened(out TextIssue issue)
    {
        issue = Spelling(3, "роботает", "работает");
        var coordinator = new CorrectionCardCoordinator();
        coordinator.Open(Context("он роботает", [issue]), issue);
        return coordinator;
    }

    private static CorrectionCardContext Context(
        string text,
        IReadOnlyList<TextIssue> issues,
        bool analysisInFlight = false,
        string targetId = "field-1") => new(targetId, text, issues, analysisInFlight);

    /// <summary>
    /// A snapshot for the in-flight test only. <c>Target</c> is never touched on this path,
    /// which is what lets the flag be checked without a live automation element.
    /// </summary>
    private static TextSnapshot Snapshot(int requestId, AnalysisIndicatorState indicator) =>
        new(null!, "текст", [], DateTimeOffset.Now, RequestId: requestId, IndicatorState: indicator);

    internal static TextIssue Spelling(int start, string original, string replacement) => new(
        start, original.Length, original, replacement,
        "Орфография", "Слово не найдено в русском орфографическом словаре.",
        IssueCategory.Orthography, IssueSeverity.Warning,
        CanApplyAutomatically: false, RuleId: "ru.spelling.typo");
}
