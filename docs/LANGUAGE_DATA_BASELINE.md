# Language data baseline

Branch: `feat/full-local-dictionaries-synonyms-punctuation`  
Date: 2026-07-20  
Parent: `fix/real-dictionaries-no-false-corrections` (Hunspell + identical-candidate policy)

## Pre-expansion metrics

| Layer | Status |
|-------|--------|
| Tests | 381 passed / 3 skipped |
| RU Hunspell stems | ~146 269 |
| EN Hunspell stems | ~49 568 |
| Lexical JSON (before expand) | 64 demo/core |
| Lexical JSON (after expand) | **345** CC0 entries |
| Morphology side-car | `morphology-index.json` |
| SQLite optional | rebuilt from JSON (app uses JSON primary; SQLite store placeholder) |
| Identical policy | `CorrectionCandidateValidityPolicy` active |

## Current metrics (2026-08-08)

All figures below are computed from the generated artifacts, not estimated.

| Layer | Value |
|-------|-------|
| Tests | 490 passed / 3 skipped / 4 failed (all four are environment-only: local Qwen server and Release binaries) |
| RU Hunspell stems | 146 269 (`ru_RU.dic`, byte-identical to LibreOffice upstream) |
| EN Hunspell stems | 49 568 (`en_US.dic`, now built from source — see `tools/build-spelling/build_en_us_dictionary.py`) |
| RU lexical entries | 58 441 |
| RU entries with definitions | 48 379 (was 176) |
| RU entries with synonyms | 24 515 (was 28) |
| RU entries with antonyms | 11 855 (was 6) |
| RU definitions / synonyms / antonyms | 91 786 / 66 402 / 23 103 |
| RU entries with `pos=unknown` | 3 693 (was 5 020) |
| EN lexical entries | 84 236, all with definitions |
| EN definitions / synonyms / antonyms | 131 217 / 144 120 / 6 634 |
| Rule pack | 88 rules, `ru-1.1.0` (was 35, `ru-1.0.0`) |
| Runtime store | **SQLite primary** — 142 853 entries, 675 802 forms, FTS5 over definitions |
| Startup / RAM | 35 ms / 35 MB working set (was 1 991 ms / 406 MB) |

## Layers (separated)

1. **Spelling** — Hunspell `resources/spelling/hunspell/*` (ru_RU, en_US)
2. **Lexical definitions/synonyms/translations** — `writelight-lexical-open.json` (ru),
   `writelight-lexical-en.json` (en), `writelight-lexical-core.json` (authored CC0 core)
3. **Morphology index** — `morphology-index.json` (+ pack inflections)
4. **Punctuation** — `RuleBasedAnalyzer` + `resources/rules/*/punctuation.json` + `PunctuationRuleCatalog`
5. **Historical** — optional Dal importer (public-domain path)
6. **Ozhegov** — licensed-only importer (no full dump)
7. **Runtime storage** — `writelight-lexical.db`, built from layers 2–3;
   see [LEXICAL_STORAGE_BENCHMARK.md](LEXICAL_STORAGE_BENCHMARK.md)

Provenance and licences for every layer: `resources/lexical/sources.manifest.json`,
verified on each test run by `LexicalProvenanceTests`.

## False-positive invariant

`привет → привет` is forbidden and regression-tested.
