# WriteLite — лексический слой и AI pipeline

## Лексический слой (этапы 5–9)

### Архитектура

```text
DoubleClickWordObserver
  → EditableTextTargetPolicy
  → WordRangeResolver
  → OfflineLexicalKnowledgeService (+ demo/full pack)
  → ContextualLexicalRanker
  → LexicalPopupWindow
  → LexicalReplacementService (safe write)
```

- **Не зависит от Qwen**: offline pack — единственный источник синонимов/переводов/толкований/примеров.
- Qwen может быть добавлен позже только для ранжирования, не как единственный источник.
- Логи: только технические метаданные (request id, counts), без пользовательского текста.

### Demo pack

```text
resources/lexical/writelight-lexical-demo.json
License: CC0-1.0 (hand-authored WriteLite demo)
isDemo: true
```

Это **не** полный словарь. Отсутствие слова показывается честным empty state.

### Полный pack

1. Подготовьте JSON формата `LexicalPackDocument` (см. `LexicalPackModels.cs`).
2. Укажите `license`, `packVersion`, `formatVersion=1`.
3. Положите файл в `%AppBase%/resources/lexical/pack.json` или `writelight-lexical-demo.json`.
4. Опционально задайте `sha256` (hex SHA-256 всего файла).
5. Не включайте данные с неизвестной лицензией.

### Замена синонима

Повторные проверки: target, range UTF-16, original match, write support, password/read-only.
Сохранение регистра через `WordMorphologyService.ApplySurfaceCase` / `InflectLike`.

---

## AI benchmark

```powershell
cd ai
python scripts/run_fixed_benchmark.py
# optional neural:
python scripts/run_fixed_benchmark.py --model-dir path\to\hf_checkpoint
```

Suite: `ai/data/benchmark/fixed_suite.jsonl`  
Report: `ai/outputs/fixed_benchmark_report.json`

Existing tools:

```powershell
python scripts/prepare_dataset.py
python scripts/evaluate.py --baseline
python scripts/train_qwen_lora.py --config configs/qwen_smoke.yaml
```

### Training note

Full LoRA retrain is **not** executed in this iteration by default:

- requires sufficient RAM/VRAM and long wall time;
- smoke weights must not be presented as production GEC quality;
- new weights should replace baseline only when fixed benchmark improves F0.5 and reduces protected-token damage.

Commands (when hardware allows):

```powershell
cd ai
python scripts/prepare_qwen_dataset.py
python scripts/train_qwen_lora.py --config configs/qwen_standard.yaml
python scripts/run_fixed_benchmark.py --model-dir outputs\qwen_standard\merged
python scripts/export_writelight_qwen.py
```

---

## Privacy

- AI analysis is local-only; Qwen is reachable only over loopback HTTP.
- Lexical lookup is local.
- Diagnostics never log full user documents.
