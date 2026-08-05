# Итерация 1 — этапы 5+ (продолжение)

## Уточнения к этапам 1–4

- Idle grace по умолчанию **4 с** (`EditingIdleGraceSeconds`, 2–30).
- `InsideBottomRight` — только last resort; запрещён при overlap caret/anchor/существенной части поля.
- Немедленное скрытие main UI на unsupported/read-only/password.

## Этапы 5–9 — лексика

Реализовано и покрыто unit-тестами:

- контракты и сервисы в `Services/Lexical/`
- demo pack CC0
- double-click observer + `LexicalPopupWindow`
- безопасная замена

## Этап 10–11 — AI

- fixed suite + `run_fixed_benchmark.py`
- training pipeline документирован; полное переобучение не запускалось без улучшения baseline
