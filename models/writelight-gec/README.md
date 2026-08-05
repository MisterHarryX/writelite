# WriteLite GEC model pack (optional)

Place exported model files here after training:

```text
model.manifest.json
encoder.onnx            # optional
encoder.int8.onnx       # optional
hf/                     # HuggingFace export from ai/scripts/export_onnx.py
```

Without these files the app uses the **Lite** offline engine embedded in `WriteLite.AI.Local`.

Export:

```powershell
cd ai
python scripts/export_onnx.py --model-dir outputs/standard/best --output-dir ..\models\writelight-gec
```
