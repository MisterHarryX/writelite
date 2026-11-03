using WriteLite.Language.Core;
using WriteLite.Services.Ai;
using WriteLite.Services.Translation;

namespace WriteLite.Services.FieldActions;

/// <summary>An action the indicator's pen offers on text in another application.</summary>
public enum FieldTextAction
{
    Shorten,
    Improve,
    Formal,
    Simplify,
    Translate,
}

/// <summary>The proposed text for one action, not yet written anywhere.</summary>
public sealed record FieldActionResult(
    FieldTextAction Action,
    string Original,
    string Suggestion,
    string Backend,
    string? Notice = null)
{
    public bool IsNoOp => string.Equals(Original.Trim(), Suggestion.Trim(), StringComparison.Ordinal);
}

/// <summary>
/// Shortens, improves or translates a fragment of another application's field and writes the
/// accepted result back through the verified correction path.
/// </summary>
/// <remarks>
/// <para>The point is that none of this requires opening WriteLite: the pen beside the field
/// offers the action, a small card shows the proposal, and «Заменить» edits the field in place.
/// Rewrites run on the same local model the editor uses; translation runs on the offline
/// translator (Marian models in <c>models/writelite-translate</c>) and falls back to the local
/// model only when those files are missing.</para>
///
/// <para>Nothing is written without the user pressing «Заменить», and the write is a
/// <see cref="CanonicalCorrection"/> bound to the exact text the action read — if the user kept
/// typing in the meantime, the write refuses rather than replacing the wrong words.</para>
/// </remarks>
public sealed class FieldTextActionService(IEditorAiService? rewriter, ITextTranslator? translator)
{
    public static string Title(FieldTextAction action, string? text = null) => action switch
    {
        FieldTextAction.Shorten => "Сократить",
        FieldTextAction.Improve => "Улучшить",
        FieldTextAction.Formal => "Официальнее",
        FieldTextAction.Simplify => "Проще",
        FieldTextAction.Translate => text is not null && LanguageDetector.IsMostlyLatin(text)
            ? "Перевести на русский"
            : "Перевести на английский",
        _ => action.ToString(),
    };

    public async Task<FieldActionResult> RunAsync(
        FieldTextAction action,
        string text,
        string contextBefore,
        string contextAfter,
        CancellationToken cancellationToken)
    {
        if (action == FieldTextAction.Translate)
        {
            var toEnglish = !LanguageDetector.IsMostlyLatin(text);
            var from = toEnglish ? "ru" : "en";
            var to = toEnglish ? "en" : "ru";
            if (translator is not null && translator.Supports(from, to))
            {
                var translated = await translator.TranslateAsync(text, from, to, cancellationToken).ConfigureAwait(false);
                return new FieldActionResult(action, text, translated, "writelite-translate");
            }

            if (rewriter is not null)
            {
                var viaModel = await rewriter.RewriteAsync(
                    new RewriteRequest(RewriteOperation.Translate, text, contextBefore, contextAfter,
                        toEnglish ? "на английский" : "на русский"),
                    cancellationToken).ConfigureAwait(false);
                return new FieldActionResult(action, text, viaModel.Suggestion, viaModel.Backend,
                    "Офлайн-переводчик не установлен; перевод сделан локальной моделью и может быть неточным.");
            }

            throw new InvalidOperationException("Переводчик недоступен: нет файлов модели перевода.");
        }

        if (rewriter is null)
        {
            throw new InvalidOperationException("WriteAI выключен — включите его в настройках, чтобы переписывать текст.");
        }

        var operation = action switch
        {
            FieldTextAction.Shorten => RewriteOperation.Shorten,
            FieldTextAction.Formal => RewriteOperation.Formal,
            FieldTextAction.Simplify => RewriteOperation.Simplify,
            _ => RewriteOperation.ImproveStyle,
        };
        var result = await rewriter.RewriteAsync(
            new RewriteRequest(operation, text, contextBefore, contextAfter), cancellationToken).ConfigureAwait(false);
        return new FieldActionResult(action, text, result.Suggestion, result.Backend, result.Notice);
    }

    /// <summary>Writes an accepted proposal over the scope it was made for.</summary>
    public static async Task<(bool Succeeded, string Message)> ApplyAsync(
        FieldTextScope scope,
        string replacement,
        CancellationToken cancellationToken)
    {
        var target = scope.Snapshot.Target;
        var read = await target.TryReadTextAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!read.Succeeded) return (false, "Поле ввода сейчас недоступно.");

        var live = read.Text;
        var start = scope.Start;
        if (start + scope.Length > live.Length
            || !string.Equals(live.Substring(start, scope.Length), scope.Text, StringComparison.Ordinal))
        {
            // The user typed elsewhere in the field: find the fragment again if it is unique.
            var found = live.IndexOf(scope.Text, StringComparison.Ordinal);
            if (found < 0 || live.IndexOf(scope.Text, found + 1, StringComparison.Ordinal) >= 0)
            {
                return (false, "Текст изменился — выберите действие ещё раз.");
            }

            start = found;
        }

        var status = CanonicalCorrection.TryBind(live, start, scope.Length, scope.Text, replacement, out var correction);
        if (status != CorrectionBindingStatus.Bound || correction is null)
        {
            return (false, "Текст изменился — выберите действие ещё раз.");
        }

        var write = await target.TryApplyCorrectionAsync(correction, live, cancellationToken).ConfigureAwait(false);
        return write.Succeeded
            ? (true, "Готово")
            : (false, "Не удалось изменить текст в этом поле. Скопируйте результат и вставьте вручную.");
    }
}
