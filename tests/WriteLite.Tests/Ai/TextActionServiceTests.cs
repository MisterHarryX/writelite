using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Ai;

namespace WriteLite.Tests.Ai;

/// <summary>
/// The AI actions' plumbing against a fake model: availability messages, chunking to the
/// model's context, the answer checks, «Исправить ошибки» without a model, progress and
/// cancellation. The model's quality is not under test here; the code around it is.
/// </summary>
[TestClass]
public sealed class TextActionServiceTests
{
    /// <summary>A model that answers with a function of the conversation, and records every call.</summary>
    private sealed class FakeModel : ILocalTextModel
    {
        private readonly Func<IReadOnlyList<(string Role, string Content)>, double, string?> _answer;

        public FakeModel(Func<IReadOnlyList<(string Role, string Content)>, double, string?> answer)
        {
            _answer = answer;
        }

        public bool IsInstalled { get; init; } = true;

        public LocalModelReadiness Readiness { get; init; } = LocalModelReadiness.Ready;

        public List<(IReadOnlyList<(string Role, string Content)> Messages, int MaxTokens, double Temperature)> Calls { get; } = [];

        public Func<Task>? BeforeAnswer { get; init; }

        public Task<LocalModelReadiness> EnsureReadyAsync(CancellationToken cancellationToken) => Task.FromResult(Readiness);

        public async Task<string?> CompleteAsync(
            IReadOnlyList<(string Role, string Content)> messages,
            int maxTokens,
            double temperature,
            CancellationToken cancellationToken)
        {
            Calls.Add((messages, maxTokens, temperature));
            if (BeforeAnswer is not null)
            {
                await BeforeAnswer();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return _answer(messages, temperature);
        }
    }

    /// <summary>The text the model was asked to work on: everything after the last «Текст:».</summary>
    private static string TextOf(IReadOnlyList<(string Role, string Content)> messages)
    {
        var last = messages[^1].Content;
        var at = last.LastIndexOf("Текст:\n", StringComparison.Ordinal);
        return at < 0 ? last : last[(at + "Текст:\n".Length)..];
    }

    private sealed class FakeChecker(Func<string, IReadOnlyList<TextIssue>> analyze) : ITextAnalyzer
    {
        public int Calls { get; private set; }

        public IReadOnlyList<TextIssue> Analyze(string text)
        {
            Calls++;
            return analyze(text);
        }
    }

    private static TextIssue Issue(string text, string original, string replacement,
        IssueCategory category = IssueCategory.Orthography, double confidence = 0.95, bool auto = true)
    {
        var start = text.IndexOf(original, StringComparison.Ordinal);
        return new TextIssue(start, original.Length, original, replacement, "t", "e", category,
            category == IssueCategory.Style ? IssueSeverity.Suggestion : IssueSeverity.Error, auto, Confidence: confidence);
    }

    private const string Paragraph =
        "Вчера мы с командой обсуждали запуск нового сервиса доставки. Главная проблема в том, что курьеров не хватает в вечерние часы.";

    // ── Availability ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Without_an_installed_model_the_message_says_so_and_names_the_action()
    {
        var service = new TextActionService(new FakeModel((_, _) => "x") { IsInstalled = false });

        var error = await Assert.ThrowsExactlyAsync<LocalModelUnavailableException>(
            () => service.RunAsync(TextActionRequest.ForText(RewriteOperation.Shorten, Paragraph)));

        StringAssert.Contains(error.Message, "не установлена");
        StringAssert.Contains(error.Message, "Сократить");
    }

    [TestMethod]
    public async Task With_no_model_at_all_the_action_is_refused_clearly()
    {
        var service = new TextActionService(model: null);

        var error = await Assert.ThrowsExactlyAsync<LocalModelUnavailableException>(
            () => service.RunAsync(TextActionRequest.ForText(RewriteOperation.KeyPoints, Paragraph)));

        StringAssert.Contains(error.Message, "WriteAI");
    }

    [TestMethod]
    public async Task A_model_that_is_installed_but_not_running_gets_its_own_message()
    {
        var model = new FakeModel((_, _) => "x") { Readiness = LocalModelReadiness.NotRunning };
        var service = new TextActionService(model);

        var error = await Assert.ThrowsExactlyAsync<LocalModelUnavailableException>(
            () => service.RunAsync(TextActionRequest.ForText(RewriteOperation.Rewrite, Paragraph)));

        StringAssert.Contains(error.Message, "Не удалось запустить");
        Assert.AreEqual(0, model.Calls.Count, "Nothing may be sent to a model that is not up.");
    }

    [TestMethod]
    public async Task A_model_that_never_answers_is_reported_rather_than_returning_the_text_unchanged()
    {
        var service = new TextActionService(new FakeModel((_, _) => null));

        var error = await Assert.ThrowsExactlyAsync<LocalModelUnavailableException>(
            () => service.RunAsync(TextActionRequest.ForText(RewriteOperation.Rewrite, Paragraph)));

        StringAssert.Contains(error.Message, "не ответила");
    }

    [TestMethod]
    public async Task Empty_text_is_refused_before_anything_runs()
    {
        var model = new FakeModel((_, _) => "x");
        var service = new TextActionService(model);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.RunAsync(new TextActionRequest(RewriteOperation.Rewrite, ["  ", ""])));

        Assert.AreEqual(0, model.Calls.Count);
    }

    // ── «Исправить ошибки» ───────────────────────────────────────────────────

    [TestMethod]
    public async Task Fixing_errors_uses_the_checker_and_needs_no_model()
    {
        const string text = "Я пришол домой, и обьяснил что случилось.";
        var checker = new FakeChecker(source =>
        [
            Issue(source, "пришол", "пришёл"),
            Issue(source, "обьяснил", "объяснил"),
            Issue(source, "обьяснил что", "объяснил, что", IssueCategory.Punctuation, 0.9, auto: false),
            Issue(source, "домой", "домой к себе", IssueCategory.Style, 0.99)
        ]);
        var service = new TextActionService(model: null, checker);

        var result = await service.RunAsync(TextActionRequest.ForText(RewriteOperation.Grammar, text));

        // The two spelling fixes win over the overlapping, less certain punctuation one;
        // the style suggestion is an opinion, not an error, and is never applied.
        Assert.AreEqual("Я пришёл домой, и объяснил что случилось.", result.Suggestion);
        Assert.AreEqual("writelite-rules", result.Backend);
        Assert.IsFalse(result.IsAdvisory);
        Assert.AreEqual(1, checker.Calls);
    }

    [TestMethod]
    public async Task Fixing_a_whole_note_keeps_one_suggestion_per_paragraph()
    {
        var checker = new FakeChecker(source => source.Contains("эфекта", StringComparison.Ordinal)
            ? [Issue(source, "эфекта", "эффекта")]
            : []);
        var service = new TextActionService(model: null, checker);

        var result = await service.RunAsync(new TextActionRequest(
            RewriteOperation.Grammar,
            ["Заголовок", "", "Нет никакого эфекта.", "Всё верно."]));

        CollectionAssert.AreEqual(
            new[] { "Заголовок", "", "Нет никакого эффекта.", "Всё верно." },
            result.Suggestions.ToArray());
    }

    [TestMethod]
    public async Task A_clean_text_says_there_was_nothing_to_fix()
    {
        var service = new TextActionService(model: null, new FakeChecker(_ => []));

        var result = await service.RunAsync(TextActionRequest.ForText(RewriteOperation.Grammar, "Всё написано правильно."));

        Assert.IsTrue(result.IsNoOp);
        Assert.IsNotNull(result.Notice);
    }

    [TestMethod]
    public void Fixes_that_no_longer_match_the_text_are_never_applied()
    {
        const string text = "Мама мыла раму.";
        var stale = new TextIssue(0, 4, "Папа", "Отец", "t", "e", IssueCategory.Orthography, IssueSeverity.Error);

        Assert.AreEqual(0, TextActionService.ChooseFixes(text, [stale]).Count);
    }

    // ── Rewrites ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Shorten_accepts_a_shorter_answer_and_asks_for_a_word_budget()
    {
        var model = new FakeModel((_, _) => "Обсудили запуск доставки: вечером не хватает курьеров.");
        var service = new TextActionService(model);

        var result = await service.RunAsync(TextActionRequest.ForText(RewriteOperation.Shorten, Paragraph));

        Assert.AreEqual("Обсудили запуск доставки: вечером не хватает курьеров.", result.Suggestion);
        StringAssert.Contains(model.Calls[0].Messages[^1].Content, "Сократи текст до");
        Assert.IsNull(result.Notice);
    }

    [TestMethod]
    public async Task A_shorten_answer_that_is_not_shorter_is_retried_then_the_text_is_left_alone()
    {
        var model = new FakeModel((messages, _) => TextOf(messages) + " И ещё немного.");
        var service = new TextActionService(model);

        var result = await service.RunAsync(TextActionRequest.ForText(RewriteOperation.Shorten, Paragraph));

        Assert.AreEqual(2, model.Calls.Count, "One retry, with a stricter budget.");
        Assert.AreEqual(Paragraph, result.Suggestion, "A failed piece is left exactly as it was.");
        Assert.IsTrue(result.IsNoOp);
        StringAssert.Contains(result.Notice!, "не удалось сократить");
    }

    [TestMethod]
    public async Task English_words_in_a_Russian_answer_are_rejected()
    {
        var answers = new Queue<string>(["Вчера мы hiring обсуждали доставку и курьеров.", "Вчера команда обсуждала доставку и нехватку курьеров вечером."]);
        var model = new FakeModel((_, _) => answers.Dequeue());
        var service = new TextActionService(model);

        var result = await service.RunAsync(TextActionRequest.ForText(RewriteOperation.Rewrite, Paragraph));

        Assert.AreEqual("Вчера команда обсуждала доставку и нехватку курьеров вечером.", result.Suggestion);
    }

    [TestMethod]
    public async Task Expand_asks_twice_with_a_worked_example_and_keeps_the_longer_answer()
    {
        var answers = new Queue<string>([Paragraph + " Это важно.", Paragraph + " Это важно, потому что вечером заказов больше всего, а людей меньше."]);
        var model = new FakeModel((_, _) => answers.Dequeue());
        var service = new TextActionService(model);

        var result = await service.RunAsync(TextActionRequest.ForText(RewriteOperation.Expand, Paragraph));

        Assert.AreEqual(2, model.Calls.Count);
        Assert.AreEqual(4, model.Calls[0].Messages.Count, "system, example question, example answer, request");
        Assert.AreEqual("assistant", model.Calls[0].Messages[2].Role);
        StringAssert.EndsWith(result.Suggestion, "а людей меньше.");
        StringAssert.Contains(result.Notice!, "Проверьте подробности");
    }

    [TestMethod]
    public async Task Headings_and_other_short_lines_are_passed_through_untouched()
    {
        var model = new FakeModel((_, _) => "Обсуждали доставку и курьеров.");
        var service = new TextActionService(model);

        var result = await service.RunAsync(new TextActionRequest(RewriteOperation.Shorten, ["План", Paragraph]));

        Assert.AreEqual("План", result.Suggestions[0], "A two-word heading is not something to shorten.");
        Assert.AreEqual(1, model.Calls.Count);
    }

    [TestMethod]
    [DataRow(RewriteOperation.Expand)]
    [DataRow(RewriteOperation.Rewrite)]
    [DataRow(RewriteOperation.Shorten)]
    public void A_heading_is_never_sent_to_the_model(RewriteOperation operation)
    {
        // Measured on the shipped model: «Планы на запуск» expanded into a chain of invented headings.
        Assert.IsFalse(TextActionService.ShouldProcess(operation, "Планы на запуск"));
        Assert.IsFalse(TextActionService.ShouldProcess(operation, "Что сделать до пятницы"));
        Assert.IsTrue(TextActionService.ShouldProcess(operation, "Встреча перенесена на четверг, нужно подготовить отчёт."));
    }

    [TestMethod]
    public void Degenerate_answers_are_rejected()
    {
        const string original = "Вчера мы обсуждали запуск сервиса доставки.";

        Assert.IsTrue(TextActionService.IsDegenerate(original,
            "Планы: Новые задачи: Ожидаемая структура: Регулярные смены: Ожидание завершения: Помогаем другим"));
        Assert.IsTrue(TextActionService.IsDegenerate(original,
            "доставка доставка доставка доставка сервис сервис сервис доставка доставка сервис доставка доставка"));
        Assert.IsFalse(TextActionService.IsDegenerate(original,
            "Вчера команда подробно обсуждала, как запустить новый сервис доставки и где взять курьеров."));
    }

    [TestMethod]
    public async Task A_long_paragraph_is_cut_to_fit_the_context_and_joined_back()
    {
        var sentence = "Это одно из многих предложений длинного абзаца про планы команды на квартал. ";
        var longText = string.Concat(Enumerable.Repeat(sentence, 40)).TrimEnd();
        var model = new FakeModel((messages, _) => TextOf(messages).Replace("многих ", string.Empty, StringComparison.Ordinal));
        var service = new TextActionService(model);

        var result = await service.RunAsync(TextActionRequest.ForText(RewriteOperation.Rewrite, longText));

        Assert.IsTrue(model.Calls.Count > 1, "A paragraph longer than the context must be split.");
        foreach (var call in model.Calls)
        {
            var piece = TextOf(call.Messages);
            Assert.IsTrue(piece.Length <= TextActionService.ChunkChars, $"piece of {piece.Length} chars");

            // The prompt and the answer budget together must fit the server's context.
            var promptTokens = call.Messages.Sum(message => message.Content.Length) / 3;
            Assert.IsTrue(promptTokens + call.MaxTokens <= TextActionService.ContextTokens + 64,
                $"prompt≈{promptTokens} + answer {call.MaxTokens} exceeds the context");
        }

        Assert.AreEqual(longText.Replace("многих ", string.Empty, StringComparison.Ordinal), result.Suggestion);
    }

    [TestMethod]
    public async Task Progress_counts_every_model_call()
    {
        var model = new FakeModel((messages, _) => TextOf(messages) + " (иначе)");
        var service = new TextActionService(model);
        var reports = new List<TextActionProgress>();
        var progress = new SynchronousProgress(reports.Add);

        await service.RunAsync(new TextActionRequest(RewriteOperation.Rewrite, [Paragraph, Paragraph, Paragraph]), progress);

        Assert.IsTrue(reports.Count >= 3);
        Assert.AreEqual(new TextActionProgress(3, 3), reports[^1]);
    }

    [TestMethod]
    public async Task Cancelling_stops_between_calls_and_reports_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var model = new FakeModel((messages, _) => TextOf(messages) + " (иначе)")
        {
            BeforeAnswer = () =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            }
        };
        var service = new TextActionService(model);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => service.RunAsync(new TextActionRequest(RewriteOperation.Rewrite, [Paragraph, Paragraph]), null, cancellation.Token));

        Assert.AreEqual(1, model.Calls.Count, "Nothing more is sent after the user cancelled.");
    }

    // ── «Узнать главное», «Объясни простыми словами» ─────────────────────────

    [TestMethod]
    public async Task Key_points_are_read_from_any_list_shape_and_returned_as_advice()
    {
        var model = new FakeModel((_, _) => "— Запуск сервиса доставки, — Не хватает курьеров вечером, — Не хватает курьеров вечером");
        var service = new TextActionService(model);

        var result = await service.RunAsync(TextActionRequest.ForText(RewriteOperation.KeyPoints, Paragraph));

        Assert.IsTrue(result.IsAdvisory, "The main points are about the text, not a replacement for it.");
        Assert.AreEqual("— Запуск сервиса доставки\n— Не хватает курьеров вечером", result.Suggestion);
        Assert.AreEqual(Paragraph, result.Original);
    }

    [TestMethod]
    public void A_dash_inside_a_sentence_is_not_a_list()
    {
        var points = TextActionService.ParsePoints("— Фотосинтез — это процесс в растениях\n2) Нужен свет");

        CollectionAssert.AreEqual(new[] { "Фотосинтез — это процесс в растениях", "Нужен свет" }, points);
    }

    [TestMethod]
    public async Task Key_points_of_a_long_note_are_gathered_per_piece_and_condensed()
    {
        var calls = 0;
        var model = new FakeModel((messages, _) =>
        {
            calls++;
            return TextOf(messages).StartsWith("— ", StringComparison.Ordinal)
                ? "— Итог один\n— Итог два\n— Итог три"
                : $"— Мысль {calls}а\n— Мысль {calls}б\n— Мысль {calls}в";
        });
        var service = new TextActionService(model);
        var paragraphs = Enumerable.Range(0, 6).Select(index => $"Абзац номер {index}. " + string.Concat(Enumerable.Repeat("Подробности. ", 40))).ToList();

        var result = await service.RunAsync(new TextActionRequest(RewriteOperation.KeyPoints, paragraphs));

        Assert.IsTrue(model.Calls.Count > 2);
        Assert.AreEqual("— Итог один\n— Итог два\n— Итог три", result.Suggestion, "Too many points are condensed in one more call.");
    }

    [TestMethod]
    public async Task Explain_strips_the_lead_in_the_example_teaches()
    {
        var model = new FakeModel((_, _) => "Простыми словами: вечером мало курьеров, а заказов много.");
        var service = new TextActionService(model);

        var result = await service.RunAsync(TextActionRequest.ForText(RewriteOperation.Explain, Paragraph));

        Assert.AreEqual("Вечером мало курьеров, а заказов много.", result.Suggestion);
        Assert.IsTrue(result.IsAdvisory);
        Assert.AreEqual(4, model.Calls[0].Messages.Count);
    }

    // ── Prompt budgets ───────────────────────────────────────────────────────

    [TestMethod]
    [DataRow(RewriteOperation.Shorten)]
    [DataRow(RewriteOperation.Expand)]
    [DataRow(RewriteOperation.Rewrite)]
    [DataRow(RewriteOperation.Explain)]
    [DataRow(RewriteOperation.KeyPoints)]
    public void The_answer_budget_never_exceeds_the_room_left_in_the_context(RewriteOperation operation)
    {
        var limit = operation == RewriteOperation.Expand ? TextActionService.ExpandChunkChars : TextActionService.ChunkChars;
        var text = new string('а', limit);

        var budget = TextActionService.MaxTokens(operation, text);

        Assert.IsTrue(budget >= 48);
        Assert.IsTrue(text.Length / 3 + budget + 250 <= TextActionService.ContextTokens + 1, $"{operation}: {budget}");
    }

    [TestMethod]
    public void Sentences_are_split_at_their_ends_and_rejoin_exactly()
    {
        var text = string.Concat(Enumerable.Repeat("Первое предложение здесь! Второе — тут? Третье… ", 30));

        var pieces = TextActionService.SplitForModel(text, 200);

        Assert.IsTrue(pieces.All(piece => piece.Length <= 200));
        Assert.AreEqual(text, string.Concat(pieces));
    }

    private sealed class SynchronousProgress(Action<TextActionProgress> report) : IProgress<TextActionProgress>
    {
        public void Report(TextActionProgress value) => report(value);
    }
}
