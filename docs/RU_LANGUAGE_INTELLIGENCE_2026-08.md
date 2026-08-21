# Russian language intelligence — August 2026

Upgrade of WriteLite's Russian lexicon, candidate generation and contextual
correction. Everything below is measured, not estimated; every number has a
command that reproduces it.

## What changed

| Layer | Before | After |
|---|---|---|
| Russian surface forms | 146 269 Hunspell stems | **3 091 012** indexed forms (+ Hunspell, kept) |
| Form metadata | none | POS, register flags, corpus frequency, lemma, grammatical tag per form |
| Candidate generation | Hunspell `Suggest` | Levenshtein automaton over a minimal DAFSA + multi-signal scorer |
| Wrong keyboard layout | ad-hoc Latin→Cyrillic table | full ЙЦУКЕН/QWERTY mapping, both directions |
| Cyrillic/Latin homoglyphs | tokens skipped entirely | detected and normalised |
| Contextual correction | none | fine-tuned encoder, int8 ONNX, 29 MB |
| Frozen benchmark | 20 items | **841** items across 14 categories |
| Error corpus | 12 000 chat records | **28 321** records, 45.8 % clean, 3 820 hard negatives |

## Data layers and licences

| Source | Contributes | Licence |
|---|---|---|
| OpenCorpora 0.92 (rev. 417150), via `pymorphy3-dicts-ru` | 3 063 288 forms, 181 429 lemmas, grammemes | CC BY-SA 3.0 |
| WriteLite Modern Vocabulary Pack (authored in-repo) | 2 652 lemmas → 27 724 forms: tech, brands, internet/gaming slang, abbreviations, obscenities | CC0-1.0 |
| FrequencyWords (OpenSubtitles 2018, ru) | frequency ranks for 316 152 forms | MIT |
| LibreOffice ru_RU Hunspell | retained as a second opinion | BSD-like |

Full provenance, SHA-256 hashes and redistribution terms:
`resources/lexical/sources.manifest.json`, enforced by `LexicalProvenanceTests`.

## The form index

`resources/lexical/ru-forms.*` — built by `ai/scripts/build_ru_form_index.py`.

A minimal DAFSA holds all 3.09 M forms in **4.7 MB** (141 124 nodes, 392 167
arcs). Word ids are the lexicographic rank, recovered during traversal from
per-arc subtree counts, so the 37 MB metadata array needs no keys and stays
memory-mapped rather than resident.

SymSpell was rejected on arithmetic: at three million forms an edit-distance-2
deletion index needs tens of millions of keys and gigabytes of RAM, against a
35 MB baseline working set. A Levenshtein automaton walking the DAFSA answers
the same query from the same 4.7 MB.

```bash
python ai/scripts/extract_opencorpora_forms.py && python ai/scripts/build_ru_form_index.py && python ai/scripts/verify_ru_form_index.py
```

`verify_ru_form_index.py` is the guard that matters: it enumerates the graph,
checks the enumeration is sorted, that every source form is reachable, that
sampled ids equal their lexicographic rank, and that every reported edit
distance matches an independent Damerau-Levenshtein implementation.

## Candidate scoring

Edit distance alone ranks badly for Russian typing. The scorer multiplies a
distance-derived base by: first-letter agreement (relaxed when the first letter
is itself a known confusion, which is why "зделать" reaches "сделать"), length
ratio, ЙЦУКЕН key adjacency, Russian confusion patterns (unstressed vowel
neutralisation, voiced/voiceless pairs, тся/ться, doubled consonants), corpus
frequency, and register flags that stop an ordinary word being rewritten into a
proper name, an archaism or an obscenity.

Automatic replacement additionally requires a high absolute score **and** a
clear margin over the runner-up (`RussianCorrectionConfidence`). Manual mode
uses the same intelligence at a lower threshold.

## Contextual reranker

`models/writelight-reranker/` — `cointegrated/rubert-tiny2` (MIT, 29 M
parameters) fine-tuned as a binary sentence-acceptability classifier, exported
to ONNX and dynamically quantised to int8.

Chosen over prompting the existing 0.5 B generative model because a binary
encoder cannot rewrite the user's text, and it answers in ~1 ms on CPU.

| | PyTorch fp32 | ONNX int8 (shipped) |
|---|---|---|
| pairwise accuracy (620 held-out pairs) | 0.8839 | **0.8726** |
| — non-word pairs | 0.9490 | 0.9320 |
| — real-word pairs | 0.8252 | **0.8190** |
| accuracy / precision / recall / F1 | 0.726 / 0.682 / 0.847 / 0.755 | 0.725 / 0.680 / 0.852 / 0.756 |
| artefact size | 116.9 MB | **29.4 MB** |
| ms per sentence (CPU) | — | **1.00** |

Training: 14 682 pairs, 3 epochs, batch 32, lr 5e-5, seed 20260809, CPU only
(CUDA wheels could not be downloaded in this environment). Config and metrics:
`experiments/reranker-001/result.json`.

```bash
python ai/scripts/train_ru_reranker.py --experiment reranker-001 --epochs 3
python ai/scripts/evaluate_ru_reranker.py --split test
```

### What the real-word detector does and does not catch

Measured by `ai/scripts/calibrate_realword_margin.py` over the frozen
benchmark, sweeping the gate from 0.05 to 0.95:

- **Catches** real words that are grammatically impossible in place. "Он должен
  **будит** прийти" scores +0.83 for "будет" — far above any sane gate.
- **Does not catch** pairs separated by meaning rather than grammar.
  компания/кампания leans the correct way in both directions, but by ~0.02.
  Deciding what a sentence is *about* needs knowledge a 29 M encoder trained on
  15 k pairs does not have.
- Margins 0.20–0.60 behave identically on this data (3 flagged, 2 correct,
  1 false positive over 277 clean sentences), so the shipped 0.35 sits
  mid-plateau rather than on a cliff.
- Widening candidate generation from the confusion sets to every real word one
  edit away was measured and is **strictly worse**: 6× the candidates, more
  false positives, no extra recall. The gate stays narrow.

## Benchmark

`ai/data/benchmark/ru_frozen_v1.jsonl` — 841 items, sha256
`16adfd7c763d8c17c696040a90178a8e7e9c83f5d6d09f99eb017538ec91293d`. Frozen: fix
by adding a v2, never by editing v1. The corpus builder excludes any sentence
appearing in it.

```bash
dotnet build tools/spellbench/spellbench.csproj -c Release
tools/spellbench/bin/Release/net10.0-windows/spellbench.exe
```

| metric | Hunspell-only | form index | + mixed-script fix |
|---|---|---|---|
| detection precision | 0.787 | 0.954 | **0.958** |
| detection recall | 0.541 | 0.541 | **0.602** |
| detection F1 | 0.641 | 0.691 | **0.739** |
| correction accuracy | 0.362 | 0.473 | **0.529** |
| top-1 accuracy | 0.669 | 0.874 | **0.880** |
| top-3 recall | 0.822 | 0.911 | **0.916** |
| false positives / 100 clean tokens | 0.746 | 0.149 | **0.149** |
| no-change accuracy | 0.845 | 0.977 | **0.977** |
| slang preservation | 0.611 | 0.963 | **0.963** |
| profanity preservation | 0.148 | 0.926 | **0.926** |
| p95 ms / sentence | 59.53 | 2.14 | **1.08** |
| dictionary load ms | 325 | 747 | **390** |
| peak working set MB | 96.1 | 127.3 | 126.3 |

Homoglyph detection went 0.000 → 1.000 F1 with no change in false-positive
rate: `SpellTextAnalyzer.DetectLanguage` used to return "no language" for
tokens mixing Cyrillic and Latin, which silently exempted exactly the words
most likely to be wrong.

Categories still at zero F1 in this harness — `morphology`, `punctuation`,
`real_word` — are outside what a spelling pass can see. `real_word` is served
by the contextual layer, which spellbench does not invoke; its numbers are the
reranker table above.

## Offline and privacy

Runtime reads only local files: the DAFSA, its metadata, the Hunspell pair, the
confusion sets and the ONNX graph. `RussianOfflineAndPerformanceTests` runs the
whole stack and asserts every artefact resolved to a path under the deployment
directory, and that `WriteLite.Language.Russian` references no networking
assembly. Network access was used during development only, to fetch openly
licensed data and the base model.
