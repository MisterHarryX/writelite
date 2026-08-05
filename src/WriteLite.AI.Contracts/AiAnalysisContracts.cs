namespace WriteLite.AI.Contracts;

/// <summary>Current wire schema version for local AI request/response.</summary>
public static class AiSchema
{
    public const int CurrentVersion = 1;
    public const string ModelFamily = "writelight-local";
}

public enum AiAnalysisMode
{
    Correct = 0,
    Explain = 1
}

public enum AiModelProfile
{
    Lite = 0,
    Standard = 1,
    Quality = 2,
    Auto = 3
}

public sealed record AiTextAnalysisRequest(
    string Text,
    string? Language,
    AiAnalysisMode Mode,
    int SchemaVersion,
    AiModelProfile Profile = AiModelProfile.Auto,
    string? RequestId = null);

public sealed record AiTextIssue(
    int Start,
    int Length,
    string Original,
    string Replacement,
    AiIssueType Type,
    string Explanation,
    double Confidence,
    bool SafeToApply = false,
    bool LowConfidence = false);

public sealed record AiTextAnalysisResult(
    string CorrectedText,
    string DetectedLanguage,
    IReadOnlyList<AiTextIssue> Issues,
    string ModelVersion,
    int SchemaVersion,
    TimeSpan ProcessingTime,
    AiModelProfile ProfileUsed,
    string Backend,
    bool Uncertain = false,
    string? Warning = null,
    string? ErrorCode = null);

public static class AiErrorCodes
{
    public const string ModelMissing = "MODEL_MISSING";
    public const string ModelCorrupt = "MODEL_CORRUPT";
    public const string OutOfMemory = "OUT_OF_MEMORY";
    public const string BackendFailed = "BACKEND_FAILED";
    public const string Timeout = "TIMEOUT";
    public const string Cancelled = "CANCELLED";
    public const string InvalidResponse = "INVALID_RESPONSE";
    public const string SchemaMismatch = "SCHEMA_MISMATCH";
    public const string TextTooLong = "TEXT_TOO_LONG";
    public const string TextTooShort = "TEXT_TOO_SHORT";
    public const string EmptyResult = "EMPTY_RESULT";
    public const string ExcessiveRewrite = "EXCESSIVE_REWRITE";
    public const string Ok = "OK";
}
