using System.Text.RegularExpressions;
using WriteLite.AI.Local;
using WriteLite.Services.LanguageEngine;

namespace WriteLite.Services.Ai;

/// <summary>A prepared continuation, or the absence of one.</summary>
/// <param name="Text">What would be inserted if the user accepts. Never null; empty means none.</param>
/// <param name="Anchor">Caret offset the suggestion was prepared for.</param>
/// <param name="DocumentVersion">Document version it was prepared against.</param>
public readonly record struct WritingSuggestion(string Text, int Anchor, int DocumentVersion)
{
    public static readonly WritingSuggestion None = new(string.Empty, 0, 0);

    public bool HasText => !string.IsNullOrEmpty(Text);

    /// <summary>True when this suggestion still describes the document in front of the user.</summary>
    public bool AppliesTo(int caret, int version) => HasText && Anchor == caret && DocumentVersion == version;
}

/// <summary>
/// Prepares a short continuation of what the user is writing, locally and asynchronously.
/// </summary>
/// <remarks>
/// <para><b>What this is and is not.</b> §34–36 ask for help formulating text, not for a
/// document generator: the continuation is one clause, it is prepared during a pause and never
/// during typing, it is never inserted without an explicit keystroke, and it is discarded the
/// moment the caret moves. Everything here is shaped by those four constraints, which is why
/// there is no streaming, no history and no conversation.</para>
///
/// <para><b>Grounding beats fluency.</b> §36 is explicit that a completion must not invent
/// facts, and a small instruct model asked to continue Russian prose will happily supply dates,
/// figures and names. Two things prevent that. The prompt asks for a structural continuation
/// rather than an informative one, and the result is then <em>filtered</em>: a completion
/// containing a digit, a percentage, a date or a capitalised word that does not already appear
/// in the context is dropped rather than shown. Dropping is cheap — the user sees nothing,
/// which is the normal state — and a wrong fact in ghost text is not.</para>
///
/// <para><b>Bounded context, like every other model path here.</b> The prompt carries the
/// current sentence and the one before it, so a 300-page document costs the same as a note and
/// the model never sees more of the user's writing than it needs.</para>
/// </remarks>
public sealed partial class WritingAssistanceService
{
    private readonly QwenModelBackend? _backend;
    private CancellationTokenSource? _pending;
    private readonly object _gate = new();

    public WritingAssistanceService(QwenModelBackend? backend) => _backend = backend;

    /// <summary>Characters of context sent with a completion request.</summary>
    public const int ContextBudget = 320;

    /// <summary>Longest completion accepted. One clause, not a paragraph.</summary>
    public const int MaxCompletionChars = 120;

    /// <summary>Shortest context that can produce a useful continuation.</summary>
    public const int MinimumContextChars = 24;

    public bool IsAvailable => _backend is { IsAvailable: true };

    /// <summary>The most recent suggestion, for a UI that polls rather than subscribes.</summary>
    public WritingSuggestion Current { get; private set; } = WritingSuggestion.None;

    public event EventHandler<WritingSuggestion>? SuggestionChanged;

    /// <summary>Drops any prepared suggestion and cancels a request in flight.</summary>
    /// <remarks>
    /// Called on every keystroke, on caret movement and on Escape. Cancelling is the normal
    /// path, not the error path: most prepared completions are superseded before anyone sees
    /// them, and that is the design working.
    /// </remarks>
    public void Dismiss()
    {
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }

        if (Current.HasText)
        {
            Current = WritingSuggestion.None;
            SuggestionChanged?.Invoke(this, WritingSuggestion.None);
        }
    }

    /// <summary>
    /// Prepares a continuation for the caret position, replacing any earlier request.
    /// </summary>
    /// <returns>The suggestion, or <see cref="WritingSuggestion.None"/>.</returns>
    public async Task<WritingSuggestion> RequestAsync(
        string text,
        int caret,
        int documentVersion,
        CancellationToken cancellationToken = default)
    {
        Dismiss();

        if (!IsAvailable) return WritingSuggestion.None;
        if (string.IsNullOrEmpty(text)) return WritingSuggestion.None;

        caret = Math.Clamp(caret, 0, text.Length);
        if (!IsGoodPlaceToSuggest(text, caret)) return WritingSuggestion.None;

        var context = BuildContext(text, caret);
        if (context.Length < MinimumContextChars) return WritingSuggestion.None;

        CancellationTokenSource linked;
        lock (_gate)
        {
            _pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked = _pending;
        }

        string? raw;
        try
        {
            raw = await _backend!.CompleteAsync(
                    SystemPrompt,
                    UserPrompt(context),
                    maxTokens: 48,
                    temperature: 0.3,
                    linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return WritingSuggestion.None;
        }
        catch (Exception ex)
        {
            CompatibilityLogger.Technical("writing-assist-failed", $"type={ex.GetType().Name}");
            return WritingSuggestion.None;
        }

        if (linked.IsCancellationRequested) return WritingSuggestion.None;

        var completion = Clean(raw, context);
        if (completion.Length == 0) return WritingSuggestion.None;

        var suggestion = new WritingSuggestion(completion, caret, documentVersion);
        Current = suggestion;
        SuggestionChanged?.Invoke(this, suggestion);
        CompatibilityLogger.Technical("writing-assist-ready", $"length={completion.Length}");
        return suggestion;
    }

    /// <summary>
    /// True where a continuation is worth preparing at all.
    /// </summary>
    /// <remarks>
    /// Mid-word is the wrong moment — the user is still choosing the word — and so is the
    /// position right after a sentence terminator, where a continuation would be a new sentence
    /// rather than help with this one. §35 also asks that suggestions not follow every
    /// character, which the caller enforces with a long debounce; this is the shape test that
    /// goes with it.
    /// </remarks>
    public static bool IsGoodPlaceToSuggest(string text, int caret)
    {
        if (caret < MinimumContextChars) return false;
        if (caret < text.Length && !char.IsWhiteSpace(text[caret])) return false;

        var previous = text[caret - 1];
        if (char.IsLetterOrDigit(previous)) return true;

        // After a space, provided the word before it is finished and the sentence is not.
        if (previous != ' ') return false;

        for (var i = caret - 2; i >= 0; i--)
        {
            if (text[i] == ' ') continue;
            return char.IsLetterOrDigit(text[i]) || text[i] == ',';
        }

        return false;
    }

    /// <summary>The current sentence and the one before it, up to the caret.</summary>
    private static string BuildContext(string text, int caret)
    {
        var upToCaret = text[..caret];
        var window = SentenceWindowBuilder.Around(upToCaret, Math.Max(0, caret - 1), ContextBudget / 2);

        var context = window.IsEmpty
            ? upToCaret
            : (window.Previous.Length > 0 ? window.Previous + " " : string.Empty) + upToCaret[window.Start..];

        context = context.TrimStart();
        return context.Length <= ContextBudget ? context : context[^ContextBudget..];
    }

    private const string SystemPrompt =
        "Ты помогаешь дописывать русский текст. Продолжи фразу пользователя одним коротким "
        + "фрагментом — не более восьми слов. Не начинай новое предложение, не добавляй "
        + "пояснений, не повторяй уже написанное. Не придумывай факты, числа, даты, имена и "
        + "названия: продолжай только грамматически и по смыслу нейтрально. "
        + "Ответ — только сам фрагмент продолжения.";

    private static string UserPrompt(string context) => context;

    /// <summary>
    /// Trims the model's answer to a usable fragment, or returns empty.
    /// </summary>
    /// <remarks>
    /// <para>The filters are the §36 requirement expressed as code. A completion is rejected
    /// outright when it contains a digit, a percentage sign or a capitalised word the context
    /// does not already contain — those are the shapes an invented fact takes. It is truncated
    /// at the first sentence terminator, because the contract is "finish this sentence", and it
    /// is rejected when it merely repeats what the user has already typed.</para>
    ///
    /// <para>Everything here fails toward the empty string, which the UI renders as no ghost
    /// text at all. The cost of an over-eager filter is a suggestion nobody sees; the cost of a
    /// lenient one is a fabricated date in the user's document.</para>
    /// </remarks>
    internal static string Clean(string? raw, string context)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var value = raw.Trim().Trim('"', '«', '»', '`');

        // One fragment: stop at the first sentence end, keeping the terminator.
        var terminator = value.IndexOfAny(['.', '!', '?', '\n']);
        if (terminator >= 0) value = value[..(terminator + 1)];

        value = value.Trim();
        if (value.Length == 0 || value.Length > MaxCompletionChars) return string.Empty;

        // Truncating at the first period can cut an abbreviation in half — «см. приказ» becomes
        // «см.» — and a two-letter fragment is not help with anything. Three letters is the
        // shortest continuation worth painting.
        if (value.Count(char.IsLetter) < 3) return string.Empty;

        // Nothing that looks like a fabricated fact.
        if (value.Any(char.IsDigit)) return string.Empty;
        if (value.Contains('%') || value.Contains('№')) return string.Empty;

        foreach (Match match in CapitalisedWord().Matches(value))
        {
            if (!context.Contains(match.Value, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        }

        // Not a repetition of what is already written.
        var tail = context.Length <= 40 ? context : context[^40..];
        if (tail.Contains(value, StringComparison.OrdinalIgnoreCase)) return string.Empty;

        // A continuation joins what precedes it; leading punctuation would not.
        if (!char.IsLetter(value[0]) && value[0] != ',') return string.Empty;

        return context.EndsWith(' ') || value.StartsWith(',') ? value : " " + value;
    }

    [GeneratedRegex(@"\p{Lu}\p{Ll}+", RegexOptions.CultureInvariant)]
    private static partial Regex CapitalisedWordRegex();

    private static Regex CapitalisedWord() => CapitalisedWordRegex();
}
