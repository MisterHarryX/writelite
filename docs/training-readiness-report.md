# Training readiness report

Date: 2026-08-13, Phase 5. **Supersedes the 2026-08-12 (Phase 2) assessment**, whose
conclusions are preserved in `docs/language-engine-phase2-report.md` and whose central
caveat — that the local model's contribution was unmeasured because the benchmark hung — no
longer applies. It has since been measured three times, in Phases 3, 4 and 5.

**Recommendation: do not start training.** Two tasks now have evidence that would justify it,
one has evidence against it, and two more have evidence that they are already solved without
a model. The list below is concrete because §38 asked for it to be, but the ordering matters
more than the entries: **the largest remaining defect in the product is candidate
*generation*, and no training task on this list addresses it.**

---

## 1. What changed since the last assessment

The Phase 2 report's headline was that the biggest measured defects were not model problems.
Three phases later that has held up, and the evidence has hardened:

| | Phase 2 | Phase 5 |
|---|---:|---:|
| deterministic F1 | 0.783 | **0.807** |
| deterministic strict correction | 0.578 | **0.588** |
| deterministic final-text correction | — | **0.626** |
| no-change accuracy | 0.891 | **0.963** |
| local model precision | unmeasured | **0.115** (Phase 4, unchanged by routing) |
| local model as a *judge* | not implemented | **0.906** vs 0.962 deterministic |

Five of the largest problems attributed to the model across Phases 3–5 turned out to be
pipeline defects: the character diff (18 % of AI false positives), confidence pinned at
1.000, severity hardcoded to `Error`, a field-type mismatch rejecting valid JSON, and a
scoring convention that understated correction accuracy by ~4 pp. Phase 5 added two more —
the proper-name register penalty firing unconditionally, and the contextual reranker silently
reversing every Latin-target layout recovery.

That base rate is the single most important input to a training decision.

---

## 2. The question §38 asks

> What exact tasks remain that cannot be solved reasonably by rules, dictionaries,
> morphology, ranking, word-aware diff, or layout handling?

Answered by elimination against the Phase 5 measurements.

| Candidate task | Solvable without training? | Evidence |
|---|---|---|
| Sub-word diff fragments | **Yes — done** | `LinguisticDiff`; 18 % of AI FPs structurally eliminated |
| Latin-target layout | **Yes — done** | layout correction 0.800 → 0.943 |
| Proper-name correction | **Yes — done** | 0.733 → 0.867, a one-line register bug |
| Homoglyph repair | **Yes — done** | 0.967 → 1.000 |
| Introductory-word commas | **Yes — done** | closed list; punctuation recall 0.273 → 0.394 |
| Candidate ranking, reachable targets | **Yes — already 0.962** | judge scored 0.906 on the same lists |
| NO_CHANGE classification | **Yes — already 0.994** | judge matched it exactly, at 435 ms/call |
| Punctuation needing clause structure | **No** | 17 of 24 misses; needs parsing or a model |
| Morphology form choice in context | **No** | scorer has no access to the sentence |
| Real-word pairs | **No** | both forms lexicon-valid; nothing generates a candidate |

Three tasks survive. Two are worth training. One is worth solving another way first.

---

## 3. Task 1 — Punctuation decision

The strongest candidate, and the only one where supervision is genuinely free.

| | |
|---|---|
| **Task name** | `punctuation_decision` |
| **Current baseline** | punctuation recall **0.394**, final-text correction **0.273** (after the two Phase 5 rules). 17 of 24 remaining misses need clause structure: fronted subordinate clauses, gerund phrases, subject–predicate dashes, appositives, asyndetic boundaries |
| **Target** | recall ≥ 0.70 at **no cost to no-change accuracy** (currently 0.963). A punctuation model that adds commas to clean text is worse than no model — this is the acceptance criterion, not a stretch goal |
| **Input format** | `{sentence, position}` — a comma-free sentence and a candidate boundary offset. Bounded to one sentence; no document context |
| **Output format** | one of `COMMA` \| `NO_CHANGE`, plus a confidence. Binary, positional, no free text |
| **Training data** | **already exported**: 27,989 examples (14,010 COMMA / 13,979 NO_CHANGE) in `ai/data/training/punctuation_decision.jsonl`, from Wiktionary and chat corpora. Any well-punctuated Russian text labels itself, so this scales to millions without annotation. Negatives are capped per sentence at the positive count — uncapped, the task is 95 % NO_CHANGE and a classifier learns to answer NO_CHANGE unconditionally |
| **Recommended model type** | **Encoder classifier**, not generative. Extend the existing 29 M `writelight-reranker` with a second head, or train a small BERT-class encoder. A generative model is the wrong shape: the answer is one bit and the position is given |
| **Estimated training cost** | 2–6 GPU-hours on a single consumer GPU for a 29–110 M encoder over ~30 k examples with the existing tokenizer. Under $20 rented, or one evening locally |
| **Runtime requirements** | ONNX, CPU, one forward pass per candidate boundary. Comparable to the existing reranker (17 ms for a four-candidate sentence), which already runs behind the analysis debounce. **Must not run per keystroke** — it belongs on the orchestrated pass |

**Why not rules, given two rules just worked?** Because the two that worked were the two
closed-list clusters. The remaining 17 need to know where a clause ends, whether a token is a
gerund with dependents, and whether `и` joins predicates or clauses. Russian comma placement
is unusually rule-describable *in the aggregate* and that is exactly why the tractable part
was already taken.

---

## 4. Task 2 — Morphology form selection in context

| | |
|---|---|
| **Task name** | `morphology_form_selection` |
| **Current baseline** | morphology recall **0.319**, strict correction **0.021**, final-text **0.128**. 8 of the 19 remaining wrong replacements are this: `хвостм` ranks `хвоста` over `хвостом`, `страницв` ranks `страницы` over `страница`, `времч` ranks `время` over `времени` |
| **Target** | correction accuracy ≥ 0.50 on the morphology category, with no regression in `typo_simple` (0.900) or `typo_keyboard` (0.886), which share the same ranker |
| **Input format** | `{sentence, target_span, candidates[]}` — the candidate list is the product's own generator output, so the label space is closed and matches deployment |
| **Output format** | index into the candidate list, or `NO_CHANGE`. A ranking, not a generation |
| **Training data** | **partially exported**: 7,948 examples in `ai/data/training/candidate_ranking.jsonl`. **4,453 of them (56.0 %) have the correct answer absent from the candidate list** and are flagged `candidate_generation_failure`. They are exported rather than dropped on purpose — a ranker trained only on solvable examples learns that the answer is always present, which is the assumption that breaks in production. Usable supervision is therefore ~3,500 examples, which is **thin**; the form index can generate more synthetically from any inflected corpus |
| **Recommended model type** | Extend the existing 29 M reranker. It already scores candidate acceptability in context and already ships; this is the same task on a class it currently mishandles |
| **Estimated training cost** | 1–4 GPU-hours. The model exists, the tokenizer exists, the inference path exists |
| **Runtime requirements** | Already deployed. No new runtime, no new memory budget |

**Caveat that must be resolved first.** The current reranker is **net-negative on selection**:
0.938 against 0.962 for pure lexical ranking on identical candidate lists, and it demoted
`конфиг` below `конфет`. Phase 5 fixed its cross-script behaviour; its same-script authority
has not been re-examined. **Retraining a component that currently makes things worse, without
first understanding why, would be the expensive way to find out.**

---

## 5. Task 3 — Real-word errors: not yet a training task

| | |
|---|---|
| **Task name** | `real_word_disambiguation` |
| **Current baseline** | recall **0.075**, correction **0.025**. 80 corpus items; 79 of them have no candidate generated at all |
| **Why it is not on the training list yet** | The bottleneck is generation, not decision. Both forms of `учится`/`учиться`, `компания`/`кампания`, `будет`/`будит` are lexicon-valid, so the spelling layer produces no candidates and there is nothing for any ranker or judge to rank. The existing confusion-set path (405 sets) is the mechanism, and it is a **data** asset, not a model |
| **Do this first** | Expand the confusion sets from the error corpus and from Wiktionary homophone/paronym data — the same pipeline that built the 405 sets. Then re-measure. If recall is still low *with candidates available*, the residue is a decision problem and belongs on the training list |
| **Target for the deterministic attempt** | candidate availability ≥ 0.60 on the `real_word` category, measurable directly with `tools/judgebench` |

---

## 6. Explicitly not training candidates

| Task | Why not |
|---|---|
| **Candidate ranking (general)** | Deterministic ranking scores 0.962 on reachable targets. The local model scored 0.906 on identical lists at 435 ms/call. There is no headroom to buy |
| **NO_CHANGE classification** | Deterministic 0.994. The judge matched it exactly and cost 435 ms to do so. The deterministic layer gets this right by *not offering candidates* for correct words, which is free |
| **Generic generative correction** | Precision 0.115 against 0.958–0.972 for every deterministic layer, unchanged by routing at any threshold. Phase 4's conclusion, re-confirmed in Phase 5 with the diff defect removed |
| **Sub-word diff repair** | Was never a model problem. Fixed in code |
| **Layout recovery** | Fixed in code, 0.800 → 0.943 |

---

## 7. Preconditions before any training run

1. **The reranker's same-script authority must be explained.** It is currently net-negative on
   selection accuracy. Training Task 2 on top of an unexplained regression compounds it.
2. **Candidate generation must be widened, and re-measured.** At 35.1 % of single-token
   targets unreachable, a ranker trained to perfection would still leave a third of them
   untouched. Cheapest first: escalate the distance-2 sweep when distance-1 candidates score
   poorly (currently it escalates only when distance 1 returns *nothing*), and expand the
   confusion sets.
3. **Golden data must stay out.** `tools/trainexport` guards against 841 ids and 1,283 content
   fingerprints; `GoldenDatasetIntegrityTests` verifies the written artifacts independently.
   Both must stay green. Current status: **0 blocked, 0 leaked**.
4. **A held-out evaluation split that is not the golden set.** The golden corpus is the
   acceptance gate; it cannot also be the development signal, or Phase 6 will tune against it
   without meaning to.

---

## 8. Data readiness summary

| dataset | examples | status |
|---|---:|---|
| `punctuation_decision.jsonl` | 27,989 | **ready** — balanced 14,010 / 13,979 |
| `candidate_ranking.jsonl` | 7,948 | **partial** — 3,495 solvable, 4,453 flagged unreachable |
| real-word confusion sets | 405 sets | **insufficient** — expand before training |
| golden benchmark | 841 items | **evaluation only, guarded** |

No training has been performed, started, or configured. The export exists so that the data
question is answered with numbers rather than estimates when Phase 6 makes the decision.
