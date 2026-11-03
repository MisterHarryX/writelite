# WriteLite offline translator

Helsinki-NLP opus-mt models (ru-en, en-ru; CC-BY 4.0), exported to int8 ONNX.
The `.onnx` graphs (~140 MB per direction) are not in git, like every other model here.
Regenerate them with:

    python -I ai/scripts/export_marian_onnx.py models/writelite-translate

Tracked per direction: `source.tsv`, `target.tsv` (SentencePiece pieces), `vocab.tsv`, `config.json`.
