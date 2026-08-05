namespace WriteLite.Services.Settings;

/// <summary>
/// User-facing application settings. No absolute technical paths.
/// </summary>
public sealed class WriteLiteAppSettings
{
    // Main
    public bool CheckingEnabled { get; set; } = true;
    public bool ShowIndicator { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool OpenMainWindowOnStart { get; set; }
    public bool MinimizeToTrayOnClose { get; set; } = true;

    // Checking
    public bool ExtendedChecking { get; set; } = true;
    public bool EnableOrthography { get; set; } = true;
    public bool EnablePunctuation { get; set; } = true;
    public bool EnableGrammar { get; set; } = true;
    public bool EnableStyle { get; set; } = true;
    public bool EnableTypography { get; set; } = true;
    public bool EnableRepeats { get; set; } = true;
    public bool SafeApplyAll { get; set; } = true;
    public string Language { get; set; } = "ru-RU";

    // Behaviour
    public int AnalysisDelayMs { get; set; } = 400;
    public int MaxTextLength { get; set; } = 20_000;
    public bool ShowGreenIndicatorWhenClean { get; set; } = true;
    public bool HidePanelAfterSuccessfulApply { get; set; } = true;

    /// <summary>
    /// Soften WriteLite underlines when the host app also draws its own spellcheck marks.
    /// WriteLite cannot safely disable system spellcheck globally.
    /// </summary>
    public bool MinimizeUnderlineDuplication { get; set; }

    /// <summary>
    /// When true (default), indicator/underlines appear only after confirmed text editing,
    /// not on mere focus/reading.
    /// </summary>
    public bool ShowUiOnlyWhileEditing { get; set; } = true;

    /// <summary>
    /// Seconds used to distinguish active typing from a completed idle check.
    /// The result itself remains visible until the edited field loses focus.
    /// </summary>
    public int EditingIdleGraceSeconds { get; set; } = 4;

    /// <summary>Enable double-click dictionary card on editable fields.</summary>
    public bool LexicalCardEnabled { get; set; } = true;

    // Local offline AI (Lite engine always available; Qwen optional when server/pack present).
    // Default ON so grammar/punctuation AI works without network.
    public bool LocalAiEnabled { get; set; } = true;

    /// <summary>Lite | Standard | Quality | Auto. Standard prefers live WriteLite-Qwen.</summary>
    public string LocalAiProfile { get; set; } = "Standard";

    /// <summary>OpenAI-compatible loopback base for WriteLite-Qwen (no trailing slash).</summary>
    public string QwenEndpoint { get; set; } = "http://127.0.0.1:8742";

    /// <summary>Prefer Qwen neural path when LocalAiEnabled and endpoint is healthy.</summary>
    public bool PreferQwen { get; set; } = true;

    // Local analysis scheduling limits.
    public int AiDebounceMs { get; set; } = 1500;
    public int AiMinTextLength { get; set; } = 12;
    public int AiMaxTextLength { get; set; } = 12_000;

    /// <summary>Backward-compatible alias for the local AI toggle.</summary>
    public bool AiCheckingEnabled
    {
        get => LocalAiEnabled;
        set => LocalAiEnabled = value;
    }

    // Schema for forward-compat
    public int SchemaVersion { get; set; } = 3;
}
