namespace WriteLite.Services.Ai;

/// <summary>
/// What WriteLite tells the user about WriteAI, and the only place that decides it.
/// </summary>
/// <remarks>
/// <para>WriteAI is the product name for WriteLite's local generative subsystem. The
/// implementation underneath is a quantised Qwen2.5 served by llama.cpp, and that stays
/// recorded in the model card and in technical diagnostics — §3 of the Phase 7 brief draws
/// the line between product branding and provenance, and this type is on the branding side
/// of it.</para>
///
/// <para>Five states and nothing else. Before this existed the status a user saw was
/// assembled from transport details at four call sites, which is how «Ошибка проверки Qwen:
/// No connection could be made because the target machine actively refused it» reached the
/// settings page. §54: no raw transport errors in normal UI.</para>
/// </remarks>
public enum WriteAiState
{
    /// <summary>The user has switched WriteAI off.</summary>
    Disabled,

    /// <summary>Available and idle.</summary>
    Ready,

    /// <summary>Starting up or warming a model.</summary>
    Loading,

    /// <summary>Working on a request.</summary>
    Busy,

    /// <summary>Enabled, but the local model is not reachable.</summary>
    Unavailable,
}

public static class WriteAiStatus
{
    /// <summary>The product name, for anywhere a label is built by concatenation.</summary>
    public const string ProductName = "WriteAI";

    /// <summary>The short status line, as the user reads it.</summary>
    public static string Describe(WriteAiState state) => state switch
    {
        WriteAiState.Disabled => "WriteAI выключен",
        WriteAiState.Ready => "WriteAI готов",
        WriteAiState.Loading => "WriteAI загружается",
        WriteAiState.Busy => "WriteAI занят",
        _ => "WriteAI недоступен",
    };

    /// <summary>
    /// The status line plus what the user can do about it.
    /// </summary>
    /// <remarks>
    /// Every unavailable state says the same two things, because they are the two things that
    /// matter: the editor keeps working, and the deterministic checking is unaffected. A user
    /// who reads "WriteAI недоступен" and nothing else has no way to know whether their
    /// spelling is still being checked.
    /// </remarks>
    public static string DescribeWithHint(WriteAiState state) => state switch
    {
        WriteAiState.Disabled =>
            "WriteAI выключен. Проверка орфографии, пунктуации и грамматики работает как обычно.",
        WriteAiState.Ready =>
            "WriteAI готов. Умные действия доступны, текст не покидает этот компьютер.",
        WriteAiState.Loading =>
            "WriteAI загружается. Умные действия станут доступны через несколько секунд.",
        WriteAiState.Busy =>
            "WriteAI занят. Дождитесь окончания текущего действия или отмените его.",
        _ =>
            "WriteAI недоступен: локальная модель не отвечает. Проверка текста продолжает "
            + "работать, недоступны только умные действия.",
    };

    /// <summary>
    /// The technical backend tag, for diagnostics and the status bar.
    /// </summary>
    /// <remarks>
    /// Maps the transport's own identifiers — which still say <c>writelight-qwen</c>, because
    /// renaming a wire protocol value would break a running server — onto product vocabulary.
    /// </remarks>
    public static string BackendLabel(string? lastBackend, bool available) => lastBackend switch
    {
        var b when string.Equals(b, "writelight-qwen", StringComparison.OrdinalIgnoreCase)
            => available ? "WriteAI" : "WriteAI (offline)",
        var b when string.Equals(b, "lite-fallback", StringComparison.OrdinalIgnoreCase)
            => "WriteAI Lite (резервный режим)",
        var b when string.Equals(b, "lite", StringComparison.OrdinalIgnoreCase)
            => available ? "WriteAI Lite" : "WriteAI Lite (модель не запущена)",
        _ => available ? "WriteAI" : "WriteAI недоступен",
    };
}
