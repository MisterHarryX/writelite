# Training

## Environment

```powershell
cd ai
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
python scripts/prepare_dataset.py
```

## Smoke training

```powershell
python scripts/train.py --config configs/smoke.yaml --smoke
```

- Fixed seed  
- Tiny max_steps  
- Writes `outputs/smoke/train_metrics.json`  
- If torch/transformers missing, writes stub metrics and exits 0  

## Full training

```powershell
python scripts/train.py --config configs/standard.yaml
```

Supports: gradient accumulation, fp16/bf16 when CUDA present, checkpoints, resume via `resume_from_checkpoint`, logging of train metrics.

## Export

```powershell
python scripts/export_onnx.py --model-dir outputs/standard/best --output-dir ..\models\writelight-gec
python scripts/quantize.py --model-dir ..\models\writelight-gec
```

App copies `models/writelight-gec/**` to output when present. Without weights, Lite profile remains fully functional.
