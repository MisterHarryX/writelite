using WriteLite.Services.Ai;

namespace WriteLite.Services.Reading;

/// <summary>The two sides of a card, before anyone has agreed to keep it.</summary>
public readonly record struct StudyCardDraft(string Front, string Back);

/// <summary>How an attempt to draft a card ended.</summary>
public enum StudyCardDraftStatus
{
    /// <summary>The local model produced a usable question and answer.</summary>
    Drafted,

    /// <summary>No model is bound to this build at all.</summary>
    NotConfigured,

    /// <summary>A model is configured but the runtime did not answer.</summary>
    ModelUnavailable,

    /// <summary>The model answered, but with nothing that could be read as a card.</summary>
    Unusable,

    /// <summary>The reader cancelled, or closed the editor while the model was thinking.</summary>
    Cancelled,

    /// <summary>Something went wrong. <see cref="StudyCardDraftResult.Message"/> says what.</summary>
    Failed
}

/// <summary>
/// The outcome of one drafting attempt.
/// </summary>
/// <param name="Message">
/// A sentence for the reader. Never a stack trace, and never absent for a failure —
/// a card editor that goes quiet is indistinguishable from one that is broken.
/// </param>
public sealed record StudyCardDraftResult(
    StudyCardDraftStatus Status,
    StudyCardDraft? Draft = null,
    string? Message = null)
{
    public bool IsSuccess => Status == StudyCardDraftStatus.Drafted && Draft is not null;
}

/// <summary>
/// Turns a passage into a question and an answer using the local model.
/// </summary>
/// <remarks>
/// Deliberately built on <see cref="IEditorAiService"/> — the same loopback model the
/// editor's rewrite menu already uses — rather than on a second runtime. There is one
/// local model in WriteLite and this asks it a different question, which is why the
/// whole service is a prompt, a parser and a set of honest failure states.
///
/// The honesty matters more than it sounds. <c>IEditorAiService</c> degrades to a
/// deterministic rule fallback when the runtime is unreachable, and for a custom
/// instruction that fallback has nothing to do and returns the input unchanged. Read
/// naively, that echo parses into a perfectly plausible-looking card whose front is
/// the passage's first line — a card the reader would be told was drafted by WriteLite
/// AI. Every path here therefore checks which backend actually answered, and reports
/// "the model is not running" rather than dressing up an echo.
/// </remarks>
public sealed class StudyCardDraftService(IEditorAiService? ai)
{
    /// <summary>Longest passage sent to the model. Beyond a paragraph a card stops being a card.</summary>
    private static readonly int MaxPassage = WriteLiteDefaults.TextLimits.StudyCardMaxPassage;

    /// <summary>How long the reader waits before the attempt is called off.</summary>
    private static readonly TimeSpan DraftTimeout = WriteLiteDefaults.Debounce.StudyCardDraftTimeout;

    private readonly IEditorAiService? _ai = ai;

    /// <summary>True when a local model is bound, so the AI action is worth offering.</summary>
    public bool IsAvailable => _ai is not null;

    /// <summary>True when that model also reports itself reachable right now.</summary>
    public bool IsModelReady => _ai?.IsModelAvailable == true;

    public async Task<StudyCardDraftResult> DraftAsync(
        string passage,
        CancellationToken cancellationToken = default)
    {
        if (_ai is null)
        {
            return new StudyCardDraftResult(
                StudyCardDraftStatus.NotConfigured,
                Message: "Локальная модель не подключена в этой сборке.");
        }

        if (string.IsNullOrWhiteSpace(passage))
        {
            return new StudyCardDraftResult(
                StudyCardDraftStatus.Unusable,
                Message: "Нечего превращать в карточку — фрагмент пуст.");
        }

        var text = passage.Trim();
        if (text.Length > MaxPassage)
        {
            text = text[..MaxPassage];
        }

        var request = new RewriteRequest(
            RewriteOperation.Custom,
            text,
            Instruction:
            "Составь учебную карточку по этому фрагменту. Ответь ровно двумя строками на русском языке, " +
            "без пояснений и без markdown:\n" +
            "Вопрос: <короткий вопрос по фрагменту>\n" +
            "Ответ: <краткий ответ по фрагменту>");

        // A model that is loading, wedged or gone answers slowly or not at all. The
        // reader's own cancellation and this deadline are linked so that whichever
        // happens first ends the wait, and neither leaves a task running against a
        // dialog that has closed.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(DraftTimeout);

        try
        {
            var result = await _ai.RewriteAsync(request, deadline.Token).ConfigureAwait(false);

            // The rule fallback cannot answer a custom instruction and says so by
            // handing the text back. Presenting that as a draft is the bug this check
            // exists to prevent.
            if (IsFallback(result.Backend))
            {
                CompatibilityLogger.Technical(
                    "reading-card-draft-unavailable", $"backend={result.Backend}");

                return new StudyCardDraftResult(
                    StudyCardDraftStatus.ModelUnavailable,
                    Message: "Локальная модель сейчас не отвечает. Карточку можно написать вручную.");
            }

            if (Parse(result.Suggestion, text) is not { } draft)
            {
                CompatibilityLogger.Technical("reading-card-draft-unusable", "reason=parse");

                return new StudyCardDraftResult(
                    StudyCardDraftStatus.Unusable,
                    Message: "Модель ответила, но разобрать карточку не удалось. Попробуйте ещё раз.");
            }

            return new StudyCardDraftResult(StudyCardDraftStatus.Drafted, draft);
        }
        catch (OperationCanceledException)
        {
            // The reader's own cancellation is a cancellation; the deadline expiring is
            // a model that never answered, and the two deserve different sentences.
            return cancellationToken.IsCancellationRequested
                ? new StudyCardDraftResult(StudyCardDraftStatus.Cancelled)
                : new StudyCardDraftResult(
                    StudyCardDraftStatus.ModelUnavailable,
                    Message: $"Модель не ответила за {DraftTimeout.TotalSeconds:0} секунд.");
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical(
                "reading-card-draft-failed",
                $"type={exception.GetType().Name} message={exception.Message}");

            return new StudyCardDraftResult(
                StudyCardDraftStatus.Failed,
                Message: "Не удалось составить карточку. Подробности — в журнале диагностики.");
        }
    }

    /// <summary>True when this answer came from the deterministic rules rather than the model.</summary>
    internal static bool IsFallback(string? backend) =>
        backend is null
        || backend.StartsWith("offline", StringComparison.OrdinalIgnoreCase)
        || backend.Equals("none", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the model's two lines, tolerating the ways it gets the shape wrong.
    /// </summary>
    /// <remarks>
    /// Small local models add a preamble, wrap the answer in asterisks, or use "Q:"
    /// and "A:" despite being asked in Russian. All of that is cheaper to accept here
    /// than to fight in the prompt.
    ///
    /// What is not accepted is an answer that is simply the passage back. That used to
    /// be turned into a card — front from the first line, back from the second — and
    /// saved with the badge that says WriteLite AI wrote it. Returning null instead
    /// leaves the reader in the manual editor, which is the honest place to be when the
    /// model has not contributed anything.
    /// </remarks>
    internal static StudyCardDraft? Parse(string? response, string passage)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        string? front = null;
        string? back = null;

        foreach (var raw in response.Split('\n'))
        {
            var line = raw.Trim().Trim('*', '#', '-', '•', ' ');
            if (line.Length == 0)
            {
                continue;
            }

            if (front is null && TryTake(line, ["Вопрос:", "Front:", "Q:", "Лицевая сторона:"]) is { } question)
            {
                front = question;
                continue;
            }

            if (back is null && TryTake(line, ["Ответ:", "Back:", "A:", "Обратная сторона:"]) is { } answer)
            {
                back = answer;
            }
        }

        // No labels at all: take the first two non-empty lines in order, which is what
        // the model produces when it drops the format but keeps the structure.
        if (front is null && back is null)
        {
            var lines = response
                .Split('\n')
                .Select(line => line.Trim().Trim('*', '#', '-', '•', ' '))
                .Where(line => line.Length > 0)
                .Take(2)
                .ToArray();

            if (lines.Length == 2)
            {
                front = lines[0];
                back = lines[1];
            }
        }

        front = front?.Trim();
        back = back?.Trim();

        // A card needs a question. A back that is missing falls back to the passage,
        // which is a real answer; a front that is missing means the model produced
        // prose, not a card.
        if (string.IsNullOrWhiteSpace(front))
        {
            return null;
        }

        if (Echoes(front, passage))
        {
            return null;
        }

        return new StudyCardDraft(front, string.IsNullOrWhiteSpace(back) ? passage : back!);
    }

    /// <summary>True when a "question" is really just the passage handed back.</summary>
    private static bool Echoes(string front, string passage)
    {
        var normalizedFront = Squash(front);
        var normalizedPassage = Squash(passage);

        return normalizedFront.Length > 0
               && (normalizedPassage.Equals(normalizedFront, StringComparison.OrdinalIgnoreCase)
                   || normalizedPassage.StartsWith(normalizedFront, StringComparison.OrdinalIgnoreCase));
    }

    private static string Squash(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ").Trim();

    private static string? TryTake(string line, string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var value = line[prefix.Length..].Trim().Trim('*', ' ');
                return value.Length > 0 ? value : null;
            }
        }

        return null;
    }
}
