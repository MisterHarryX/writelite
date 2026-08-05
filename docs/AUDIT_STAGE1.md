# Аудит WriteLite — этап 1 (исходное состояние)

Дата: 2026-07-19  
Сборка: `dotnet build WriteLite.sln -c Release` — **успешно**  
Тесты: `dotnet test` — **280 passed, 3 skipped**, 0 failed

## Структура решения

| Проект | Назначение |
|--------|------------|
| `WriteLite.App` | WPF UI, UIA-мониторинг, overlay, настройки |
| `WriteLite.AI.Contracts` | Контракты локального AI-анализа |
| `WriteLite.AI.Local` | Lite rules, Qwen backend, protected spans, diff |
| `WriteLite.Tests` | MSTest unit/integration |
| `ai/` | Python training/eval pipeline, датасеты, smoke LoRA |
| `models/writelight-qwen` | GGUF/adapter pack для локального Qwen |
| `resources/rules` | JSON-правила RU/EN |

---

## 1. Определение активного редактируемого поля

**Точки входа:** `TextFieldMonitor` + `EditableTextTargetPolicy` + `CompositeTextTargetAdapter`.

Механизмы:

- `Automation.AddAutomationFocusChangedEventHandler`
- WinEvent `EVENT_SYSTEM_FOREGROUND`
- polling каждые **400 ms**

Алгоритм:

1. Берётся `AutomationElement.FocusedElement`.
2. Подъём по дереву (до 8 уровней) через `TreeWalker.ControlViewWalker`.
3. `EditableTextTargetPolicy.IsEditableTextTarget`:
   - `IsEnabled`, `!IsOffscreen`, `!IsPassword`
   - `HasKeyboardFocus` + `IsKeyboardFocusable`
   - `ValuePattern` editable **или** (`TextPattern` + Edit/Document)
4. Выбор адаптера (`ValuePattern` / `TextPattern` / Win32).
5. Отклонение слишком маленьких bounds (`W < 20` или `H < 12`).
6. Собственный процесс WriteLite игнорируется.

**Identity:** `ActiveTextTargetManager` + `ActiveTextTargetIdentity` (processId, HWND, runtimeId, controlType, bounds) с `GenerationId`.

---

## 2. Отслеживание изменений текста

**Сейчас:** сравнение `_lastText` при poll/focus refresh.  
**Нет** подписки на UIA TextChanged/ValueChanged как основного драйвера.  
**Нет** явного разделения «фокус» vs «печать» — первый фокус с текстом трактуется как изменение (`_lastText == null`) и **сразу показывает UI**.

Подавление мониторинга: применение исправления, открытая панель suggestions, shutdown, временное окно ~450 ms.

---

## 3. Быстрый и глубокий анализ

| Слой | Компонент | Debounce |
|------|-----------|----------|
| Fast | `RuleBasedAnalyzer` + `SpellTextAnalyzer` | ~180 ms |
| Deep | `HybridTextAnalysisService` → rules/spell + LanguageTool + local AI | settings / ~400–1500 ms |

Поток:

1. Fast публикует `TextSnapshot` с индикатором `FastAnalyzing` → результат.
2. Deep мержится через `WriteLiteIssueMerger` (fast hits не стираются).
3. Stale-guard: generation + identity + `_lastText`.

---

## 4. Геометрия подчёркиваний

1. `InlineErrorOverlayController` фильтрует orthography/punctuation/grammar с **точным** совпадением диапазона.
2. `TextPatternRangeGeometryProvider` → rects через UIA TextPattern.
3. `InlineIssueGeometryBuilder` **не** добавляет issues без rects (нет полной линии на всё поле).
4. `InlineErrorOverlayWindow` рисует волну; hit-area только вокруг линии.

---

## 5. Клики по overlay

- Окно transparent, `WS_EX_NOACTIVATE`, **без** `WS_EX_TRANSPARENT`.
- `WM_NCHITTEST`: hit → `HTCLIENT`, miss → `HTTRANSPARENT` (клик уходит в host-app).
- LButtonDown+Up на том же issue → `IssueClicked` → `CorrectionPopupWindow`.

---

## 6. Точечные исправления

- `TextCorrectionService` / `CorrectionFlowService`: валидация диапазона, original match, apply single/all.
- Запись: `ValuePattern.SetValue` или range replace через adapter.
- Gate: `AsyncOperationGate` против double-click.
- После apply: suppress + `RefreshOnceAfterSuppressionAsync`.

---

## 7. Popup-окна

| Окно | Роль | Placement |
|------|------|-----------|
| `BubbleWindow` | индикатор | `OverlayPlacementService.PlaceIndicator` |
| `SuggestionsWindow` | панель | `PlacePanel` |
| `CorrectionPopupWindow` | меню ошибки | `PlaceCorrectionPopup` + monitor work area |

Кандидаты ограничены (Right/Left/Above/Below). DPI: scale physical → WPF units.

---

## 8. Режим чтения vs ввода

**Фактически отсутствует.**  
`AnalysisIndicatorState.Typing` есть, но `BubbleWindow` перемапливает Typing → IssuesFound.  
UI показывается при любом editable focus с непустым текстом после анализа.

Пароли / read-only / static: policy + tests; OK.

---

## 9. Словарные / лексические источники

| Источник | Путь / класс |
|----------|----------------|
| Seed spell RU/EN | `SeedSpellDictionary` / `LocalSpellChecker` |
| User dictionary | `UserDictionaryService` |
| Morphology | `DictionaryMorphologyService` |
| Rules JSON | `resources/rules/{ru,en}` |
| Ignore rules/words | `WriteLiteIgnoreService` |
| LanguageTool | embedded ThirdParty + `WriteLiteLanguageEngine` |

Отдельного offline-словаря синонимов/переводов/толкований **нет** (этап 5+).

---

## 10. Локальный Qwen

- `QwenModelBackend`: loopback `http://127.0.0.1:8742`, OpenAI-compatible.
- GGUF + llama-server в `models/writelight-qwen`, context ~768, CPU Q4.
- `AiCallRouter` + `PreferQwen` + profile Lite/Standard/Quality.
- Fallback: Lite rules engine; отсутствие Qwen не ломает baseline.
- ИИ-анализ выполняется только локальными Lite/Qwen-компонентами.

---

## 11. Датасеты и обучающие скрипты

```
ai/data/seed/{ru,en}_clean.jsonl
ai/data/processed/{train,validation,test,all}.jsonl
ai/data/chat/...
ai/scripts/{prepare_dataset,train,train_qwen_lora,evaluate,benchmark_qwen_local,...}
ai/configs/{smoke,standard,qwen_smoke,qwen_standard}.yaml
```

Есть smoke-training outputs; полноценный production GEC-quality retrain — не зафиксирован как baseline.

---

## 12. Ограничения текущей модели

- Малый размер (0.5B–0.6B instruct + LoRA smoke).
- Короткий context (768 tokens).
- Риск ложных правок → строгая валидация, protected tokens, diff.
- Не предназначена для «переписать весь текст».
- Hybrid: rules + spell + LT + neural только когда router разрешает.

---

## Точки расширения (без дублирования)

| Задача | Куда |
|--------|------|
| Режим «только при вводе» | новый `UiInteractionStateMachine` + `TextFieldMonitor` + `App.OnSnapshotChanged` |
| Placement | расширить/обернуть `OverlayPlacementService` → `SmartPopupPlacementService` |
| Стили подчёркиваний | `InlineIssuePresentation` / `UnderlineStyleProvider` + theme |
| Лексика | новый слой рядом с `Morphology` / `Spelling` |
| Settings | `WriteLiteAppSettings` + `SettingsPage` |

---

## Наблюдаемые UX-проблемы (исправляются этапами 2–4)

1. Индикатор/overlay появляются при чтении (фокус + существующий текст).
2. Placement простой, мало кандидатов, слабый scoring.
3. Подчёркивания одного цвета, относительно толстая волна.
4. Нет инструкций по отключению системной проверки орфографии.
