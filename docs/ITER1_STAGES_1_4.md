# Итерация 1 — этапы 1–4

## Базовая линия

- Сборка Release: OK
- Тесты до изменений: 280 passed, 3 skipped
- Тесты после: **307 passed**, 3 skipped

## Этап 1 — Аудит

Файл: `docs/AUDIT_STAGE1.md`

Зафиксированы: UIA-поток, fast/deep анализ, overlay, popup, словари, Qwen, датасеты, точки расширения.

## Этап 2 — UI только при вводе

| Компонент | Назначение |
|-----------|------------|
| `UiInteractionStateMachine` | NoTarget → EditableFocused → Typing → IdleEditing → PopupInteraction → Hidden |
| `UserInputActivityTracker` | recency клавиш без логирования текста |
| `TextFieldMonitor` | baseline на фокусе, UI после text delta / apply |
| `App.OnSnapshotChanged` | `ShowMainUi` gate |
| `ShowUiOnlyWhileEditing` | настройка (по умолчанию true) |

Тесты: `UiInteractionStateMachineTests` (10).

## Этап 3 — Smart placement

| Компонент | Назначение |
|-----------|------------|
| `SmartPopupPlacementService` | 8+ кандидатов, scoring, multi-monitor work area |
| `OverlayPlacementService` | façade, совместимость API |

Тесты: `SmartPopupPlacementServiceTests` + прежние SystemLayer placement.

## Этап 4 — Подчёркивания

| Компонент | Назначение |
|-----------|------------|
| `IssueUnderlineStyle` / `IssueUnderlineTheme` | цвета по категориям, thickness, wave, opacity |
| `InlineIssueGeometryBuilder` | отказ от field-wide / NaN геометрии |
| Settings | инструкции по системной орфографии + minimize duplication |
| `WriteLiteTheme.xaml` | спокойнее палитра #111318 / #FF8A3D |

Тесты: `IssueUnderlineStyleTests`.

## Не заявлено

- Глобальное отключение системной проверки орфографии (невозможно безопасно).
- Лексическая карточка, AI benchmark, retrain (этапы 5+).
