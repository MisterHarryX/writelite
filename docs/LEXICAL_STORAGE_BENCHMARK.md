# Lexical storage: JSON vs SQLite

Date: 2026-08-08
Decision: **migrate to SQLite as the primary runtime store; keep JSON as the
build/export format and as a fallback.**

## Why this was measured

The lexical layer previously parsed every JSON pack into a `Dictionary` at
startup. After the Russian pack gained Wiktionary senses (55.0 MB) and an English
pack was added (40.0 MB), the application was parsing ~108 MB of JSON on every
launch. The question was whether that cost is real and whether SQLite removes it
without making lookups slow enough to notice.

## Method

`tools/lexbench` measures the real `OfflineLexicalKnowledgeService`, not a
synthetic harness:

- **Cold**: one process per measurement, three runs; the number reported is
  representative of the run, not an average of a warm loop.
- **Memory**: full `GC.Collect` before and after loading; managed heap via
  `GC.GetTotalMemory(true)`, process working set via `Process.WorkingSet64`,
  both sampled before the lookup sample is drawn.
- **Latency**: 2 000 lemmas sampled from the pack with a fixed seed (20260808),
  200 warm-up calls first, reported as median and p95 in microseconds.
- **Same data both sides**: the "before" column is the same service pointed at a
  directory containing only the JSON packs.

Reproduce:

```bash
dotnet build tools/lexbench -c Release
tools/lexbench/bin/Release/net10.0-windows/lexbench.exe json <lexical-dir>
```

## Results

Machine: Windows 11, .NET 10, workstation GC. 142 853 entries / 675 802 forms.

| Metric | JSON (before) | SQLite (after) | Change |
|---|---:|---:|---|
| Load time | 1 991 – 2 006 ms | **33 – 37 ms** | **57× faster** |
| Managed heap after load | 193.3 MB | **0.1 MB** | −99.9% |
| Process working set | 405.8 – 406.1 MB | **35.0 MB** | **−91%** |
| Lookup, median | 0.5 – 0.6 µs | 152.6 – 157.9 µs | slower |
| Lookup, p95 | 0.9 – 1.0 µs | 273.7 – 289.9 µs | slower |
| Form resolve only, median | — | 25 µs | — |
| Lookups resolved | 2 000 / 2 000 | 2 000 / 2 000 | identical |
| On-disk size | 107.8 MB JSON | 144.9 MB db | +37.1 MB |
| Full-text definition search | not possible | ~1 ms | new capability |

## Reading the trade-off

The migration trades lookup latency for startup time and memory:

- A lookup went from 0.5 µs to about 0.16 ms. That is triggered by a user
  double-clicking a word, and it is roughly a tenth of a single 16 ms display
  frame, so it is below the threshold of perception. Hot words are cached and
  return without touching the database at all.
- Startup dropped by ~1.96 seconds and resident memory by ~371 MB. For an
  application that sits in the background while the user writes, that is the
  cost that was actually being paid, continuously.

Disk grows by 37 MB because the database carries the indexes and the FTS5 index
that make on-demand querying possible; that is the mechanism of the memory saving,
not an overhead separate from it.

## Schema

Built by `ai/scripts/build_lexical_sqlite.py` from the JSON packs.

| Table | Rows | Purpose |
|---|---:|---|
| `entry` | 142 853 | one row per lemma per language |
| `form` | 675 802 | every surface form, normalised for lookup |
| `definition` | 223 179 | senses, each with its `source_id` |
| `example` | 86 548 | usage examples |
| `sense_link` | 240 302 | synonyms and antonyms |
| `definition_fts` | — | FTS5 index over definition text |

Indexes: `form.normalized` (the hot path for every double-click), `form.entry_id`,
`entry.lemma`, `entry.language`, `entry.pos`, and the entry foreign keys on
`definition`, `example` and `sense_link`.

Normalisation matches the JSON loader: case-folded, and additionally ё→е for
Russian, so `ёлка`, `елка` and `Ёлка` resolve to one entry.

## Safety

The database is the preferred path, never the only one:

- `SqliteLexicalStore.TryOpen` returns `null` for a missing, empty or corrupt
  file instead of throwing, and the service falls back to the JSON packs.
- A lookup that fails at the SQL level returns `null` and the JSON index is
  still consulted if it is loaded.
- `OfflineLexicalKnowledgeService.IsDatabaseBacked` reports which path is live.
- Parity is asserted in tests: for a set of lemmas present in both stores, lemma,
  part of speech, and definition / synonym / antonym / inflection counts and
  definition text and `sourceId` must be equal.

See `tests/WriteLite.Tests/Lexical/SqliteLexicalStoreTests.cs`.

## Rebuilding

```bash
python ai/scripts/build_open_lexical_pack.py --wiktionary ai/data/lexical/ru-wiktionary-20260801.jsonl
python ai/scripts/build_en_lexical_pack.py
python ai/scripts/build_lexical_sqlite.py
```

The database is git-ignored: it is derived entirely from the JSON packs, whose
sources and licences are recorded in `resources/lexical/sources.manifest.json`.
