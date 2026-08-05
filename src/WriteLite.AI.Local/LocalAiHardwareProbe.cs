using WriteLite.AI.Contracts;

namespace WriteLite.AI.Local;

public sealed class LocalAiHardwareProbe : ILocalAiHardwareProbe
{
    public LocalAiHardwareInfo Probe()
    {
        long ram;
        try
        {
            ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (ram <= 0)
            {
                ram = 8L * 1024 * 1024 * 1024;
            }
        }
        catch
        {
            ram = 8L * 1024 * 1024 * 1024;
        }

        // VRAM/CUDA detection is best-effort without native deps.
        long? vram = null;
        var hasCuda = false;
        try
        {
            var cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
            hasCuda = !string.IsNullOrWhiteSpace(cudaPath);
        }
        catch
        {
            hasCuda = false;
        }

        return new LocalAiHardwareInfo(
            AvailableRamBytes: ram,
            AvailableVramBytes: vram,
            HasCuda: hasCuda,
            HasDirectMl: OperatingSystem.IsWindows(),
            LogicalCpuCount: Environment.ProcessorCount);
    }

    /// <param name="hasNeuralModel">True when WriteLite-Qwen pack (GGUF/adapter/endpoint) is present.</param>
    public static AiModelProfile SelectProfile(AiModelProfile requested, LocalAiHardwareInfo hw, bool hasNeuralModel)
    {
        if (requested != AiModelProfile.Auto)
        {
            // Downgrade if resources insufficient.
            if (requested == AiModelProfile.Quality && hw.AvailableRamBytes < 6L * 1024 * 1024 * 1024)
            {
                return hasNeuralModel ? AiModelProfile.Standard : AiModelProfile.Lite;
            }

            if (requested is AiModelProfile.Standard or AiModelProfile.Quality && !hasNeuralModel)
            {
                return AiModelProfile.Lite;
            }

            return requested;
        }

        // Auto: prefer Standard neural when pack exists and RAM is comfortable; else Lite rules engine.
        if (hasNeuralModel && hw.AvailableRamBytes >= 3L * 1024 * 1024 * 1024)
        {
            return AiModelProfile.Standard;
        }

        return AiModelProfile.Lite;
    }
}

