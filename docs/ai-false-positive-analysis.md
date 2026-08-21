# AI false positives — forensics

Date: 2026-08-13. Source: `benchmarks/results/P4-B2-unrestricted-qwen.json`, the full
841-item frozen corpus with the local model consulted on every sentence.

Phase 3 reported that consulting Qwen raised false positives 5.3×. That is an aggregate,
and an aggregate cannot be routed around. This is the breakdown.

---

## 1. Headline

| | |
|---|---|
| Total false positives, whole pipeline | **145** |
| Attributable to the local model | **123 (85 %)** |
| AI findings emitted | 139 |
| AI findings that matched a real error | 16 |
| **AI precision** | **0.115** |

For comparison, on the same run:

| layer | predicted | true+ | false+ | precision |
|---|---:|---:|---:|---:|
| languagetool | 36 | 35 | 1 | **0.972** |
| spell | 312 | 299 | 13 | **0.958** |
| reranker | 7 | 5 | 2 | 0.714 |
| rules | 11 | 5 | 6 | 0.455 |
| **ai** | **139** | **16** | **123** | **0.115** |

The model is an order of magnitude less precise than every other layer in the product.

**Routing does not change this.** With selective routing (13.2 % of sentences consulted)
the model emitted 26 findings, 3 true and 23 false — **precision 0.115, identical**.
Selecting fewer sentences did not select better ones.

---

## 2. Clusters

By shape and rule, which is what a routing or pipeline rule can actually target:

| cluster | count | share | cause |
|---|---:|---:|---|
| Punctuation insertions, wrong placement | 43 | 35 % | model quality |
| Grammar replacements, wrong word-level fix | 32 | 26 % | model quality |
| **Grammar insertions, sub-word fragments** | **22** | **18 %** | **our diff, not the model** |
| Orthography replacements, wrong fix | 21 | 17 % | model quality |
| Readability insertions | 4 | 3 % | model quality |
| Punctuation replacement | 1 | 1 % | model quality |

By benchmark category, the same 123:

| category | count | what it means |
|---|---:|---|
| grammar/morphology overcorrection | 32 | invented agreement and case changes |
| edits on already-correct text | 29 | the `clean` category — 277 items that must not change |
| spelling overcorrection | 15 | rewriting words the lexicon already accepted |
| punctuation overcorrection | 13 | commas where none belong |
| rewrites slang | 10 | «имба», «кринж» treated as errors |
| rewrites technical/modern terms | 9 | e.g. `База → Содержимое`, `лежит → хранится` |
| sanitises profanity | 6 | deliberate obscenity "corrected" |
| rewrites a proper name | 3 | e.g. `вечер → ночь` inside a named entity item |
| touches a Latin identifier | 1 | `Скрипт → Script` |

---

## 3. The one defect that is ours, not the model's

**Every single AI finding — all 139 — came through `WL-AI-DIFF-*`,** meaning the character
diff between the original sentence and the model's rewritten sentence. None came from the
model's own structured issue list, because the model returns `"issues": []`.

That diff is not word-aware, and 22 of the false positives (18 %) are the result:

```
''      -> 'ы'
''      -> 'ир'
''      -> 'ер'
'сторон'-> 'нами'
'по'    -> 'работу'
'а'     -> 'ы'
```

These are not corrections. They are sub-word slices of a character diff, presented to the
user as errors. The model may well have produced a sensible rewritten sentence; the
pipeline turned it into fragments.

Two aggravating factors, both now fixed:

1. **Every AI finding carried confidence exactly 1.000.** `TextCorrectionDiffService`
   constructed `TextIssue` without passing `Confidence`, so it took the record's default of
   1.0. All 123 false positives scored a perfect 1.000, which made every acceptance
   threshold below 1.0 a no-op — and is precisely why adding a confidence floor changed
   nothing measurable.
2. **Every AI finding was `IssueSeverity.Error`**, presenting an 11 %-precision source with
   the same authority as a 96 %-precision dictionary hit.

Fixed by giving diff-derived findings a category prior (punctuation 0.88, orthography 0.85,
grammar 0.72, block-level rewrites ×0.8) and a severity derived from it. This is honest
about what it is: **a category prior, not a model confidence.** The local model emits no
per-issue score and nothing in the pipeline can invent one.

---

## 4. Which routing rule suppresses which cluster

| cluster | suppressed by | effect |
|---|---|---|
| edits on already-correct text | `no-contextual-uncertainty` — 425 of 842 sentences never routed | large |
| spelling overcorrection on typos | `strong-deterministic-only` — 306 sentences skipped | large |
| rewrites slang / profanity / terminology | register protection in `ShouldAccept` | targeted |
| rewrites a proper name | register protection (`IsProperName`) | targeted |
| touches a Latin identifier | `SemanticEditGuard` → `changes-latin-identifier` | complete |
| number / percentage / negation changes | `SemanticEditGuard` | complete |
| unsupported edits outside grammar+punctuation | `unsupported-outside-eligible-category` | large |
| **punctuation insertions, wrong placement** | **nothing** | **unresolved** |
| **grammar replacements, wrong word-level fix** | **nothing reliable** | **unresolved** |
| **sub-word diff fragments** | word-boundary snapping (not implemented) | **unresolved** |

Measured effect of all suppression together: AI false positives fell from 123 to 23, and
overall false positives per 100 clean tokens from 1.841 to 0.896.

---

## 5. Unresolved clusters

These are the reason routing did not reach the no-Qwen baseline.

1. **Punctuation insertions (35 %).** The shape is legitimate — a missing comma genuinely is
   an insertion with an empty original span — so no structural rule can reject it. Only the
   placement is wrong, and judging placement is the task itself. This is the largest single
   cluster and it is squarely model quality.
2. **Grammar replacements (26 %).** Word-level, plausible-looking, wrong. Indistinguishable
   from a correct morphology fix without knowing the answer.
3. **Sub-word diff fragments (18 %).** Fixable in our code by snapping diff hunks to word
   boundaries and discarding those that cannot be aligned. Not attempted this phase because
   it changes the shape of every AI finding and would invalidate the comparison this report
   rests on; it belongs with a re-benchmark.

Clusters 1 and 2 together are 61 % of the model's false positives and have no available
suppression rule. That is the arithmetic behind the Phase 4 conclusion: **the limit is
model output quality, not routing.**
