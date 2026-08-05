# Local language core (offline)

WriteLite performs text correction locally through rules, spelling, lexical data and local models.

## Internal libraries / modules

| Module | Path | Role |
|--------|------|------|
| Rule engine | `src/WriteLite.App/Services/RuleBasedAnalyzer.cs` | Regex grammar/punctuation/orthography |
| Spell | `Services/Spelling/*` | Local dictionaries |
| Grammar helpers | `Services/Grammar/*` | Chtoby, reflexive verbs |
| Lexical | `Services/Lexical/*` | Offline packs, double-click cards |
| AI local | `WriteLite.AI.Local` | Lite rules + optional Qwen local |
| LanguageTool bundle | `ThirdParty/LanguageEngine` | Optional extended checking |

## Rule packs (JSON)

```
resources/rules/ru/spelling.json
resources/rules/ru/grammar.json
resources/rules/ru/punctuation.json
resources/rules/en/*
```

Licenses: WriteLite-authored local rules (see SourceNote fields).

## Lexical packs

```
resources/lexical/writelight-lexical-core.json
resources/lexical/writelight-lexical-demo.json
resources/lexical/sources.manifest.json
```

- Demo/core: hand-authored / CC0 where declared in manifest
- Licensed Ozhegov import path exists but requires local licensed sources (not redistributed)

## External dependencies (runtime)

| Package | Use | License note |
|---------|-----|--------------|
| .NET / WPF | UI | MIT |
| LanguageTool JARs (ThirdParty) | Optional engine | See `ThirdParty/THIRD_PARTY_NOTICES.txt` |
| Optional GGUF Qwen | Local AI | Model license separate; not committed as user secret |

No new NuGet spell engines added in this pass; expansion is internal rules + presentation + apply reliability.
