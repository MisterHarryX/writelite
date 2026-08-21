# Phase 4 — training candidates

Recorded, not started. Phase 4 was explicitly a routing phase; this is the list of things
that looked trainable while routing was measured, so the decision can be made on evidence
later rather than on impulse now.

**Nothing here is a recommendation to train today.** See §7 for what the measurements
actually argue for.

---

## 1. Punctuation placement — the strongest candidate

| | |
|---|---|
| Evidence | 43 of 123 AI false positives (35 %) are comma insertions in the wrong place |
| Deterministic ceiling | punctuation recall 0.273, correction accuracy 0.061 |
| With unrestricted Qwen | recall 0.394 — the model *can* find them |
| Problem | it places them wrongly often enough to cost more precision than it buys |
| Task shape | given a sentence and a candidate comma position, accept or reject |
| Why trainable | binary, positional, abundant supervision from any well-punctuated Russian corpus |
| Why not yet | the same task may be reachable with rules; Russian comma placement is unusually rule-describable, and the existing rule layer has never been extended for it |

## 2. Morphology agreement and case government

| | |
|---|---|
| Evidence | 32 of 123 AI false positives (26 %) are wrong word-level grammar fixes; morphology is also where the model's *gains* were largest (recall 0.319 → 0.511) |
| Deterministic ceiling | morphology correction accuracy 0.021 — LanguageTool sees a third of these and fixes almost none |
| Task shape | given a span and its sentence, choose the correct inflected form from a generated candidate set |
| Why trainable | the form index already provides all inflections of a lemma, so the label space is closed and supervision is derivable |
| Why not yet | this is candidate *ranking*, not generation — it may be better served by extending the existing 29 M reranker than by anything generative |

## 3. Candidate ranking for badly mangled tokens

| | |
|---|---|
| Evidence | 30 detected errors got the wrong replacement, **all from the spelling layer** |
| Examples | `затиели → затихли` (gold `затеяли`), `хвостм → хвост` (gold `хвостом`), `зонтк → зонта` (gold `зонтик`) |
| Task shape | rank the DAFSA's candidate list by sentence fit |
| Why trainable | exactly the existing reranker's task, on a class it currently mishandles |
| Why not yet | this is the Phase 2 real-word retrain candidate in a new guise; it should be measured against a rules/frequency fix first |

## 4. Latin-target keyboard layout (**not** a training candidate)

Recorded here because it looks like a model failure and is not.

`пшерги → перги` where the intended word was `github`; `пщщпду → пощаду` for `google`;
`знерщт → зверят` for `python`. The layout converter maps ЙЦУКЕН→QWERTY and then resolves
the result against the **Russian** lexicon only, so a mistyped English word can never be
recovered. Three of the four wrong-replacement layout cases are this.

**This is a deterministic bug with a deterministic fix** — resolve layout candidates against
the English lexicon too. No training involved.

---

## 5. What is explicitly *not* a training candidate

| Observation | Why training would not fix it |
|---|---|
| Sub-word diff fragments (18 % of AI false positives) | Our character diff is not word-aware. A better-trained model would still be sliced into fragments |
| Every AI finding scoring confidence 1.000 | A default parameter in `TextCorrectionDiffService`, now fixed |
| Every AI finding rendered as `Error` | A hardcoded severity, now derived |
| Model output rejected as `invalid-json` (Phase 3) | A field-type mismatch and a prompt-encoding bug |
| Correction accuracy understated by ~3 pp | An exact-span scoring convention; 15 of 34 span mismatches already produce the expected sentence |

Five of the largest measured problems across Phases 3 and 4 were pipeline defects. That is
the base rate this project should keep in mind before attributing anything to model
capability.

---

## 6. If training is ever authorised, the order

1. **Punctuation acceptance classifier** (§1) — largest cluster, closed task, cheap
   supervision. Only after a rules attempt has been measured and found wanting.
2. **Morphology candidate ranker** (§2) — extend the existing 29 M encoder rather than
   train anything generative.
3. **Mangled-token ranking** (§3) — same model, same training run if the data supports it.

All three are *encoder / ranking* tasks. None of them is a generative fine-tune, and none
of them requires replacing the base model.

---

## 7. What the measurements actually argue for

Ahead of any of the above:

1. **Snap AI diff hunks to word boundaries** and discard unalignable ones — removes 18 % of
   the model's false positives with no model change.
2. **Fix Latin-target layout resolution** — converts several known-wrong corrections.
3. **Extend the punctuation rule layer** before training a punctuation model.
4. **Re-benchmark.** Every one of these changes the numbers a training decision would be
   based on.

The Phase 4 conclusion stands: the local model's precision is 0.115 against 0.958–0.972 for
the deterministic layers, and no routing configuration made it worth its cost. Training is
the expensive answer to that, and it is not yet clear it is the right one.
