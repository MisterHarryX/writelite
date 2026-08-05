# Click-to-correct baseline

Branch: `fix/no-hang-click-to-correct-language-core`  
Date: 2026-07-19  
Parent: `fix/input-latency-inline-corrections` (dirty-range, fast/deep debounce, geometry cache, UIA TestHost)

## Baseline commands

```powershell
dotnet clean .\WriteLite.sln -c Release
dotnet restore .\WriteLite.sln
dotnet build .\WriteLite.sln -c Release
dotnet test .\WriteLite.sln -c Release --no-build
```

## Pre-fix symptoms (user-reported)

1. App can hang / "Not responding".
2. Click on underline often does not apply correction.
3. "Исправить" appears pressed but text unchanged.
4. Stale issues between card open and apply.
5. Punctuation shown as `а → а.`.
6. Single/double click unstable.
7. Weak local language libraries.
8. Multi-instance conflicts.
9. Popup + panel + analysis overload UI thread.

## Known production risks before this branch

| Area | Risk |
|------|------|
| Apply path | Silent failure on mismatch / write fail (`correction-failed` without UI) |
| Punctuation | Terminal hint replaced last letter with `letter+dot` |
| Multi-instance | No global mutex; multiple monitors/backends |
| UIA | Timeouts existed but no circuit-breaker skip |
| UI thread | Sync `GetAwaiter().GetResult()` remains in dispose/test paths only |

## Success criteria for this branch

- Single-instance mutex + activate pipe
- Unified `CorrectionApplicationService` with non-silent outcomes
- Safe stale relocation ±96 chars
- Punctuation pure insert + "Добавить точку"
- UI responsiveness watchdog on diagnostics
- UIA circuit breaker
- Tests green; publish smoke
