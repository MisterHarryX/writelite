# Phase 5 — engineering cleanup before training

Date: 2026-08-13. Corpus: `ru_frozen_v1` (841 items, sha256 `16adfd7c…`), unchanged.

Phase 4 ended with a conclusion and a list of doubts about it. The conclusion — that the
local model's precision is 0.115 against 0.958–0.972 for every deterministic layer, and that
no routing configuration made it worth its cost — rested on measurements taken through a
pipeline with known defects in it. Five of the largest measured problems across Phases 3 and
4 turned out to be pipeline bugs rather than model limitations, which is a base rate worth
remembering before attributing anything to model capability.

This phase removes the defects and re-measures. **No training was performed, started, or
prepared beyond dataset export.**

---

## 1. Baseline verification

Clean build from a purged tree, full suite, then the Phase 4 no-Qwen configuration re-run
against the same corpus.

| | |
|---|---|
| build errors | 0 |
| warnings | 122 (analyzer style only: MSTEST0037, MSTEST0044, MSTEST0032, CS0108, CS0414, CS0219) |
| passed | 885 |
| failed | 0 |
| skipped | 3 |

The 3 skips are live-server AI tests that self-skip without a running `llama-server`.

**Final state at the end of the phase**, after all Phase 5 changes:

| | |
|---|---|
| build errors | 0 |
| warnings | 0 (the analyzer warnings were in the prior build's incremental output; a clean rebuild reports none) |
| passed | **1028** |
| failed | **0** |
| skipped | 3 |

Excluding `TestCategory=UIA`. The one UIA integration test fails on this machine for an
environmental reason established with evidence in §12 — a desktop UI Automation provider
answering in 10 s — and passed five consecutive full runs on identical binaries earlier in
the phase. With UIA included the totals are 1032 passed / 1 failed / 1 skipped.

### The one failure, and what it was

The first run had one failure — `TrayShutdownPath_StopsApplicationWithExitCodeZero` —
reproducible, not flaky. A managed-stack capture of the hung process:

```
MessageBox.ShowCore
EditorPage.OfferRecoveryIfAny()
MainWindow.BindEditorServices(...)
App.BindMainWindowServices()
App.<OnStartup>d__38.MoveNext()
```

`BindEditorServices` runs inside `OnStartup`, and it called `CheckForRecoverableWork()`
synchronously. With any orphaned autosave snapshot on disk, WriteLite parked its dispatcher
on a modal dialog **before the tray, the monitor, or the shutdown path existed** — the
application never reached `application-running`, and the dialog sat behind a window that was
never shown. The code's own comment stated the intent correctly ("offered once the shell is
running"); the call site contradicted it.

Fixed by deferring the offer to the window's first visibility at `ApplicationIdle`
(`MainWindow.BindEditorServices`). Lifecycle smoke then exits in 5.9 s with code 0.

This is unrelated to the language work and is recorded because it was found by the
verification step, which is what the verification step is for.

### Phase 4 baseline reproduced

| | Phase 4 (`P4-A`) | Reproduced (`P5-0`) |
|---|---:|---:|
| Precision | 0.945 | **0.945** |
| Recall | 0.696 | **0.696** |
| F1 | 0.802 | **0.802** |
| Correction accuracy | 0.565 | **0.565** |
| FP / 100 tokens | 0.348 | **0.348** |
| No-change | 0.963 | **0.963** |

Identical to four decimal places. Semantic guard, confidence propagation, severity
derivation, cancellation and transport health all pass in-suite.

---

## 2. Word-aware diff — design

### What was wrong

`TextCorrectionDiffService` trimmed the common **character** prefix and suffix first, and
only then looked for tokens in the remainder. That ordering is the whole defect: after a
character-level trim there are no word boundaries left to find.

```
"Он был там."  →  "Он были там."
prefix = 6 chars ("Он был"), suffix = 0
middle: '' → 'ы'
```

Phase 4 measured 22 of the local model's 123 false positives (18 %) as this shape:
`'' → 'ы'`, `'' → 'ир'`, `'сторон' → 'нами'`. These were not corrections the model got
wrong. They were slices this code made of corrections the model may well have got right.

### What replaced it

`LinguisticDiff` in `WriteLite.Language.Core` — a zero-dependency project, so both AI paths
share one implementation rather than drifting apart. Alignment runs on **significant tokens**
(words and punctuation) from the start, with whitespace held in the gaps between them.

```csharp
readonly record struct TextEdit(
    int Start, int Length, string Original, string Replacement,
    TextEditType EditType, int WordCount);

enum TextEditType {
    WordReplacement, WordInsertion, WordDeletion,
    PunctuationInsertion, PunctuationDeletion, PunctuationReplacement,
    WhitespaceCorrection, PhraseReplacement }
```

Both consumers were rewritten onto it: `TextCorrectionDiffService` (the `WL-AI-DIFF-*` path
that produced 100 % of Phase 4's AI findings) and `TextDiffBuilder` in `WriteLite.AI.Local`
(which reported each rewrite as one take-it-or-leave-it span).

### The four design points that matter

**Word boundaries are structural, not snapped.** A span can only begin and end on a token
edge, because that is the only place alignment produces one. `Length == 0` can therefore only
mean a genuine insertion — the fragment shape is not filtered out, it is unrepresentable.

**Not over-merging falls out of the same choice (§5).** `Я думаю что он придет.` yields
exactly two edits — insert `,` after `думаю`, and `придет → придёт` — because the comma is an
unmatched *token*, not a character offset shift that drags the rest of the sentence with it.
`SplitPairwise` additionally re-separates an equal-length hunk whose words pair up within an
edit-distance budget, so two adjacent independent fixes do not fuse into one.

**Phrase edits are a first-class type, not a fallback (§6).** `не смотря → несмотря` stays
one `PhraseReplacement` precisely because the token pairs are *not* related forms — splitting
it would offer the user two edits that only make sense together.

**Rewrites are refused rather than reported.** If fewer than half the shorter side's tokens
align, `Compute` returns nothing. The old code fell back to a whole-middle block issue, and
that block is how a sentence rewrite reached the user labelled «Орфография». A caller that
wants a block can build one; a caller that receives an unrequested block ships it.

### Tests

21 tests in `LinguisticDiffTests`, including the verbatim `'' → 'ы'` and `'сторон' → 'нами'`
shapes from the Phase 4 forensics as explicit regressions, plus two invariants across a
13-sentence corpus: every edit span contains the text it claims, and applying every edit
reproduces the corrected text exactly.

---

## 3. Diff-quality metrics

Precision and recall cannot see edit shape. A pipeline that produces the correct final text
through a stream of sub-word slices scores identically to one that produces the same text
through recognisable word corrections, and the first is unusable.

`EditQuality` in the benchmark harness derives all five §8 counters from the issue span
against the source text — not from the diff's own edit type. That is deliberate: it measures
what the UI receives, and it measures every layer on the same footing. A spelling suggestion
with a half-word span is exactly as unusable as an AI one.

| metric | definition |
|---|---|
| `subword_fragment_rate` | span begins or ends inside a word, or inserts unspaced letters |
| `phrase_level_edit_rate` | edit spans more than one significant token |
| `meaningful_edit_rate` | not a fragment, and actually changes the text |
| `zero_length_nonpunctuation_insertions` | count of the `'' → 'ы'` shape |
| `overwide_edit_rate` | edit spans more than 6 significant tokens |

**These earned their keep before the model ran.** The first version of the new
`ru.punctuation.adversative-comma` rule spanned the preceding word's last letter
(`"о однако" → "о, однако"`), and `subword_fragment_rate` moved 0.000 → 0.003. That is the
shape §34 rejects. Re-expressed as a zero-length insertion at the word boundary — the same
representation the word-aware diff gives a missing comma — it returned to 0.000.

---

## 4. Latin-target keyboard layout

### Two defects, not one

**The missing direction.** `RussianCandidateGenerator` only ever mapped Latin keystrokes to a
Russian word. `пшерги` was answered with `перги`, `пщщпду` with `пощаду`, `знерщт` with
`зверят`. It now also maps Cyrillic → Latin and resolves against an English lexicon, behind
an `ILayoutTargetLexicon` seam so the Russian layer does not own English data.

**The reranker was undoing it.** Unit tests showed `пщщпду → google` ranking correctly at
0.940 against `пощаду` at 0.333 — and the pipeline still emitted `пощаду`. The contextual
reranker is a *Russian* acceptability model, and it was being handed mixed-script candidate
lists. It prefers the Russian string every time, so it silently reversed every correct
Latin-target recovery downstream of a correct ranking. It is now skipped when the candidates
span two scripts: "which language was meant" is the layout evidence's question, not that
model's.

| | Phase 4 | + layout branch | + reranker fix |
|---|---:|---:|---:|
| layout correction accuracy | 0.800 | 0.886 | **0.943** |
| overall strict | 0.565 | 0.571 | **0.576** |
| overall final-text | 0.596 | 0.602 | **0.606** |
| FP/100 · no-change | 0.348 · 0.963 | unchanged | **unchanged** |

### The English data

79,105 words, ~820 KB, lazily loaded and touched only for a Cyrillic token the Russian index
has already rejected. Derived by `ai/scripts/build_en_layout_lexicon.py` from the shipped
Open English WordNet pack (CC BY 4.0), plus two hand-maintained lists.

Those lists are **separate on purpose**: only the technical vocabulary grants a ranking bonus.
A three-letter function word is exactly the length at which a Cyrillic string collides with
an English one by accident, so function words are membership-only.

### Mixed-language protection (§10)

40 tests including 15 hard negatives: correct Russian words, ordinary Russian typos,
two-letter tokens, and the documented mixed-language cases (`middleware`, `pipeline`,
`GitHub`, `npm`, `build`, `Qwen`) which must survive untouched.

### What still misses

- `dhtvz → время` where gold wants `времени` — a form-choice problem, not a layout one.
- `нуы → ну` where gold wants `yes`. Deterministic ranking scores `ну` 0.752 against `yes`
  0.740. Winning it would mean raising trust in three-letter Latin recoveries generally,
  which is overfitting to one benchmark item.

---

## 5. Punctuation

All 24 missed punctuation gold errors were clustered before any rule was written.

| cluster | n | describable by a closed list? |
|---|---:|---|
| Introductory words | 4 | **yes** |
| Fronted subordinate clause | 3 | no — needs clause-boundary parsing |
| Adversative conjunction | 3 | **partly** — `однако`/`зато` yes, `поэтому` no |
| Extra comma before single `и`/`или` | 3 | no — inverse of the missing case |
| Gerund phrase | 2 | no — needs morphology (POS = Gerund) |
| Dash between subject and predicate | 2 | no — needs morphology |
| Asyndetic clause boundary | 2 | no — needs two-predicate detection |
| Appositive, address, homogeneous series | 5 | no — needs parsing |

Two rules were added, from the two clusters a closed list can settle. The other 17 errors
were left alone rather than guessed at.

```
punctuation recall            0.273 → 0.394
punctuation precision         0.900 → 0.929
punctuation final-text corr.  0.151 → 0.273
overall F1                    0.802 → 0.807
FP / 100 tokens               0.348 → 0.348   (unchanged)
no-change accuracy            0.963 → 0.963   (unchanged)
```

The §14 bar — recall must not cost no-change accuracy — is met exactly. Both new rules
produced **zero false positives** across all 841 items, and punctuation precision rose.

### The exclusions are the substantive part

Each rule carries its linguistic condition, exceptions, confidence and a specific explanation
in `resources/rules/ru/punctuation.json`, with positive and hard-negative tests.

- `Однако` opening a sentence means «но» and takes no comma — absent from the intro list, and
  the adversative rule structurally cannot fire there.
- `Наконец`, `Таким образом`, `Правда`, `Вообще` are adverbial as often as parenthetical.
- **`поэтому` is excluded even though the corpus wants a comma there.** `Она устала поэтому
  легла` needs one; `Именно поэтому он ушёл` does not. Separating them requires knowing
  whether a predicate precedes — parsing, not a list. It is a hard negative in the test file,
  documenting a deliberate miss.

Explanations name the rule rather than the symptom, per §15: «Вводные слова и сочетания …
отделяются запятой», not «Нужна запятая».

---

## 6. Spell candidate ranking — findings

All 25 wrong replacements came from the spelling layer. A probe printing every candidate with
its score, plus whether the expected answer was generated at all, separates ranking failures
from candidate-generation failures.

| cause | n | fixable by ranking? |
|---|---:|---|
| **Proper-name penalty applied unconditionally** | 7 | **yes — a bug** |
| Context ignored: right lemma, wrong inflection | 8 | no — needs the sentence |
| Candidate-generation failure | 6 | no |
| Modern/technical vocabulary has no frequency data | 3 | partly |
| Tie broken by the alphabet | 1 | **yes** |

### The bug

`Rank()` lowercases the word before scoring, so `char.IsUpper(original[0])` in
`RegisterFactor` was **never true**. The ×0.35 "do not rewrite an ordinary word into somebody's
surname" penalty fired on every name-flagged candidate unconditionally.

```
Шолохава → шолохова   scored 0.264, lost to «всполоха»
Гагарен  → гагарин    scored 0.287, lost to «гагаре»
мoре     → море       scored 0.378 (0.94 × 1.15 × 0.35)
```

Fixed by carrying the typed capitalisation separately, and by exempting homoglyph and layout
origins entirely — those are one-to-one character mappings, not guesses at a name.

The tie-break now consults frequency before the alphabet: `двер` scored `две` and `дверь` at
an identical 0.752, and the alphabet chose `две`.

```
proper_name correction   0.733 → 0.867
homoglyph                0.967 → 1.000
orthography              0.649 → 0.662
wrong replacements       25 → 19
```

21 ranking tests, including hard negatives that keep the register guard honest: `мере`,
`роман`, `орёл`, `вера`, `надежда`, `любовь` must not acquire capitalised names.

### The fast path (§19)

Unchanged. Candidate generation is the same code; the English lexicon loads lazily and only
for a Cyrillic token already rejected by the Russian index. Measured below.

---

## 7. Candidate-judge experiment

Implemented as an isolated path (`CandidateJudge` in `WriteLite.AI.Local`, driven by
`tools/judgebench`). Nothing in the shipping pipeline calls it.

All three selectors receive **identical candidate lists**; only the choosing differs.

| selector | selection acc. | NO_CHANGE acc. | model calls | p50 | p95 | contract violations |
|---|---:|---:|---:|---:|---:|---:|
| **deterministic** | **0.962** | 0.994 | 0 | — | — | — |
| reranker | 0.938 | 0.994 | 0 | — | — | — |
| candidate-judge (Qwen) | 0.906 | 0.994 | 290 | 435 ms | 623 ms | **0** |

Two results, and they point opposite ways.

**The strict-output contract works.** 290 calls, zero rejections — no invalid JSON, no
unknown ids, no invented tokens, no hedging between two candidates. `NO_CHANGE` is a real
option with an id like any other, never the absence of an answer, and the model used it. The
design in §21–§24 does what it was meant to do.

**The model still chooses worse than the lexical scorer**, at 435 ms per call against zero.
It does not beat the deterministic baseline, so by §22 it is not enabled.

### The finding underneath all three columns

**35.1 % of single-token targets never have the right answer on the menu** (156 of 444).

| category | generation failures |
|---|---:|
| real_word | 79 |
| morphology | 40 |
| orthography | 24 |
| others | 13 |

Both forms of a real-word pair are lexicon-valid, so the spelling layer never treats them as
errors and generates no candidates at all:

```
'учится'    -> 'учиться'    (offered: nothing)
'извинится' -> 'извиниться' (offered: nothing)
'злиться'   -> 'злится'     (offered: nothing)
```

No judge, no reranker and no amount of model quality can reach a target whose answer is
absent from the list. This gap is larger than the distance between the best and the worst
selector.

**Scope caveat, stated because the number invites over-reading.** `judgebench` measures the
candidate-*ranking* selector in isolation, so its failure count reflects what the spelling
lexicon offers — not what the whole pipeline finds. The тся/ться cases above are largely
caught by the rules layer (`ReflexiveVerbFormAnalyzer`, `ChtobyAnalyzer`), which `judgebench`
does not model: on the full pipeline 9 of the 12 тся/ться gold errors are corrected, and 3
are missed (`сосредоточится`, `злиться`, `встретится`). The 35.1 % is a real ceiling on a
ranking-based approach, and it is *not* a claim that the product misses 35 % of these errors.

---

## 8. Deterministic before / after

Full shipping configuration without the model: `rules,spell,lt,rerank,lexveto,lexsignals`.

| | Phase 4 | Phase 5 | Δ |
|---|---:|---:|---:|
| Precision | 0.9454 | **0.9459** | +0.0005 |
| Recall | 0.6962 | **0.7042** | +0.0080 |
| F1 | 0.8019 | **0.8074** | +0.0055 |
| Strict correction accuracy | 0.5654 | **0.5875** | +0.0221 |
| Final-text correction accuracy | — | **0.6258** | new |
| FP / 100 clean tokens | 0.3483 | **0.3483** | 0 |
| No-change accuracy | 0.9628 | **0.9628** | 0 |
| p50 latency | 132.5 ms | 137.5 ms | +5.0 |
| p95 latency | 152.7 ms | 153.3 ms | +0.6 |
| Peak RSS | 206.5 MB | 209.6 MB | +3.1 |

Per-category correction accuracy:

| category | Phase 4 strict | Phase 5 strict | Phase 5 final-text |
|---|---:|---:|---:|
| homoglyph | 0.967 | **1.000** | 1.000 |
| layout | 0.800 | **0.943** | 0.943 |
| proper_name | 0.733 | **0.867** | 0.867 |
| orthography | 0.649 | **0.662** | 0.714 |
| punctuation | 0.061 | 0.061 | **0.273** |
| morphology | 0.021 | 0.021 | **0.128** |
| real_word | 0.025 | 0.025 | 0.062 |
| typo_simple | 0.900 | 0.900 | 0.900 |
| typo_keyboard | 0.886 | 0.886 | 0.886 |

Correction outcomes:

| outcome | Phase 4 | Phase 5 |
|---|---:|---:|
| correct | 281 | **292** |
| correct-text-wider-span | — | 19 |
| span-mismatch | 34 | 19 |
| wrong-replacement | 30 | **19** |
| detection-only-no-replacement | 1 | 1 |

Edit quality, deterministic output: `subword 0.000, phrase 0.130, meaningful 1.000,
overwide 0.000, zero-length fragments 0` over 370 findings.

The rules layer went from 11 predictions with 4 false positives (0.636) to 15 with 4 (0.733):
every new punctuation finding is a true positive.

---

## 9. AI before / after, and the matrix

### §28 — did the sub-word cluster disappear?

Yes. Same corpus, same model, same prompt; the only change is the word-aware diff.

| | Phase 4 | Phase 5 |
|---|---:|---:|
| AI findings emitted | 139 | **71** |
| AI true positives | 16 | 15 |
| AI false positives | **123** | **56** |
| **AI precision** | **0.115** | **0.211** |
| sub-word fragments | 22 (18 %) | **0** |
| zero-length non-punctuation insertions | — | **0** |
| meaningful edit rate | — | **1.000** |
| over-wide edits | — | **0** |

Findings fell from 139 to 71 because the fragments *were* findings — one model rewrite sliced
into several. AI precision nearly doubled with no model change.

The remaining errors changed shape completely:

| Phase 4 rule id | n | | Phase 5 rule id | n |
|---|---:|---|---|---:|
| `WL-AI-DIFF-GRAMMAR` | 54 | | `WL-AI-DIFF-PUNCTUATIONINSERTION` | 42 |
| `WL-AI-DIFF-PUNCTUATION` | 44 | | `WL-AI-DIFF-WORDREPLACEMENT` | 13 |
| `WL-AI-DIFF-ORTHOGRAPHY` | 21 | | `WL-AI-SPACING` | 1 |
| `WL-AI-DIFF-READABILITY` | 4 | | | |

Every survivor is a recognisable linguistic claim — `'оплата' → 'оплаты'`,
`'независимая' → 'независимую'`, or a comma in the wrong place — rather than a slice.
**75 % are wrong comma placements.** Phase 4's largest cluster was 35 % of the model's
errors; with our own defect removed it is now three-quarters of what remains. That sharpens
the Phase 4 conclusion rather than weakening it: the model's residual weakness is
concentrated, and it is exactly where §13's rule analysis said parsing is needed.

### §30 — the matrix

| | **A** deterministic | **B** unrestricted Qwen | **C** routed Qwen | **D** candidate judge |
|---|---:|---:|---:|---:|
| Precision | **0.946** | 0.825 | 0.941 | — |
| Recall | 0.704 | **0.732** | 0.704 | — |
| F1 | **0.807** | 0.776 | 0.805 | — |
| Strict correction | 0.588 | **0.608** | 0.588 | — |
| Final-text correction | 0.626 | **0.652** | 0.626 | — |
| FP / 100 clean tokens | **0.348** | 1.493 | **0.348** | — |
| No-change accuracy | **0.963** | 0.877 | **0.963** | — |
| p50 latency | **138 ms** | 1577 ms | 176 ms | 435 ms |
| p95 latency | **153 ms** | 2052 ms | 1722 ms | 623 ms |
| Peak RSS | 210 MB | 212 MB | 213 MB | — |
| AI calls / sentence | 0 | 1.001 | 0.132 | — |
| AI layer precision | — | 0.211 | 0.000 (2 findings) | — |
| Selection accuracy | 0.962 | — | — | 0.906 |
| NO_CHANGE accuracy | 0.994 | — | — | 0.994 |
| Edit quality (subword / meaningful) | 0.000 / 1.000 | 0.000 / 1.000 | 0.000 / 1.000 | — |

**B** buys recall (+0.028) and correction accuracy (+0.020 strict, +0.026 final-text) and pays
precision (−0.121), false positives (4.3×), no-change accuracy (−0.086) and p50 latency
(11.4×).

**C** is the interesting one. With routing and the confidence floor, 13.2 % of sentences are
consulted, 35 findings are offered and 6 accepted — and every quality metric lands **exactly**
on the deterministic figures: same strict correction, same final-text, same FP/100, same
no-change. The 2 findings that survived to the report are both false positives. Routing has
become good enough to filter out essentially everything the model contributes, which means it
also filters out the model's contribution. It costs a p95 of 1722 ms to achieve parity with
doing nothing.

**D** loses to deterministic ranking on identical candidate lists (0.906 vs 0.962) and matches
it on NO_CHANGE (0.994) at 435 ms per call.

---

## 11. Product default recommendation

> **AI automatic correction OFF by default.**

Evidence, in the order it matters:

1. **No configuration improves on deterministic F1.** A scores 0.807; B 0.776; C 0.805.
2. **B's recall gain costs more than it buys.** Detection recall rises 0.704 → 0.732, which is
   14 additional gold errors found across 841 items. The same run adds 56 false positives on
   text that was correct, drops no-change accuracy from 0.963 to 0.877, and quadruples the
   false-positive rate on clean text. In a tool that runs continuously while someone writes,
   a false correction on correct text is the more expensive error.
3. **C achieves parity with A by suppressing the model almost entirely**, and pays a 1722 ms
   p95 for it. Paying that for measured parity is not a trade, it is a cost.
4. **D is measurably worse than the deterministic ranker it would replace**, and per §22 is
   therefore not enabled.
5. **The word-aware diff did not change this conclusion, but it did change its basis.** AI
   precision went 0.115 → 0.211 purely by fixing our own code. That is a real improvement and
   it is still an order of magnitude below every deterministic layer (spell 0.958,
   LanguageTool 0.972).

What this recommendation is **not**: it is not a statement that the local model is useless.
Its recall on morphology (0.319 → 0.511) and punctuation (0.394 → 0.455) is genuinely higher
than the deterministic layers'. It is a statement that at 0.211 precision it cannot be
allowed to write into a user's document unasked.

**Smart Actions are unaffected.** They are an explicit user request with a visible result the
user chose to invoke, which is a different risk profile from silent background correction,
and nothing in this phase measured or changed them. Conflating the two would be an error.

### Suggested configuration

| setting | value |
|---|---|
| Automatic AI correction | **off** |
| Deterministic pipeline (rules, spell, LanguageTool, reranker, lexical signals) | on |
| Candidate judge | off (experimental, `tools/judgebench` only) |
| Smart Actions | unchanged — explicit user invocation |

---

## 10. Strict vs final-text correction accuracy

These measure different things and both are reported. **Historical numbers are not rewritten.**

`CorrectionAccuracy` keeps its original name and meaning — exact span *and* exact replacement
— so every figure published in Phases 2–4 stays directly comparable.
`StrictExactCorrectionAccuracy` is the same number under an honest name.

`FinalTextCorrectionAccuracy` asks a different question: does applying the product's edit
produce the sentence the gold expects, regardless of whether the span matches the annotation?
It is computed by applying the gold correction and the product correction to the source
separately and comparing whole sentences, so it cannot be gamed by a broad span — a phrase
edit that repairs the error *and* changes a neighbouring word produces a different sentence
and fails.

On the Phase 5 deterministic pipeline the gap is +3.8 pp overall, concentrated exactly where
gold spans are annotated wider than a token:

| | strict | final-text |
|---|---:|---:|
| overall | 0.5875 | 0.6258 |
| punctuation | 0.061 | 0.273 |
| morphology | 0.021 | 0.128 |

A punctuation gold error is annotated as `'кажется что' → 'кажется, что'` — two words wide.
The product inserts a comma at one offset. The text is right; the span is not the annotated
one. Strict accuracy calls that a miss, and for comparing two annotations it is right to.
For asking whether the user's document ended up correct, it is the wrong question.

---

## 13. Remaining weaknesses

Ranked by measured cost.

**1. Candidate generation, not ranking (35.1 % of single-token targets).** The largest single
gap in the product. Three distinct sub-causes:

- *тся/ться and other real-word pairs* — both forms are lexicon-valid, so no candidate is
  ever generated. 79 real_word + 24 orthography targets.
- *Distance-2 escalation is conditional.* `RussianCandidateGenerator` only widens to distance
  2 when distance 1 returns **nothing**. `времч` finds `время` at distance 1, so `времени` is
  never generated. Same for `затиели → затеяли`, `профел → профиль`.
- *Distance 3+ and absent forms.* `Тарковскава → Тарковского`; `продакшен` is not in the
  index at all.

**2. No syntactic context in ranking (8 of 19 remaining wrong replacements).** `хвостм` ranks
`хвоста` over `хвостом`; `страницв` ranks `страницы` over `страница`; `времч` ranks `время`
over `времени`. Every one is a case/number/gender choice the sentence determines, and the
scorer never sees the sentence.

**3. Punctuation clusters needing morphology or parsing (17 of 24 missed).** Gerund phrases
and subject–predicate dashes need part-of-speech data that the rules layer has no access to;
the rest need clause structure.

**4. Modern and technical vocabulary has no frequency data.** `конфиг`, `нейросеть`,
`дискорде` sit at the unranked sentinel and lose to ordinary words at the same distance.

**5. The contextual reranker is net-negative on selection.** 0.938 against 0.962 for pure
lexical ranking on identical lists, and it demoted `конфиг` below `конфет`. It was fixed for
cross-script lists this phase; its same-script authority has not been re-examined and should
be.

---

## 12. Performance

| measurement | value | budget | source |
|---|---:|---:|---|
| Short-sentence latency, typing path | **0.097 ms** | 25 ms | `ShortSentenceCheckStaysFast` |
| Contextual pass, 4-error sentence | **17.2 ms** | 120 ms | `ContextualPassStaysWithinItsBudget` |
| Candidate-judge latency (p50 / p95) | 435 / 623 ms | — | `judgebench` |
| Dictionary load (one-off, off dispatcher) | 1064 ms | — | `ShortSentenceCheckStaysFast` |
| Benchmark p50 / p95 per sentence | 137.5 / 153.3 ms | — | `langbench` |
| Peak RSS, full deterministic pipeline | 209.6 MB | — | `langbench` |

Large-document behaviour, one planted typo near the **end** of the document:

| document | characters | analysis |
|---|---:|---:|
| short text | 267 | 163 ms |
| 1 page (~500 words) | 3,159 | 167 ms |
| 10 pages (~5,000 words) | 32,561 | 164 ms |
| 50 pages (~25,000 words) | 162,701 | 333 ms |

No expensive work moved onto the UI thread. The English layout lexicon is lazy and is reached
only for a Cyrillic token already rejected by the Russian index, so text without typos never
loads it. The word-aware diff runs on model responses only, never per keystroke.

### An unfixed startup risk, found by the same method

Late in the phase `TrayShutdownPath_StopsApplicationWithExitCodeZero` began failing again,
after passing five consecutive full-suite runs on identical binaries. A second stack capture
showed a different hang from the first:

```
UiaCoreApi.UiaAddEvent(...)
ClientEventManager.AddRootListener(...)
WriteLite.Services.TextFieldMonitor.Start()
App.<OnStartup>d__38.MoveNext()
```

Measuring the desktop's UI Automation tree directly explains it — one top-level window
answers a single `Current.Name` request in **10,008 ms**:

```
      4 ms  pid=1632   explorer
      1 ms  pid=11020  claude
  10008 ms  pid=13556  opera        <-- wedged provider
      0 ms  pid=16740  Telegram
      …
```

`TextFieldMonitor.Start()` registers a **global** UIA event listener **on the dispatcher
thread during startup**, so it walks that tree and blocks behind the slow provider. Nothing in
WriteLite changed; a browser window on the machine did.

This is environmental for the purposes of this phase's test results, but it is a genuine
product finding and it is the same class of defect as the modal-dialog hang fixed in §1:
**expensive, externally-controlled work on the startup dispatcher path.** A user with a
misbehaving browser or Electron app open gets a WriteLite that appears not to start. It is
recorded rather than fixed because changing UIA listener threading is a real behavioural
change with its own risk, and doing it unmeasured at the end of a long phase would be exactly
the kind of move the rest of this report argues against.

### Large-document regression (§33)

`LongDocumentOffsetTests` — 9 tests. The planted typo is found with an exact span at every
size; every reported span contains the text it claims across a 10-page document; applying
every correction right-to-left leaves the text before the first correction byte-identical.
Corrections past the first ~40 words are found, which is the specific regression the
sentence-window fix addressed.

Beyond the diff's alignment cap (1,200 significant tokens, above anything a 768-token model
context can produce) `LinguisticDiff` returns nothing rather than one document-wide span.
Silence is recoverable; a whole-document "correction" is not.

---

## 14. Product default recommendation

See §11. **AI automatic correction off by default**, deterministic pipeline on, candidate
judge experimental only, Smart Actions unchanged.

---

## 15. Training readiness

See `docs/training-readiness-report.md`, rewritten this phase with the per-task detail §38
asks for. **No training has been performed, started, or configured.**

Summary of that document:

| task | verdict |
|---|---|
| `punctuation_decision` | **train** — 27,989 balanced examples already exported, supervision is free, encoder classifier, 2–6 GPU-hours |
| `morphology_form_selection` | **train, with a precondition** — extend the existing 29 M reranker; but that reranker is currently net-negative on selection (0.938 vs 0.962) and that must be explained first |
| `real_word_disambiguation` | **not yet** — the bottleneck is candidate *generation*, not decision. Expand the 405 confusion sets and re-measure |
| candidate ranking (general) | **do not train** — deterministic 0.962; the judge scored 0.906 on identical lists |
| NO_CHANGE classification | **do not train** — deterministic 0.994; the judge matched it at 435 ms/call |
| generic generative correction | **do not train** — 0.211 precision against 0.958–0.972 |

---

## 16. Phase 6 handoff

```
Recommended training tasks:
1. punctuation_decision — binary COMMA / NO_CHANGE at a given boundary.
   Data ready (27,989 balanced examples). Encoder classifier, not generative.
   Acceptance: punctuation recall >= 0.70 with no-change accuracy still >= 0.963.

2. morphology_form_selection — choose the correct inflected form from the
   product's own candidate list, given the sentence. Extend the existing 29 M
   reranker. ~3,500 usable examples exported; generate more from the form index.
   Blocked on: explaining why the current reranker is net-negative on selection.

3. real_word_disambiguation — ONLY after the deterministic attempt.
   Expand the 405 confusion sets first, then re-measure candidate availability
   with tools/judgebench. If recall is still low with candidates present, the
   residue is a decision problem and belongs here.

Do not train:
- Candidate ranking in general. Deterministic ranking is 0.962 on reachable
  targets; the local model scored 0.906 on identical lists at 435 ms/call.
- NO_CHANGE classification. Deterministic is 0.994 and free, because the
  deterministic layer achieves it by not offering candidates for correct words.
- Generic generative correction. Precision 0.211 after the diff fix, against
  0.958 (spell) and 0.972 (LanguageTool). Routing does not repair it: at 13.2 %
  of sentences consulted, the accepted findings changed no quality metric at all.
- Anything intended to fix sub-word diff fragments, Latin-target layout,
  proper-name correction or homoglyph repair. All four were pipeline defects and
  are fixed in code this phase.

Do first, before any training:
- Widen candidate generation. 35.1 % of single-token targets have no correct
  candidate on the list; a perfect ranker would still miss them. Cheapest fix:
  escalate the distance-2 sweep when distance-1 candidates score poorly, rather
  than only when distance 1 returns nothing.
- Explain the contextual reranker's same-script authority.
- Create a held-out development split that is not the golden set.
```
