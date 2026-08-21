# WriteLite-Punctuation-v1

| | |
|---|---|
| **Name** | `WriteLite-Punctuation-v1` (experiment id `PUNC-B`) |
| **Task** | `punctuation_decision` — given a comma-free Russian sentence and a word boundary in it, does a comma belong there? Output is one probability. |
| **Status** | **Shipping in WriteLite 1.0 RC.** Integrated into the production analyzer graph in Phase 7 as a suggestion-only layer; setting `PunctuationModelEnabled`, default on. |
| **Date** | 2026-08-14 (trained), 2026-08-15 (integrated) |
| **Threshold** | 0.996, frozen in Phase 6 on the validation split and re-confirmed unchanged in Phase 7 against the target-safety probe (0 proposals on 265 boundaries of correct text). |
| **Deployment** | `models/writelite-punctuation/{model.onnx,vocab.txt,punctuation.json}`, packaged file-by-file so the PUNC-A/B/C training checkpoints in the same source directory are not shipped. |
| **Lifecycle** | Loaded lazily on the first orchestrated analysis and retained. 188–428 ms cold load, +48 MB RSS, +7 ms p50. Inference is bounded to one at a time and never runs on the UI thread. |

---

## Architecture

| | |
|---|---|
| Base model | `cointegrated/rubert-tiny2` |
| Base licence | MIT |
| Parameters | 29,194,394 (29.2 M) |
| Layers / hidden | 3 / 312 |
| Head | 2-class sequence classification (`NO_CHANGE`, `COMMA`), newly initialised |
| Max sequence | 64 tokens |
| Vocabulary | 83,829 (base + one added marker token) |

### Input encoding — `marker`

The candidate boundary is marked inline with `¦`, and the whole sentence is encoded as a
single sequence:

```
Он сказал ¦ что придёт
```

This was the subject of the PUNC-A/PUNC-B comparison. The alternative — splitting at the
boundary and encoding a sentence pair so BERT's segment embeddings mark it — scored 5.5
points of test F1 lower. The `[SEP]` between two segments is a stronger break than a comma
decision warrants, and Russian comma placement turns on the words immediately either side of
the boundary, which the marker keeps adjacent.

---

## Training data

| | |
|---|---|
| Path | `ai/data/training/punctuation_decision.jsonl` |
| sha256 | `1620b34daf5681c7…` (full hash in `experiments/PUNC-B/report.json`) |
| Producer | `tools/trainexport`, from `ai/data/errors/rerank_{train,validation,test}.jsonl` |
| Upstream sources | Russian Wiktionary dump (2026-08-01) and the in-repo chat corpus, via `ai/scripts/build_ru_error_corpus.py` |
| Licence | CC BY-SA (Wiktionary text); CC0-1.0 (WriteLite-authored confusion sets and generator) |
| Provenance label | corpus-derived, with rule-generated corruptions; the punctuation task uses **only** the corrected half of each pair |
| Total examples | 30,547 |

| split | examples | COMMA | NO_CHANGE | sentences |
|---|---:|---:|---:|---:|
| train | 26,227 | 12,912 | 13,315 | 6,135 |
| validation | 2,202 | 1,091 | 1,111 | 491 |
| test | 2,118 | 1,042 | 1,076 | 508 |

Zero sentence overlap between any pair of splits. Zero duplicate records. Zero contradictory
`(sentence, position)` pairs.

### Supervision

Any well-punctuated Russian sentence labels itself: a position where the author wrote a comma
is a COMMA example, a word boundary where they did not is a NO_CHANGE example, and the
sentence is stripped of its commas before either is shown to the model.

Negatives are capped at the number of positives per sentence. Uncapped, the task is ~95 %
NO_CHANGE and a classifier learns to answer NO_CHANGE unconditionally. Comma-free sentences
contribute one negative each at a 25 % sample rate, so the model sees the shape production
mostly sees.

### The defect this dataset was rebuilt to remove

The Phase 5 export selected negatives with `.Take(n)` — the *first* n non-comma boundaries,
which in Russian means the opening words. Negatives had a mean relative position of 0.131
against 0.486 for positives, and a classifier reading nothing but that number scored **0.837
on validation**. Negatives are now sampled uniformly, and the same position-only classifier
scores 0.525 against a 0.505 majority-class baseline.

### Golden isolation

The frozen benchmark `ru_frozen_v1` (841 items) is evaluation-only. Three independent guards:
the exporter blocks on the way out, `GoldenDatasetIntegrityTests` verifies the written
artefacts, and `ai/scripts/golden_guard.py` re-checks the file being read immediately before
the first gradient step and raises `SystemExit` on any overlap. This run:
**30,547 records checked against 1,283 content fingerprints and 841 ids — 0 leaks.**

---

## Results

### Validation (used to choose the encoding, hyperparameters and threshold)

At argmax (threshold 0.5):

| | |
|---|---:|
| Precision | 0.8798 |
| Recall | 0.8588 |
| F1 | **0.8692** |
| Specificity | 0.8848 |
| Expected calibration error | 0.0604 |

### Test (held out; touched once, after the encoding was chosen)

| | |
|---|---:|
| Precision | 0.8697 |
| Recall | 0.8455 |
| F1 | **0.8574** |
| Specificity | 0.8773 |

Reference points on the same data: a position-only classifier scores 0.525, and the
majority class scores 0.505.

### Deployed operating point

**Acceptance threshold 0.996**, chosen on validation and frozen before the golden benchmark
was evaluated.

The threshold is not 0.5 because training is balanced ~50/50 while deployment is the uncapped
distribution, and because a comma proposed into correct text costs far more than a comma
missed in a tool that runs continuously while somebody writes. The threshold was selected as
the highest-recall point whose projected contribution stays within +0.05 false positives per
100 clean tokens on top of the deterministic pipeline's 0.348.

| threshold | precision | recall | FP rate | projected added FP/100 |
|---:|---:|---:|---:|---:|
| 0.500 | 0.8798 | 0.8588 | 0.11520 | 9.26 |
| 0.900 | 0.9452 | 0.6792 | 0.03870 | 3.11 |
| 0.980 | 0.9895 | 0.5179 | 0.00540 | 0.43 |
| 0.990 | 0.9935 | 0.4225 | 0.00270 | 0.22 |
| 0.994 | 0.9974 | 0.3474 | 0.00090 | 0.07 |
| **0.996** | **1.0000** | **0.2566** | **0.00000** | **0.00** |

At 0.996 the model made zero false positives on 1,111 validation negatives. That is an
observed zero on a finite sample, not a guarantee: the 95 % upper bound on the true rate is
about 0.0027, which would project to roughly +0.22 FP/100.

### Inside the WriteLite pipeline (frozen golden benchmark, 841 items)

| | deterministic | + this model |
|---|---:|---:|
| Precision | 0.946 | 0.946 |
| Recall | 0.706 | **0.708** |
| F1 | 0.809 | **0.810** |
| Strict correction | 0.594 | 0.594 |
| Final-text correction | 0.628 | **0.630** |
| FP / 100 clean tokens | 0.348 | **0.348** |
| No-change accuracy | 0.963 | **0.963** |
| Punctuation precision | 0.929 | **0.933** |
| Punctuation recall | 0.394 | **0.424** |
| Punctuation F1 | 0.553 | **0.583** |
| Punctuation final-text | 0.273 | **0.303** |

The layer contributed **one finding across 841 items, and it was a true positive.** Read the
size of that number together with the size of the instrument: the golden corpus has 30
punctuation items, so it is a weak measurement of a punctuation layer. The validation and test
splits — 2,202 and 2,118 boundaries — are the stronger evidence for what the model knows.

### Target safety (§12)

62 correctly punctuated sentences, 265 boundaries, across the shapes where a comma classifier
is most likely to misbehave. The right answer everywhere is zero.

| category | boundaries | commas proposed at 0.996 | at a probe threshold of 0.20 |
|---|---:|---:|---:|
| short | 18 | **0** | 0 |
| informal | 24 | **0** | 2 |
| technical | 37 | **0** | 7 |
| mixed Russian/Latin | 25 | **0** | 3 |
| abbreviations | 15 | **0** | 2 |
| names | 24 | **0** | 3 |
| quotes | 13 | **0** | 2 |
| enumeration | 21 | **0** | 2 |
| already correct | 37 | **0** | 2 |
| no comma needed | 51 | **0** | 4 |
| **total** | **265** | **0** | 27 |

At the deployed threshold the model proposes nothing anywhere in this set. The 0.20 column is
kept because it shows *where* it would go wrong first: technical text and Latin tokens inside
Russian, which are common in real use and rare in a Wiktionary-derived corpus. That is the
known weakness of this artefact.

---

## Cost

| | |
|---|---:|
| On-disk artefact | 29.4 MB (`model.onnx`, int8 dynamic quantisation) |
| Vocabulary file | 1.1 MB |
| Cold load | 343–428 ms |
| Peak RSS, full pipeline | 216 MB → 265 MB (**+48 MB**) |
| p50 latency per sentence | 156 ms → 166 ms (**+10 ms**) |
| p95 latency per sentence | 170 ms → 184 ms (**+14 ms**) |
| Inference | CPU, ONNX Runtime, one batched forward pass per sentence covering every candidate boundary |
| GPU required | no |

Measured on an i5-12400F, 12 threads, with the machine otherwise idle. Sentences longer than
300 characters are not scored: their boundaries fall outside the 64-token window, and scoring
a truncated sentence is the defect the reranker diagnosis found.

---

## Runtime contract

* `models/writelite-punctuation/model.onnx`, `vocab.txt`, `punctuation.json`.
* `punctuation.json` carries the version, encoding, `maxLength`, `lowercase` and
  `acceptanceThreshold`. **rubert-tiny2 is cased** — a wrong `lowercase` flag would not throw,
  it would silently resegment every capitalised word away from how training saw it, so the
  flag travels with the artefact.
* Re-tuning the threshold is a re-export, not a rebuild.
* Absent artefacts are a supported configuration: `PunctuationDecisionModel.TryLoad` returns
  null and the pipeline keeps its rule-based punctuation unchanged.

## Behavioural limits

* **Never removes a comma.** Only boundaries without one are scored.
* **Yields to the rules layer.** A boundary already carrying a finding is dropped, not merged.
* **Never auto-applies.** `CanApplyAutomatically` is false for every finding. The
  deterministic comma rules earn that flag by being closed lists with stated exceptions; a
  probability above a threshold is a different kind of claim.

## Reproduction

```bash
python ai/scripts/train_punctuation.py --experiment PUNC-B --encoding marker
python ai/scripts/choose_punctuation_threshold.py --experiment PUNC-B
python ai/scripts/export_punctuation_onnx.py --experiment PUNC-B --encoding marker --threshold 0.996
```

Seed 20260814. Trained on CPU in 21.6 minutes.

## Licence

The artefact inherits **MIT** from `cointegrated/rubert-tiny2`. Training data is CC BY-SA
(Wiktionary-derived) and CC0-1.0 (WriteLite-authored). No unlicensed scraped content was used.
