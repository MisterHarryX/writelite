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

## Layers (separated)

1. **Spelling** — Hunspell `resources/spelling/hunspell/*`
2. **Lexical definitions/synonyms/translations** — `writelight-lexical-core.json`
3. **Morphology index** — `morphology-index.json` (+ pack inflections)
4. **Punctuation** — `RuleBasedAnalyzer` + `resources/rules/*/punctuation.json` + `PunctuationRuleCatalog`
5. **Historical** — optional Dal importer (public-domain path)
6. **Ozhegov** — licensed-only importer (no full dump)

## False-positive invariant

`привет → привет` is forbidden and regression-tested.
