# Local AI routing

Date: 2026-08-13. Machine: Windows 11 Pro 26200, .NET SDK 10.0.301, CPU-only inference.

Phase 4. Follows `docs/language-engine-phase3-report.md`, which established that the local
generative model has real, category-specific value and was being consulted far too broadly
to realise it.

**No training was performed in this phase.**

---

## 1. The problem this solves

Phase 3 measured the local model across the full frozen corpus and found two facts that are
true simultaneously:

| | no Qwen | unrestricted Qwen |
|---|---:|---:|
| **morphology recall** | 0.319 | **0.511** |
| **punctuation recall** | 0.273 | **0.394** |
| detection precision | 0.945 | 0.713 |
| detection F1 | 0.802 | 0.719 |
| no-change accuracy | 0.963 | 0.865 |
| false positives /100 tokens | 0.348 | 1.841 |
| **correction accuracy** | **0.565** | **0.565** |
| p50 latency | 133 ms | 1116 ms |

The gains and the losses land on **disjoint category sets**. The model adds recall exactly
where the deterministic stack is weakest, and subtracts precision exactly where it is
strongest — F1 fell on typo_simple, typo_keyboard, homoglyph, technical and proper_name
with recall unchanged in every one of them, which is the signature of added noise rather
than added capability.

That disjointness is the entire opportunity. If the model is only asked where it helps,
most of the gain should survive and most of the cost should not.

---

## 2. Architecture

```
text
 │
 ├─ deterministic pipeline (rules → spell → LanguageTool → reranker → lexicon)
 │        │
 │        ▼
 │   deterministic findings
 │        │
 ├────────┴─► ILocalAiRoutingPolicy.ShouldConsult(text, findings)
 │                 │ no  → done, zero inference cost
 │                 │ yes
 │                 ▼
 │            Qwen consultation (serialised, cancellation-safe)
 │                 │
 │                 ▼
 │            for each candidate:
 │              ILocalAiRoutingPolicy.ShouldAccept(text, candidate, findings)
 │                 ├─ semantic edit guard
 │                 ├─ deterministic authority
 │                 ├─ register / user-dictionary protection
 │                 ├─ support classification (A/B/C/D)
 │                 └─ category-aware confidence floor
 │                 ▼
 └────────────► merge → filter → UI
```

### One structural change was required

`HybridTextAnalysisService` previously ran the deterministic pipeline and the model **in
parallel** and merged both. Routing is impossible in that shape: the decision to consult is
a function of what the deterministic stack found, so the model has to run after it.

The cost of sequencing is that a routed sentence pays `local + AI` instead of
`max(local, AI)`. The benefit is that an unrouted sentence pays nothing at all, and §6 shows
which dominates.

### Components

| Type | Responsibility |
|---|---|
| `ILocalAiRoutingPolicy` | The two decisions, as an interface so alternatives are testable |
| `LocalAiRoutingPolicy` | The measured policy below |
| `LocalAiRoutingOptions` | Every threshold, in one place, tunable and benchmarked |
| `AiSupportClass` | How much independent evidence backs a finding |
| `SemanticEditGuard` | Rejects edits that change what a span asserts |
| `AiRoutingMetrics` | §18 counters |
| `AiRoutingTrace` / `AiFindingTrace` | §17 per-decision explanation, no user text |

The policy is deterministic and side-effect free, so all of it is testable without a model
running. That is deliberate: the Phase 3 defects were only visible end-to-end, and this
phase's rules needed to be assertable directly.

---

## 3. Routing policy — when the model is consulted

Evaluated in order; the first match wins and returns a machine-stable reason.

| Reason | Decision | Why |
|---|---|---|
| `too-short` | skip | Below 12 characters there is no context to reason about |
| `protected-or-code` | skip | URLs, paths, JSON and code are not Russian prose |
| `no-letters` | skip | Nothing to correct |
| `strong-deterministic-only` | **skip** | Everything found is a high-confidence orthography fix. The DAFSA, keyboard-layout map and homoglyph normaliser score 0.97–1.00 recall here; the model measurably only adds noise |
| `unresolved-contextual-finding` | **consult** | A grammar or punctuation finding with no replacement, or low confidence in the one it has. LanguageTool sees a third of morphology errors and corrects 2 % of them — this is precisely the gap |
| `contextual-risk-markers` | **consult** | Nothing found, but the sentence has the shape that hides a comma or agreement problem. Without this rule the model could only second-guess existing findings, and its measured gains were in *detection* |
| `no-contextual-uncertainty` | skip | Default. Clean prose with nothing suspicious |

The default is **skip**. Consultation must be argued for.

---

## 4. Acceptance policy — when the model is believed

Calling the model and believing it are separate decisions, and Phase 3 conflated them.

### Support classes

| Class | Meaning | Threshold |
|---|---|---:|
| A `ConfirmsDeterministic` | Same span, same fix as a deterministic layer | 0.30 |
| B `RerankExisting` | Same span, different choice among existing candidates | 0.45 |
| C `RuleSupported` | New span, but the lexicon corroborates (replacement is a known word, original is not) | 0.60 |
| D `Unsupported` | New span, nothing behind it | 0.90 |

Class D is the highest-risk class and gets the strictest bar. An unsupported edit outside
grammar and punctuation is refused regardless of confidence, because those are the only two
categories where the model has demonstrated competence.

### Gates that run before any confidence arithmetic

1. **Semantic edit guard.** Any change to digits, negation, Latin identifiers, percentages
   or currency is refused outright, on every support class.
2. **Deterministic authority (§10).** A span already answered with high confidence by the
   deterministic stack cannot be overwritten by a contradictory model proposal.
3. **Register protection.** Obscenity is never sanitised. Slang, names, abbreviations and
   borrowings are not rewritten by unsupported findings. A user-dictionary word is immune.

The ordering matters: a model can always claim confidence 0.99, so a confidence floor alone
cannot separate a spelling fix from a fact change. Meaning is checked first.

---

## 5. Two defects the adversarial tests found in this policy

Recorded because both were mine, and both would have shipped.

**The first policy had no semantic guard at all.** An unsupported grammar edit claiming 0.99
cleared the confidence floor, so `15%` → `50%` and `WriteLite` → `Райтлайт` were accepted.
A `SemanticEditGuard` existed, but only inside the benchmark tool — it had never been part
of the product. It now runs on every candidate, before thresholds.

**The guard then rejected the most common correction in the corpus.** `небыл` → `не был`
scored as `changes-negation`, because the joined form contains no standalone `не` token
while the corrected form does. Counting *word-initial* `не`/`ни` instead makes joining and
splitting equivalent. It over-counts on unrelated words like «небо», but both sides of the
same edit over-count identically, so only a genuine appearance or disappearance of negation
registers.

Without the adversarial tests, the router would have silently blocked the single
highest-frequency Russian fix in the product.

---

## 6. Benchmark — every configuration

Full 841-item frozen corpus, production pipeline, 0 timeouts in every run.

| Configuration | P | R | F1 | Corr | FP/100 | No-change | Morph R | Punct R | p50 | AI calls |
|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| **A no Qwen** | **0.945** | 0.696 | **0.802** | 0.565 | **0.348** | **0.963** | 0.319 | 0.273 | 141 ms | 0 % |
| B unrestricted Qwen | 0.713 | **0.724** | 0.719 | 0.565 | 1.841 | 0.865 | **0.511** | **0.394** | 1229 ms | 100 % |
| C selective routing | 0.888 | 0.700 | 0.783 | 0.565 | 0.896 | 0.920 | 0.362 | 0.273 | **133 ms** | 13.2 % |
| D + confidence floor | 0.887 | 0.698 | 0.781 | 0.565 | 0.896 | 0.920 | 0.340 | 0.273 | 131 ms | 13.2 % |
| E + strict acceptance | 0.940 | 0.696 | 0.800 | 0.565 | 0.348 | 0.963 | 0.319 | 0.273 | 137 ms | 13.2 % |
| D2 + working confidence | 0.940 | 0.696 | 0.800 | 0.565 | 0.348 | 0.963 | 0.319 | 0.273 | 136 ms | 13.2 % |

### What routing achieved

Measured against unrestricted consultation, which was the Phase 3 shipping behaviour:

| | B unrestricted | C routing | change |
|---|---:|---:|---:|
| AI calls | 100 % | **13.2 %** | −87 pp |
| p50 latency | 1229 ms | **133 ms** | **−89 %** |
| detection F1 | 0.719 | 0.783 | +0.064 |
| precision | 0.713 | 0.888 | +0.175 |
| false positives /100 | 1.841 | 0.896 | −51 % |
| no-change accuracy | 0.865 | 0.920 | +0.055 |

That is a large, real recovery, and latency returns to the no-Qwen experience.

### What routing did not achieve

**Config A still dominates every routed configuration.** F1 0.802 against 0.783, precision
0.945 against 0.888, false positives 0.348 against 0.896 — at identical correction accuracy.

And the recall the model was kept for did not survive selection: morphology 0.511 → 0.362,
punctuation 0.394 → **0.273, exactly the baseline**. Routing captured under a quarter of the
morphology gain and none of the punctuation gain.

The strictest configurations converge onto the baseline outright. E and D2 both land at
F1 0.800, FP 0.348, no-change 0.963 — the no-Qwen numbers — while still paying 13.2 % of
the inference cost for three accepted findings across 841 sentences.

---

## 7. Why selective routing could not work

The measurement that settles it:

| | AI findings | true+ | false+ | **precision** |
|---|---:|---:|---:|---:|
| unrestricted (100 % of sentences) | 139 | 16 | 123 | **0.115** |
| routed (13.2 % of sentences) | 26 | 3 | 23 | **0.115** |

**Cutting volume 5.3× left the error rate exactly unchanged.** Routing selected fewer
sentences, not better ones — the model's finding quality is uniform at roughly 89 % wrong
regardless of which sentences it is asked about. No rule that selects *sentences* can fix a
defect distributed evenly *across* sentences.

Against the other layers on the same run — spell 0.958, LanguageTool 0.972, reranker 0.714
— the model is an order of magnitude less precise than anything else in the product.

### The acceptance policy works; there is just nothing worth accepting

Once the confidence signal was real (D2), the floor rejected 32 of 35 findings and the
configuration collapsed to the baseline. That is the acceptance policy behaving exactly as
designed: it is a filter, and almost everything offered fails it. A filter cannot create
value that is not present in its input.

---

## 8. Routing metrics (§18)

From config C, 842 sentences:

| reason | sentences | share |
|---|---:|---:|
| `no-contextual-uncertainty` (skip) | 425 | 50.5 % |
| `strong-deterministic-only` (skip) | 306 | 36.3 % |
| `contextual-risk-markers` (consult) | 101 | 12.0 % |
| `unresolved-contextual-finding` (consult) | 10 | 1.2 % |

| | C | D | D2 / E |
|---|---:|---:|---:|
| AI calls per sentence | 0.132 | 0.132 | 0.132 |
| findings offered | 35 | 35 | 35 |
| findings accepted | 31 (88.6 %) | 27 (77.1 %) | 3 (8.6 %) |

The two skip rules together account for 87 % of sentences, and `strong-deterministic-only`
alone spares 306 — those are the typo, layout and homoglyph sentences where the model
measurably only added noise.

---

## 9. Latency

| configuration | p50 | p95 |
|---|---:|---:|
| no Qwen | 141 ms | 171 ms |
| unrestricted Qwen | 1229 ms | 1371 ms |
| **routed** | **133 ms** | 1285 ms |

Routing restores the median to the no-Qwen experience: 87 % of sentences never touch the
model. The p95 remains high because the 13 % that do route pay full inference cost, which
is the correct shape — the tail belongs to the sentences that genuinely asked for it.

Sequencing local before AI costs nothing at the median for this reason, and the p50 is in
fact marginally *better* than the no-Qwen run (133 vs 141 ms) — within run-to-run noise.

---

## 10. Remaining weaknesses

1. **The model's precision, 0.115.** Unaddressable by routing; see
   `docs/ai-false-positive-analysis.md` §5. 61 % of its false positives — wrong comma
   placement and plausible-but-wrong word-level fixes — have no available suppression rule,
   because judging them is the task itself.
2. **Sub-word diff fragments, 18 % of AI false positives.** Ours, not the model's. Every AI
   finding arrives through a character diff that is not word-aware. Fixable by snapping
   hunks to word boundaries; not attempted here because it changes the shape of every AI
   finding and would invalidate the comparisons this report rests on.
3. **Correction accuracy is stuck at 0.565 in every configuration**, and ~3 pp of that is a
   scoring convention rather than a product limit (15 of 34 span mismatches already produce
   the expected sentence). The genuine remaining loss is 30 wrong replacements, all from
   spell-layer candidate ranking.
4. **No candidate-judge mode was implemented.** §8/§29 propose giving the model a bounded
   candidate list and asking it to select rather than generate. It is a new inference path,
   not a routing change, and it was not built. Its plausibility is weakened by the D2/E
   result — when the acceptance policy demanded corroboration, only 3 of 35 findings
   qualified, which suggests the model rarely agrees with the deterministic candidate set in
   the first place. That is a hypothesis, not a measurement, and it is stated as one.

---

## 11. Recommendation

**Do not ship Qwen in the analysis path in any configuration measured here.**

The honest summary is that routing is good engineering applied to a component that does not
currently earn its place: it recovered most of the cost (latency −89 %, false positives
−51 %) but could not make the model additive, because the limit is output quality and not
consultation volume.

The routing layer itself should be kept. It is measured, tested and observable, and it is
exactly the machinery needed to re-evaluate the model cheaply after the pipeline defects in
§10.2 are fixed — at which point the question deserves to be asked again with new numbers.

`LocalAiEnabled` still defaults to true. Changing it is a product decision with UX
consequences and it has not been changed here; the evidence for changing it is in §6.
