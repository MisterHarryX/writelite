using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using WriteLite.AI.Local;
using WriteLite.Models;

namespace WriteLite.Services.Ai;

/// <summary>Whether the local model can take a request right now, and if not, why.</summary>
public enum LocalModelReadiness
{
    Ready,

    /// <summary>No model pack on this machine.</summary>
    NotInstalled,

    /// <summary>The pack is there but the server neither answers nor could be started.</summary>
    NotRunning
}

/// <summary>
/// The local language model as the AI actions see it: one conversation in, text out.
/// </summary>
/// <remarks>
/// A seam rather than the backend itself, so the plumbing — chunking, guards, the
/// unavailable messages — is tested against a fake that answers instantly and
/// deterministically, and so nothing here can grow a dependency on HTTP.
/// </remarks>
public interface ILocalTextModel
{
    /// <summary>True when a model pack exists, whether or not its server is running.</summary>
    bool IsInstalled { get; }

    Task<LocalModelReadiness> EnsureReadyAsync(CancellationToken cancellationToken);

    /// <summary>Null when the model did not answer: timeout, server gone, empty reply.</summary>
    Task<string?> CompleteAsync(
        IReadOnlyList<(string Role, string Content)> messages,
        int maxTokens,
        double temperature,
        CancellationToken cancellationToken);
}

/// <summary>The shipped implementation: WriteLite's loopback Qwen runtime.</summary>
public sealed class QwenTextModel : ILocalTextModel
{
    private readonly QwenModelBackend _backend;

    public QwenTextModel(QwenModelBackend backend)
    {
        _backend = backend;
    }

    public bool IsInstalled => _backend.IsAvailable || _backend.LastError is null;

    public async Task<LocalModelReadiness> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        // EnsureReadyAsync probes health synchronously before it awaits anything; on the
        // dispatcher that is a two-second freeze of the window that asked.
        var ready = await Task.Run(() => _backend.EnsureReadyAsync(cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        if (!ready)
        {
            return _backend.IsAvailable ? LocalModelReadiness.NotRunning : LocalModelReadiness.NotInstalled;
        }

        // The backend hands out an HTTP client even when the server it tried to start never
        // came up, so "ready" is only believed once the server itself says so.
        return await ProbeAsync(_backend.ActiveEndpoint, cancellationToken).ConfigureAwait(false)
            ? LocalModelReadiness.Ready
            : LocalModelReadiness.NotRunning;
    }

    public Task<string?> CompleteAsync(
        IReadOnlyList<(string Role, string Content)> messages,
        int maxTokens,
        double temperature,
        CancellationToken cancellationToken) =>
        _backend.CompleteAsync(messages, maxTokens, temperature, cancellationToken);

    private static async Task<bool> ProbeAsync(string endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var baseUri = endpoint.EndsWith('/') ? endpoint : endpoint + "/";
            using var response = await client.GetAsync(baseUri + "health", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return false;
        }
    }
}

/// <summary>Thrown when an action needs the local model and the model cannot be used.</summary>
/// <remarks>
/// An <see cref="InvalidOperationException"/>, because that is what the preview window
/// already shows verbatim: the message is written for the reader, not for a log.
/// </remarks>
public sealed class LocalModelUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// One request: the text, already split into the pieces that can be replaced independently.
/// </summary>
/// <param name="Segments">
/// A selection is one segment. A whole note is one segment per paragraph, so a result can
/// be written back paragraph by paragraph and the headings, lists and pictures around
/// them keep their formatting.
/// </param>
public sealed record TextActionRequest(
    RewriteOperation Operation,
    IReadOnlyList<string> Segments,
    string? Instruction = null)
{
    public static TextActionRequest ForText(RewriteOperation operation, string text, string? instruction = null) =>
        new(operation, [text], instruction);
}

/// <summary>
/// What an action produced, segment by segment, before anyone accepted it.
/// </summary>
/// <param name="Suggestions">
/// One entry per input segment for a rewrite; a single entry for an advisory answer, which
/// is about the text rather than a replacement for it.
/// </param>
public sealed record TextActionResult(
    RewriteOperation Operation,
    IReadOnlyList<string> Originals,
    IReadOnlyList<string> Suggestions,
    string Backend,
    bool IsAdvisory,
    string? Notice)
{
    public string Original => string.Join("\n", Originals);

    public string Suggestion => string.Join("\n", Suggestions);

    public bool IsNoOp => !IsAdvisory && Originals.SequenceEqual(Suggestions, StringComparer.Ordinal);

    /// <summary>The shape the preview window takes.</summary>
    public RewriteResult ToRewriteResult() => new(Original, Suggestion, Operation, Backend, IsAdvisory, Notice);
}

/// <summary>Progress through a request that needed more than one model call.</summary>
public readonly record struct TextActionProgress(int Done, int Total);

/// <summary>
/// WriteLite's AI corrector: «Узнать главное», «Объясни простыми словами», «Сократить»,
/// «Удлинить», «Переписать», «Исправить ошибки», and the editor's other rewrites.
/// </summary>
/// <remarks>
/// <para><b>The model is small, and the code is built around that.</b> WriteAI is a
/// half-billion-parameter Qwen fine-tuned for grammar correction, served with a 768-token
/// context. Measured against it while this was written: asked to "expand" it returns the
/// text unchanged; asked to "shorten" without a target it trims a word or two; asked to
/// "fix errors" it rewrites phrasing it was not asked to touch. So:</para>
/// <list type="bullet">
/// <item>Text is cut into pieces that fit the context with room for the answer, and each
/// piece is a separate call — one note of any length, never a truncated prompt.</item>
/// <item>Explain and expand are asked with a worked example first, which is what moves a
/// model this size from echoing to doing; shorten is given a word budget.</item>
/// <item>Every answer is checked against what was asked — shorter for shorten, longer for
/// expand, no stray English, not the prompt echoed — retried once, and a piece that still
/// fails is left exactly as it was and counted in the notice.</item>
/// <item>«Исправить ошибки» does not use the model at all when the checking stack is
/// bound. The rules, the dictionary and the language engine find errors and propose
/// minimal fixes; the model's habit of rewriting a correct sentence is the opposite of
/// what a corrector is for.</item>
/// </list>
///
/// <para><b>Nothing here applies anything.</b> The service returns a proposal; the preview
/// window is the only path from a proposal to the text.</para>
/// </remarks>
public sealed partial class TextActionService
{
    /// <summary>Largest piece sent in one call for most actions, in characters.</summary>
    public const int ChunkChars = 700;

    /// <summary>Expansion needs room for an answer about twice its input.</summary>
    public const int ExpandChunkChars = 420;

    /// <summary>Server context, in tokens; the whole conversation and the answer share it.</summary>
    public const int ContextTokens = QwenModelBackend.DefaultServerContextTokens;

    /// <summary>The most text one request may carry. A long book chapter is not a note.</summary>
    public const int MaxRequestChars = 60_000;

    /// <summary>Confidence below which a checker finding is not applied by «Исправить ошибки».</summary>
    public const double MinimumFixConfidence = 0.6;

    private const string SystemPrompt =
        "Ты — редактор текста WriteLite. Работай только с присланным текстом. " +
        "Отвечай ТОЛЬКО итоговым текстом, без пояснений, без кавычек, без markdown. Пиши по-русски.";

    private readonly ILocalTextModel? _model;
    private readonly ITextAnalyzer? _checker;

    public TextActionService(ILocalTextModel? model, ITextAnalyzer? checker = null)
    {
        _model = model;
        _checker = checker;
    }

    /// <summary>True when the checking stack can fix errors without the model.</summary>
    public bool CanFixWithoutModel => _checker is not null;

    public bool HasModel => _model?.IsInstalled == true;

    public static bool IsAdvisory(RewriteOperation operation) =>
        operation is RewriteOperation.KeyPoints or RewriteOperation.Explain or RewriteOperation.Translate;

    public async Task<TextActionResult> RunAsync(
        TextActionRequest request,
        IProgress<TextActionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var segments = request.Segments.Select(segment => segment ?? string.Empty).ToList();
        var total = segments.Sum(segment => segment.Length);

        if (segments.All(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("Здесь нет текста, с которым можно что-то сделать.");
        }

        if (total > MaxRequestChars)
        {
            throw new InvalidOperationException(
                $"Слишком много текста для одного действия: {total} символов из {MaxRequestChars}. Выделите часть.");
        }

        CompatibilityLogger.Technical(
            "text-action-start",
            $"operation={request.Operation} segments={segments.Count} chars={total}");

        if (request.Operation == RewriteOperation.Grammar && _checker is not null)
        {
            return await FixWithCheckerAsync(request.Operation, segments, progress, cancellationToken).ConfigureAwait(false);
        }

        await RequireModelAsync(request.Operation, cancellationToken).ConfigureAwait(false);

        return request.Operation switch
        {
            RewriteOperation.KeyPoints => await KeyPointsAsync(segments, progress, cancellationToken).ConfigureAwait(false),
            RewriteOperation.Explain or RewriteOperation.Translate =>
                await AdvisoryAsync(request, segments, progress, cancellationToken).ConfigureAwait(false),
            _ => await TransformAsync(request, segments, progress, cancellationToken).ConfigureAwait(false)
        };
    }

    // ── Availability ─────────────────────────────────────────────────────────

    private async Task RequireModelAsync(RewriteOperation operation, CancellationToken cancellationToken)
    {
        var action = ActionName(operation);

        if (_model is null || !_model.IsInstalled)
        {
            throw new LocalModelUnavailableException(
                $"Для действия «{action}» нужна локальная модель WriteAI, а она не установлена. "
                + "Проверка орфографии и «Исправить ошибки» работают и без неё.");
        }

        var readiness = await _model.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (readiness != LocalModelReadiness.Ready)
        {
            throw new LocalModelUnavailableException(
                $"Не удалось запустить локальную модель WriteAI, поэтому «{action}» сейчас недоступно. "
                + "Проверьте её на странице «Локальный движок» и нажмите «Ещё раз».");
        }
    }

    public static string ActionName(RewriteOperation operation) => operation switch
    {
        RewriteOperation.KeyPoints => "Узнать главное",
        RewriteOperation.Explain => "Объясни простыми словами",
        RewriteOperation.Shorten => "Сократить",
        RewriteOperation.Expand => "Удлинить",
        RewriteOperation.Rewrite => "Переписать",
        RewriteOperation.Grammar => "Исправить ошибки",
        RewriteOperation.ImproveStyle => "Улучшить стиль",
        RewriteOperation.Formal => "Официальнее",
        RewriteOperation.Casual => "Проще и разговорнее",
        RewriteOperation.Simplify => "Упростить",
        RewriteOperation.Translate => "Перевести на английский",
        _ => "Своя инструкция"
    };

    // ── «Исправить ошибки» ───────────────────────────────────────────────────

    private async Task<TextActionResult> FixWithCheckerAsync(
        RewriteOperation operation,
        IReadOnlyList<string> segments,
        IProgress<TextActionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var suggestions = new List<string>(segments.Count);
        var fixes = 0;

        for (var index = 0; index < segments.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new TextActionProgress(index, segments.Count));

            var segment = segments[index];
            if (string.IsNullOrWhiteSpace(segment))
            {
                suggestions.Add(segment);
                continue;
            }

            var issues = await CheckAsync(segment, cancellationToken).ConfigureAwait(false);
            var chosen = ChooseFixes(segment, issues);
            var applied = IssueApplication.Apply(segment, chosen);

            if (applied.Ok)
            {
                suggestions.Add(applied.Text);
                fixes += chosen.Count;
            }
            else
            {
                suggestions.Add(segment);
            }
        }

        progress?.Report(new TextActionProgress(segments.Count, segments.Count));

        CompatibilityLogger.Technical("text-action-fix", $"segments={segments.Count} fixes={fixes}");

        return new TextActionResult(
            operation,
            segments,
            suggestions,
            "writelite-rules",
            IsAdvisory: false,
            Notice: fixes == 0 ? "Проверка не нашла ошибок, которые можно исправить автоматически." : null);
    }

    /// <summary>
    /// The deterministic answer of the checking stack, without waiting for the model lane.
    /// </summary>
    /// <remarks>
    /// The staged analyzer publishes its deterministic lane in about a second and its model
    /// lane up to a minute later, having added nothing measurable. A user who pressed
    /// «Исправить ошибки» is waiting for the preview, so the model lane is cancelled as soon
    /// as the deterministic answer is in.
    /// </remarks>
    private async Task<IReadOnlyList<TextIssue>> CheckAsync(string text, CancellationToken cancellationToken)
    {
        if (_checker is not IStagedTextAnalyzer staged)
        {
            return await _checker!.AnalyzeAsync(text, cancellationToken).ConfigureAwait(false);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var deterministic = new TaskCompletionSource<IReadOnlyList<TextIssue>>(TaskCreationOptions.RunContinuationsAsynchronously);

        var full = staged.AnalyzeStagedAsync(
            text,
            (lane, issues) =>
            {
                if (lane == AnalysisLane.Deterministic)
                {
                    deterministic.TrySetResult(issues);
                }
            },
            linked.Token);

        var first = await Task.WhenAny(deterministic.Task, full).ConfigureAwait(false);
        if (first == deterministic.Task)
        {
            linked.Cancel();
            ObserveQuietly(full);
            return deterministic.Task.Result;
        }

        try
        {
            return await full.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deterministic.Task.IsCompleted)
        {
            return deterministic.Task.Result;
        }
    }

    private static void ObserveQuietly(Task task) =>
        task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// The findings «Исправить ошибки» applies: real errors with one concrete fix, no style
    /// opinions, and never two edits over the same characters.
    /// </summary>
    internal static List<TextIssue> ChooseFixes(string text, IReadOnlyList<TextIssue> issues)
    {
        var candidates = issues
            .Where(issue => issue.Replacement is not null
                            && issue.Class is not IssueClass.Style
                            && issue.Category is not (IssueCategory.Style or IssueCategory.Readability)
                            && (issue.CanApplyAutomatically || issue.Confidence >= MinimumFixConfidence)
                            && issue.Start >= 0
                            && issue.Start + issue.Length <= text.Length
                            && string.Equals(text.Substring(issue.Start, issue.Length), issue.Original, StringComparison.Ordinal)
                            && !string.Equals(issue.Original, issue.Replacement, StringComparison.Ordinal))
            // Strongest first, so where two findings overlap the more certain one wins.
            .OrderByDescending(issue => issue.CanApplyAutomatically)
            .ThenByDescending(issue => issue.Confidence)
            .ThenBy(issue => issue.Start)
            .ToList();

        var chosen = new List<TextIssue>();
        foreach (var candidate in candidates)
        {
            var overlaps = chosen.Any(other =>
                candidate.Start < other.Start + Math.Max(other.Length, 1)
                && other.Start < candidate.Start + Math.Max(candidate.Length, 1));

            if (!overlaps)
            {
                chosen.Add(candidate);
            }
        }

        return chosen;
    }

    // ── Rewrites ─────────────────────────────────────────────────────────────

    private async Task<TextActionResult> TransformAsync(
        TextActionRequest request,
        IReadOnlyList<string> segments,
        IProgress<TextActionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var operation = request.Operation;
        var limit = operation == RewriteOperation.Expand ? ExpandChunkChars : ChunkChars;

        // Every segment becomes one or more pieces; a piece is one model call.
        var plan = segments
            .Select(segment => ShouldProcess(operation, segment) ? SplitForModel(segment, limit) : null)
            .ToList();

        var calls = plan.Sum(pieces => pieces?.Count ?? 0);
        if (calls == 0)
        {
            throw new InvalidOperationException(
                operation is RewriteOperation.Shorten or RewriteOperation.Expand
                    ? "Текст слишком короткий: здесь нечего сокращать или раскрывать."
                    : "Здесь нет текста, который можно переписать.");
        }

        var suggestions = new List<string>(segments.Count);
        var done = 0;
        var failed = 0;
        var answered = 0;

        for (var index = 0; index < segments.Count; index++)
        {
            if (plan[index] is not { } pieces)
            {
                suggestions.Add(segments[index]);
                continue;
            }

            var rewritten = new List<string>(pieces.Count);
            foreach (var piece in pieces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new TextActionProgress(done, calls));

                var answer = await TransformPieceAsync(request, piece, cancellationToken).ConfigureAwait(false);
                done++;

                if (answer.Answered)
                {
                    answered++;
                }

                if (answer.Text is null)
                {
                    failed++;
                    rewritten.Add(piece);
                }
                else
                {
                    rewritten.Add(answer.Text);
                }
            }

            suggestions.Add(JoinPieces(segments[index], pieces, rewritten));
        }

        progress?.Report(new TextActionProgress(calls, calls));
        cancellationToken.ThrowIfCancellationRequested();

        if (answered == 0)
        {
            throw new LocalModelUnavailableException(
                "Модель WriteAI не ответила. Возможно, она перегружена или ещё запускается — нажмите «Ещё раз».");
        }

        CompatibilityLogger.Technical(
            "text-action-transform",
            $"operation={operation} calls={calls} failed={failed}");

        return new TextActionResult(
            operation,
            segments,
            suggestions,
            "writelite-qwen",
            IsAdvisory: false,
            Notice: TransformNotice(operation, failed, calls));
    }

    private static string? TransformNotice(RewriteOperation operation, int failed, int calls)
    {
        var parts = new List<string>();

        if (failed > 0)
        {
            parts.Add(failed == calls
                ? operation switch
                {
                    RewriteOperation.Shorten => "Модели не удалось сократить этот текст — он остался как был.",
                    RewriteOperation.Expand =>
                        "Модели не удалось заметно раскрыть этот текст. WriteAI небольшая и лучше правит, чем дописывает.",
                    _ => "Модели не удалось предложить другой вариант — текст остался как был."
                }
                : $"Часть текста ({failed} из {calls} фрагментов) модель не обработала — она осталась как была.");
        }

        if (operation == RewriteOperation.Expand && failed < calls)
        {
            parts.Add("Проверьте подробности: модель могла добавить то, чего не было в исходном тексте.");
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private readonly record struct PieceAnswer(string? Text, bool Answered);

    private async Task<PieceAnswer> TransformPieceAsync(
        TextActionRequest request,
        string piece,
        CancellationToken cancellationToken)
    {
        var operation = request.Operation;
        var leading = piece.Length - piece.TrimStart().Length;
        var trailing = piece.Length - piece.TrimEnd().Length;
        var core = piece.Trim();

        var answered = false;
        string? best = null;

        // Expansion is asked twice at different temperatures and the better answer kept;
        // everything else is asked once and retried only when the answer fails its check.
        double[] attempts = operation == RewriteOperation.Expand
            ? [0.5, 0.8]
            : [Temperature(operation), RetryTemperature(operation)];

        for (var attempt = 0; attempt < attempts.Length; attempt++)
        {
            var raw = await _model!.CompleteAsync(
                Conversation(request, core, retry: attempt > 0),
                MaxTokens(operation, core),
                attempts[attempt],
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (raw is null)
            {
                continue;
            }

            answered = true;
            var cleaned = Clean(raw, core);
            if (cleaned is not null && Accepts(operation, core, cleaned))
            {
                if (operation != RewriteOperation.Expand)
                {
                    best = cleaned;
                    break;
                }

                if (best is null || cleaned.Length > best.Length)
                {
                    best = cleaned;
                }
            }
        }

        return best is null
            ? new PieceAnswer(null, answered)
            : new PieceAnswer(piece[..leading] + best + piece[(piece.Length - trailing)..], answered);
    }

    /// <summary>
    /// Whether an answer did what was asked.
    /// </summary>
    /// <remarks>
    /// Deliberately coarse. These do not judge quality — the preview is where a person does
    /// that — they reject the answers a small model is known to give instead of doing the
    /// task: the input echoed back, an English word in a Russian sentence, a "shortened"
    /// text that is not shorter.
    /// </remarks>
    internal static bool Accepts(RewriteOperation operation, string original, string answer)
    {
        if (answer.Length == 0)
        {
            return false;
        }

        if (!LatinWord().IsMatch(original) && LatinWord().IsMatch(answer)
            && operation is not RewriteOperation.Translate)
        {
            return false;
        }

        if (IsDegenerate(original, answer))
        {
            return false;
        }

        var same = string.Equals(Squash(original), Squash(answer), StringComparison.Ordinal);

        return operation switch
        {
            RewriteOperation.Shorten => answer.Length <= original.Length * 0.9 && answer.Length >= original.Length * 0.15,
            RewriteOperation.Expand => answer.Length >= original.Length * 1.15 && answer.Length <= original.Length * 4 + 200,
            RewriteOperation.Grammar => Similar(original, answer),
            _ => !same && answer.Length <= original.Length * 3 + 200
        };
    }

    /// <summary>
    /// The shapes a small model falls into when it runs out of things to say: a chain of
    /// «Ярлык: Ярлык: Ярлык:», or the same few words over and over.
    /// </summary>
    internal static bool IsDegenerate(string original, string answer)
    {
        var colons = answer.Count(ch => ch == ':');
        if (colons >= 4 && colons > original.Count(ch => ch == ':') * 2 + 2)
        {
            return true;
        }

        // Repetition counts only when the model brought it: a text that repeats itself is
        // allowed to come back repeating itself.
        return Variety(answer) < 0.4 && Variety(original) >= 0.6;
    }

    /// <summary>Share of distinct words; 1 for short texts, where the measure means nothing.</summary>
    private static double Variety(string text)
    {
        var words = Words(text);
        return words.Length < 12
            ? 1
            : (double)words.Distinct(StringComparer.CurrentCultureIgnoreCase).Count() / words.Length;
    }

    /// <summary>A grammar fix may change a few words, not rephrase the sentence.</summary>
    private static bool Similar(string original, string answer)
    {
        var a = Words(original);
        var b = Words(answer);
        if (a.Length == 0)
        {
            return false;
        }

        var common = a.Intersect(b, StringComparer.CurrentCultureIgnoreCase).Count();
        return common >= a.Distinct(StringComparer.CurrentCultureIgnoreCase).Count() * 0.7
               && Math.Abs(a.Length - b.Length) <= Math.Max(2, a.Length / 5);
    }

    private static string[] Words(string text) =>
        text.Split((char[])[' ', '\n', '\t', ',', '.', '!', '?', ';', ':', '«', '»', '"', '(', ')'], StringSplitOptions.RemoveEmptyEntries);

    private static string Squash(string text) => Whitespace().Replace(text, " ").Trim().TrimEnd('.', '!', '?');

    /// <summary>Very short lines — headings, labels — are not worth shortening, expanding or rewriting.</summary>
    internal static bool ShouldProcess(RewriteOperation operation, string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return false;
        }

        var words = Words(segment).Length;

        // A short line with no sentence ending is a heading or a label. Asked to expand
        // «Планы на запуск», the shipped model produced a chain of invented sub-headings;
        // asked to rewrite it, it produces a sentence. Neither belongs in a heading.
        var trimmed = segment.TrimEnd();
        if (words < 8 && trimmed.Length > 0 && !".!?…".Contains(trimmed[^1], StringComparison.Ordinal))
        {
            return operation == RewriteOperation.Grammar;
        }

        return operation switch
        {
            RewriteOperation.Shorten => words >= 6,
            RewriteOperation.Expand => words >= 4,
            _ => words >= 2
        };
    }

    /// <summary>
    /// Cuts a long paragraph into pieces of at most <paramref name="limit"/> characters at
    /// sentence boundaries, so no prompt outgrows the model's context.
    /// </summary>
    internal static List<string> SplitForModel(string text, int limit)
    {
        if (text.Length <= limit)
        {
            return [text];
        }

        var sentences = SentenceEnd().Split(text).Where(sentence => sentence.Length > 0).ToList();
        var pieces = new List<string>();
        var current = new StringBuilder();

        foreach (var sentence in sentences)
        {
            if (current.Length > 0 && current.Length + sentence.Length > limit)
            {
                pieces.Add(current.ToString());
                current.Clear();
            }

            // A single sentence longer than the limit is cut at a word boundary: rare, and
            // better than a prompt the server truncates from the front.
            var remaining = sentence;
            while (remaining.Length > limit)
            {
                var cut = remaining.LastIndexOf(' ', limit);
                if (cut <= 0)
                {
                    cut = limit;
                }

                pieces.Add(remaining[..cut]);
                remaining = remaining[cut..];
            }

            current.Append(remaining);
        }

        if (current.Length > 0)
        {
            pieces.Add(current.ToString());
        }

        return pieces;
    }

    private static string JoinPieces(string original, IReadOnlyList<string> pieces, IReadOnlyList<string> rewritten)
    {
        if (pieces.Count == 1)
        {
            return rewritten[0];
        }

        // Pieces came from splitting after a sentence end, so the separator the original had
        // is at the end of each piece; re-joining with nothing in between preserves it.
        var builder = new StringBuilder();
        for (var index = 0; index < rewritten.Count; index++)
        {
            var text = rewritten[index];
            if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]) && text.Length > 0 && !char.IsWhiteSpace(text[0]))
            {
                builder.Append(' ');
            }

            builder.Append(text);
        }

        return builder.ToString();
    }

    // ── «Узнать главное», «Объясни простыми словами» ─────────────────────────

    private async Task<TextActionResult> KeyPointsAsync(
        IReadOnlyList<string> segments,
        IProgress<TextActionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var chunks = PackSegments(segments, ChunkChars);
        var points = new List<string>();
        var answered = 0;

        for (var index = 0; index < chunks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new TextActionProgress(index, chunks.Count + (chunks.Count > 1 ? 1 : 0)));

            var raw = await _model!.CompleteAsync(
                Conversation(new TextActionRequest(RewriteOperation.KeyPoints, segments), chunks[index], retry: false),
                160,
                0.2,
                cancellationToken).ConfigureAwait(false);

            if (raw is null)
            {
                continue;
            }

            answered++;
            points.AddRange(ParsePoints(raw));
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (answered == 0)
        {
            throw new LocalModelUnavailableException(
                "Модель WriteAI не ответила. Возможно, она перегружена или ещё запускается — нажмите «Ещё раз».");
        }

        points = Deduplicate(points);

        // A long note gives a handful of points per piece. When that is too many to be
        // "the main thing", the points themselves are condensed in one more call — they are
        // short, so they fit where the note did not.
        if (chunks.Count > 1 && points.Count > 6)
        {
            progress?.Report(new TextActionProgress(chunks.Count, chunks.Count + 1));
            var joined = string.Join("\n", points.Select(point => "— " + point));
            if (joined.Length <= ChunkChars * 1.4)
            {
                var raw = await _model!.CompleteAsync(
                    Conversation(new TextActionRequest(RewriteOperation.KeyPoints, segments), joined, retry: false),
                    160,
                    0.2,
                    cancellationToken).ConfigureAwait(false);

                var condensed = raw is null ? [] : Deduplicate(ParsePoints(raw));
                if (condensed.Count is >= 2 and < 9)
                {
                    points = condensed;
                }
            }
        }

        progress?.Report(new TextActionProgress(1, 1));

        if (points.Count == 0)
        {
            throw new InvalidOperationException("Модель не смогла выделить главное в этом тексте. Попробуйте ещё раз.");
        }

        var text = string.Join("\n", points.Take(10).Select(point => "— " + point));
        return new TextActionResult(RewriteOperation.KeyPoints, segments, [text], "writelite-qwen", IsAdvisory: true, Notice: null);
    }

    private async Task<TextActionResult> AdvisoryAsync(
        TextActionRequest request,
        IReadOnlyList<string> segments,
        IProgress<TextActionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var chunks = PackSegments(segments, ChunkChars);
        var answers = new List<string>();

        for (var index = 0; index < chunks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new TextActionProgress(index, chunks.Count));

            string? accepted = null;
            foreach (var temperature in new[] { Temperature(request.Operation), 0.1 })
            {
                var raw = await _model!.CompleteAsync(
                    Conversation(request, chunks[index], retry: temperature != Temperature(request.Operation)),
                    MaxTokens(request.Operation, chunks[index]),
                    temperature,
                    cancellationToken).ConfigureAwait(false);

                var cleaned = raw is null ? null : StripLeadIn(Clean(raw, chunks[index]));
                if (cleaned is not null
                    && (request.Operation == RewriteOperation.Translate || !LatinWord().IsMatch(cleaned) || LatinWord().IsMatch(chunks[index])))
                {
                    accepted = cleaned;
                    break;
                }
            }

            if (accepted is not null)
            {
                answers.Add(accepted);
            }
        }

        progress?.Report(new TextActionProgress(chunks.Count, chunks.Count));
        cancellationToken.ThrowIfCancellationRequested();

        if (answers.Count == 0)
        {
            throw new LocalModelUnavailableException(
                "Модель WriteAI не ответила. Возможно, она перегружена или ещё запускается — нажмите «Ещё раз».");
        }

        var notice = answers.Count < chunks.Count
            ? $"Модель ответила не на весь текст: {answers.Count} из {chunks.Count} частей."
            : null;

        return new TextActionResult(
            request.Operation,
            segments,
            [string.Join("\n\n", answers)],
            "writelite-qwen",
            IsAdvisory: true,
            notice);
    }

    /// <summary>Paragraphs packed into pieces that fit one call, splitting only the ones that do not fit alone.</summary>
    internal static List<string> PackSegments(IReadOnlyList<string> segments, int limit)
    {
        var pieces = new List<string>();
        var current = new StringBuilder();

        foreach (var segment in segments.Where(segment => !string.IsNullOrWhiteSpace(segment)).Select(segment => segment.Trim()))
        {
            foreach (var part in SplitForModel(segment, limit))
            {
                if (current.Length > 0 && current.Length + part.Length + 1 > limit)
                {
                    pieces.Add(current.ToString());
                    current.Clear();
                }

                if (current.Length > 0)
                {
                    current.Append('\n');
                }

                current.Append(part.Trim());
            }
        }

        if (current.Length > 0)
        {
            pieces.Add(current.ToString());
        }

        return pieces;
    }

    /// <summary>
    /// Reads a list out of whatever shape the model gave it: one point per line, all on one
    /// line separated by dashes, numbered, or bulleted.
    /// </summary>
    internal static List<string> ParsePoints(string raw)
    {
        var lines = raw.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Only a line that is itself a bullet is split further, and only where a dash
        // follows a comma or a full stop: «— один, — два». A dash inside a sentence —
        // «Фотосинтез — это процесс» — is punctuation, not a list.
        var parts = lines.SelectMany(line => PointMarker().IsMatch(line) ? InlineBullets().Split(line) : [line]);

        return parts
            .Select(part => PointMarker().Replace(part, string.Empty).Trim().TrimEnd(',', ';').Trim())
            .Where(part => part.Length >= 3 && part.Any(char.IsLetter))
            .Select(part => char.ToUpper(part[0], System.Globalization.CultureInfo.CurrentCulture) + part[1..])
            .ToList();
    }

    private static List<string> Deduplicate(IEnumerable<string> points)
    {
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        return points.Where(point => seen.Add(point.TrimEnd('.'))).ToList();
    }

    // ── Prompts ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The conversation for one call. Explain and expand carry a worked example first.
    /// </summary>
    internal static IReadOnlyList<(string Role, string Content)> Conversation(
        TextActionRequest request,
        string text,
        bool retry)
    {
        var instruction = Instruction(request.Operation, text, request.Instruction, retry);
        var messages = new List<(string Role, string Content)> { ("system", SystemPrompt) };

        if (Example(request.Operation) is { } example)
        {
            messages.Add(("user", Instruction(request.Operation, example.Input, null, retry: false) + "\n\nТекст:\n" + example.Input));
            messages.Add(("assistant", example.Output));
        }

        messages.Add(("user", instruction + "\n\nТекст:\n" + text));
        return messages;
    }

    private static (string Input, string Output)? Example(RewriteOperation operation) => operation switch
    {
        RewriteOperation.Expand => (
            "Мы переезжаем в новый офис. Он больше старого.",
            "Мы переезжаем в новый офис, и это важное событие для всей команды. Новое помещение заметно больше старого, "
            + "поэтому у каждого появится больше места для работы, а для встреч можно будет выделить отдельные комнаты."),
        RewriteOperation.Explain => (
            "Инфляция снижает покупательную способность денег.",
            "Простыми словами: когда цены растут, на те же деньги можно купить меньше, чем раньше."),
        _ => null
    };

    internal static string Instruction(RewriteOperation operation, string text, string? custom, bool retry)
    {
        var words = Words(text).Length;
        return operation switch
        {
            RewriteOperation.KeyPoints =>
                "Выдели главное: перечисли 2–4 ключевые мысли текста короткими пунктами, каждый с новой строки, начиная с «— ».",
            RewriteOperation.Explain => "Объясни простыми словами, что значит этот текст. Ответь 2–3 короткими предложениями, без списков и без новых подробностей.",
            RewriteOperation.Shorten => retry
                ? $"Сократи текст до {Math.Max(3, words * 2 / 5)} слов. Оставь только главную мысль."
                : $"Сократи текст до {Math.Max(3, words * 11 / 20)} слов. Оставь только самое важное.",
            RewriteOperation.Expand => "Удлини текст, добавив подробности и пояснения, но не выдумывая новых фактов.",
            RewriteOperation.Rewrite => retry
                ? "Перескажи этот текст своими словами: измени порядок слов и замени слова синонимами, сохранив смысл."
                : "Перепиши текст другими словами, сохранив смысл.",
            RewriteOperation.Grammar => "Исправь грамматику, пунктуацию и орфографию. Больше ничего не меняй.",
            RewriteOperation.ImproveStyle => "Улучши стиль, читаемость и структуру предложений, сохранив смысл.",
            RewriteOperation.Formal => "Сделай текст более официальным и профессиональным.",
            RewriteOperation.Casual => "Сделай текст более разговорным и простым.",
            RewriteOperation.Simplify => "Упрости текст, не теряя важного смысла.",
            RewriteOperation.Translate => "Переведи текст на английский язык.",
            RewriteOperation.Custom when !string.IsNullOrWhiteSpace(custom) => custom.Trim(),
            _ => "Перепиши текст, сохранив смысл."
        };
    }

    /// <summary>A rewrite that came back unchanged needs more freedom; anything else, less.</summary>
    private static double RetryTemperature(RewriteOperation operation) =>
        operation is RewriteOperation.Rewrite or RewriteOperation.Casual or RewriteOperation.Formal ? 0.75 : 0.1;

    private static double Temperature(RewriteOperation operation) => operation switch
    {
        RewriteOperation.Rewrite => 0.5,
        RewriteOperation.Expand => 0.5,
        RewriteOperation.Explain => 0.3,
        RewriteOperation.Grammar or RewriteOperation.Translate => 0.1,
        _ => 0.2
    };

    /// <summary>
    /// The answer budget: enough for what the action legitimately produces, never more than
    /// the context left after the prompt.
    /// </summary>
    internal static int MaxTokens(RewriteOperation operation, string text)
    {
        // About three characters per token for Russian on this tokenizer, measured.
        var input = text.Length / 3 + 1;
        var wanted = operation switch
        {
            RewriteOperation.Shorten => input + 32,
            RewriteOperation.Expand => input * 5 / 2 + 64,
            RewriteOperation.Explain => Math.Max(120, input + 64),
            RewriteOperation.KeyPoints => 160,
            _ => input * 3 / 2 + 48
        };

        // System prompt, instruction, example and chat template: about 250 tokens at most.
        var room = ContextTokens - input - 250;
        return Math.Clamp(wanted, 48, Math.Max(48, room));
    }

    private static string? Clean(string raw, string original) => ResponseCleaner.Clean(raw, original);

    /// <summary>Removes the frame the explain example teaches, so the answer reads as prose.</summary>
    private static string? StripLeadIn(string? text)
    {
        if (text is null)
        {
            return null;
        }

        foreach (var lead in new[] { "Простыми словами:", "Простыми словами,", "Объяснение:" })
        {
            if (text.StartsWith(lead, StringComparison.OrdinalIgnoreCase))
            {
                var rest = text[lead.Length..].TrimStart();
                return rest.Length == 0 ? null : char.ToUpper(rest[0], System.Globalization.CultureInfo.CurrentCulture) + rest[1..];
            }
        }

        return text;
    }

    [GeneratedRegex(@"\b[A-Za-z]{3,}\b")]
    private static partial Regex LatinWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // Splits after sentence-ending punctuation, keeping the punctuation and the space
    // with the sentence that owns them.
    [GeneratedRegex(@"(?<=[.!?…»)]\s+)")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"[,;.]\s+(?:[—–-]\s+)+")]
    private static partial Regex InlineBullets();

    [GeneratedRegex(@"^\s*([—–\-•*·]+|\d+[.)])\s*")]
    private static partial Regex PointMarker();
}
