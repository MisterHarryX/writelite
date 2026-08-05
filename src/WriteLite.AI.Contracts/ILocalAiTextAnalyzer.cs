namespace WriteLite.AI.Contracts;

/// <summary>
/// Local AI analyzer boundary. Implementations must not send user text over the network.
/// </summary>
public interface ILocalAiTextAnalyzer : IAsyncDisposable
{
    bool IsAvailable { get; }

    string ModelVersion { get; }

    AiModelProfile ActiveProfile { get; }

    Task WarmupAsync(CancellationToken cancellationToken = default);

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
