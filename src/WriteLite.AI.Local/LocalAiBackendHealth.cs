namespace WriteLite.AI.Local;

/// <summary>
/// What the local inference backend is actually doing — as opposed to whether its HTTP
/// port answers.
/// </summary>
/// <remarks>
/// Phase 2 established that <c>/health == 200</c> is not evidence of a working backend.
/// The llama.cpp server kept returning 200 while its single inference slot was held by an
/// abandoned connection and no work was being done at all. Anything that asks "is the AI
/// available?" needs to be able to tell <see cref="Ready"/> from <see cref="Wedged"/>, and
/// the old boolean could not.
/// </remarks>
public enum LocalAiBackendState
{
    /// <summary>No model pack and no endpoint — the feature is simply not installed.</summary>
    NotInstalled = 0,

    /// <summary>Installed, nothing running yet.</summary>
    Stopped = 1,

    /// <summary>A server process has been launched and has not answered yet.</summary>
    Starting = 2,

    /// <summary>Reachable and inference has completed recently.</summary>
    Ready = 3,

    /// <summary>Reachable and currently working on a request.</summary>
    Busy = 4,

    /// <summary>
    /// HTTP answers but inference does not complete. The Phase 2 failure mode: alive by
    /// every superficial measure, incapable of producing a result.
    /// </summary>
    Wedged = 5,

    /// <summary>A recovery attempt is in progress.</summary>
    Recovering = 6,

    /// <summary>Unreachable, or recovery has given up.</summary>
    Failed = 7,
}

/// <summary>
/// A point-in-time view of the backend, for diagnostics and for callers deciding whether
/// to route work to the model.
/// </summary>
/// <remarks>
/// Contains no user text and no request content — only counters, timestamps and state, so
/// it is safe to log and to show in a diagnostics pane under the product's privacy rules.
/// </remarks>
/// <param name="State">Current backend state.</param>
/// <param name="ActiveRequests">Inference requests on the wire right now.</param>
/// <param name="LastSuccessUtc">When inference last produced a usable result.</param>
/// <param name="LastTimeoutUtc">When a request last exceeded its hard timeout.</param>
/// <param name="LastCancellationUtc">When a caller last abandoned a request.</param>
/// <param name="ConsecutiveTimeouts">Hard timeouts since the last success.</param>
/// <param name="RecoveryAttempts">Recovery attempts since the process started.</param>
/// <param name="LastRecoveryUtc">When recovery was last attempted.</param>
/// <param name="Endpoint">Host and port only — never a full URL with query or userinfo.</param>
public sealed record LocalAiHealthSnapshot(
    LocalAiBackendState State,
    int ActiveRequests,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastTimeoutUtc,
    DateTimeOffset? LastCancellationUtc,
    int ConsecutiveTimeouts,
    int RecoveryAttempts,
    DateTimeOffset? LastRecoveryUtc,
    string Endpoint)
{
    /// <summary>True when it is worth routing work to the model.</summary>
    public bool CanServe => State is LocalAiBackendState.Ready or LocalAiBackendState.Busy;

    /// <summary>A single line for logs and the diagnostics pane. Contains no user text.</summary>
    public override string ToString()
        => $"state={State} active={ActiveRequests} consecutiveTimeouts={ConsecutiveTimeouts} "
           + $"recoveryAttempts={RecoveryAttempts} endpoint={Endpoint}";
}
