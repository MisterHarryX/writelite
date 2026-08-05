# WriteLite-Qwen stage report

**Date:** 2026-07-11  
**Model:** `WriteLite-Qwen-0.6B-GEC-1.0.0-dev`  
**Base:** `Qwen/Qwen2.5-0.5B-Instruct` (local mirror under `ai/models/`)

## Architecture (unchanged hybrid)

```text
Rules + Spell + LanguageTool
  + LocalCorrectionEngine (Lite assist, always offline)
  + AiCallRouter → optional WriteLite-Qwen (loopback)
  + Diff + Validator + Merger
Fallback: any neural failure → Lite/rules only
```

## Dataset

| Split | Count |
|-------|------:|
| train | 9551 |
| validation | 1279 |
| test | 1170 |
| **all** | **12000** |

Path: `ai/data/chat/` · format: chat messages + fixed system prompt · license seed Apache-2.0.

## Real smoke training (not stub)

| Field | Value |
|-------|-------|
| Device | CPU (PyTorch 2.13.0+cpu; CUDA wheel unavailable for Python 3.14) |
| GPU | RTX 4070 present in system, unused this run |
| Train examples (smoke) | 256 |
| Val examples | 64 |
| LoRA r / alpha / dropout | 8 / 16 / 0.05 |
| Target modules | q,k,v,o,gate,up,down |
| Steps | 20 |
| Train loss | **1.672** |
| Final eval loss | **0.833** |
| Duration | ~701 s |
| Adapter | `ai/outputs/qwen_smoke/adapter/` (+ checkpoint-20) |
| Trainable params | 4,399,104 (0.88%) |

### Inference before vs after (fixed chat template)

| Input | Before (base) | After (WriteLite-Qwen smoke) |
|-------|---------------|------------------------------|
| `привет … небыл в школе` | markdown JSON with `text` | schema with `correctedText` + issues; «не был» |
| `I has a new computer…` | free-form fix | structured JSON |
| `where are you going…` | free-form | `Where are you going? I don't know.` |

Live HTTP (`serve_qwen_local.py` on 127.0.0.1:8742):

```json
{"language":"ru","text":"Привет, как дела? Я сегодня не был в школе."}
```

C# parser accepts `correctedText` **or** `text` alias; offsets always re-diffed.

## Runtime

| Component | Status |
|-----------|--------|
| `QwenModelBackend` | loopback OpenAI-compatible HTTP only |
| `local_endpoint.txt` | `http://127.0.0.1:8742` |
| GGUF + llama-server | pack layout ready; binary not bundled yet |
| Python at end-user | **not required** (dev server only) |

## C# / tests

- Build: OK  
- Tests: **248 passed** (live server test inconclusive if server down)  
- Lite engine + rules preserved  
- Router skips URL/email/path-only / short text  

## Known limits (honest)

1. Smoke LoRA is short (20 steps) — Quality needs full `qwen_standard.yaml` train.  
2. CPU-only torch on Python 3.14; install CUDA-capable Python 3.11/3.12 for GPU speed.  
3. GGUF quant + bundled llama-server still next packaging step.  
4. Neural output quality must be measured on full hybrid vs rules-only test set after longer train.  
5. Model may still emit simplified JSON; validator + Lite fallback keep app safe.

## Commands

```powershell
# Dataset
cd ai
python scripts/prepare_qwen_dataset.py --target-size 12000

# Smoke train
python scripts/train_qwen_lora.py --config configs/qwen_smoke.yaml --smoke --model .\models\Qwen2.5-0.5B-Instruct

# Dev server (loopback)
python scripts/serve_qwen_local.py --adapter outputs/qwen_smoke/adapter --port 8742

# App
dotnet run --project .\src\WriteLite.App\WriteLite.App.csproj
```

## Next recommended steps

1. Full LoRA train on 9.5k+ examples (standard config) with CUDA.  
2. Merge adapter → GGUF Q4_K_M → ship `llama-server.exe`.  
3. Hybrid eval table: rules | LT | base Qwen | WriteLite-Qwen | full pipeline.  
4. Settings UI: Local AI profile + “deep check” toggle.
