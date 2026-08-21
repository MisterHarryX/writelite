namespace WriteLite.AI.Contracts;

/// <summary>
/// Local AI analyzer boundary. Implementations must not send user text over the network.
/// </summary>
public interface ILocalAiTextAnalyzer : IAsyncDisposable
{
    bool IsAvailable { get; }

    string ModelVersion { get; }

    AiModelProfile ActiveProfile { get; }

    /// <param name="startBackend">
    /// Whether the generative model server may be started, or only detected if already
    /// running. Callers on a path the user did not ask for — application startup — pass
    /// false, so that a session with no Smart Action in it never loads a model.
    /// </param>
    Task WarmupAsync(bool startBackend = true, CancellationToken cancellationToken = default);

    Task<AiTextAnalysisResult> AnalyzeAsync(
        AiTextAnalysisRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Optional capability probe for hardware-aware profile selection.</summary>
public interface ILocalAiHardwareProbe
{
    LocalAiHardwareInfo Probe();
}

public sealed record LocalAiHardwareInfo(
    long AvailableRamBytes,
    long? AvailableVramBytes,
    bool HasCuda,
    bool HasDirectMl,
    int LogicalCpuCount);
