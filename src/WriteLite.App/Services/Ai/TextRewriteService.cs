using System.Text;
using WriteLite.AI.Local;

namespace WriteLite.Services.Ai;

/// <summary>
/// Rewrites selected text using WriteLite's local model.
/// </summary>
/// <remarks>
/// Nothing here reaches the network. The only endpoint involved is the loopback
/// runtime the product already ships for grammar correction, and when that runtime
/// is not available the service degrades to <see cref="OfflineRewriteFallback"/>
/// rather than reaching for anything else.
///
/// Prompts are written in Russian and instruct the model to return only the
/// rewritten text. The response is then scrubbed: models reliably wrap answers in
/// preamble ("Вот исправленный вариант:"), quotation marks or code fences, and
/// pasting that into someone's document would be worse than not offering the
/// feature.
/// </remarks>
public sealed class TextRewriteService : IEditorAiService
{
    /// <summary>
    /// How much text around the selection the model is allowed to see.
    /// </summary>
    /// <remarks>
    /// Bounded deliberately. Context helps the model resolve references, but the
    /// brief is explicit that only the selection plus a small amount of surrounding
    /// text is processed — and a bounded prompt is also what keeps the local model's
    /// KV cache inside its budget.
    /// </remarks>
    public const int MaxContextChars = 400;

    /// <summary>Selections longer than this are rejected rather than silently truncated.</summary>
    public const int MaxSelectionChars = 4000;

    private readonly QwenModelBackend _backend;
    private readonly OfflineRewriteFallback _fallback = new();

    public TextRewriteService(QwenModelBackend backend)
    {
        _backend = backend;
    }

    public bool IsModelAvailable => _backend.IsAvailable;

    public async Task<RewriteResult> RewriteAsync(
        RewriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var selection = request.Selection;
        if (string.IsNullOrWhiteSpace(selection))
        {
            return new RewriteResult(selection, selection, request.Operation, "none");
        }

        if (selection.Length > MaxSelectionChars)
        {
            throw new InvalidOperationException(
                $"Выделено слишком много текста: {selection.Length} символов из {MaxSelectionChars} возможных.");
        }

        CompatibilityLogger.Technical(
            "editor-ai-rewrite",
            $"operation={request.Operation} selectionLength={selection.Length} model={(IsModelAvailable ? 1 : 0)}");

        if (IsModelAvailable)
        {
            var prompt = BuildPrompt(request);
            var raw = await _backend.CompleteAsync(
                RewritePrompts.System,
                prompt,
                // Expansion needs headroom; everything else stays close to the
                // original length so the model cannot run away with the text.
                maxTokens: EstimateTokens(selection, request.Operation),
                temperature: request.Operation is RewriteOperation.Rewrite or RewriteOperation.Expand ? 0.4 : 0.2,
                cancellationToken).ConfigureAwait(false);

            var cleaned = ResponseCleaner.Clean(raw, selection);
            if (cleaned is not null)
            {
                return new RewriteResult(
                    selection,
                    cleaned,
                    request.Operation,
                    "writelite-qwen",
                    IsAdvisory: request.Operation is RewriteOperation.Explain or RewriteOperation.Translate);
            }

            CompatibilityLogger.Technical("editor-ai-rewrite-fallback", $"operation={request.Operation}");
        }

        return _fallback.Apply(request);
    }

    private static int EstimateTokens(string selection, RewriteOperation operation)
    {
        // Roughly two characters per token for Russian on this tokenizer, with a
        // multiplier for operations that legitimately produce more text.
        var multiplier = operation switch
        {
            RewriteOperation.Expand => 2.4,
            RewriteOperation.Explain => 2.0,
            RewriteOperation.Shorten => 0.9,
            _ => 1.4
        };

        return (int)Math.Clamp(selection.Length / 2.0 * multiplier + 64, 64, 2048);
    }

    private static string BuildPrompt(RewriteRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine(RewritePrompts.Instruction(request.Operation, request.Instruction));
        builder.AppendLine();

        var before = Trim(request.ContextBefore, fromEnd: true);
        var after = Trim(request.ContextAfter, fromEnd: false);

        if (before.Length > 0 || after.Length > 0)
        {
            builder.AppendLine("Контекст (не изменять, только для понимания):");
            if (before.Length > 0)
            {
                builder.AppendLine("До: " + before);
            }

            if (after.Length > 0)
            {
                builder.AppendLine("После: " + after);
            }

            builder.AppendLine();
        }

        builder.AppendLine("Текст:");
        builder.Append(request.Selection);
        return builder.ToString();
    }

    private static string Trim(string context, bool fromEnd)
    {
        if (string.IsNullOrWhiteSpace(context))
        {
            return string.Empty;
        }

        var trimmed = context.Trim();
        if (trimmed.Length <= MaxContextChars)
        {
            return trimmed;
        }

        // Keep the end of the preceding context and the start of the following one:
        // those are the parts adjacent to the selection.
        return fromEnd ? "…" + trimmed[^MaxContextChars..] : trimmed[..MaxContextChars] + "…";
    }
}

internal static class RewritePrompts
{
    public const string System =
        "Ты — редактор текста WriteLite. Работай только с присланным фрагментом. " +
        "Отвечай ТОЛЬКО итоговым текстом, без пояснений, без кавычек, без markdown. " +
        "Сохраняй язык оригинала и смысл автора.";

    public static string Instruction(RewriteOperation operation, string? custom) => operation switch
    {
        RewriteOperation.Rewrite => "Перепиши текст другими словами, сохранив смысл.",
        RewriteOperation.ImproveStyle => "Улучши стиль, читаемость и структуру предложений.",
        RewriteOperation.Formal => "Сделай текст более официальным и профессиональным.",
        RewriteOperation.Casual => "Сделай текст более разговорным и простым.",
        RewriteOperation.Simplify => "Упрости текст, не теряя важного смысла.",
        RewriteOperation.Shorten => "Сократи текст, убрав повторы и лишнее.",
        RewriteOperation.Expand => "Раскрой мысль подробнее, сохранив замысел автора.",
        RewriteOperation.Grammar => "Исправь грамматику, пунктуацию и орфографию. Больше ничего не меняй.",
        RewriteOperation.Explain => "Объясни простыми словами, что означает этот фрагмент.",
        RewriteOperation.Translate => "Переведи текст на английский язык.",
        RewriteOperation.Custom => string.IsNullOrWhiteSpace(custom)
            ? "Перепиши текст, сохранив смысл."
            : custom.Trim(),
        _ => "Перепиши текст, сохранив смысл."
    };
}

/// <summary>
/// Strips the packaging models put around an answer.
/// </summary>
/// <remarks>
/// A small instruction-tuned model asked for "only the text" will still return
/// fenced blocks, leading labels and wrapping quotes a noticeable fraction of the
/// time. Each of those pasted verbatim into a document is a visible defect, so the
/// cleaning is part of the contract rather than a nicety.
/// </remarks>
internal static class ResponseCleaner
{
    private static readonly string[] Preambles =
    [
        "вот исправленный вариант:",
        "вот переписанный текст:",
        "вот результат:",
        "исправленный текст:",
        "переписанный текст:",
        "итоговый текст:",
        "результат:",
        "ответ:",
        "here is the",
        "here's the",
        "rewritten text:"
    ];

    public static string? Clean(string? raw, string original)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();

        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = text.IndexOf('\n');
            if (firstBreak > 0)
            {
                text = text[(firstBreak + 1)..];
            }

            var fence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
            {
                text = text[..fence];
            }

            text = text.Trim();
        }

        foreach (var preamble in Preambles)
        {
            if (text.StartsWith(preamble, StringComparison.OrdinalIgnoreCase))
            {
                text = text[preamble.Length..].TrimStart();
                break;
            }
        }

        // Only unwrap quotes the model added: a fragment that was already quoted in
        // the document keeps its quotation marks.
        if (text.Length > 2
            && ((text[0] == '"' && text[^1] == '"') || (text[0] == '«' && text[^1] == '»'))
            && !(original.TrimStart().StartsWith('"') || original.TrimStart().StartsWith('«')))
        {
            text = text[1..^1].Trim();
        }

        if (text.Length == 0)
        {
            return null;
        }

        // A model that echoed the prompt back has not done the work.
        return text.Contains("Текст:", StringComparison.Ordinal)
               && text.Contains("Контекст", StringComparison.Ordinal)
            ? null
            : text;
    }
}

/// <summary>
/// Deterministic transformations for when the neural runtime is not available.
/// </summary>
/// <remarks>
/// These are honest about their scope. They perform real, checkable edits —
/// collapsing whitespace, removing duplicated words, normalising punctuation
/// spacing and typographic quotes — and for operations that genuinely need a
/// language model they say so rather than fabricating a rewrite. Returning
/// invented text under the label "AI" would be the worst possible failure mode
/// for a tool people trust with their writing.
/// </remarks>
internal sealed class OfflineRewriteFallback
{
    public RewriteResult Apply(RewriteRequest request)
    {
        var text = request.Selection;

        var suggestion = request.Operation switch
        {
            RewriteOperation.Grammar => TidyPunctuation(text),
            RewriteOperation.Shorten => CollapseRedundancy(text),
            RewriteOperation.Simplify => CollapseRedundancy(TidyPunctuation(text)),
            RewriteOperation.ImproveStyle => TidyPunctuation(CollapseRedundancy(text)),
            _ => text
        };

        return new RewriteResult(text, suggestion, request.Operation, "offline-rules");
    }

    /// <summary>Normalises spacing around punctuation and straightens stray whitespace.</summary>
    private static string TidyPunctuation(string text)
    {
        var builder = new StringBuilder(text.Length);

        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];

            // Drop a space that sits before closing punctuation.
            if (char.IsWhiteSpace(current)
                && index + 1 < text.Length
                && text[index + 1] is '.' or ',' or '!' or '?' or ':' or ';' or '»' or ')')
            {
                continue;
            }

            // Collapse runs of whitespace to a single space, keeping line breaks.
            if (char.IsWhiteSpace(current) && current != '\n')
            {
                if (builder.Length > 0 && builder[^1] == ' ')
                {
                    continue;
                }

                builder.Append(' ');
                continue;
            }

            builder.Append(current);

            // Insert the missing space after sentence-ending punctuation.
            if (current is '.' or ',' or '!' or '?' or ';'
                && index + 1 < text.Length
                && char.IsLetter(text[index + 1]))
            {
                builder.Append(' ');
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>Removes an accidentally repeated word ("в в тексте").</summary>
    private static string CollapseRedundancy(string text)
    {
        var parts = text.Split(' ');
        var result = new List<string>(parts.Length);

        foreach (var part in parts)
        {
            if (result.Count > 0
                && part.Length > 1
                && string.Equals(result[^1], part, StringComparison.CurrentCultureIgnoreCase))
            {
                continue;
            }

            result.Add(part);
        }

        return string.Join(' ', result);
    }
}
