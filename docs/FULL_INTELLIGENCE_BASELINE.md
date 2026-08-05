# Full Intelligence Baseline

Branch: `feature/full-local-language-intelligence`  
Date: 2026-07-19

## Pre-change control

```text
dotnet clean WriteLite.sln -c Release  → OK
dotnet build WriteLite.sln -c Release  → OK
dotnet test  (pre-change line from prior iteration): 328 passed, 3 skipped, 0 failed
```

## Starting commits

```text
b25ca5d feat(ai): add fixed benchmark suite and lexical/AI documentation
8029615 feat(ui): wire lexical popup, double-click observer and safe replace
cb82f5a feat(ui): add double-click word detection and lexical popup
...
```

## Already present before this iteration

- Lexical contracts + offline service + demo pack (~12 lemmas)
- Double-click observer + LexicalPopupWindow + safe replace
- Smart placement, edit-only UI, category underlines
- Fixed benchmark + training scripts
