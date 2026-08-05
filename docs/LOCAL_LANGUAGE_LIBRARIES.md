# Local language libraries

## Internal projects

| Project | Role |
|---------|------|
| `WriteLite.Language.Core` | Contracts + `CorrectionCandidateValidityPolicy` |
| `WriteLite.Language.Russian` | Russian letter/fold helpers |
| `WriteLite.Language.English` | English letter helpers |
| `WriteLite.Language.Lexical` | Boundary note: lexical ≠ spelling |

## App integration

| Component | Role |
|-----------|------|
| `HunspellSpellingLexicon` | Offline Hunspell load/check/suggest |
| `CompositeSpellingLexicon` | Hunspell + seed |
| `LocalSpellChecker` | Production `ISpellChecker` |
| `SpellTextAnalyzer` | Orthography issues only with non-identical strong suggestions |
| `OfflineLexicalKnowledgeService` | Definitions/synonyms only |

## External packages

| Package | Version | License | Use |
|---------|---------|---------|-----|
| WeCantSpell.Hunspell | 5.0.1 | MIT | Hunspell runtime |
| System.Text.Encoding.CodePages | 9.0.0 | MIT | Encoding for AFF SET |

## Boundary rule

```text
Absence from a lexical definition pack is not an orthographic error.
```
