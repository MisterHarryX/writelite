# Licenses

| Component | License | Commercial | Modify | Ship weights | Decision |
|-----------|---------|------------|--------|--------------|----------|
| google/mt5-small | Apache-2.0 | Yes | Yes | Yes | Primary train base |
| Qwen2.5-0.5B-Instruct | Apache-2.0 | Yes | Yes | Yes | Backup |
| WriteLite synthetic seed | Apache-2.0 | Yes | Yes | n/a | Include |
| LanguageTool 6.4 (existing) | LGPL-2.1 | Yes (LGPL terms) | Yes | n/a | Already bundled |
| WriteLite Lite rules JSON | WriteLite | Yes | Yes | n/a | App-owned |

See also `ai/data/licenses.json` and `src/WriteLite.App/ThirdParty/THIRD_PARTY_NOTICES.txt`.

**Excluded by default:** research-only GEC corpora with unclear commercial terms (e.g. some Lang-8 dumps).
