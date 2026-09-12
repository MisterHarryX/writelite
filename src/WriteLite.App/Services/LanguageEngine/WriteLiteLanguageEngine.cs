using System.Diagnostics;
using WriteLite.Models;

namespace WriteLite.Services.LanguageEngine;

/// <summary>
/// Extended local language engine as an <see cref="ITextAnalyzer"/>.
/// Safe when runtime is missing: returns empty and reports fallback status.
/// </summary>
public sealed class WriteLiteLanguageEngine : ITextAnalyzer, ITextAnalysisProgressSource, IAsyncDisposable, IDisposable
{
    private readonly WriteLiteLanguageOptions _options;
    private readonly IWriteLiteLanguageEngineHost _host;
    private readonly WriteLiteLanguageClient _client;
    private readonly WriteLiteIssueMapper _mapper = new();
    private readonly object _statusGate = new();
    private readonly object _metricsGate = new();
    private readonly object _progressGate = new();
    private string _userStatus = "Используется базовая проверка WriteLite";
    private WriteLiteLanguageEngineState _publicState = WriteLiteLanguageEngineState.Stopped;
    private int _startAttempts;
    private int _restartCount;
    private int _consecutiveFailures;
    private int _recoveryScheduled;
    private DateTimeOffset? _lastSuccessUtc;
    private TimeSpan _lastAnalysisDuration;
    private int _lastIssueCount;
    private DateTimeOffset _nextAllowedAutoStartUtc = DateTimeOffset.MinValue;
    private bool _extendedEnabled = true;
    private bool _disposed;
    private TextAnalysisProgress _lastProgress = TextAnalysisProgress.Complete(string.Empty);

    public WriteLiteLanguageEngine(
        WriteLiteLanguageOptions? options = null,
        IWriteLiteLanguageEngineHost? host = null)
    {
        _options = options ?? new WriteLiteLanguageOptions { EnableEngine = true };
        _options.HostAddress = WriteLiteLanguageOptions.DefaultHostAddress;
        _extendedEnabled = _options.EnableEngine;
        _host = host ?? new WriteLiteLanguageEngineHost(_options);
        _client = new WriteLiteLanguageClient(_options);
        _host.StateChanged += (_, _) => OnHostStateChanged();
        SyncStatusFromHost();
    }

    public string UserFacingStatus
    {
        get
        {
            lock (_statusGate)
            {
                return _userStatus;
            }
        }
    }

    public WriteLiteLanguageEngineState EngineState
    {
        get
        {
            lock (_statusGate)
            {
                return _publicState;
            }
        }
    }

    public bool IsReady => _extendedEnabled && _host.IsReady;

    public int? ProcessId => _host.ProcessId;

    public int RestartCount
    {
        get
        {
            lock (_metricsGate)
            {
                return _restartCount;
            }
        }
    }

    public DateTimeOffset? LastSuccessUtc
    {
        get
        {
            lock (_metricsGate)
            {
                return _lastSuccessUtc;
            }
        }
    }

    public TimeSpan LastAnalysisDuration
    {
        get
        {
            lock (_metricsGate)
            {
                return _lastAnalysisDuration;
            }
        }
    }

    public int LastIssueCount
    {
        get
        {
            lock (_metricsGate)
            {
                return _lastIssueCount;
            }
        }
    }

    public bool ExtendedEnabled => _extendedEnabled;

    public event EventHandler? StatusChanged;

    public event EventHandler<TextAnalysisProgress>? AnalysisProgressChanged;

    public TextAnalysisProgress GetProgress(string text)
    {
        lock (_progressGate)
        {
            return string.Equals(_lastProgress.Text, text ?? string.Empty, StringComparison.Ordinal)
                ? _lastProgress
                : TextAnalysisProgress.Pending(text ?? string.Empty);
        }
    }

    /// <summary>Starts the host in background without blocking the UI thread.</summary>
    public void StartInBackground()
    {
        if (_disposed || !_extendedEnabled)
        {
            SetStatus(WriteLiteLanguageEngineState.Stopped, "Используется базовая проверка WriteLite");
            return;
        }

        SetStatus(WriteLiteLanguageEngineState.Starting, "Расширенная проверка запускается");
        _ = Task.Run(async () =>
        {
            try
            {
                await _host.StartAsync().ConfigureAwait(false);
                Interlocked.Exchange(ref _consecutiveFailures, 0);
                Interlocked.Exchange(ref _startAttempts, 0);
                Interlocked.Exchange(ref _recoveryScheduled, 0);
                SyncStatusFromHost();
            }
            catch
            {
                Interlocked.Increment(ref _consecutiveFailures);
                ScheduleBackoff();
                SetStatus(WriteLiteLanguageEngineState.Unavailable, "Расширенная проверка временно недоступна");
                CompatibilityLogger.State("language-engine-fallback-active");
            }
        });
    }

    /// <summary>
    /// Enables or disables the extended engine without restarting WriteLite.
    /// Disabling stops the child process; enabling starts it again.
    /// </summary>
    public async Task SetExtendedEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        if (enabled == _extendedEnabled && (!enabled || _host.IsReady || _host.State == WriteLiteLanguageEngineState.Starting))
        {
            if (!enabled)
            {
                SetStatus(WriteLiteLanguageEngineState.Stopped, "Используется базовая проверка WriteLite");
            }

            return;
        }

        _extendedEnabled = enabled;
        _options.EnableEngine = enabled;

        if (!enabled)
        {
            Interlocked.Exchange(ref _startAttempts, 0);
            Interlocked.Exchange(ref _recoveryScheduled, 0);
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            try
            {
                await _host.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // best effort
            }

            SetStatus(WriteLiteLanguageEngineState.Stopped, "Используется базовая проверка WriteLite");
            CompatibilityLogger.State("language-engine-stopped");
            return;
        }

        Interlocked.Exchange(ref _startAttempts, 0);
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        Interlocked.Exchange(ref _recoveryScheduled, 0);
        _nextAllowedAutoStartUtc = DateTimeOffset.MinValue;
        StartInBackground();
    }

    public async Task<bool> TryRestartAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return false;
        }

        if (!_extendedEnabled)
        {
            await SetExtendedEnabledAsync(true, cancellationToken).ConfigureAwait(false);
            // Wait briefly for readiness.
            var deadline = DateTime.UtcNow.Add(WriteLiteDefaults.Networking.LanguageEngineStartupTimeout);
            while (DateTime.UtcNow < deadline && !_host.IsReady)
            {
                await Task.Delay(WriteLiteDefaults.Networking.LanguageEngineRestartPollInterval, cancellationToken).ConfigureAwait(false);
            }

            return _host.IsReady;
        }

        try
        {
            lock (_metricsGate)
            {
                _restartCount++;
            }

            CompatibilityLogger.State("language-engine-restarting");
            await _host.StopAsync(cancellationToken).ConfigureAwait(false);
            SetStatus(WriteLiteLanguageEngineState.Starting, "Расширенная проверка запускается");
            await _host.StartAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            Interlocked.Exchange(ref _startAttempts, 0);
            Interlocked.Exchange(ref _recoveryScheduled, 0);
            _nextAllowedAutoStartUtc = DateTimeOffset.MinValue;
            SyncStatusFromHost();
            return _host.IsReady;
        }
        catch
        {
            Interlocked.Increment(ref _consecutiveFailures);
            ScheduleBackoff();
            SetStatus(WriteLiteLanguageEngineState.Unavailable, "Расширенная проверка временно недоступна");
            CompatibilityLogger.State("language-engine-fallback-active");
            return false;
        }
    }

    public IReadOnlyList<TextIssue> Analyze(string text)
        => AnalyzeAsync(text).GetAwaiter().GetResult();

    public async Task<IReadOnlyList<TextIssue>> AnalyzeAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        text ??= string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            ReportProgress(TextAnalysisProgress.Complete(text));
            return [];
        }

        ReportProgress(TextAnalysisProgress.Pending(text));
        if (_disposed || !_extendedEnabled)
        {
            ReportProgress(new TextAnalysisProgress(
                text, 0, text.Length, IsComplete: false, IncompleteReason: "engine-disabled"));
            return [];
        }

        if (!_host.IsReady)
        {
            MaybeRequestAutoStart();
            ReportProgress(new TextAnalysisProgress(
                text, 0, text.Length, IsComplete: false, IncompleteReason: "engine-unavailable"));
            return [];
        }

        var sw = Stopwatch.StartNew();
        try
        {
            if (_host.Port is int port)
            {
                _options.SetBoundPort(port);
            }

            SetStatus(WriteLiteLanguageEngineState.Busy, "Анализирует текст");
            var matches = await CheckWithChunkingAsync(text, cancellationToken).ConfigureAwait(false);
            var mapped = _mapper.MapAll(text, new WriteLiteLanguageResponseDto { Matches = matches });
            sw.Stop();
            lock (_metricsGate)
            {
                _lastAnalysisDuration = sw.Elapsed;
                _lastIssueCount = mapped.Issues.Count;
                _lastSuccessUtc = DateTimeOffset.UtcNow;
            }

            Interlocked.Exchange(ref _consecutiveFailures, 0);
            SetStatus(WriteLiteLanguageEngineState.Ready, "Языковой движок готов");
            return mapped.Issues;
        }
        catch (OperationCanceledException)
        {
            var current = GetProgress(text);
            ReportProgress(current with { IsComplete = false, IncompleteReason = "cancelled" });
            throw;
        }
        catch (WriteLiteLanguageEngineException)
        {
            CompatibilityLogger.State("language-engine-fallback-active");
            var current = GetProgress(text);
            ReportProgress(current with { IsComplete = false, IncompleteReason = "engine-failed" });
            SetStatus(WriteLiteLanguageEngineState.Unavailable, "Расширенная проверка временно недоступна");
            return [];
        }
        catch (Exception ex)
        {
            CompatibilityLogger.AccessError("language-engine-analyze", null, ex);
            CompatibilityLogger.State("language-engine-fallback-active");
            var current = GetProgress(text);
            ReportProgress(current with { IsComplete = false, IncompleteReason = "engine-failed" });
            return [];
        }
    }

    private void MaybeRequestAutoStart()
    {
        if (!_extendedEnabled || _disposed)
        {
            return;
        }

        if (DateTimeOffset.UtcNow < _nextAllowedAutoStartUtc)
        {
            return;
        }

        if (_host.State is WriteLiteLanguageEngineState.Starting or WriteLiteLanguageEngineState.Ready)
        {
            return;
        }

        if (_host.State is WriteLiteLanguageEngineState.Stopped
            or WriteLiteLanguageEngineState.Failed
            or WriteLiteLanguageEngineState.Unavailable)
        {
            // Cap auto-starts to avoid restart loops.
            if (Interlocked.Increment(ref _startAttempts) > Math.Max(0, WriteLiteDefaults.Networking.EngineAutoStartMaxAttempts))
            {
                ScheduleBackoff();
                return;
            }

            if (Volatile.Read(ref _consecutiveFailures) >= WriteLiteDefaults.Networking.EngineAutoStartFailureLimit)
            {
                // Require explicit restart after too many failures.
                SetStatus(WriteLiteLanguageEngineState.Unavailable, "Расширенная проверка временно недоступна");
                return;
            }

            StartInBackground();
        }
    }

    private void ScheduleBackoff()
    {
        // Mild then exponential: 3s, 9s, 27s, 60s max — avoids tight loops without multi-minute silence.
        var failures = Math.Max(1, Volatile.Read(ref _consecutiveFailures));
        var seconds = Math.Min(
            WriteLiteDefaults.Networking.EngineRecoveryBackoffMax.TotalSeconds,
            WriteLiteDefaults.Networking.EngineRecoveryBackoffBase.TotalSeconds
                * Math.Pow(WriteLiteDefaults.Networking.EngineRecoveryBackoffFactor, failures - 1));
        _nextAllowedAutoStartUtc = DateTimeOffset.UtcNow.AddSeconds(seconds);
    }

    private void OnHostStateChanged()
    {
        if (_host.State is WriteLiteLanguageEngineState.Failed
            or WriteLiteLanguageEngineState.Unavailable)
        {
            // Unexpected death — baseline continues; schedule limited recovery.
            Interlocked.Increment(ref _consecutiveFailures);
            ScheduleBackoff();
            CompatibilityLogger.State("language-engine-fallback-active");
            ScheduleRecoveryAfterUnexpectedExit();
        }
        else if (_host.State == WriteLiteLanguageEngineState.Ready)
        {
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            Interlocked.Exchange(ref _startAttempts, 0);
            Interlocked.Exchange(ref _recoveryScheduled, 0);
        }

        SyncStatusFromHost();
    }

    /// <summary>
    /// After an unexpected child exit, attempt a single delayed restart without tight polling.
    /// </summary>
    private void ScheduleRecoveryAfterUnexpectedExit()
    {
        if (!_extendedEnabled || _disposed)
        {
            return;
        }

        if (Volatile.Read(ref _consecutiveFailures) >= WriteLiteDefaults.Networking.EngineAutoStartFailureLimit)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _recoveryScheduled, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Honour backoff window, but recover promptly after first failure.
                var delay = WriteLiteDefaults.Networking.LanguageEngineRecoveryBaseDelay;
                var until = _nextAllowedAutoStartUtc - DateTimeOffset.UtcNow;
                if (until > delay)
                {
                    delay = until;
                }

                await Task.Delay(delay).ConfigureAwait(false);
                if (_disposed || !_extendedEnabled)
                {
                    return;
                }

                if (_host.IsReady || _host.State == WriteLiteLanguageEngineState.Starting)
                {
                    return;
                }

                // Allow recovery path even if soft start attempts were exhausted earlier.
                Interlocked.Exchange(ref _startAttempts, 0);
                lock (_metricsGate)
                {
                    _restartCount++;
                }

                CompatibilityLogger.State("language-engine-restarting");
                StartInBackground();
            }
            catch
            {
                // non-fatal
            }
            finally
            {
                Interlocked.Exchange(ref _recoveryScheduled, 0);
            }
        });
    }

    private async Task<List<WriteLiteLanguageMatchDto>> CheckWithChunkingAsync(
        string text,
        CancellationToken cancellationToken)
    {
        var max = Math.Max(500, _options.MaxTextLength);
        if (text.Length <= max)
        {
            using var client = new WriteLiteLanguageClient(_options, _host.BaseAddress ?? _options.BaseUrl);
            var dto = await client.CheckAsync(text, _options.Language, cancellationToken).ConfigureAwait(false);
            ReportProgress(TextAnalysisProgress.Complete(text));
            return dto.Matches?.ToList() ?? [];
        }

        var chunks = SemanticTextChunker.CreatePlan(text, max);
        var checkedCharacters = 0;
        using var longClient = new WriteLiteLanguageClient(_options, _host.BaseAddress ?? _options.BaseUrl);
        var results = await SemanticTextChunker.RunAsync(
            chunks,
            async (chunk, token) =>
            {
                var dto = await longClient.CheckAsync(chunk.Text, _options.Language, token).ConfigureAwait(false);
                return dto.Matches?.ToList() ?? [];
            },
            maxConcurrency: SemanticTextChunker.DefaultMaxConcurrency,
            onOwnedCharactersChecked: count =>
            {
                var totalChecked = Interlocked.Add(ref checkedCharacters, count);
                ReportProgress(new TextAnalysisProgress(
                    text,
                    Math.Min(totalChecked, text.Length),
                    text.Length,
                    IsComplete: totalChecked >= text.Length));
            },
            cancellationToken).ConfigureAwait(false);

        var merged = MergeChunkMatches(text, results);
        ReportProgress(TextAnalysisProgress.Complete(text));
        return merged;
    }

    /// <summary>
    /// Split on paragraph/sentence boundaries when possible; never mid-surrogate.
    /// </summary>
    public static List<(int Start, string Chunk)> SplitIntoChunks(string text, int maxLen)
        => SemanticTextChunker.CreatePlan(text ?? string.Empty, maxLen)
            .Select(chunk => (chunk.Start, chunk.Text))
            .ToList();

    public static List<WriteLiteLanguageMatchDto> MergeChunkMatches(
        string document,
        IReadOnlyList<SemanticChunkResult<List<WriteLiteLanguageMatchDto>>> results)
    {
        document ??= string.Empty;
        var candidates = new List<WriteLiteLanguageMatchDto>();
        foreach (var result in results.OrderBy(item => item.Chunk.Index))
        {
            foreach (var match in result.Result ?? [])
            {
                if (match.Offset < 0 || match.Length < 0
                    || match.Offset > result.Chunk.Text.Length
                    || match.Length > result.Chunk.Text.Length - match.Offset)
                {
                    continue;
                }

                var globalStart = result.Chunk.Start + match.Offset;
                if (globalStart < 0 || globalStart > document.Length
                    || match.Length > document.Length - globalStart
                    || !result.Chunk.Owns(globalStart, match.Length, document.Length))
                {
                    continue;
                }

                candidates.Add(CloneAtGlobalOffset(match, globalStart));
            }
        }

        return candidates
            .GroupBy(match => new
            {
                match.Offset,
                match.Length,
                Rule = match.Rule?.Id ?? string.Empty,
                Message = match.Message ?? string.Empty,
                Replacements = string.Join("\u001f", (match.Replacements ?? []).Select(item => item.Value))
            })
            .Select(group => group
                .OrderByDescending(match => match.Replacements?.Count ?? 0)
                .ThenByDescending(match => match.Context?.Text?.Length ?? 0)
                .First())
            .OrderBy(match => match.Offset)
            .ThenBy(match => match.Length)
            .ThenBy(match => match.Rule?.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static WriteLiteLanguageMatchDto CloneAtGlobalOffset(
        WriteLiteLanguageMatchDto match,
        int globalOffset)
        => new()
        {
            Message = match.Message,
            ShortMessage = match.ShortMessage,
            Offset = globalOffset,
            Length = match.Length,
            Replacements = match.Replacements,
            Context = match.Context,
            Sentence = match.Sentence,
            Type = match.Type,
            Rule = match.Rule,
            IgnoreForIncompleteSentence = match.IgnoreForIncompleteSentence,
            ContextForSureMatch = match.ContextForSureMatch
        };

    private void ReportProgress(TextAnalysisProgress progress)
    {
        lock (_progressGate)
        {
            _lastProgress = progress;
        }

        AnalysisProgressChanged?.Invoke(this, progress);
    }

    private static int FindBreak(string text, int start, int end)
    {
        for (var i = end - 1; i > start + (end - start) / 2; i--)
        {
            var ch = text[i];
            if (ch is '\n' or '\r' or '.' or '!' or '?' or ';' or '…')
            {
                return Math.Min(text.Length, i + 1);
            }
        }

        for (var i = end - 1; i > start + (end - start) / 2; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return i + 1;
            }
        }

        return end;
    }

    private void SyncStatusFromHost()
    {
        if (!_extendedEnabled)
        {
            SetStatus(WriteLiteLanguageEngineState.Stopped, "Используется базовая проверка WriteLite");
            return;
        }

        if (_host.IsReady)
        {
            SetStatus(WriteLiteLanguageEngineState.Ready, "Языковой движок готов");
            return;
        }

        switch (_host.State)
        {
            case WriteLiteLanguageEngineState.Starting:
                SetStatus(WriteLiteLanguageEngineState.Starting, "Расширенная проверка запускается");
                break;
            case WriteLiteLanguageEngineState.Busy:
                SetStatus(WriteLiteLanguageEngineState.Busy, "Анализирует текст");
                break;
            case WriteLiteLanguageEngineState.Stopping:
            case WriteLiteLanguageEngineState.Stopped:
                SetStatus(WriteLiteLanguageEngineState.Stopped, "Используется базовая проверка WriteLite");
                break;
            case WriteLiteLanguageEngineState.Failed:
            case WriteLiteLanguageEngineState.Unavailable:
            case WriteLiteLanguageEngineState.Disabled:
                SetStatus(WriteLiteLanguageEngineState.Unavailable, "Расширенная проверка временно недоступна");
                CompatibilityLogger.State("language-engine-fallback-active");
                break;
            default:
                SetStatus(_host.State, "Используется базовая проверка WriteLite");
                break;
        }
    }

    private void SetStatus(WriteLiteLanguageEngineState state, string userText)
    {
        lock (_statusGate)
        {
            if (_publicState == state && _userStatus == userText)
            {
                return;
            }

            _publicState = state;
            _userStatus = userText;
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _extendedEnabled = false;
        try
        {
            // Dispose is the process-termination path. Do not wait for the
            // host's startup semaphore here: a readiness probe can own it
            // while WPF is already tearing down. Host.Dispose performs the
            // bounded force-stop and closes the kill-on-close job object.
            _host.Dispose();
        }
        catch
        {
            // best effort
        }

        _client.Dispose();
        SetStatus(WriteLiteLanguageEngineState.Stopped, "Используется базовая проверка WriteLite");
        await ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
