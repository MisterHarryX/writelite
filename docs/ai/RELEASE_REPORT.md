# WriteLite Local AI — Release Report (MVP)

**Date:** 2026-07-11  
**Branch workspace:** WriteLite-Starter  

## 1. Итоговая архитектура

Гибрид (Approach C):

1. Быстрые правила + словарь + LanguageTool (как было).  
2. Локальный ИИ Lite (C# `LocalCorrectionEngine`) — по умолчанию.  
3. Опциональный ONNX pack (Standard/Quality) с fallback на Lite.  
4. Валидатор + diff + merger.

Проекты: `WriteLite.AI.Contracts`, `WriteLite.AI.Local`, интеграция в `WriteLite.App`.

## 2. Выбранная модель

- **Runtime Lite:** встроенный `writelight-lite-1.0.0` (rules JSON + engine).  
- **Train target:** `google/mt5-small` (Apache-2.0).  
- **Backup:** `Qwen2.5-0.5B-Instruct` (Apache-2.0).

## 3. Причины выбора

- mT5: RU+EN, seq2seq GEC, коммерческая лицензия, умеренный размер.  
- Lite C#: нулевая зависимость от Python/весов у пользователя.  
- Hybrid: устойчивость JSON/offset, сохранение существующего pipeline.

## 4. Использованные датасеты

- `ai/data/seed/ru_clean.jsonl`, `en_clean.jsonl` (синтетический seed).  
- Генератор ошибок → `ai/data/processed/{train,validation,test}.jsonl` (111 пар на seed=42).

## 5. Лицензии

См. `docs/ai/licenses.md`, `ai/data/licenses.json`, `ThirdParty/THIRD_PARTY_NOTICES.txt`.

## 6. Параметры обучения

`ai/configs/smoke.yaml` / `standard.yaml` — seed 42, mT5-small, max lengths 128–256, grad accum, fp16 optional.

## 7. Результаты обучения

На машине разработки: **torch не установлен** → smoke-train записал stub checkpoint (`outputs/smoke/checkpoint-stub.json`).  
Полное обучение: `pip install -r ai/requirements.txt` затем `python scripts/train.py --config configs/standard.yaml`.

## 8. Результаты evaluation

Baseline (`evaluate.py --baseline`) на test split (n=9):

| Metric | Value |
|--------|-------|
| exact_match | ~0.22 |
| CER | ~0.075 |
| WER | ~0.32 |
| token_f1 | ~0.75 |
| latency mean | &lt; 0.1 ms |

C# Lite engine покрыт regression-тестами (RU/EN/protected/clean/injection).

## 9. Результаты benchmark

`LocalAiBenchmarkTests`: p95 latency short texts &lt; 500 ms (обычно ≪ 50 ms).

## 10. Размер модели

- Lite: embedded JSON ≪ 1 MB.  
- mT5-small weights (when trained/exported): ~300 MB + tokenizer.

## 11. Потребление RAM / VRAM

- Lite: &lt; ~200 MB additional.  
- Standard neural (planned): ~1–2 GB.  
- GPU optional.

## 12. Производительность CPU / GPU

Lite CPU-only, подходит для weak PCs. Neural path not required for MVP.

## 13. Интеграция с WriteLite

- `LocalAiEnabled=true` по умолчанию.  
- `LocalAiTextProvider` → hybrid analyzer.
- Внешние ИИ-провайдеры отсутствуют.
- Status: «Локальный ИИ (Lite|…)».

## 14. Созданные файлы (основные)

- `src/WriteLite.AI.Contracts/**`  
- `src/WriteLite.AI.Local/**`  
- `src/WriteLite.App/Services/Ai/LocalAiTextProvider.cs`  
- `ai/**`  
- `docs/ai/**`  
- `models/writelight-gec/README.md`  
- `tests/WriteLite.Tests/Ai/LocalAi*.cs`

## 15. Изменённые файлы

- `WriteLite.sln`, `WriteLite.App.csproj`, `App.xaml.cs`  
- `WriteLiteAppSettings.cs`, `HybridTextAnalysisService.cs`  
- `THIRD_PARTY_NOTICES.txt`, `README.md`, `.gitignore`  
- legacy default tests for AI settings

## 16. Результат сборки

`dotnet build WriteLite.sln` — **успех, 0 errors**.

## 17–19. Тесты

| | |
|--|--|
| Всего | 242 |
| Успешно | 241 |
| Пропущено | 1 (`LiveHost_Check_Stop_FreesProcessAndPort`) |
| Неуспешно | 0 |

## 20. Известные ограничения

1. Neural ONNX inference **ещё не исполняется** в runtime (host готов, ORT GenAI не подключён) — всегда Lite fallback.  
2. Lite — эвристики + словарь, не полноценная NLU-грамматика.  
3. Полное mT5 обучение требует `torch`/`transformers` и времени GPU/CPU.  
4. Перевод RU↔EN — контракт есть, приоритет MVP — correction.  
5. Comma heuristics для русского могут быть избыточны на сложных предложениях (validator снижает risk).

## 21. Инструкция запуска

```powershell
dotnet run --project .\src\WriteLite.App\WriteLite.App.csproj
```

## 22. Инструкция обучения

```powershell
cd ai
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
python scripts/prepare_dataset.py
python scripts/train.py --config configs/smoke.yaml --smoke
python scripts/train.py --config configs/standard.yaml
```

## 23. Обновление модели

1. Обучить / экспортировать в `models/writelight-gec/`.  
2. Обновить `model.manifest.json` version.  
3. Пересобрать приложение (content copy).  
4. Откат: удалить pack → автоматически Lite.

## 24. Готовность MVP

| Критерий | Статус |
|----------|--------|
| Сборка | ✅ |
| Существующие функции | ✅ |
| Локальный запуск (Lite) | ✅ |
| Без Python у пользователя | ✅ |
| RU / EN | ✅ |
| Пунктуация / грамматика / опечатки (базовые) | ✅ |
| Валидация ответа | ✅ |
| CancellationToken / stale | ✅ |
| URL/email/path/numbers | ✅ |
| Fallback без модели | ✅ |
| Автотесты / benchmark / docs / licenses | ✅ |
| Нет скрытой отправки текста (local) | ✅ |
| Neural weights production pack | ⏳ optional next |

**MVP локального ИИ (Lite + pipeline обучения) — готов к использованию.**  
Neural Standard weights — следующий шаг после `pip install` + full train.

## 25. Следующие улучшения

1. Подключить Microsoft.ML.OnnxRuntime / GenAI к `OnnxModelBackend`.  
2. Полный fine-tune mT5 на расширенном датасете.  
3. Отдельный explain-head или шаблоны объяснений по типу ошибки.  
4. UI-toggle Local AI profile в Settings.  
5. Streaming / sentence-window для длинных текстов.  
6. Translation mode end-to-end.
