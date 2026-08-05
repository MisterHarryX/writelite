# WriteLite Local AI — Task Board (WriteLite-Qwen)

**Lead Architect:** Grok  
**Updated:** 2026-07-11  
**Pivot:** mT5 abandoned as primary → **WriteLite-Qwen** (Qwen family) as intelligent layer over rules/LT

## Architecture (locked)

```text
Text → Rules + Spell + LanguageTool (always)
     → AiRouter (when to call neural)
     → WriteLite-Qwen (optional local process / GGUF)
     → Diff + Validator + Merger
     → UI
Fallback: if Qwen missing/fail/low-confidence → rules+LT only (LocalCorrectionEngine still available as Lite assist)
```

| ID | Name | Owner | Priority | Status | Done when |
|----|------|-------|----------|--------|-----------|
| Q01 | Pivot docs + model naming | Lead | P0 | done | WriteLite-Qwen-0.6B-GEC chosen |
| Q02 | Dataset ≥10k chat JSONL | Data | P0 | done | 12k chat pairs |
| Q03 | Real Qwen LoRA train (no stub) | Training | P0 | done | adapter + train_loss 1.67 |
| Q04 | Export merge + GGUF quant | Training | P0 | partial | adapter pack; GGUF next |
| Q05 | C# Qwen runtime (loopback) | Inference | P0 | done | QwenModelBackend + server |
| Q06 | AiRouter + hybrid merge | Integration | P0 | done | router + fallback |
| Q07 | Control examples + eval | Eval/QA | P0 | partial | before/after + live HTTP |
| Q08 | Tests + build green | QA | P0 | done | suite green |

## Model decision (provisional → confirm after smoke)

| Candidate | Role | Notes |
|-----------|------|-------|
| **Qwen/Qwen3-0.6B** or **Qwen2.5-0.5B-Instruct** | Lite/Standard base | Prefer Qwen3-0.6B if HF + transformers support works; else Qwen2.5-0.5B-Instruct as proven fallback |
| Qwen2.5-1.5B / Qwen3-1.7B | Quality | Separate pack, never co-loaded |

**Product name:** `WriteLite-Qwen-0.6B-GEC` (version `1.0.0-dev` until full train)  
**Runtime preference:** GGUF + llama.cpp sidecar (localhost) or LLamaSharp; C# host; no Python for end users.

## File ownership

| Zone | Owner |
|------|-------|
| `ai/**` | Data + Training |
| `src/WriteLite.AI.Local/**` | Inference |
| `src/WriteLite.App/Services/Ai/**` | Integration |
| `docs/ai/**` | Lead |
| `tests/**/Ai/**` | QA |
