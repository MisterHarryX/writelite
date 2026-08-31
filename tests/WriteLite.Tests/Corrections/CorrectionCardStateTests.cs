using WriteLite.Models;
using WriteLite.Services;

namespace WriteLite.Tests.Corrections;

/// <summary>
/// The invariants that make the reported card impossible.
/// </summary>
/// <remarks>
/// The defect was a card showing, at the same moment: a finished dictionary explanation, a
/// «Проверяем → Проверяем» change row, an orange apply action, a red «Текст изменился»
/// warning and a «Повторить» button. Five assertions about five different moments. These
/// tests hold the phases apart at the level where the decision is made, so the window
/// rendering them has nothing left to get wrong.
/// </remarks>
[TestClass]
public sealed class CorrectionCardStateTests
{
    [TestMethod]
    public void AnIdenticalSuggestionIsNotACorrection()
    {
        var state = CorrectionCardState.ForIssue(Spelling("Проверяем", "Проверяем"));

        Assert.AreEqual(CorrectionCardPhase.NoSuggestion, state.Phase);
        Assert.IsFalse(state.ShowChange, "there is no change to draw");
        Assert.IsFalse(state.ShowPrimaryAction, "nothing to apply, so no apply action");
        Assert.AreEqual("Слово не найдено в словаре", state.Headline);
        Assert.IsTrue(state.ShowDictionary);
        Assert.IsTrue(state.ShowIgnore);
    }

    /// <summary>
    /// The reproduction. Two strings that differ, and render as one.
    /// </summary>
    /// <remarks>
    /// <para>Every rendering path already dropped findings where <c>Original</c> and
    /// <c>Replacement</c> are equal, which is why the card in the screenshot is surprising.
    /// The card is not drawn from those strings: <see cref="CorrectionCardText"/> strips
    /// analyzer-injected emphasis markers, folds CR/LF and tabs to a single glyph each, and
    /// truncates past 80 characters to a shared ellipsis. Each of those can turn two
    /// different strings into one fragment, and the data-level check cannot see it — so the
    /// card drew «Проверяем → Проверяем» with an apply button underneath, promising a change
    /// that pressing it could not make.</para>
    ///
    /// <para>Every case below reached the old card as a correction.</para>
    /// </remarks>
    [TestMethod]
    [DataRow("Проверяем", "**Проверяем**", DisplayName = "emphasis markers stripped for display")]
    [DataRow("Проверяем", "__Проверяем__", DisplayName = "underscore emphasis stripped for display")]
    [DataRow("Проверяем", "~~Проверяем~~", DisplayName = "strikethrough markers stripped for display")]
    public void AReplacementThatOnlyDiffersWhereTheCardCannotShowItIsNotACorrection(
        string original,
        string replacement)
    {
        Assert.IsFalse(
            WriteLite.Language.Core.CorrectionCandidateValidityPolicy.IsIdenticalCorrection(original, replacement),
            "precondition: the data-level check passes this pair, which is how it reached the card");

        var state = CorrectionCardState.ForIssue(Spelling(original, replacement));

        Assert.AreEqual(CorrectionCardPhase.NoSuggestion, state.Phase);
        Assert.IsFalse(state.ShowChange);
        Assert.IsFalse(state.ShowPrimaryAction);
    }

    [TestMethod]
    public void ALongReplacementThatDiffersPastTheTruncationCapIsNotACorrection()
    {
        // The card shows at most 80 characters and then an ellipsis; a difference after that
        // point is invisible, so both sides render as the same fragment.
        var original = new string('а', 100) + "первый";
        var replacement = new string('а', 100) + "второй";

        var state = CorrectionCardState.ForIssue(Spelling(original, replacement));

        Assert.AreEqual(CorrectionCardPhase.NoSuggestion, state.Phase);
        Assert.IsFalse(state.ShowPrimaryAction);
    }

    [TestMethod]
    public void ARealCorrectionKeepsItsChangeRowAndOneAction()
    {
        var state = CorrectionCardState.ForIssue(Spelling("роботает", "работает"));

        Assert.AreEqual(CorrectionCardPhase.Result, state.Phase);
        Assert.IsTrue(state.ShowChange);
        Assert.AreEqual("роботает", state.OriginalDisplay);
        Assert.AreEqual("работает", state.ReplacementDisplay);
        Assert.IsTrue(state.ShowPrimaryAction);
        Assert.IsFalse(state.ShowRetry, "nothing has failed");
        Assert.IsFalse(state.ShowStatus);
        Assert.IsFalse(state.ShowProgress);
    }

    /// <summary>§7: the large action names an action, never a state of the application.</summary>
    [TestMethod]
    public void ThePrimaryActionIsAVerbAndNeverTheWordItself()
    {
        Assert.AreEqual("Исправить", CorrectionCardState.ForIssue(Spelling("Проверяем", "Проверям")).PrimaryActionLabel);
        Assert.AreEqual("Исправить", CorrectionCardState.ForIssue(Spelling("роботает", "работает")).PrimaryActionLabel);

        // An insertion has no "before" word, so its own phrasing is already the action.
        var insert = new TextIssue(
            8, 0, string.Empty, ",", "Пунктуация", "Нужна запятая",
            IssueCategory.Punctuation, IssueSeverity.Warning, RuleId: "ru.punctuation.comma");
        Assert.AreEqual("Добавить запятую", CorrectionCardState.ForIssue(insert).PrimaryActionLabel);
    }

    /// <summary>§1: a check in flight shows progress and nothing that needs a result.</summary>
    [TestMethod]
    public void CheckingShowsNoResultAndNoRetry()
    {
        var state = CorrectionCardState.Checking(Spelling("роботает", "работает"));

        Assert.IsTrue(state.ShowProgress);
        Assert.IsFalse(state.ShowChange, "a stale change row is a stale result");
        Assert.IsFalse(state.ShowExplanation);
        Assert.IsFalse(state.ShowPrimaryAction);
        Assert.IsFalse(state.ShowRetry);
        Assert.IsFalse(state.ShowStatus);
        Assert.IsFalse(state.ShowDictionary);
        Assert.IsFalse(state.ShowIgnore);
    }

    /// <summary>§4: text changing is not a processing failure and never offers «Повторить».</summary>
    [TestMethod]
    public void ATextChangeRechecksAndDropsEveryActionFromTheOldResult()
    {
        var stale = CorrectionCardState.ForIssue(Spelling("роботает", "работает")).Recheck();

        Assert.AreEqual(CorrectionCardPhase.Checking, stale.Phase);
        Assert.IsFalse(stale.ShowRetry, "retry is for a failed write, not for text the user edited");
        Assert.IsFalse(stale.ShowPrimaryAction);
        Assert.IsFalse(stale.ShowChange);
        Assert.IsFalse(stale.ShowStatus);
    }

    [TestMethod]
    public void ATextChangeAfterAFailureAlsoRechecksRatherThanKeepingTheFailure()
    {
        var failed = CorrectionCardState.Failed(
            Spelling("роботает", "работает"), "не удалось", offerCopy: true, offerRetry: true);

        var rechecked = failed.Recheck();

        Assert.AreEqual(CorrectionCardPhase.Checking, rechecked.Phase);
        Assert.IsFalse(rechecked.ShowRetry);
        Assert.IsFalse(rechecked.ShowCopy);
        Assert.IsFalse(rechecked.ShowStatus);
    }

    [TestMethod]
    public void OnlyAFailedWriteOffersRetry()
    {
        var issue = Spelling("роботает", "работает");

        Assert.IsFalse(CorrectionCardState.ForIssue(issue).ShowRetry);
        Assert.IsFalse(CorrectionCardState.Checking(issue).ShowRetry);
        Assert.IsFalse(CorrectionCardState.Applying(issue).ShowRetry);
        Assert.IsFalse(CorrectionCardState.Hidden.ShowRetry);
        Assert.IsTrue(CorrectionCardState.Failed(issue, "не удалось", false, offerRetry: true).ShowRetry);

        // A read-only field will not become writable on a second attempt.
        Assert.IsFalse(CorrectionCardState.Failed(issue, "только для чтения", true, offerRetry: false).ShowRetry);
    }

    [TestMethod]
    public void AFailedWriteReplacesTheApplyActionRatherThanSittingBesideIt()
    {
        var failed = CorrectionCardState.Failed(
            Spelling("роботает", "работает"), "не удалось", offerCopy: true, offerRetry: true);

        Assert.IsTrue(failed.ShowStatus);
        Assert.IsFalse(failed.ShowPrimaryAction, "the failure is the state, not an annotation on the result");
        Assert.IsTrue(failed.ShowChange, "the user still needs to see which correction failed");
    }

    /// <summary>«В словарь» is offered only where there is exactly one word to add — §18.</summary>
    [TestMethod]
    public void TheDictionaryActionFollowsWhetherThereIsAWordToAdd()
    {
        Assert.IsTrue(CorrectionCardState.ForIssue(Spelling("роботает", "работает")).ShowDictionary);

        var phrase = Spelling("вопреки новых", "вопреки новым") with { Category = IssueCategory.Grammar };
        Assert.IsFalse(CorrectionCardState.ForIssue(phrase).ShowDictionary, "grammar findings are not dictionary words");

        var fragment = Spelling("а,", "а.") with { Category = IssueCategory.Orthography };
        Assert.IsFalse(CorrectionCardState.ForIssue(fragment).ShowDictionary, "a fragment is not a word");
    }

    /// <summary>
    /// The screenshot, asserted as a set of combinations that no phase produces.
    /// </summary>
    /// <remarks>
    /// This is the regression that matters: not "the card looks right for input X", but "there
    /// is no input, and no sequence of transitions, for which these controls coexist". Every
    /// state the card can be in is enumerated and checked against the incompatible pairs.
    /// </remarks>
    [TestMethod]
    public void NoStateEverRendersIncompatibleControlsTogether()
    {
        foreach (var state in EveryState())
        {
            var where = $"{state.Phase}";

            Assert.IsFalse(state.ShowProgress && state.ShowPrimaryAction,
                $"{where}: a progress state is not a clickable action");
            Assert.IsFalse(state.ShowProgress && state.ShowStatus,
                $"{where}: a check in flight has not failed");
            Assert.IsFalse(state.ShowProgress && state.ShowRetry,
                $"{where}: nothing to retry while the check is running");
            Assert.IsFalse(state.ShowProgress && state.ShowExplanation,
                $"{where}: a finished explanation belongs to a finished analysis");
            Assert.IsFalse(state.ShowPrimaryAction && state.ShowRetry,
                $"{where}: two competing primary actions");
            Assert.IsFalse(state.ShowPrimaryAction && state.ShowStatus,
                $"{where}: the reported card — an apply action under a failure message");
            Assert.IsFalse(state.ShowChange && state.ShowHeadline,
                $"{where}: a change row and 'no suggestion' are opposite claims");
            Assert.IsFalse(state.ShowHeadline && state.ShowPrimaryAction,
                $"{where}: nothing to apply on an informational card");
            Assert.IsFalse(state.ShowChange && state.OriginalDisplay == state.ReplacementDisplay,
                $"{where}: «X → X» is not a correction");
        }
    }

    /// <summary>
    /// The shared test both correction surfaces ask.
    /// </summary>
    /// <remarks>
    /// The popup card and the suggestions panel show the same findings and used to decide
    /// independently whether one was worth an apply action — the popup on
    /// <c>!IsNullOrWhiteSpace(Replacement)</c>, the panel on <c>!IsNullOrEmpty(Replacement)</c>.
    /// Neither looked at what the card would draw, so both could offer to apply a change the
    /// user cannot see. Pinned here because it is now the one answer both of them use.
    /// </remarks>
    [TestMethod]
    public void TheNoOpTestIsWhatBothCorrectionSurfacesAsk()
    {
        Assert.IsTrue(CorrectionCardText.IsNoOpForDisplay(Spelling("Проверяем", "Проверяем")));
        Assert.IsTrue(CorrectionCardText.IsNoOpForDisplay(Spelling("Проверяем", "**Проверяем**")));
        Assert.IsTrue(CorrectionCardText.IsNoOpForDisplay(Spelling("слово", null)));
        Assert.IsTrue(CorrectionCardText.IsNoOpForDisplay(Spelling("слово", string.Empty)));

        Assert.IsFalse(CorrectionCardText.IsNoOpForDisplay(Spelling("роботает", "работает")));
        Assert.IsFalse(CorrectionCardText.IsNoOpForDisplay(Spelling("вообщем", "в общем")));

        // An insertion has no "before" side to compare against, and always changes the text.
        Assert.IsFalse(CorrectionCardText.IsNoOpForDisplay(new TextIssue(
            8, 0, string.Empty, ",", "Пунктуация", "Нужна запятая",
            IssueCategory.Punctuation, IssueSeverity.Warning, RuleId: "ru.punctuation.comma")));
    }

    private static IEnumerable<CorrectionCardState> EveryState()
    {
        TextIssue[] issues =
        [
            Spelling("роботает", "работает"),
            Spelling("Проверяем", "Проверяем"),
            Spelling("Проверяем", "**Проверяем**"),
            new(8, 0, string.Empty, ",", "Пунктуация", "Нужна запятая",
                IssueCategory.Punctuation, IssueSeverity.Warning, RuleId: "ru.punctuation.comma"),
        ];

        yield return CorrectionCardState.Hidden;
        foreach (var issue in issues)
        {
            var result = CorrectionCardState.ForIssue(issue);
            yield return result;
            yield return CorrectionCardState.Checking(issue);
            yield return CorrectionCardState.Applying(issue);
            yield return result.Recheck();
            foreach (var copy in new[] { true, false })
            {
                foreach (var retry in new[] { true, false })
                {
                    var failed = CorrectionCardState.Failed(issue, "не удалось", copy, retry);
                    yield return failed;
                    yield return failed.Recheck();
                }
            }
        }
    }

    private static TextIssue Spelling(string original, string? replacement) => new(
        0, original.Length, original, replacement,
        "Орфография", "Слово не найдено в русском орфографическом словаре.",
        IssueCategory.Orthography, IssueSeverity.Warning,
        CanApplyAutomatically: false, RuleId: "ru.spelling.typo");
}
