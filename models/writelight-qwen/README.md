# WriteLite-Qwen model pack

Expected layout after training/export:

```text
models/writelight-qwen/
  model.manifest.json
  system_prompt.txt
  local_endpoint.txt      # optional: http://127.0.0.1:8742
  adapter/                # LoRA adapter (dev)
  merged-hf/              # optional merged weights
  *.gguf                  # preferred production weights
  llama-server.exe        # optional bundled binary
```

## Developer flow

```powershell
cd ai
python scripts/prepare_qwen_dataset.py --target-size 12000
python scripts/train_qwen_lora.py --config configs/qwen_smoke.yaml --smoke
python scripts/export_writelight_qwen.py --adapter-dir outputs/qwen_smoke/adapter --output-dir ..\models\writelight-qwen
python scripts/serve_qwen_local.py --adapter outputs/qwen_smoke/adapter --port 8742
```

Write `local_endpoint.txt` with:

```text
http://127.0.0.1:8742
```

Without this pack, WriteLite continues on rules + spell + LanguageTool + Lite engine.

## Production memory profile

WriteLite starts the bundled `llama-server` with a fixed single-slot, CPU-only
profile: 768 context tokens, batch 256 and micro-batch 128. The shipped Q4_K_M
GGUF is about 380 MiB; the bounded KV cache and one request slot keep normal
runtime memory within the 1–2 GiB desktop budget. Do not replace this profile
with a large context or multiple parallel slots without measuring the resulting
memory use.
