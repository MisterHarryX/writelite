# Hang root cause analysis v2

## Method

1. Static scan of production code for blocking patterns:
   - `.GetAwaiter().GetResult()`, `.Result`, `.Wait()`, `WaitForExit`, `Dispatcher.Invoke`
2. Runtime signals from prior stability work (`fix/input-latency-inline-corrections`)
3. UI responsiveness watchdog (250 ms / 2 s thresholds)
4. Lifecycle smoke with single-instance

No production hang dump was captured in this session with a live “Not responding” dialog; root causes below are from **code-path proof** + prior telemetry patterns. When a hang is observed, use:

```powershell
dotnet-dump collect -p <pid>
# or ProcDump -ma <pid> hang.dmp
```

Do not commit dumps.

## Stack-trace classes (expected / observed patterns)

### A. UI Automation COM on UI-adjacent work

**Symptom:** process “Not responding” while focusing foreign apps / reading text.

**Mechanism:** UIA COM apartments can block when the target app is busy. Even when work is on a worker thread, flooding of concurrent UIA ops starves UI messages.

**Mitigation shipped:**

- `AutomationTextTarget` timeout (750 ms) + `WaitAsync`
- `UiaCircuitBreaker` trips on timeout, skips ops for ~8 s
- Overlay/popup paths must not block on UIA on the dispatcher

### B. Silent apply loops + re-analysis storms

**Symptom:** click “Исправить” → no text change → user retries → analysis storms.

**Mechanism:** `ReplaceTargetRangeAsync` / `ReplaceTargetTextAsync` returned on `CanApplyManual` failure **without MessageBox**, so users re-clicked under load.

**Mitigation shipped:**

- `CorrectionApplicationService` always returns `UserMessage`
- UI shows popup status + MessageBox on failure
- Gate prevents concurrent apply

### C. Sync waits (residual, non-UI hot path)

Located (should stay off UI handlers):

| Location | Call | Notes |
|----------|------|--------|
| `HybridTextAnalysisService` | `AnalyzeAsync().GetAwaiter().GetResult()` | Sync wrapper for tests/legacy |
| `WriteLiteLanguageEngine` | sync Analyze / Dispose | Dispose path |
| `WriteLiteLanguageEngineHost` | `StopOwnedProcessAsync().GetResult()` | Shutdown |
| `LocalAiTextProvider` | Dispose async sync | Shutdown |
| `QwenModelBackend` | health `GetResult` | Startup probe on worker context |

**Rule:** UI click handlers must only `await` or `BeginInvoke`.

### D. Multi-instance interference

**Symptom:** hangs / flaky underlines when two WriteLite.exe run.

**Mechanism:** dual focus hooks + dual LanguageTool/Qwen.

**Mitigation shipped:**

- `Global\WriteLite.Application.SingleInstance`
- Secondary instance signals `SHOW_MAIN_WINDOW` via named pipe and exits 0

### E. Analysis backlog

**Symptom:** lag while typing with panel+popup open.

**Mechanism:** overlapping fast/deep analyses.

**Mitigation (existing + extended):**

- Debounced analyzer cancels previous; max concurrent 1
- `DroppedAsStaleCount` telemetry
- Dirty-range / priority from prior branch retained

## Dispatcher watchdog

`UiResponsivenessWatchdog` posts a high-priority callback every ~400 ms:

- delay ≥ 250 ms → log `ui-delay`
- delay ≥ 2000 ms → log `ui-hang-suspected`
- Diagnostics page: **Норма / Задержка / Обнаружено зависание**

Never kills the process automatically.

## Conclusion

Primary user-visible “click does nothing” root cause: **silent failed apply + stale ranges**, not missing write APIs.

Primary hang risk: **UIA COM latency + multi-instance + analysis/UI contention**. Addressed with timeouts, circuit breaker, single-instance, bounded debounce, and non-blocking apply UX.
