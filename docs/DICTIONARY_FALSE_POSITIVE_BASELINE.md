# Dictionary false-positive baseline

Branch: `fix/real-dictionaries-no-false-corrections`  
Date: 2026-07-19

## Observed incorrect behaviour

Screenshot / user report:

```text
привет → привет
«Слово не найдено в локальном словаре»
```

This is a critical false positive: a correct common word must never be underlined with an identical candidate.

## Root causes (pre-fix)

1. **Tiny seed dictionary** (~34 RU / ~20 EN stems). Any missing word was treated as unknown.
2. **`SpellTextAnalyzer`** created orthography issues whenever `!IsKnown && Suggestions.Count > 0` without rejecting identical replacements after `PreserveCase`.
3. **Lexical packs** (definitions) were sometimes confused with spelling truth by product messaging; absence from lexical pack must not mean “spelling error”.
4. **No full Hunspell lexicon** wired into the offline analyzer path.

## Required invariants after fix

- `привет` → no issue
- No candidate where `original == replacement` (after NFC / fold policy)
- Unknown without strong suggestion → no red underline (default)
- Full RU/EN Hunspell resources from LibreOffice dictionaries
