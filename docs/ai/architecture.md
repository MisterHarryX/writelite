# Local AI architecture

## Pipeline

```text
TextFieldMonitor
      │
      ▼
HybridTextAnalysisService
      ├─► WriteLiteOrchestratingAnalyzer
      │      RuleBasedAnalyzer
      │      SpellTextAnalyzer
      │      LanguageTool engine (optional)
      │
      └─► AiTextAnalysisService
             LocalAiTextProvider  (default ON, local-only)
                LocalAiTextAnalyzer
                   Lite: LocalCorrectionEngine
                   Standard/Quality: Qwen loopback → Lite fallback
                   AiResultValidator + TextDiffBuilder
      │
      ▼
WriteLiteIssueMerger → UI → safe apply
```

## Projects

| Project | Role |
|---------|------|
| `WriteLite.AI.Contracts` | Schema v1 DTOs, issue types, `ILocalAiTextAnalyzer` |
| `WriteLite.AI.Local` | Offline runtime (Lite engine, validation, ONNX host stub) |
| `WriteLite.App` Services/Ai | Providers, hybrid merge, app wiring |
| `ai/` | Python train/eval/export (developers only) |

## Contracts (schema v1)

- Request: text, language hint, mode, schemaVersion, profile
- Response: correctedText, language, issues[], modelVersion, schemaVersion, timing, backend, uncertain flag
- Each issue: start, length, original, replacement, type, explanation, confidence, safeToApply

## Validation rules

1. Parse JSON / map DTO  
2. Schema version check  
3. Range check  
4. `original` must match source slice  
5. No overlapping issues  
6. Protected tokens (URL, email, path, numbers, product names) preserved  
7. Reject excessive rewrite  
8. Deterministic rebuild via right-to-left apply  

## Failure modes

Missing model, corrupt weights, OOM, timeout, cancel, invalid output → empty AI issues, local rules continue, app stays up.

## Privacy

Local path never opens sockets for analysis. Logs: event type, duration, model version, input length, issue count — never user text.
