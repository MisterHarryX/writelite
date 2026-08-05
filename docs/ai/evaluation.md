# Evaluation

```powershell
cd ai
python scripts/evaluate.py --baseline
python scripts/evaluate.py --model-dir outputs/standard/best
```

## Metrics

- Exact match  
- CER / WER  
- Token F1  
- Clean false-positive rate  
- Latency p50 / p95  

Buckets: language (`ru`/`en`), `clean`, `protected` (URL/email/path).

## C# regression

`tests/WriteLite.Tests/Ai/LocalAiAnalyzerTests.cs` covers:

- RU capitalization / «не был» / punctuation  
- EN agreement  
- URL/email/path/product preservation  
- clean text stability  
- emoji indexes  
- cancellation  
- ONNX missing → Lite fallback  
- overlap validation  
- prompt-injection isolation  
