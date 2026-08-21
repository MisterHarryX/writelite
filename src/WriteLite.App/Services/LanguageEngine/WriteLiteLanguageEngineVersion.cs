namespace WriteLite.Services.LanguageEngine;

/// <summary>
/// Versions of the shipped language components, for diagnostics and the About screen.
/// </summary>
/// <remarks>
/// <para>§60 of the Phase 7 brief. Three separately versioned things travel in one release and
/// they fail in different ways, so a bug report that says "WriteLite 1.0" does not say enough:
/// the deterministic engine, the punctuation model, and the local generative subsystem each
/// have their own version.</para>
///
/// <para><b>Product name and provenance are both here, deliberately.</b> §3 draws the line:
/// users see WriteAI, and the record of what is underneath stays accurate. A licence
/// obligation is not satisfied by a product name, so <see cref="WriteAiBaseModel"/> and
/// <see cref="WriteAiRuntime"/> name the upstream work in the place a maintainer or an auditor
/// would look. These strings belong in diagnostics and About, not in ordinary UI — §53.</para>
/// </remarks>
public static class WriteLiteLanguageEngineVersion
{
    /// <summary>The deterministic engine: rules, spelling, morphology, lexical signals.</summary>
    public const string LanguageEngine = "WriteLite Language Engine 1.0";

    /// <summary>The Russian rule pack version, which moves independently of the engine.</summary>
    public const string RulePack = "ru-1.3.0";

    /// <summary>The trained comma classifier accepted in Phase 6.</summary>
    public const string PunctuationModel = "WriteLite-Punctuation-v1";

    /// <summary>Its frozen acceptance threshold, chosen on validation before the golden set.</summary>
    public const double PunctuationThreshold = 0.996;

    /// <summary>The local generative subsystem, as the product names it.</summary>
    public const string WriteAi = "WriteAI Local 1.0";

    /// <summary>The upstream base model. Product branding does not replace attribution.</summary>
    public const string WriteAiBaseModel = "Qwen2.5";

    /// <summary>The inference runtime WriteAI is served by.</summary>
    public const string WriteAiRuntime = "llama.cpp";

    /// <summary>The punctuation model's own base encoder.</summary>
    public const string PunctuationBaseModel = "rubert-tiny2";

    /// <summary>One line for a diagnostics dump.</summary>
    public static string Describe() =>
        $"{LanguageEngine}; rules {RulePack}; {PunctuationModel} @ {PunctuationThreshold:0.000} "
        + $"({PunctuationBaseModel}, int8 ONNX); {WriteAi} ({WriteAiBaseModel} via {WriteAiRuntime})";
}
