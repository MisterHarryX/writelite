using System.Text.Json.Serialization;

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

    /// <summary>
    /// The trained comma classifier, WriteLite-Punctuation-v1.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="EnablePunctuation"/> — which governs punctuation findings as a
    /// category — because this is the one punctuation layer that is a model. Turning it off
    /// leaves every deterministic comma rule running, which is what makes it safe to default
    /// on: the worst case of switching it off is the Phase 6 configuration A.
    /// </remarks>
    public bool PunctuationModelEnabled { get; set; } = true;

    /// <summary>
    /// Which register the style layer measures against: General, Academic, Business, Formal,
    /// Technical, Creative, Social or Student.
    /// </summary>
    /// <remarks>
    /// Style is the only layer that is allowed to have an opinion about correct Russian, and
    /// what counts as an improvement depends on what is being written. «Заюзать библиотеку» is
    /// unremarkable in a technical chat and worth a note in a thesis. §19: the profile changes
    /// which suggestions appear, never whether the text is treated as erroneous.
    /// </remarks>
    public string StyleProfile { get; set; } = "General";

    // WriteAI — the local offline generative subsystem (Lite engine always available; the
    // local model server is optional). Default ON so Smart Actions work without a network.
    //
    // Renamed from LocalAiEnabled / PreferQwen / QwenEndpoint in Phase 7. The old names remain
    // as code-level aliases below, and WriteLiteSettingsStore migrates the persisted keys once
    // on load, so an existing settings.json keeps working.
    public bool WriteAiEnabled { get; set; } = true;

    /// <summary>Lite | Standard | Quality | Auto. Standard prefers the live WriteAI server.</summary>
    public string LocalAiProfile { get; set; } = "Standard";

    /// <summary>OpenAI-compatible loopback base for the WriteAI server (no trailing slash).</summary>
    public string WriteAiEndpoint { get; set; } = "http://127.0.0.1:8742";

    /// <summary>Prefer the WriteAI neural path when enabled and the endpoint is healthy.</summary>
    public bool PreferWriteAi { get; set; } = true;

    // Local analysis scheduling limits.
    public int AiDebounceMs { get; set; } = 1500;
    public int AiMinTextLength { get; set; } = 12;
    public int AiMaxTextLength { get; set; } = 12_000;

    /// <summary>
    /// Compatibility aliases for the pre-Phase-7 names.
    /// </summary>
    /// <remarks>
    /// <c>JsonIgnore</c> so the file carries only the new keys — a property that both persists
    /// and forwards would write the same value twice under two names, and the next reader
    /// would have no way to tell which one the user last changed. Reading an old file is
    /// handled once, explicitly, in <c>WriteLiteSettingsStore</c>. These exist so the ~40
    /// call sites that say <c>LocalAiEnabled</c> keep compiling: §4 of the brief asks for the
    /// rename without a large risky sweep, and this is where the line falls.
    /// </remarks>
    [JsonIgnore]
    public bool LocalAiEnabled
    {
        get => WriteAiEnabled;
        set => WriteAiEnabled = value;
    }

    /// <inheritdoc cref="LocalAiEnabled"/>
    [JsonIgnore]
    public bool PreferQwen
    {
        get => PreferWriteAi;
        set => PreferWriteAi = value;
    }

    /// <inheritdoc cref="LocalAiEnabled"/>
    [JsonIgnore]
    public string QwenEndpoint
    {
        get => WriteAiEndpoint;
        set => WriteAiEndpoint = value;
    }

    /// <inheritdoc cref="LocalAiEnabled"/>
    [JsonIgnore]
    public bool AiCheckingEnabled
    {
        get => WriteAiEnabled;
        set => WriteAiEnabled = value;
    }

    // Ambience player.
    /// <summary>
    /// Playback volume, 0–1. Low by default: this is background sound underneath
    /// writing, and the website's own player caps itself at a fifth of full volume
    /// for the same reason.
    /// </summary>
    public double AmbienceVolume { get; set; } = 0.12;

    /// <summary>Whether ambience was playing when the application last closed.</summary>
    public bool AmbienceEnabled { get; set; }

    /// <summary>Whether the current track repeats instead of handing over to the next one.</summary>
    public bool AmbienceLoop { get; set; }

    /// <summary>
    /// Keyboard shortcuts the user changed, keyed by shortcut identifier.
    /// </summary>
    /// <remarks>
    /// Only the differences from the shipped bindings, so this file says what someone chose
    /// rather than restating the defaults — and a later release that changes a default
    /// reaches everyone who never touched that shortcut. Unknown identifiers and unparseable
    /// gestures are dropped on load; see <see cref="ShortcutRegistry"/>.
    /// </remarks>
    public Dictionary<string, string> Shortcuts { get; set; } = [];

    // Schema for forward-compat. 4 renames the local-AI keys to WriteAI.
    public int SchemaVersion { get; set; } = 4;
}
