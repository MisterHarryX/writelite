# Local inference

## Profiles

| Profile | Backend | RAM target | Notes |
|---------|---------|------------|-------|
| Lite | C# `LocalCorrectionEngine` | &lt; 200 MB | Ships with app, no weights |
| Standard | ONNX when available | ~1–2 GB | Falls back to Lite |
| Quality | Larger model when present | ~2–4 GB+ | Falls back to Standard/Lite |
| Auto | Hardware probe | — | Chooses Standard if ONNX+RAM OK else Lite |

## C# entry points

- `LocalAiTextAnalyzer` (`WriteLite.AI.Local`)
- `LocalAiTextProvider` (`WriteLite.App`)
- Settings: `LocalAiEnabled`, `LocalAiProfile`

## Model directory

```text
models/writelight-gec/
  model.manifest.json
  encoder.onnx          (optional)
  encoder.int8.onnx     (optional)
  hf/                   (optional training export)
```

Integrity: zero-length ONNX rejected. Missing dir → Lite only.

## Runtime guarantees

- Single-flight gate (no parallel model thrash)  
- Request cache (10 min)  
- CancellationToken  
- No network sockets for local analysis  
- Warmup on app start when Local AI enabled  
