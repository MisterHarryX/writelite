# Troubleshooting

| Symptom | Check |
|---------|--------|
| No AI issues | `LocalAiEnabled`? text length between min/max? hybrid debounce still waiting? |
| Qwen unavailable | Check the local loopback endpoint; Lite fallback remains available |
| High RAM | Switch profile to Lite; remove ONNX pack |
| Model not used | `models/writelight-gec/*.onnx` present? Lite fallback is expected until ORT GenAI is wired |
| Training fails | `pip install -r ai/requirements.txt`; GPU optional for smoke |
| Tests fail on defaults | `LocalAiEnabled` defaults true; language is fixed to `ru-RU` |

## Logs

`logs/compatibility.log` next to the binary — technical events only, no field text.
