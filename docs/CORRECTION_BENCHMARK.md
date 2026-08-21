# Russian correction benchmark

## The corpus

`ai/data/benchmark/ru_frozen_v1.jsonl` — 841 items, one JSON object per line.

```
{"id","category","source","target","errors":[{"start","end","original","replacement","type"}],"must_not_change":[],"notes"}
```

Offsets are character offsets into `source`; the generator asserts
`source[start:end] == original` for every span, and the runner re-asserts it on load.
Clean-style items have `source == target` and `errors == []`.

Counts: `clean` 277, `real_word` 80, `orthography` 76, `typo_simple` 70,
`morphology` 46, `slang` 46, `typo_keyboard` 35, `layout` 35, `homoglyph` 30,
`punctuation` 30, `proper_name` 30, `modern_term` 30, `technical` 30, `profanity` 26.

497 gold error spans over 5478 tokens. sha256 of the jsonl:
`16adfd7c763d8c17c696040a90178a8e7e9c83f5d6d09f99eb017538ec91293d`.
Frozen 2026-08-09 — **do not modify; regenerate as v2 if changes are required.**
`ai/data/benchmark/_gen/build.py` rebuilds it deterministically from the `_gen/data_*.py` sources.

## Metric definitions

* **Detection** — a prediction counts as a true positive when its character span
  overlaps a gold span; each prediction is consumed by at most one gold error
  (greedy, left to right). Leftover predictions are false positives.
* **Correction accuracy** (`CorrectionAccuracy`, also reported as
  `StrictExactCorrectionAccuracy`) — over *all* gold errors: span matched exactly **and**
  `Replacement` equals the expected string. Unchanged since Phase 2; every figure published
  in Phases 2–4 is this metric and stays directly comparable.
* **Final-text correction accuracy** (`FinalTextCorrectionAccuracy`, added Phase 5) — over
  *all* gold errors: applying the product's edit to the source yields the same sentence as
  applying the gold correction, **regardless of whether the spans agree**. Computed by
  applying each correction to the source separately and comparing whole sentences, so a
  broad span cannot game it — a phrase edit that repairs the error *and* changes a
  neighbouring word produces a different sentence and fails.

  The two measure different things and both are reported. Strict accuracy is the right
  convention for comparing two annotations; final-text is the right one for asking whether
  the user's document ended up correct. A punctuation gold error is annotated two words wide
  (`'кажется что' → 'кажется, что'`) while the product inserts one comma at one offset: the
  text is right, the span is not the annotated one. Measured gap on the Phase 5 deterministic
  pipeline: +3.8 pp overall, +21 pp on punctuation, +11 pp on morphology.

  **Historical results are not restated under the new metric.** Reports written before
  Phase 5 quote strict accuracy only, and that is what they mean.
* **Edit quality** (added Phase 5) — the shape of what is emitted, independent of whether it
  is right: `subword_fragment_rate`, `phrase_level_edit_rate`, `meaningful_edit_rate`,
  `zero_length_nonpunctuation_insertions`, `overwide_edit_rate`. All derived from the issue
  span against the source text rather than from any layer's internal edit type, so every
  layer is measured on the same footing. A pipeline that reaches the correct final text
  through sub-word slices scores identically to one that reaches it through recognisable
  word corrections on precision and recall; these separate them.
* **Top-1 / top-3** — over *detected* errors only, against the analyzer's own
  filtered suggestion list (`CorrectionCandidateValidityPolicy.FilterSuggestions`, max 5).
* **Clean FP** — false positives on the 277 `clean` items only, per 100 tokens
  (`[\p{L}_][\p{L}\p{N}_]*`, the analyzer's own token regex) and per sentence.
* **No-change accuracy** — fraction of `clean + slang + profanity` items (349)
  that come back with zero issues.
* **Preservation** — `must_not_change` tokens not overlapped by any issue.

## Running it

```
dotnet build tools/spellbench/spellbench.csproj -c Release
tools/spellbench/bin/Release/net10.0-windows/spellbench.exe [corpus.jsonl] [out.json]
```

Defaults: `ai/data/benchmark/ru_frozen_v1.jsonl` and
`ai/outputs/benchmark/<timestamp>.json`, resolved against the repo root.
`spellbench` is not in `WriteLite.sln`; build it by path. Single-threaded, offline,
no sampling — same corpus in, same numbers out. To reproduce the baseline, rename
`ru-forms.{wldawg,meta.bin,strings}` in the **build output** `resources/lexical`
directory and run from a working directory that has no `resources/lexical` of its own
(the index loader also probes the CWD). Never touch the repo's `resources/lexical`.

## BEFORE / AFTER — measured 2026-08-09

BEFORE = Hunspell ru_RU + WriteLite seed. AFTER = the same plus the Russian form index.

| metric | before | after |
|---|---|---|
| detection precision | 0.787 | **0.954** |
| detection recall | 0.541 | 0.541 |
| detection F1 | 0.641 | **0.691** |
| correction accuracy (all 497 errors) | 0.362 | **0.473** |
| top-1 accuracy (detected) | 0.669 | **0.874** |
| top-3 recall (detected) | 0.822 | **0.911** |
| clean false positives / 100 tokens | 0.746 (15) | **0.149 (3)** |
| clean sentences with any FP | 13 / 277 | **3 / 277** |
| no-change accuracy (clean+slang+profanity) | 0.845 | **0.977** |
| slang preservation | 0.611 | **0.963** |
| profanity preservation | 0.148 | **0.926** |
| real-word detection | 0.013 | 0.000 |
| total ms (841 items) | 12363 | **1914** |
| ms/sentence p50 | 0.085 | 0.104 |
| ms/sentence p95 | 59.53 | **2.14** |
| dictionary load ms | 325 | 747 |
| peak working set MB | 96.1 | 127.3 |

Per-category correction accuracy (before → after): `typo_simple` 0.63 → 0.90,
`typo_keyboard` 0.54 → 0.89, `technical` 0.67 → 0.97, `modern_term` 0.40 → 0.80,
`orthography` 0.43 → 0.55, `layout` 0.80 → 0.80, `proper_name` 0.80 → 0.60 (regression).

Recall is unchanged overall because the categories the index does not address are
structurally out of a spell checker's reach: `real_word`, `morphology` and
`punctuation` (160 items, 160 gold spans) score 0 recall in both configurations by
construction, and `homoglyph` scores 0 in both because `SpellTextAnalyzer.DetectLanguage`
returns `null` for tokens mixing Cyrillic and Latin, so those tokens are never checked.
Those four categories are the standing to-do list; the index's win is precision,
ranking, tail latency and — decisively — leaving correct slang and обсценная лексика alone.
