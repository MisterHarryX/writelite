using WriteLite.Models;
using WriteLite.Services.Ai;

namespace WriteLite.Tests.Ai;

/// <summary>
/// The safety contract of the writing-assistance layer, tested without a model.
/// </summary>
/// <remarks>
/// Everything asserted here is a property of the filter and the trigger, not of the model —
/// which is the point. §36 asks that completions not invent facts, and a promise that depends
/// on what a 0.5 B model happens to say is not a promise. These are the checks that hold
/// whatever comes back.
/// </remarks>
[TestClass]
public sealed class WritingAssistanceTests
{
    private const string Context = "В результате исследования мы пришли к выводу, что";

    [TestMethod]
    public void Completion_ContainingDigits_IsDropped()
    {
        Assert.AreEqual(string.Empty, WritingAssistanceService.Clean("в 2024 году показатель вырос", Context));
        Assert.AreEqual(string.Empty, WritingAssistanceService.Clean("рост составил 15 процентов", Context));
    }

    [TestMethod]
    public void Completion_ContainingPercentOrNumberSign_IsDropped()
    {
        Assert.AreEqual(string.Empty, WritingAssistanceService.Clean("рост составил почти 20%", Context));
        Assert.AreEqual(string.Empty, WritingAssistanceService.Clean("это указано в приказе № такой", Context));
    }

    [TestMethod]
    public void Completion_IntroducingAnUngroundedName_IsDropped()
    {
        // A capitalised word the context has never seen is the shape a hallucinated name,
        // organisation or place takes.
        Assert.AreEqual(string.Empty, WritingAssistanceService.Clean("это подтвердил Иванов", Context));
        Assert.AreEqual(string.Empty, WritingAssistanceService.Clean("как показала Москва", Context));
    }

    [TestMethod]
    public void Completion_ReusingANameFromContext_IsKept()
    {
        const string grounded = "По данным отдела продаж выручка выросла, и отдел продаж";
        var cleaned = WritingAssistanceService.Clean("продолжит эту работу", grounded);
        Assert.AreNotEqual(string.Empty, cleaned);
    }

    [TestMethod]
    public void Completion_IsTruncatedAtTheFirstSentenceEnd()
    {
        var cleaned = WritingAssistanceService.Clean(
            "предложенный подход применим. Далее мы рассмотрим другие варианты.", Context);
        StringAssert.EndsWith(cleaned.TrimEnd(), "применим.");
        Assert.IsFalse(cleaned.Contains("Далее"));
    }

    [TestMethod]
    public void Completion_ThatRepeatsWhatIsAlreadyWritten_IsDropped()
    {
        Assert.AreEqual(string.Empty, WritingAssistanceService.Clean("пришли к выводу, что", Context));
    }

    [TestMethod]
    public void Completion_TruncatedToAnAbbreviationStub_IsDropped()
    {
        // «см. приказ …» keeps only «см.» after the sentence-end truncation, which helps nobody.
        Assert.AreEqual(string.Empty, WritingAssistanceService.Clean("см. приказ выше", Context));
    }

    [TestMethod]
    public void Completion_LongerThanOneClause_IsDropped()
    {
        var tooLong = new string('а', WritingAssistanceService.MaxCompletionChars + 1);
        Assert.AreEqual(string.Empty, WritingAssistanceService.Clean(tooLong, Context));
    }

    [TestMethod]
    public void Trigger_DeclinesMidWordAndTooEarly()
    {
        const string text = "В результате исследования мы пришли";

        // Mid-word: the user is still choosing this word.
        Assert.IsFalse(WritingAssistanceService.IsGoodPlaceToSuggest(text, 5));

        // Too little context to continue anything.
        Assert.IsFalse(WritingAssistanceService.IsGoodPlaceToSuggest("Привет", 6));

        // End of a finished word, with enough behind it.
        Assert.IsTrue(WritingAssistanceService.IsGoodPlaceToSuggest(text, text.Length));
    }

    [TestMethod]
    public void SuggestionAppliesOnlyToTheCaretAndVersionItWasPreparedFor()
    {
        var suggestion = new WritingSuggestion(" продолжение", 42, 7);

        Assert.IsTrue(suggestion.AppliesTo(42, 7));
        Assert.IsFalse(suggestion.AppliesTo(43, 7), "a moved caret must invalidate it");
        Assert.IsFalse(suggestion.AppliesTo(42, 8), "an edited document must invalidate it");
        Assert.IsFalse(WritingSuggestion.None.AppliesTo(0, 0));
    }

    [TestMethod]
    public void FieldSuggestion_IsAnInsertionThatNeverAutoApplies()
    {
        var issue = WritingAssistanceCoordinator.ToIssue(new WritingSuggestion(" и это подтверждается", 30, 1));

        Assert.AreEqual(30, issue.Start);
        Assert.AreEqual(0, issue.Length, "a continuation replaces nothing");
        Assert.AreEqual(string.Empty, issue.Original);
        Assert.IsFalse(issue.CanApplyAutomatically, "§68: a generated continuation is never auto-applied");
        Assert.AreEqual(IssueClass.Style, issue.Class, "§38: it must not look like an error");
        Assert.AreEqual(CorrectionCertainty.Suggestion, issue.Certainty);
    }

    [TestMethod]
    public async Task WithoutAModel_NothingIsPreparedAndNothingThrows()
    {
        var service = new WritingAssistanceService(backend: null);
        Assert.IsFalse(service.IsAvailable);

        var suggestion = await service.RequestAsync(Context + " ", Context.Length + 1, 1);
        Assert.IsFalse(suggestion.HasText);

        var coordinator = new WritingAssistanceCoordinator(service);
        Assert.IsNull(await coordinator.RequestAsync(Context + " ", Context.Length + 1));

        // Dismiss on an idle service is a no-op, not a crash: it is called on every keystroke.
        service.Dismiss();
        coordinator.Dismiss();
    }
}
