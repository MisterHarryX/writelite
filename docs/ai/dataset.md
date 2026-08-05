# Dataset

## Record format

```json
{
  "id": "unique-id",
  "language": "ru",
  "source": "text with errors",
  "target": "corrected text",
  "issues": [],
  "sourceType": "synthetic|clean-identity",
  "license": "Apache-2.0",
  "split": "train|validation|test"
}
```

## Pipeline

```powershell
cd ai
python scripts/prepare_dataset.py --seed 42
```

- Seed clean sentences: `ai/data/seed/{ru,en}_clean.jsonl`
- Synthetic errors: `writelight_ai/errors.py` (seeded, typed transforms)
- Splits by **target** hash to reduce leakage
- Dedupe by source+target fingerprint
- Provenance: `data/processed/dataset_meta.json`
- Licenses: `ai/data/licenses.json`

## Transform categories

Typo (swap/drop/dup), punctuation strip, capitalization, Russian «не» glue & common misspellings, English misspellings & agreement, word repetition, spacing before punctuation.

## Clean identity pairs

Included so the model learns **not** to rewrite correct text (false-positive control).
