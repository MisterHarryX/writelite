# Model selection — WriteLite-Qwen

## Decision (2026-07-11)

| Role | Choice | Rationale |
|------|--------|-----------|
| **Primary base** | `Qwen/Qwen2.5-0.5B-Instruct` (ship name: **WriteLite-Qwen-0.6B-GEC**) | Apache-2.0, RU+EN, small enough for desktop, mature HF tooling; Qwen3-0.6B used as alt when available |
| **Quality base** | Qwen ~1.5–1.7B Instruct | Separate pack, never co-loaded with Lite |
| **Rejected as primary** | mT5-small | Weaker instruction/JSON control for explanations; pivot to Qwen family |

Product model id:

```text
WriteLite-Qwen-0.6B-GEC-1.0.0-dev
```

## Hybrid layering (locked)

```text
Rules + Spell + LanguageTool     ← always
LocalCorrectionEngine (Lite)     ← always available offline assist
AiCallRouter                     ← when to call neural
WriteLite-Qwen (GGUF/local server) ← optional intelligent layer
C# Diff + AiResultValidator      ← never trust model offsets blindly
Merger → UI
```

## Runtime comparison

| Runtime | CPU | GPU | No Python | Qwen | Ship | C# | Choice |
|---------|-----|-----|-----------|------|------|-----|--------|
| GGUF + llama-server (loopback) | Yes | Yes | Yes | Yes | Yes | HTTP localhost | **Preferred** |
| LLamaSharp | Yes | Yes | Yes | Yes | Native | P/Invoke | Alternative |
| ONNX Runtime GenAI | Yes | Yes | Yes | Partial | NuGet | Good | Secondary |
| Python sidecar | Yes | Yes | **No** | Yes | Dev only | HTTP | **Train + dev serve only** |

## Inference policy

- temperature = 0 / do_sample=false  
- thinking off  
- fixed system prompt  
- max_new_tokens limited  
- text is data, not instructions  
