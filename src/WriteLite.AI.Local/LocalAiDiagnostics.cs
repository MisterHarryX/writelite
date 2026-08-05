namespace WriteLite.AI.Local;

/// <summary>
/// Optional technical logging sink. Host app wires this to CompatibilityLogger.
/// Never pass user text — only lengths, statuses, codes, durations.
/// </summary>
public static class LocalAiDiagnostics
{
    /// <summary>eventName, detail (no user content).</summary>
    public static Action<string, string>? Log { get; set; }

    public static void Technical(string eventName, string detail)
    {
        try
        {
            Log?.Invoke(eventName, detail);
        }
        catch
        {
            // never break analysis on log failure
        }
    }
}
