# WriteLite-Qwen (developer toolkit)

Python is for **training / eval / export only**. End users do not install Python.

## Product model

```text
WriteLite-Qwen-0.6B-GEC
base: Qwen/Qwen2.5-0.5B-Instruct (alt: Qwen3-0.6B)
```

Hybrid stack in the app:

1. Rules + spell + LanguageTool  
2. Lite `LocalCorrectionEngine`  
3. Optional WriteLite-Qwen (loopback llama-server / GGUF)  
4. C# diff + validator + merge  

## Dataset (≥10k)

```powershell
cd ai
python scripts/prepare_qwen_dataset.py --target-size 12000
# → data/chat/{train,validation,test}.jsonl
```

## Real LoRA training (not a stub)

```powershell
# Prefer local copy of base model if HF hub is flaky:
# ai/models/Qwen2.5-0.5B-Instruct/

python scripts/train_qwen_lora.py --config configs/qwen_smoke.yaml --smoke --model .\models\Qwen2.5-0.5B-Instruct
python scripts/train_qwen_lora.py --config configs/qwen_standard.yaml --model .\models\Qwen2.5-0.5B-Instruct

python scripts/export_writelight_qwen.py --adapter-dir outputs/qwen_smoke/adapter --output-dir ..\models\writelight-qwen --merge
```

Smoke training writes:

- `outputs/qwen_smoke/adapter/` — real LoRA weights  
- `train_report.json` — loss, device, steps  
- `samples_before.json` / `samples_after.json`

## Local server (dev bridge to C#)

```powershell
python scripts/serve_qwen_local.py --adapter outputs/qwen_smoke/adapter --port 8742
# models/writelight-qwen/local_endpoint.txt → http://127.0.0.1:8742
```

Production path: GGUF + `llama-server.exe` (loopback only).

## Evaluate

```powershell
python scripts/eval_hybrid_compare.py --adapter-dir outputs/qwen_smoke/adapter --limit 100
```
