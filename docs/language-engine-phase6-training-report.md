# Phase 6 — training the specialised models

Date: 2026-08-14. Corpus: `ru_frozen_v1` (841 items, sha256 `16adfd7c…`), unchanged and frozen.

Phase 5 ended with a training-readiness report that recommended one task confidently
(`punctuation_decision`), one conditionally (`morphology_form_selection`, blocked on
explaining why the existing reranker is net-negative), and one not at all
(`real_word_disambiguation`, blocked on data). This phase acts on that list.

Two of the three answers changed once the evidence was gathered, and both changes came from
auditing something that had been taken on trust: the exported punctuation dataset, and the
reranker's failure mode. Neither was visible from reading the code.

---

## 1. Phase 5 baseline reproduction

Clean build from a purged tree (`dotnet clean` then `dotnet build`), full suite excluding
`TestCategory=UIA`, then the Phase 5 deterministic configuration re-run against the same
corpus.

| | |
|---|---|
| build errors | 0 |
| warnings | 130 (analyzer style only, test project: MSTEST0037/0044/0032, CS0108, CS0414) |
| passed | **1028** |
| failed | **0** |
| skipped | 3 (live-server AI tests, self-skipping without `llama-server`) |

### The one failure, and what it was

The first run failed `Provider_Disabled_DoesNotCallQwen`, which passed in isolation.
`LocalAiDiagnostics.Log` is a process-wide static sink, the suite runs at
`ExecutionScope.MethodLevel`, and four tests across two classes assign it. The test asserts
that no `qwen-request-started` event was logged — and observes whatever a concurrently
running test logged into *its* list. `Provider_UnreachableServer_UsesFallback`, in the same
class, deliberately attempts a Qwen request.

A test-isolation defect, not a product regression. Fixed by marking the four sink-touching
tests `[DoNotParallelize]` rather than the whole class, so the rest of the file still
parallelises. Recorded because the verification step found it, which is what it is for.

### Baseline reproduced

Configuration `rules,spell,lt,rerank,lexveto,lexsignals`, against `P5-6-ranking.json`:

| | Phase 5 | Reproduced (`P6-0`) |
|---|---:|---:|
| Precision | 0.946 | **0.946** |
| Recall | 0.704 | **0.704** |
| F1 | 0.807 | **0.807** |
| Strict correction | 0.588 | **0.588** |
| Final-text correction | 0.626 | **0.626** |
| FP / 100 clean tokens | 0.348 | **0.348** |
| No-change accuracy | 0.963 | **0.963** |

Edit quality unchanged: `subword 0.000, phrase 0.130, meaningful 1.000, overwide 0.000,
zero-length fragments 0`.

### Integrity checks

| check | result |
|---|---|
| corpus sha256 | `16adfd7c763d8c17c696040a90178a8e7e9c83f5d6d09f99eb017538ec91293d` — matches `ru_frozen_v1.meta.json`, byte-identical under LF |
| corpus item count | 841 |
| training export reproducibility | re-ran `trainexport` to a scratch directory; both `.jsonl` artefacts byte-identical to the committed ones |
| golden leakage, independent check | 0 content-fingerprint overlaps, 0 id overlaps, verified by a Python reimplementation of the C# fingerprint rather than by the C# guard checking itself |
| golden guard scope | 841 ids, 1,283 sentence fingerprints |

---

## 2. Golden stays frozen — and the guard moved

The exporter blocked golden content on the way out, and `GoldenDatasetIntegrityTests`
verified the written artefacts. Both guard the pipeline that produced *today's* files. Phase 6
adds a third check at the last possible moment — `ai/scripts/golden_guard.py`, called from
the trainer immediately before the first gradient step, on the file actually being read.

It raises `SystemExit`. There is deliberately no flag to downgrade it to a warning, because a
model that has seen the benchmark cannot be un-trained and there is no way to tell afterwards
which items it saw.

The fingerprint matches the C# implementation exactly: letters and digits only, lowercased,
ё folded to е, everything else collapsed to a single space. Every training run in this phase
reports `0 leaks` over all 30,547 punctuation records before training begins.

---

## 3. The candidate-generation gap

Phase 5 measured 35.1 % of single-token ranking targets as having no correct candidate on the
list. `tools/candcov` was built to measure that directly on any corpus — before and after a
change — rather than as a by-product of comparing selectors.

Coverage alone is a number anyone can raise by offering more candidates, so three counters
travel with it: candidates per target, **offer rate on correct tokens** (the false-candidate
explosion), and per-class coverage.

### The missing set, by class

Classes are derived from the words and the form index, not from corpus labels — the golden
set's `category` is a benchmark-design label and the error corpus's `errors[].type` describes
how a corruption was *made*. Neither answers what relationship holds between the two words.

Golden, baseline (`spell` only): **65.3 % coverage, 152 misses**.

| class | targets | covered | coverage |
|---|---:|---:|---:|
| orthography | 255 | 247 | 0.969 |
| **real_word** | **100** | **0** | **0.000** |
| layout | 34 | 34 | 1.000 |
| case_form | 18 | 0 | 0.000 |
| тся/ться | 12 | 5 | 0.417 |
| verb_form | 10 | 0 | 0.000 |
| agreement | 7 | 0 | 0.000 |
| morphology | 2 | 0 | 0.000 |

The development split (non-golden, 1,177 targets) shows the same shape at 62.6 % coverage:
real_word 230 at 0.000, case_form 97 at 0.000, тся/ться 12 at 0.667.

**Every uncovered class is a valid-word class.** The spelling generator is only ever asked
about a token the index rejects, which is how the pipeline gets its 0.994 NO_CHANGE accuracy
for free, and simultaneously why «учится» can never be offered «учиться».

### One reading corrected before acting on it

A defect in the first version of `candcov` produced 0.000 coverage everywhere. The golden set
and the error corpus use **opposite conventions for the same field name**: golden records an
annotation (`original` broken, `replacement` the fix), the error corpus records a corruption
(`original` the word it started from, `replacement` what it wrote instead). Reading either by
name gives the wrong answer on half the project's data. Resolved by position — whichever
field is not what `source` contains at the span is the answer.

### What was added

**тся/ться, as a closed rule rather than a list.** `RussianMorphologyAlternatives` generates
the reflexive partner by inserting or removing the soft sign, gated on index membership. The
relation is total on this ending, so it covers every reflexive verb in the language.

`ReflexiveVerbFormAnalyzer` gained the direction it never had. It had only ever asked whether
an infinitive was standing where a predicate belongs, via an 18-entry hardcoded map. The
opposite question — «Он хочет учится» — was unreachable. The new rule's entire safety property
is **adjacency**: the governor must be the immediately preceding token with nothing but spaces
between. The old five-token window would rewrite «Он должен, кажется, уйти» into «казаться»,
and that case is a hard negative in the test file.

22 tests, of which 13 are negatives.

**Confusion sets wired into the generation layer.** The 405 sets already drive the shipping
`FindRealWordErrors` path; exposing them through `ContextualAlternatives` makes the coverage
measurement reflect what the product actually has.

A length guard of `word.Length < 3` was removed from that path after measuring: it was
discarding «ни»/«не», «в»/«во», «ей»/«ней» — among the most frequently confused pairs in the
language, and all two letters long. Coverage on the development split moved 71.7 % → 81.7 %
on that one change.

### Coverage before and after

| | golden | development |
|---|---:|---:|
| coverage before (`spell`) | 0.653 | 0.626 |
| coverage after (`spell+morph`) | **0.765** | **0.817** |
| тся/ться coverage | 0.417 → **1.000** | 0.667 → **1.000** |
| real_word coverage | 0.000 → 0.420 | 0.000 → 0.839 |
| mean candidates / target | 1.45 → 1.58 | 1.53 → 1.73 |
| p95 candidates / target | 6 → 6 | 6 → 6 |
| **offer rate on correct tokens** | 0.348 % → 5.957 % | 2.318 % → 6.999 % |

### The widening that was measured and rejected

Reaching `case_form`, `agreement` and `verb_form` requires offering other inflections of the
same lemma — «языками» and «язык» are both correct Russian, and only the sentence separates
them. That was implemented in the harness (never in the product) and measured:

| layer | golden coverage | offer rate on correct tokens | candidates per correct token |
|---|---:|---:|---:|
| `spell` | 0.653 | 0.348 % | 0.012 |
| `spell+morph` | 0.765 | 5.957 % | 0.071 |
| `spell+morph+paradigm` | **0.845** | **78.348 %** | **6.683** |

Paradigm expansion buys 8.0 points of coverage and questions **78 % of every correct word the
user writes**, at 6.7 candidates each — a 560× increase in candidates per correct token over
the baseline. That is the false-candidate explosion, quantified.

It is rejected, and the code stays in `tools/candcov` rather than in
`RussianMorphologyAlternatives`, so the rejection is reproducible. The comment on
`ContextualCorrectionRefiner.RealWordMargin` records an earlier measurement of a similar
widening — "6× the candidates, more false positives, no extra recall" — which is the same
result found again by a different route.

### What the real_word residue actually is

The 405 confusion sets are already consulted by the shipping pipeline, and golden `real_word`
recall is still 0.075. Of the 100 golden real_word misses, **42 have their answer in the sets
already** — the product generates those candidates and the contextual gate rejects them.

That reclassifies most of the real_word gap from a generation problem to a **decision**
problem, and it is the same finding that decides the morphology gate in §5. The remaining 58
are a genuine generation gap whose fix is expanding the sets, which is data work and is
explicitly out of scope for this phase (§18 of the brief).

### Pipeline effect of the accepted changes

| | Phase 5 baseline | + reflexive rule |
|---|---:|---:|
| Precision | 0.946 | 0.946 |
| Recall | 0.704 | **0.706** |
| F1 | 0.807 | **0.809** |
| Strict correction | 0.588 | **0.594** |
| Final-text correction | 0.626 | **0.628** |
| FP / 100 | 0.348 | **0.348** |
| No-change | 0.963 | **0.963** |

Strict correction accuracy gains 0.006 at no false-positive cost.

**This number was measured twice, and the first measurement was wrong.** The first run
reported every metric as identical, and that reading was produced by a stale binary:
`tools/langbench` keeps its own copy of `WriteLite.dll` and its own copy of the rule pack in
`bin/Release/`, and `dotnet run --no-build` had used both from before the change. It surfaced
only when the rule finally did fire and the catalog rejected an undeclared `RuleId`. The
Phase 6 brief opens by saying not to measure against stale binaries; this is what that looks
like when it happens, and the fix was to rebuild every tool rather than only the solution.

The rule is now declared in `resources/rules/ru/grammar.json` with its condition, exceptions,
examples and its own tests, like every other rule in the pack.

Full suite after the candidate-generation changes: **1063 passed, 0 failed, 3 skipped.**

---

## 4. The punctuation dataset audit

Phase 5 exported 27,989 balanced `punctuation_decision` examples and called them ready. The
brief said not to assume the export was perfect. It was not.

### The positional shortcut

Negatives were selected with `.Take(positives.Count)` — the *first* n non-comma boundaries in
the sentence, which in Russian means the first few words.

| | mean relative position | median |
|---|---:|---:|
| COMMA | 0.486 | 0.507 |
| NO_CHANGE | **0.131** | **0.099** |

A classifier reading nothing but that one number, with its threshold swept on train, scores:

```
position-only classifier: threshold=0.270  train 0.8377  validation 0.8368
```

**83.7 % validation accuracy while reading no Russian at all.** Any model trained on this
dataset would have reported a respectable number for having learned "commas come later in
sentences". This is the single most important finding of the audit.

### Comma-free sentences were absent entirely

`positives.Count == 0` ended the builder, so every training sentence contained at least one
comma. Most sentences a user writes contain none — the model would never have been trained on
the question it will mostly be asked.

### No test split

The exporter read `rerank_train` and `rerank_validation` only. The error corpus has always
carried a test split. Without it, the golden set is the only held-out data — and it is the
acceptance gate, so using it to choose a model or a threshold would tune against the thing
that judges the tuning.

### What was fixed, and the re-audit

Negatives are now sampled uniformly without replacement from all eligible boundaries, with a
seed derived from the sentence id so the export still reproduces byte-for-byte. Comma-free
sentences contribute one negative each at a 25 % sample rate. All three splits are read.

| | before | after |
|---|---:|---:|
| examples | 27,989 | 30,547 |
| splits | train, validation | train, validation, **test** |
| COMMA / NO_CHANGE | 14,010 / 13,979 | 15,045 / 15,502 |
| COMMA mean relative position | 0.486 | 0.486 |
| NO_CHANGE mean relative position | **0.131** | **0.491** |
| **position-only classifier (validation)** | **0.8368** | **0.5254** |
| majority-class baseline (validation) | — | 0.5045 |
| comma-free sentences | 0 % | 6.8 % |
| exact duplicate records | 0 | 0 |
| contradictory (sentence, position) pairs | 0 | 0 |
| sentence overlap train ∩ validation | 0 | 0 |
| sentence overlap train ∩ test | — | 0 |
| sentence overlap validation ∩ test | — | 0 |
| golden content leaks | 0 | 0 |

The shortcut is gone: position now carries 2.1 points over the majority class, which is noise.

Per-split counts are written into `manifest.json` so the balance is auditable without
re-reading the data.

---

## 5. The morphology reranker — diagnosis

The brief makes morphology training conditional on explaining why the shipped 29 M reranker is
net-negative on selection (0.938 against 0.962 for pure lexical ranking). `tools/rerankdiag`
was built to answer it, on the non-golden development corpus.

### The disagreements

Development validation split, 111 decisions where both a real candidate list and a reachable
answer exist:

| | |
|---|---:|
| deterministic accuracy | 0.8198 |
| reranker accuracy | 0.6667 |
| both right | 64 |
| both wrong | 10 |
| **reranker broke a correct answer** | **27** |
| reranker fixed a wrong answer | 10 |
| net effect | **−17 decisions** |
| mean margin when it broke one | **0.0218** |

On a larger train sample (763 decisions) the picture is identical: 188 broken against 83
fixed, net −105.

### The mechanism

The model overturns correct lexical answers on differences of one to fifteen thousandths of a
probability:

```
'пренимая' should be 'принимая': lexical said 'принимая', model said 'пронимая'  (margin 0.000)
'сбея'     should be 'себя':     lexical said 'себя',     model said 'смея'      (margin 0.001)
'динёк'    should be 'денёк':    lexical said 'денёк',    model said 'линек'     (margin 0.003)
'чоще'     should be 'чаще':     lexical said 'чаще',     model said 'чище'      (margin 0.005)
```

**Its score differences between two plausible candidates are smaller than its noise floor.**

Truncation is not the cause: 3 of 763 targets fall past the 64-token window. Nor is candidate
ordering — the deterministic list is handed to both selectors identically.

The cause is task mismatch. The reranker was trained as a sentence-level binary classifier —
label 1 "correct Russian", label 0 "one word substituted" — and roughly half its negatives
were **non-words**. That teaches orthographic anomaly detection. Ranking two *well-formed*
candidate sentences needs a calibrated relative preference between two grammatical readings,
which binary corrupted/clean training never asked it to produce.

### The margin sweep, which settles it

"Consult the model only when it wins by at least M, otherwise keep the lexical answer" — which
is exactly what the shipping refiner does at 0.05:

| M | consulted | share | accuracy |
|---:|---:|---:|---:|
| 0.00 | 763 | 100.0 % | 0.6592 |
| 0.01 | 492 | 64.5 % | 0.7471 |
| 0.02 | 365 | 47.8 % | 0.7615 |
| **0.05** | 209 | 27.4 % | 0.7955 |
| 0.10 | 138 | 18.1 % | 0.7942 |
| 0.20 | 84 | 11.0 % | 0.7929 |
| 0.50 | 41 | 5.4 % | 0.7916 |
| ∞ | 0 | 0.0 % | **0.7969** |

Accuracy rises monotonically as the model is consulted less, and **peaks at never consulting
it**. Even at a margin of 0.50 — where it speaks on 5.4 % of decisions and is winning by half
a probability point — it is still behind pure lexical ranking.

`RerankMargin = 0.05` is not a tuned operating point. It is a suppression device, and its
near-parity in the shipping configuration is the guard doing the work rather than the model.

### The second finding, which decides the gate

The class breakdown of those 763 decisions is **733 orthography, 30 тся/ться, and zero
case_form, agreement or verb_form**.

There are almost no morphology *decisions* in the data — because a morphology target is a
valid word, so the generator produces nothing for it, so it is a candidate-generation failure
rather than a decision. The task the brief proposes training does not exist in trainable form.

---

## 6. Morphology go / no-go

> **NO-GO.**

Three independent reasons, in the order they were found:

1. **There is no learnable contextual-selection gap to train.** Zero case/agreement/verb-form
   decisions exist in the candidate-ranking data. Those targets are generation failures, and
   §3 measured that closing them means questioning 78 % of every correct word.
2. **The existing model's failure is not a training-data problem that more training fixes.**
   Its margins between two plausible candidates are at the noise floor, and no acceptance
   threshold makes its opinions worth taking.
3. **The precondition the brief set was not met.** Training a replacement on top of an
   unexplained regression was the thing to avoid; the regression is now explained, and the
   explanation says the task shape is wrong, not that the weights are.

What *would* justify revisiting it, stated so the decision is falsifiable: a candidate list
that contains morphological alternatives without exploding on correct text, and a training
objective that ranks two well-formed sentences rather than separating well-formed from
corrupted. Neither exists today.

### A pipeline finding that came out of the diagnosis

Disabling the reranker entirely, on the frozen golden corpus:

| | rerank on | rerank off |
|---|---:|---:|
| Precision | 0.946 | **0.951** |
| Recall | **0.704** | 0.696 |
| F1 | **0.807** | 0.804 |
| Strict correction | **0.588** | 0.580 |
| Final-text correction | **0.626** | 0.620 |
| FP / 100 | 0.348 | **0.299** |
| No-change | 0.963 | **0.968** |

The reranker is a small net positive on the *pipeline* while being clearly negative on
*selection*, because it does two jobs with one model: candidate reranking, and real-word
detection through `FindRealWordErrors`. The recall it contributes comes from the second.

That separation is the actionable result, and it is a Phase 7 decision rather than a Phase 6
change: **keep the reranker for real-word detection, stop using it for candidate selection.**

---

## 7. Training hardware

| | |
|---|---|
| GPU | NVIDIA RTX 4070, 12 GB VRAM, compute 8.9, driver 580.88 |
| CPU | Intel i5-12400F, 12 threads |
| RAM | 32 GB |
| Disk | 806 GB free |
| Python | 3.14.6 |
| torch | 2.13.0 **+cpu** — no CUDA wheel installed |
| stack | transformers 5.13.1, datasets 5.0.0, accelerate 1.14.0, onnx 1.22.0, onnxruntime 1.28.0 |

**Training ran on CPU, and the GPU was deliberately left unused.** Measured before deciding:
rubert-tiny2 at batch 32, sequence 64, on 12 threads costs **245 ms per step** — 3.3 minutes
per epoch over 26k examples, about 13 minutes for a full run. Actual runs took 21–23 minutes
each with other work on the machine.

Phase 5 estimated 2–6 GPU-hours for this task. That estimate was high by roughly an order of
magnitude for a 29 M encoder on 30k examples. Installing a ~3 GB CUDA build of torch to save
ten minutes per experiment is the kind of waste the brief's §23 warns about, so it was not
done. Three experiments cost about 65 CPU-minutes in total.

---

## 8. Punctuation training

### The three experiments

Each varies one thing, and each had a hypothesis written before it ran.

| | PUNC-A | PUNC-B | PUNC-C |
|---|---|---|---|
| **hypothesis** | segment embeddings mark the boundary exactly, with no new vocabulary | an inline marker keeps left and right context contiguous, where the Russian comma signal lives | down-weighting COMMA moves probability mass so high-precision operation does not require living at 0.995 |
| encoding | segment pair | marker | marker |
| COMMA class weight | 1.0 | 1.0 | **0.35** |
| epochs / lr / batch | 4 / 5e-5 / 32 | same | same |
| training time | 22.5 min | 21.6 min | 21.3 min |

All three: rubert-tiny2, 29.2 M parameters, max length 64, seed 20260814, early stopping on
validation F1 with best-checkpoint retention, CPU.

### Standalone results

| model | val F1 | **test F1** | test specificity | ECE |
|---|---:|---:|---:|---:|
| PUNC-A | 0.8201 | 0.8021 | 0.7816 | 0.0754 |
| **PUNC-B** | **0.8692** | **0.8574** | 0.8773 | **0.0604** |
| PUNC-C | 0.8582 | 0.8556 | **0.8968** | 0.0814 |

Reference points on the same test split: position-only 0.525, majority class 0.505.

**PUNC-A vs PUNC-B — hypothesis supported.** The marker encoding wins by 5.5 points of test
F1. The `[SEP]` between two segments is a stronger break than a comma decision warrants.

**PUNC-C — hypothesis not supported.** Class weighting bought specificity (0.8968) and lost
F1, and at the constrained operating point it changed nothing that matters: recall 0.2612
against PUNC-B's 0.2566 is about five examples on 1,091 positives. Calibration got worse
(ECE 0.0814 against 0.0604). It is not carried forward.

A fourth run was started and killed at 6 % rather than completed: PUNC-C was originally queued
with the *segment* encoding, which PUNC-B had by then shown to be the weaker of the two, so it
would have tested class weighting on the losing arm. It produced no result and is not counted
as an experiment.

### Calibration and the threshold

The argmax threshold of 0.5 is a statement about the training prior. Training is balanced
~50/50 because an uncapped corpus is 95 % NO_CHANGE; deployment is the uncapped distribution,
and it carries an asymmetry the loss never saw — WriteLite runs continuously while somebody
writes, so a comma proposed into correct text costs far more than a comma missed.

`ai/scripts/choose_punctuation_threshold.py` converts a per-boundary false-positive rate into
the product's own units, using the golden corpus's measured `clean` denominator (277 items,
2,010 word tokens, 1,614 comma-free boundaries) — a property of the benchmark, not a result
from it.

PUNC-B on validation:

| threshold | P | R | FP rate | projected added FP/100 |
|---:|---:|---:|---:|---:|
| 0.500 | 0.8798 | 0.8588 | 0.11520 | 9.26 |
| 0.900 | 0.9452 | 0.6792 | 0.03870 | 3.11 |
| 0.980 | 0.9895 | 0.5179 | 0.00540 | 0.43 |
| 0.990 | 0.9935 | 0.4225 | 0.00270 | 0.22 |
| 0.994 | 0.9974 | 0.3474 | 0.00090 | 0.07 |
| **0.996** | **1.0000** | **0.2566** | **0.00000** | **0.00** |

**0.996 was chosen and frozen before the golden set was evaluated.** The zero is an observed
zero on 1,111 validation negatives, not a guarantee: its 95 % upper bound is about 0.0027,
projecting to roughly +0.22 FP/100.

The calibration bins show the model is usable but overconfident in the middle of its range
(around 0.60 confidence its accuracy is ~0.51) and reliable at the top: in [0.95, 1.00),
n = 1,144, mean confidence 0.984, accuracy 0.950. Operating deep in that top bin is what makes
the frozen threshold work, and it is also why the middle of the range is not used for anything.

### Target safety

62 correctly punctuated sentences, 265 boundaries, in the shapes where a comma classifier is
most likely to misbehave. The right answer everywhere is zero.

| category | boundaries | proposed at 0.996 | proposed at a 0.20 probe |
|---|---:|---:|---:|
| short | 18 | 0 | 0 |
| informal | 24 | 0 | 2 |
| technical | 37 | 0 | 7 |
| mixed Russian/Latin | 25 | 0 | 3 |
| abbreviations | 15 | 0 | 2 |
| names | 24 | 0 | 3 |
| quotes | 13 | 0 | 2 |
| enumeration | 21 | 0 | 2 |
| already correct | 37 | 0 | 2 |
| no comma needed | 51 | 0 | 4 |
| **total** | **265** | **0** | 27 |

The 0.20 column is kept because it shows where the model goes wrong first: **technical text
and Latin tokens inside Russian**, at roughly twice the rate of ordinary prose. Those are
common in real use and rare in a Wiktionary-derived corpus. It is the known weakness of this
artefact and the thing to watch in Phase 7.

### Deployed path verification

The probe runs through the ONNX artefact, the C# WordPiece tokenizer and the frozen
threshold — the deployed path, not the training one. Cross-checked against the Python
checkpoint on the same inputs:

| sentence | boundary | Python p(COMMA) | C# ONNX p(COMMA) |
|---|---|---:|---:|
| Ну ладно тогда до встречи. | after «Ну» | 0.9779 | 0.976 |
| Мне кажется ты прав. | after «Мне» | 0.3868 | 0.363 |
| Он пришёл домой. | after «Он» | 0.0045 | below threshold |

Agreement to within int8 quantisation error, which is what confirms the tokenizer, the marker
token and the sequence handling all survived the export.

---

## 9. Full pipeline benchmark

Frozen golden corpus, 841 items, sha256 `16adfd7c…`. Machine otherwise idle.

Configuration **C** of the brief's matrix — deterministic + punctuation + morphology — does
not exist, because morphology received a NO-GO in §6.

| | **A** deterministic | **B** + punctuation model | **D** unrestricted Qwen |
|---|---:|---:|---:|
| Precision | **0.946** | **0.946** | 0.822 |
| Recall | 0.706 | 0.708 | **0.732** |
| F1 | 0.809 | **0.810** | 0.774 |
| Strict correction | 0.594 | 0.594 | **0.612** |
| Final-text correction | 0.628 | 0.630 | **0.652** |
| FP / 100 clean tokens | **0.348** | **0.348** | 1.493 |
| No-change accuracy | **0.963** | **0.963** | 0.877 |
| Punctuation precision | 0.929 | **0.933** | 0.652 |
| Punctuation recall | 0.394 | 0.424 | **0.455** |
| Punctuation F1 | 0.553 | **0.583** | 0.536 |
| Punctuation final-text | 0.273 | 0.303 | **0.364** |
| p50 latency | **155.9 ms** | 166.1 ms | 1435.5 ms |
| p95 latency | **170.3 ms** | 183.7 ms | 1667.0 ms |
| Peak RSS | **216.4 MB** | 264.7 MB | 221.1 MB |
| Edit quality (subword / meaningful) | 0.000 / 1.000 | 0.000 / 1.000 | 0.000 / 1.000 |

D reproduces Phase 5's unrestricted-Qwen configuration to within noise (F1 0.774 against
0.776, FP/100 1.493 identical, no-change 0.877 identical), which is the check that the
reference has not drifted.

### What B actually contributed

**One finding across 841 items, and it was a true positive.** Layer attribution: the rules
source went from 19 predictions with 4 false positives to 20 with 4. Punctuation precision
rose rather than fell.

That number has to be read together with the size of the instrument. **The golden corpus
contains 30 punctuation items.** It was designed to measure a whole pipeline, and it is a weak
measurement of one punctuation layer operating at a threshold that fires on about a quarter of
true commas. The validation and test splits — 2,202 and 2,118 boundaries — are the stronger
evidence for what this model knows, and there it scores 0.857 test F1.

The alternative reading is that the operating point is too conservative, and the sweep says
what loosening it would cost: 0.994 buys validation recall 0.347 for a projected +0.07 FP/100,
0.990 buys 0.423 for +0.22. Both were left on the table. **Choosing between them by looking at
the golden result would be chasing the acceptance gate**, which is the one thing the brief's
§32 forbids; it is a Phase 7 decision, to be made on real usage or on a larger punctuation
evaluation set.

---

## 10. Acceptance decision

Against the brief's §30 criteria, all measured on the frozen corpus after the threshold was
frozen:

| criterion | target | measured | |
|---|---|---:|---|
| F1 | > 0.807 | **0.810** | pass |
| Final-text correction | > 0.626 | **0.630** | pass |
| Punctuation recall | > baseline 0.394 | **0.424** | pass |
| FP / 100 | close to 0.348 | **0.348** | pass, exactly |
| No-change accuracy | close to 0.963 | **0.963** | pass, exactly |
| Consumer-PC suitable | — | +48 MB RSS, +10 ms p50, 29.4 MB on disk, CPU only | pass |

> **`WriteLite-Punctuation-v1` (PUNC-B) is accepted**, behind a feature flag, as a
> suggestion-only layer that never auto-applies and never removes a comma.

The honest qualification, stated because the acceptance is thin: on this corpus the model
earns its place by adding one correct finding and costing nothing measurable. The case for it
rests on the held-out splits, on the safety probe, and on the fact that its cost is genuinely
zero on every metric the product is judged by — not on the golden delta, which is one item.

Model card: `docs/models/writelite-punctuation-v1.md`.

---

## 11. Rejected, and why

| what | why |
|---|---|
| **PUNC-A** (segment encoding) | 5.5 points of test F1 below PUNC-B, and 6 points less recall at the FP budget. Superseded, not defective. |
| **PUNC-C** (COMMA class weight 0.35) | Hypothesis not supported. Bought specificity, lost F1 and calibration, changed constrained recall by about five examples. |
| **Morphology form selection** | NO-GO. No learnable selection task exists in the data; the existing reranker's margins are at its noise floor at every threshold. §5–§6. |
| **Real-word disambiguation** | Not trained. The brief made it conditional on data readiness, and §3 measured that most of the residue is a decision problem inside candidates the product already generates. The 405 confusion sets were not expanded — that is data work with its own licensing review. |
| **Paradigm candidate expansion** | +8.0 points of coverage for questioning 78 % of every correct word. §3. |
| **ё/е alternation as a candidate class** | Deliberately not added. Writing е for ё is standard Russian orthography, not an error; the corpus generator labels it as one because it can. Flagging it would generate false positives across nearly every text. |
| **Generalising the тся/ться `FiniteMap`** | Declined this phase. It would widen an auto-applying rule across the whole language on syntactic gates tuned for 18 verbs, and needs lexicon access the rules layer does not have. Recorded as a candidate, not attempted unmeasured at the end of a phase. |
| **CUDA training environment** | ~3 GB download to save ten minutes per run on a 29 M model. §7. |

No task was retrained more than once after a failed hypothesis. Total serious experiments: 3.

---

## 12. Performance and cost

| measurement | A deterministic | B + punctuation |
|---|---:|---:|
| p50 per sentence | 155.9 ms | 166.1 ms |
| p95 per sentence | 170.3 ms | 183.7 ms |
| p99 per sentence | 185.8 ms | 202.3 ms |
| max per sentence | 398.7 ms | 391.1 ms |
| peak RSS | 216.4 MB | 264.7 MB |
| managed heap | 60.2 MB | 78.5 MB |

| artefact | size |
|---|---:|
| `writelite-punctuation/model.onnx` (int8) | 29.4 MB |
| `writelite-punctuation/vocab.txt` | 1.1 MB |
| cold load | 343–428 ms |
| `writelight-reranker/model.onnx` (existing, unchanged) | 29.4 MB |

The punctuation model runs one batched forward pass per sentence covering every candidate
boundary, on the orchestrated pass. It is never on the per-keystroke path. Sentences over 300
characters are not scored, because their boundaries fall outside the 64-token window.

One test failure was observed and is environmental: `Lookup_IsFastEnoughForThePerTokenPath`
asserts a lexical-signal lookup under 50 µs and measured 84 µs while a training job held the
CPU. On an idle machine the full suite is **1063 passed, 0 failed, 3 skipped**.

---

## 13. Phase 7 handoff

```
Models accepted for production integration:
- WriteLite-Punctuation-v1 (PUNC-B). rubert-tiny2, 29.2M params, int8 ONNX, 29.4 MB,
  CPU-only, +48 MB RSS, +10 ms p50. Frozen acceptance threshold 0.996.
  Suggestion-only: never auto-applies, never removes a comma, yields to the rules layer.
  Card: docs/models/writelite-punctuation-v1.md

Models rejected:
- PUNC-A (segment encoding) - 5.5 pts test F1 below PUNC-B.
- PUNC-C (COMMA class weight 0.35) - hypothesis not supported; worse F1 and calibration
  for no gain at the operating point.
- A new morphology form-selection model - NO-GO. There is no learnable selection task
  in the data, and the existing reranker's failure is task shape, not weights.
- Any generic generative correction model. Not trained, per the brief and per Phase 5.

Keep deterministic:
- Spelling candidate generation and lexical ranking. 0.962 selection accuracy on
  reachable targets; no model in three phases has beaten it.
- All comma rules a closed list can settle (introductory words, adversative
  conjunctions). Zero false positives across 841 items, and they carry an explanation
  the model cannot.
- Tsya/tsya in both directions, including the new adjacency-gated infinitive rule.
- NO_CHANGE. 0.994, achieved for free by not offering candidates for correct words.

Keep generative Qwen only for:
- Smart Actions (Rewrite, Shorten, Expand, Explain, Translate, Formalize) - explicit
  user invocation with a visible result, untouched by this phase.
- As a reference configuration in the benchmark matrix.
- NOT for automatic background correction: 0.822 precision, 1.493 FP/100, no-change
  0.877, p50 1435 ms.

Recommended default automatic-correction architecture:
    rules + spell + LanguageTool + lexical veto + lexical signals
      + WriteLite-Punctuation-v1 @ 0.996, suggestion-only, behind a flag
    AI automatic correction OFF by default (unchanged from Phase 5)

Phase 7 should decide, with evidence this phase deliberately did not gather:
1. The punctuation threshold. 0.996 contributed one finding on a 30-item punctuation
   corpus. 0.994 (+0.07 FP/100) and 0.990 (+0.22 FP/100) are the alternatives, and
   choosing between them by looking at the golden result would be chasing the gate.
   Decide on real usage or on a larger punctuation evaluation set.
2. Splitting the reranker's two jobs. It is net-negative on candidate selection at
   every threshold and net-positive on the pipeline through real-word detection.
   Turning it off for selection while keeping FindRealWordErrors is a measurable
   change this phase did not make.
3. Whether to expand the 405 confusion sets. 58 of 100 golden real_word misses are a
   genuine generation gap; the rest are a decision gap inside candidates that already
   exist. Data work with its own licensing review.
4. Technical and mixed-script text. The safety probe shows this is where the
   punctuation model degrades first. Worth a targeted evaluation set before shipping.
```

Phase 6 did not begin Phase 7 integration work. The punctuation layer exists behind the
`punct` benchmark flag and is not wired into the application's analyzer graph.

---

## 14. Artefacts produced

| path | what |
|---|---|
| `models/writelite-punctuation/` | deployed int8 ONNX, vocab, `punctuation.json` manifest |
| `models/writelite-punctuation/PUNC-{A,B,C}/hf/` | training checkpoints, retained for re-export |
| `experiments/PUNC-{A,B,C}/report.json` | config, dataset hash, guard result, all metrics |
| `experiments/PUNC-{A,B,C}/threshold.json` | full threshold sweep, frozen choice |
| `ai/scripts/train_punctuation.py` | trainer |
| `ai/scripts/choose_punctuation_threshold.py` | threshold selection against the FP budget |
| `ai/scripts/export_punctuation_onnx.py` | int8 export |
| `ai/scripts/golden_guard.py` | fail-fast leakage guard |
| `tools/candcov` | candidate-generation coverage |
| `tools/rerankdiag` | reranker failure diagnosis |
| `tools/punctsafety` | target-safety probe |
| `benchmarks/results/P6-*.json` | every run in this report |
| `docs/models/writelite-punctuation-v1.md` | model card |
