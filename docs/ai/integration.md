# Integration with WriteLite

## Wiring (`App.xaml.cs`)

1. Build orchestrator (rules + spell + LanguageTool).  
2. Create `LocalAiTextProvider` with a loopback-only Qwen endpoint.
3. Wrap it in `AiTextAnalysisService` + `HybridTextAnalysisService`.
4. `TextFieldMonitor` uses the hybrid analyzer.

## Settings

| Key | Default | Meaning |
|-----|---------|---------|
| `LocalAiEnabled` | `true` | Offline AI path |
| `LocalAiProfile` | `Auto` | Lite/Standard/Quality/Auto |
| `AiMinTextLength` / `AiMaxTextLength` | 12 / 12000 | Gates |
| `AiDebounceMs` | 1500 | Local inference debounce |

## Conflict resolution

`WriteLiteIssueMerger`: local rules first, engine second, AI tertiary.  
AI issues re-checked against current text fingerprint; stale offsets dropped.

## UI status

Status line appends `· Локальный ИИ (Lite|…)` when local AI is active.
