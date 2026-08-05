# Full Intelligence Audit

## Wiring verification

| Component | Connected |
|-----------|-----------|
| `OfflineLexicalKnowledgeService` | Yes — `App.OnStartup` → `LoadFromDirectory` |
| `DoubleClickWordObserver` | Yes — low-level mouse hook when checking enabled |
| `LexicalPopupWindow` | Yes — code-built WPF window (not XAML), reusable |
| `LexicalReplacementService` | Yes — synonym apply via `ReplaceTargetTextAsync` |
| Qwen / LanguageTool | Unchanged hybrid path |

## Gaps found (addressed in this iteration)

1. Demo pack ~12 lemmas — expanded core pack (~64) + SQLite index.
2. No morphology/syntax in lookup result — added rule-based morphology + role analyzer.
3. No antonyms / historical / Ozhegov layering — added with legal gates.
4. LexicalPopup lacked morphology/role tabs — added.
5. Ozhegov was not represented — licensed pack importer only (no scrape).
6. Dal not legally importable automatically without PD source — importer script + sample historical layer (not labeled as Dal).

## Double-click notes

- Uses WH_MOUSE_LL + UIA TextPattern selection settle delay.
- Ignores password/read-only via `EditableTextTargetPolicy`.
- Overlay remains selective hit-test only (underlines).

## Residual risks

- Full UIA double-click quality depends on host app TextPattern support.
- Core pack is still limited coverage (honest `isDemo`/status messages).
- SQLitePCLRaw package may surface advisory NU1903 on some versions.
