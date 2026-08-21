using WriteLite.Services.Reading;

namespace WriteLite.Tests.Reading;

/// <summary>
/// Reading a card out of what the local model actually returns.
/// </summary>
/// <remarks>
/// The prompt asks for two labelled lines; a small quantised model obliges most of
/// the time and improvises the rest. Every shape below was cheaper to accept here
/// than to argue with in the prompt.
///
/// The line that matters most is the one that is *not* accepted. When the local
/// runtime is unreachable the rewrite service degrades to deterministic rules, and
/// those rules have nothing to do with a custom instruction, so they hand the passage
/// straight back. Parsed generously, that echo becomes a plausible card whose front is
/// the first line of the passage — and it was being saved with the badge that says
/// WriteLite AI wrote it. A refusal is the only correct answer there.
/// </remarks>
[TestClass]
public sealed class StudyCardDraftTests
{
    private const string Passage = "SQL — декларативный язык для работы с реляционными базами данных.";

    [TestMethod]
    public void The_requested_shape_parses()
    {
        var draft = StudyCardDraftService.Parse(
            "Вопрос: Что такое SQL?\nОтвет: Декларативный язык для работы с реляционными базами данных.",
            Passage);

        Assert.IsNotNull(draft);
        Assert.AreEqual("Что такое SQL?", draft.Value.Front);
        Assert.AreEqual("Декларативный язык для работы с реляционными базами данных.", draft.Value.Back);
    }

    [TestMethod]
    public void Markdown_emphasis_around_the_labels_is_ignored()
    {
        var draft = StudyCardDraftService.Parse(
            "**Вопрос:** Что такое SQL?\n**Ответ:** Язык запросов.",
            Passage);

        Assert.AreEqual("Что такое SQL?", draft!.Value.Front);
        Assert.AreEqual("Язык запросов.", draft.Value.Back);
    }

    [TestMethod]
    public void English_labels_are_accepted_despite_the_russian_prompt()
    {
        var draft = StudyCardDraftService.Parse("Front: What is SQL?\nBack: A query language.", Passage);

        Assert.AreEqual("What is SQL?", draft!.Value.Front);
        Assert.AreEqual("A query language.", draft.Value.Back);
    }

    [TestMethod]
    public void A_preamble_before_the_two_lines_does_not_break_it()
    {
        var draft = StudyCardDraftService.Parse(
            "Конечно! Вот карточка:\n\nВопрос: Что такое SQL?\nОтвет: Язык запросов.",
            Passage);

        Assert.AreEqual("Что такое SQL?", draft!.Value.Front);
        Assert.AreEqual("Язык запросов.", draft.Value.Back);
    }

    [TestMethod]
    public void Two_unlabelled_lines_are_taken_in_order()
    {
        var draft = StudyCardDraftService.Parse("Что такое SQL?\nЯзык запросов.", Passage);

        Assert.AreEqual("Что такое SQL?", draft!.Value.Front);
        Assert.AreEqual("Язык запросов.", draft.Value.Back);
    }

    [TestMethod]
    public void An_empty_response_is_not_a_card()
    {
        Assert.IsNull(
            StudyCardDraftService.Parse(string.Empty, Passage),
            "Nothing came back, so there is nothing to show the reader as a draft.");
    }

    [TestMethod]
    public void A_question_with_no_answer_keeps_the_passage_as_the_answer()
    {
        var draft = StudyCardDraftService.Parse("Вопрос: Что такое SQL?", Passage);

        Assert.AreEqual("Что такое SQL?", draft!.Value.Front);
        Assert.AreEqual(Passage, draft.Value.Back);
    }

    // ── The echo ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void The_passage_handed_back_verbatim_is_refused()
    {
        Assert.IsNull(
            StudyCardDraftService.Parse(Passage, Passage),
            "The offline rules return the input unchanged for a custom instruction. " +
            "Read as a card that is a question the model never asked.");
    }

    [TestMethod]
    public void A_front_that_is_only_the_opening_of_the_passage_is_refused()
    {
        Assert.IsNull(
            StudyCardDraftService.Parse("SQL — декларативный язык\nдля работы с базами.", Passage));
    }

    [TestMethod]
    public void The_offline_rule_backend_is_recognised_as_no_model_at_all()
    {
        Assert.IsTrue(StudyCardDraftService.IsFallback("offline-rules"));
        Assert.IsTrue(StudyCardDraftService.IsFallback("none"));
        Assert.IsTrue(StudyCardDraftService.IsFallback(null));
        Assert.IsFalse(StudyCardDraftService.IsFallback("writelite-qwen"));
    }

    // ── Availability and failure ─────────────────────────────────────────────

    [TestMethod]
    public void Drafting_is_optional_and_absent_without_a_model()
    {
        var service = new StudyCardDraftService(null);

        Assert.IsFalse(service.IsAvailable, "Without a model the reader still gets the manual editor.");
        Assert.IsFalse(service.IsModelReady);
    }

    [TestMethod]
    public async Task Drafting_without_a_model_says_so_rather_than_throwing()
    {
        var result = await new StudyCardDraftService(null).DraftAsync(Passage);

        Assert.AreEqual(StudyCardDraftStatus.NotConfigured, result.Status);
        Assert.IsFalse(result.IsSuccess);
        Assert.IsNotNull(result.Message, "A failure the reader cannot read is a failure twice.");
    }

    [TestMethod]
    public async Task A_model_that_falls_back_to_rules_is_reported_as_unavailable()
    {
        var result = await new StudyCardDraftService(new StubAi("offline-rules", Passage))
            .DraftAsync(Passage);

        Assert.AreEqual(StudyCardDraftStatus.ModelUnavailable, result.Status,
            "An echo from the rule fallback must never be presented as an AI draft.");
        Assert.IsNull(result.Draft);
    }

    [TestMethod]
    public async Task A_real_answer_from_the_model_becomes_a_draft()
    {
        var result = await new StudyCardDraftService(
                new StubAi("writelite-qwen", "Вопрос: Что такое SQL?\nОтвет: Язык запросов."))
            .DraftAsync(Passage);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Что такое SQL?", result.Draft!.Value.Front);
        Assert.AreEqual("Язык запросов.", result.Draft.Value.Back);
    }

    [TestMethod]
    public async Task Prose_from_the_model_is_reported_rather_than_shaped_into_a_card()
    {
        var result = await new StudyCardDraftService(new StubAi("writelite-qwen", "   "))
            .DraftAsync(Passage);

        Assert.AreEqual(StudyCardDraftStatus.Unusable, result.Status);
        Assert.IsNotNull(result.Message);
    }

    [TestMethod]
    public async Task Cancellation_is_reported_as_cancellation_and_nothing_else()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await new StudyCardDraftService(new StubAi("writelite-qwen", "Вопрос: A\nОтвет: B"))
            .DraftAsync(Passage, cancellation.Token);

        Assert.AreEqual(StudyCardDraftStatus.Cancelled, result.Status);
    }

    [TestMethod]
    public async Task A_throwing_runtime_does_not_take_the_reader_down_with_it()
    {
        var result = await new StudyCardDraftService(new ThrowingAi()).DraftAsync(Passage);

        Assert.AreEqual(StudyCardDraftStatus.Failed, result.Status);
        Assert.IsNotNull(result.Message);
    }

    /// <summary>A rewrite service that answers with a fixed suggestion from a named backend.</summary>
    private sealed class StubAi(string backend, string suggestion) : Services.Ai.IEditorAiService
    {
        public bool IsModelAvailable => true;

        public Task<Services.Ai.RewriteResult> RewriteAsync(
            Services.Ai.RewriteRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(new Services.Ai.RewriteResult(
                request.Selection, suggestion, request.Operation, backend));
        }
    }

    private sealed class ThrowingAi : Services.Ai.IEditorAiService
    {
        public bool IsModelAvailable => true;

        public Task<Services.Ai.RewriteResult> RewriteAsync(
            Services.Ai.RewriteRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("runtime is wedged");
    }
}
