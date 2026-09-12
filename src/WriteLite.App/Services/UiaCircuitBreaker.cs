namespace WriteLite.Services;

/// <summary>
/// Marks a target temporarily unavailable after UIA timeouts so the UI thread never blocks on retries.
/// </summary>
public sealed class UiaCircuitBreaker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _openUntil = new(StringComparer.Ordinal);
    private static TimeSpan DefaultCooldown => WriteLiteDefaults.Analysis.UiaCircuitBreakerCooldown;

    public bool IsOpen(string? targetId)
    {
        if (string.IsNullOrEmpty(targetId)) return false;
        lock (_gate)
        {
            if (!_openUntil.TryGetValue(targetId, out var until)) return false;
            if (DateTimeOffset.UtcNow >= until)
            {
                _openUntil.Remove(targetId);
                return false;
            }

            return true;
        }
    }

    public void Trip(string? targetId, TimeSpan? cooldown = null)
    {
        if (string.IsNullOrEmpty(targetId)) return;
        lock (_gate)
        {
            _openUntil[targetId] = DateTimeOffset.UtcNow + (cooldown ?? DefaultCooldown);
        }

        CompatibilityLogger.Technical("uia-circuit-open", $"target=set cooldownMs={(cooldown ?? DefaultCooldown).TotalMilliseconds:F0}");
    }

    public void Reset(string? targetId)
    {
        if (string.IsNullOrEmpty(targetId)) return;
        lock (_gate)
        {
            _openUntil.Remove(targetId);
        }
    }

    public int OpenCount
    {
        get { lock (_gate) return _openUntil.Count; }
    }
}
