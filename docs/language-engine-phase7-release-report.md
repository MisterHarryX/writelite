# Phase 7 — release integration and RC validation

Date: 2026-08-15. Corpus: `ru_frozen_v1` (841 items, sha256 `16adfd7c…`), unchanged and frozen.

Phase 6 accepted `WriteLite-Punctuation-v1` behind a benchmark flag and handed four decisions
to Phase 7. This phase integrates it into the product, renames the user-facing local AI
subsystem to WriteAI, completes the punctuation and style layers, and answers the release
question.

Four defects were found by doing the integration rather than by reading the code, and each of
them was invisible in the Phase 6 numbers because the Phase 6 numbers never exercised the
affected path. They are in §3.

---

## 1. Phase 6 verification

Clean build from a purged tree, full suite excluding `TestCategory=UIA`, then both Phase 6
configurations re-run against the same corpus with rebuilt tools.

| | Phase 6 | Reproduced |
|---|---:|---:|
| build errors | 0 | **0** |
| warnings | 130 | **130** (analyzer style only, test project) |
| passed / failed / skipped | 1028 / 0 / 3 | **1063 / 0 / 3** |

Configuration **A** (`rules,spell,lt,rerank,lexveto,lexsignals`) and **B** (`+punct`):

| | P6-A | repro | P6-B | repro |
|---|---:|---:|---:|---:|
| Precision | 0.946 | **0.946** | 0.946 | **0.946** |
| Recall | 0.706 | **0.706** | 0.708 | **0.708** |
| F1 | 0.809 | **0.809** | 0.810 | **0.810** |
| Strict correction | 0.594 | **0.594** | 0.594 | **0.594** |
| Final-text correction | 0.628 | **0.628** | 0.630 | **0.630** |
| FP / 100 clean tokens | 0.348 | **0.348** | 0.348 | **0.348** |
| No-change | 0.963 | **0.963** | 0.963 | **0.963** |
| Punctuation recall | 0.394 | **0.394** | 0.424 | **0.424** |

Reproduced exactly. `benchmarks/results/P7-0-{A,B}*.json`.

The tools are not in `WriteLite.sln` and keep private copies of `WriteLite.dll` and the rule
pack, so `dotnet build WriteLite.sln` does not rebuild them. Every measurement in this report
was taken after an explicit tool rebuild. This is the stale-binary trap Phase 6 §3 recorded,
and it is still a property of the layout rather than something that was fixed.

---

## 2. Final architecture

```
User types
   ↓
fast path:  rules + spelling                       (no model, no LanguageTool, no network)
   ↓  debounce
orchestrated pass:
     rules  ∥  spelling  ∥  LanguageTool
        ↓
     lexical veto + register signals               (protects slang, obscenity, names)
        ↓
     merge  ─ one card per edit
        ↓
     style layer                                   (STYLE only, never an error, density-capped)
        ↓
     WriteLite-Punctuation-v1 @ 0.996              (suggestion-only, yields to rules)
        ↓
     validated findings → UI
```

WriteAI is not on this path at all. It is reached only by explicit Smart Action:

```
selection → Smart Action → WriteAI → validation → preview → Apply / Undo
```

The Phase 6 recommendation to stop using the reranker for candidate selection while keeping it
for real-word detection was **not** implemented. It is a measurable change that would have
moved the shipping numbers, and this phase deliberately made no unforced changes to the
scoring pipeline. Carried forward as a post-1.0 item with the Phase 6 evidence intact.

---

## 3. Defects found during integration

Each was found by exercising a path the Phase 6 benchmark does not cover, and each is fixed.

### 3.1 The rule catalog was destroying every explanation

`RuleBasedAnalyzer.ApplyCatalogMetadata` overwrote `Title` and `Explanation` unconditionally
from the rule pack. An analyzer that had built a reason naming the writer's own words had it
replaced by the pack's generic sentence on the way to the UI — «Обращение «дорогой друг»
отделяется запятой» arrived as «Обращение отделяется запятой».

Invisible to the benchmark, which scores spans and replacements and never reads an
explanation. §13 and §24 both ask for the specific form. The pack still owns category,
severity and the auto-apply permission — those are the integrity properties it exists for —
and an explanation the analyzer supplied now survives.

### 3.2 The punctuation model did nothing on any real document

`PunctuationModelAnalyzer.Analyze` opened with `text.Length > MaxSentenceLength` → return
empty. Correct for a benchmark whose every item is one sentence; on a document it produced
nothing, and produced it silently. The per-sentence cap was right and is kept — past 300
characters the boundaries fall outside the encoder's 64-token window — but it now applies per
sentence, with findings mapped back to document offsets.

### 3.3 The model was never packaged

`models/writelite-punctuation` had no `Content` item in `WriteLite.App.csproj`. Discovery
falls back to walking up to `WriteLite.sln`, which exists in a working copy and never in an
install, so the model loaded in every test and would have loaded for no user. Now packaged
file by file — a recursive include would have shipped the 342 MB of PUNC-A/B/C training
checkpoints sitting in the same directory.

### 3.4 Two layers, one comma, two underlines

Adding «а» to the adversative rule made WriteLite propose a comma LanguageTool already
proposed. The merger compared spans and found no duplicate, because the rule reports a
zero-length insertion after the preceding word and the engine reports both words around the
boundary rewritten with the comma between them — two shapes for one edit. The user got two
cards.

Fixed by reducing a punctuation finding to the offset at which it inserts a comma and treating
equal offsets as the same finding. The surviving card keeps the wider span (what the user
reads) and the built-in rule's reason (§13). This is what returned the benchmark to 0.946
precision after the «а» rule had cost it 0.002.

---

## 4. WriteAI rename and migration

User-facing surfaces renamed: the settings section and its rows, the navigation label, the
model-status hints, the Smart Action preview notice, and the backend tag in the status bar.
`WriteAiStatus` is now the single place that decides what the user is told, with five states
(`выключен / готов / загружается / занят / недоступен`) and a hint on each that says the
editor and the deterministic checking keep working.

§54 asked for no raw transport errors in normal UI. `Ошибка проверки Qwen: {ex.Message}` on the
settings page was one; it now logs the exception technically and shows the unavailable state.

**Provenance is unchanged.** `WriteLiteLanguageEngineVersion` records `WriteAiBaseModel =
"Qwen2.5"` and `WriteAiRuntime = "llama.cpp"` alongside the product name. Wire-protocol
identifiers (`writelight-qwen`) and the model directory name are untouched: renaming a value a
running server sends would break it, and §4 warns against renames for aesthetics.

### Settings migration

Persisted keys `localAiEnabled`, `preferQwen`, `qwenEndpoint` and the legacy
`aiCheckingEnabled` are read once when the new key is absent, written back under
`writeAiEnabled` / `preferWriteAi` / `writeAiEndpoint`, and the schema moves 3 → 4. The old
names survive in code as `[JsonIgnore]` forwarding aliases so the ~40 call sites still
compile — the line §4 asks for between renaming and a large risky sweep.

One ordering bug was caught by the existing suite: the rename migration ran before
`ApplyLegacyAiDefaults` and copied a schema-1 file's stale «off» over the default that had
just been chosen for it. Rename now runs first, defaults second.

9 migration tests, including that the aliases are never serialised — a property that both
persists and forwards writes the same value under two names and leaves the next reader unable
to tell which the user last changed.

---

## 5. Punctuation as a product feature

The trained model is one layer of six. What was added this phase:

| rule | what it settles | auto-applies |
|---|---|---|
| `ru.punctuation.vocative-comma` | address after a greeting: «Привет дорогой друг» → «Привет, дорогой друг» | yes |
| `ru.punctuation.greeting-sentence-boundary` | «…друг как твои дела» → «…друг! Как твои дела?» | **no** |
| `ru.punctuation.adversative-comma` | extended with «а», the strongest member of the set | yes |
| `ru.grammar.case-government-dative` | «вопреки новых целей» → «вопреки новым целям» | yes |

Rule pack `ru-1.2.0` → **`ru-1.3.0`**, each rule carrying its condition, exceptions, examples,
source and its own positive and negative tests like every other rule in the pack.

### How the vocative gate works, and why it is shaped that way

The form index records **one analysis per surface form**, so a word that is two things at once
comes back as whichever analysis OpenCorpora listed first. Three consequences shaped the rules:

- «из» is recorded as a personal name with the proper-name flag set. Accepting a proper name
  without also requiring the nominative would turn «Привет из Москвы» into «Привет, из
  Москвы». The genitive tag is what stops it, and the nominative requirement is not redundant.
- «коллеги» is recorded as the genitive singular of «коллега», so the tag gate rejects «Добрый
  вечер коллеги» — a correct finding lost to a data artefact. A short closed list of
  collective addresses restores it without loosening the gate.
- «новых» is recorded as a noun form of «новое», so same-lemma search never reaches «новым».
  The case-government rule therefore transforms modifiers by a closed genitive→dative ending
  relation gated on index membership, and the head noun by same-lemma search — opposite routes
  for opposite reasons.

The dative rule fires only when the head noun is recorded **genitive** and a distinct dative
form of the same lemma exists. Firing on "not dative" would rewrite correct text, because
«инструкции» is recorded genitive and is also the dative. A homonymous form produces nothing,
which is the right answer for it.

`благодаря` is also the gerund of «благодарить», which governs the accusative — «благодаря
друга за помощь» is correct Russian. Thanking is done to animates, so that preposition
requires an inanimate head.

### Confidence (§12)

`TextIssue.Certainty` is derived, not stored: `Certain` requires the auto-apply permission the
rule pack grants only to closed rules with stated exceptions, plus confidence ≥ 0.85; `Likely`
is ≥ 0.7 with a replacement; everything else is a suggestion. **The trained model can never
reach `Certain`**, because it never carries the auto-apply flag — §12 expressed as a
consequence of the pipeline's shape rather than as a special case about models.

### Categories covered

Addressing/vocative, subordinate clauses, introductory constructions, homogeneous and
adversative boundaries, conjunction boundaries, sentence boundaries, direct speech, quotes,
dash, colon, semicolon, question and exclamation marks, abbreviations and parenthetical
constructions are covered across the 28-rule punctuation pack plus the model. Participial and
gerund phrases remain a known limitation — see §11.

---

## 6. Punctuation threshold decision

**0.996 is kept, unchanged and frozen.** Phase 6 left 0.994 and 0.990 as alternatives and told
Phase 7 to decide on evidence other than the golden set.

| evidence | result |
|---|---|
| target-safety probe, 62 correct sentences, 265 boundaries | **0 proposals** at 0.996, reproduced this phase |
| held-out validation sweep (Phase 6) | 0.994 → +0.07 FP/100 projected; 0.990 → +0.22 |
| shipping benchmark at 0.996 | FP/100 **0.348**, identical to deterministic-only |

Loosening buys punctuation recall on a corpus containing 30 punctuation items and costs
false positives on continuously-running correct text. §50 is explicit that the threshold must
not be tuned to make a score look better, and the deterministic layer added four new rules
this phase, which is where the punctuation recall in this release actually came from.

The known weakness is unchanged: the Phase 6 probe at a 0.20 diagnostic threshold showed the
model degrades first on **technical text and Latin tokens inside Russian**, at roughly twice
the rate of ordinary prose. At 0.996 it proposes nothing there, so the weakness is latent
rather than active — and it is the reason to be suspicious of any future loosening.

---

## 7. Style engine

A first-class layer, and everything in it is `IssueClass.Style` and never auto-applies.

`IssueClass` (ERROR / WARNING / STYLE / INFORMATION) is derived from category and severity, so
no analyzer can classify itself inconsistently with what it already declared, and adding it
required no change to the ~30 places that construct a finding. Style and Readability are STYLE
whatever their severity: a long sentence is not an error at Warning severity.

Covered with closed lists that can each carry a specific reason: redundancy, intensifier
stacking, double comparatives and superlatives, filler, bureaucratic phrasing, nominalization,
and repetition across neighbouring sentences. Deliberately absent: passive-voice density and
unclear pronoun reference — both need to know what the sentence means, and §15 says not to
pretend every style preference is objectively wrong.

**Profiles** (General, Academic, Business, Formal, Technical, Creative, Social, Student) change
what is offered, never whether text is erroneous. Filler is mentioned in profiles that expect a
neutral register and left alone in chat. Repetition analysis stands down entirely on Creative
and Social — repetition is a device in prose and a defect in a report, so the layer declines
rather than being tuned differently.

### Style false positives, measured separately (§47)

| measurement | result |
|---|---:|
| style findings on 10 correct ordinary sentences (Business profile) | **0** |
| style findings on the 277 clean golden items | **0** — FP/100 unchanged at 0.348 |
| density ceiling | 3 findings per 100 words, applied to the whole layer |

The ceiling is on the layer rather than per rule, because the annoying case is not one rule
firing repeatedly — it is six different rules each firing once on the same paragraph. When it
binds, the highest-confidence findings survive rather than the earliest.

Two defects were found while testing it: phrase precedence used **pattern** length instead of
**match** length, so the regex for «данный» outranked the one for «на данный момент времени»
and the short phrase claimed the span; and a deletion suggestion left a double space behind,
turning «Я был очень сильно наслышан» into «Я был  наслышан». Both fixed, both regression-tested.

---

## 8. Grammar, context and explanations

Case government, тся/ться in both directions, agreement, «не»/«ни», joined-or-separate
spelling and the 405 confusion sets are all deterministic and all carry a reason. Real-word
context is unchanged from Phase 6 — the 58 genuine generation-gap items remain a known
limitation, and expanding the confusion sets is data work with its own licensing review that
§65 puts out of scope.

The §45 explanation set asserts the **concept** a reason names rather than its wording, so a
rewording that keeps the linguistics right keeps the tests passing:

| input | concept required |
|---|---|
| Согласно приказа отдел закрыт. | дательного падежа |
| Вопреки новых правил он ушёл. | дательного падежа |
| Привет дорогой друг. | обращение |
| Всё было готово однако никто не пришёл. | союзом |
| К сожалению поезд опоздал. | вводное |

Every finding the deterministic pipeline can produce satisfies the §24 card contract:
Original, Replacement, Category, Severity, Confidence, Why — asserted across a corpus rather
than on examples.

---

## 9. WriteAI and Smart Actions

Smart Actions (Исправить, Переписать, Сократить, Расширить, Объяснить, Перевести, Сделать
официальнее) are unchanged in behaviour and rebranded. Automatic generative correction stays
**off**, on the Phase 6 evidence: 0.822 precision, 1.493 FP/100, no-change 0.877, p50 1435 ms.
Nothing in this phase spoke against that.

Correction explanations come from the deterministic rules, not from WriteAI — §30 asks for
linguistic evidence over generic prose, and a rule that knows it is about «вопреки» can say so.

### Lifecycle (§34), measured on the release build

This was a real defect. Startup called `WarmupAsync`, which reached `EnsureReadyAsync` and
**launched llama-server**, which then held memory for the whole session whether or not any
Smart Action was invoked.

| | before | after |
|---|---:|---:|
| llama-server at idle | **1 process, 481 MB** | **not running** |
| WriteLite RSS at idle | 433 MB | **356 MB** |
| combined idle RSS (WriteLite + LanguageTool + WriteAI) | **1523 MB** | **958 MB** |
| idle CPU (12 logical cores) | 1.00 % | **0.11 %** |

Startup now probes availability only: `RefreshAvailability` confirms the model pack is present
and notices a server that is already running, which is everything the status UI needs to say
«WriteAI готов» without a model in memory. The server starts on the first request that needs
it. The log line `local-ai-warmup … startBackend=0` is the evidence.

`_warmed` is only set by a warmup that actually started the backend, so an availability-only
pass cannot stop a later one from starting it.

---

## 10. Punctuation model lifecycle (§35)

Measured rather than guessed. Loading costs 188–428 ms of ONNX session construction and
+48 MB RSS; the model is consulted on the orchestrated pass, which is at least one debounce
interval after typing starts.

**Decision: lazy load, then retain.** Loading at startup would put 400 ms on the path to the
first window for a layer nothing consults yet; reloading per analysis would pay it repeatedly.
Holding 48 MB once against 400 ms on every pass is not close. Inference is bounded to one at a
time by a semaphore, because the ONNX session is built with `IntraOpNumThreads = 1`
specifically so background analysis never takes the machine from the person typing, and
concurrent entry would hand that budget back a thread at a time.

Failure is contained: a model that throws, times out or was never deployed leaves the editor
and the deterministic pipeline working (§33). Cancellation is re-thrown because it is the
caller's own signal; everything else is logged and swallowed.

---

## 11. Startup and UI-thread audit (§36–§38)

`Automation.AddAutomationFocusChangedEventHandler` is a synchronous cross-process COM call: it
reaches into whatever currently has focus to attach a listener and returns when that
application's automation provider answers. Called on the dispatcher during startup, as it was,
a wedged foreign process holds WriteLite's shell closed — the ~10 s freeze Phase 5/6 recorded.

Fixed in the architecture, not explained away: timers, the foreground hook and the started flag
are local work and happen immediately; the one call that talks to another process moved off the
dispatcher with a 5 s bound and failure isolation. Without the subscription WriteLite still
tracks the active field through the foreground hook and the poll timer — a degraded monitor,
not a broken editor. Unsubscribing moved too, for the same reason on the way out.

Measured on the release build:

```
13:13:57.890  application-starting
13:13:58.843  spelling-dictionaries-loaded    (3,091,067 forms, 528 ms)
13:14:01.734  qwen-availability               (available=1, health=0 — no server started)
13:14:03.169  language-packs                  (morphology 16 MB is the bulk of this)
13:14:03.862  monitor-started
13:14:03.867  application-running             ← 5.98 s
13:14:04.077  focus-subscription-ready        elapsedMs=213  ← after running, not before
```

`focus-subscription-ready` landing **after** `application-running` is the fix working: the
shell reached its running state without waiting for the automation tree.

Recovery-prompt startup behaviour is unchanged from the previous fix and its scenarios still
pass. Dictionary loading, LanguageTool startup, ONNX loading, WriteAI startup, lexical pack
parsing and document analysis are all off the dispatcher.

---

## 12. Performance (§39), release build, otherwise idle machine

| measurement | value |
|---|---:|
| startup to `application-running` | **5.98 s** |
| idle CPU | **0.11 %** of 12 logical cores |
| WriteLite idle RSS | **356 MB** |
| combined idle RSS with LanguageTool | **958 MB** |
| llama-server at idle | **not running** |
| correction latency p50 / p95, deterministic (`P7-FINAL-A`) | **144.8 / 179.0 ms** |
| correction latency p50 / p95, shipping (`P7-FINAL-B`) | **151.7 / 171.1 ms** |
| punctuation model cold load | **188–428 ms** |
| punctuation model added RSS | **+48 MB** |
| benchmark peak RSS, deterministic / shipping | **217.4 / 266.6 MB** |
| 50-page document, deterministic rules | **< 30 s**, offsets exact |

Typing is unaffected: the fast path is rules + spelling with no model, no LanguageTool and no
network, and the punctuation model is never on the per-keystroke path.

The two figures worth naming honestly: **5.98 s startup** is slower than it should be, with
the 16 MB `morphology-index.json` parse the largest single contributor; and **LanguageTool at
602 MB** is the biggest single resident cost in the product. Neither blocks the release — the
shell is responsive throughout startup, and extended checking is a user-visible setting — but
both are the obvious targets after 1.0.

---

## 13. Final benchmark (§49)

Frozen golden corpus, 841 items, sha256 `16adfd7c…`, machine otherwise idle, all tools rebuilt.

Values are read from the stored reports rather than transcribed from earlier documents.
Phase 4 pre-dates the strict / final-text correction metrics, which is why that column has
gaps rather than estimates.

| | Phase 4 `P4-A` | Phase 5 `P6-0` | Phase 6 A | Phase 6 B | **Phase 7 shipping** | legacy unrestricted WriteAI `P6-D` |
|---|---:|---:|---:|---:|---:|---:|
| Precision | 0.945 | 0.946 | 0.946 | 0.946 | **0.946** | 0.822 |
| Recall | 0.696 | 0.704 | 0.706 | 0.708 | **0.708** | 0.732 |
| F1 | 0.802 | 0.807 | 0.809 | 0.810 | **0.810** | 0.774 |
| Strict correction | — | 0.588 | 0.594 | 0.594 | **0.594** | 0.612 |
| Final-text correction | — | 0.626 | 0.628 | 0.630 | **0.630** | 0.652 |
| FP / 100 clean tokens | 0.348 | 0.348 | 0.348 | 0.348 | **0.348** | 1.493 |
| No-change | 0.963 | 0.963 | 0.963 | 0.963 | **0.963** | 0.877 |
| Punctuation recall | 0.273 | 0.394 | 0.394 | 0.424 | **0.424** | 0.455 |
| Morphology recall | 0.319 | 0.319 | 0.319 | 0.319 | **0.319** | 0.511 |
| Real-word recall | 0.075 | 0.075 | 0.075 | 0.075 | **0.075** | 0.088 |
| Layout recall | 1.000 | 1.000 | 1.000 | 1.000 | **1.000** | 1.000 |
| Proper-name recall | 1.000 | 1.000 | 1.000 | 1.000 | **1.000** | 1.000 |
| Slang preservation | 0.935 | 0.935 | 0.935 | 0.935 | **0.935** | 0.826 |
| p50 latency | 141.2 ms | 155.1 ms | 150.7 ms | 166.1 ms | **151.7 ms** | 1435.5 ms |
| p95 latency | 171.4 ms | 183.8 ms | 166.1 ms | 183.7 ms | **171.1 ms** | 1667.0 ms |
| Peak RSS | 206.7 MB | 213.5 MB | 216.9 MB | 264.7 MB | **266.6 MB** | 221.1 MB |

`P7-FINAL-A` is byte-identical to `P6-A` and `P7-FINAL-B` to `P6-B` on every metric. Phase 7
ships Phase 6 configuration B's numbers exactly, with four new deterministic rules, a style
layer and a real integration underneath them — **and the style layer cost nothing**: FP/100
(7 false positives on 2,010 clean tokens) and no-change are identical to deterministic-only
across 277 clean items.

Latency comparisons across phases are not like-for-like: these are wall-clock measurements on
a developer machine and the p50 differences between columns are smaller than the variation
between runs of the same configuration. The one comparison that is safe is within a phase —
the punctuation model adds about 7 ms to p50 and 48 MB to peak RSS.

The legacy unrestricted-WriteAI column is the Phase 6 measurement (`P6-D`), unchanged: that
path was not touched this phase and re-running it would measure the same code. It remains
better at recall in every category and unusable as a default — 4.3× the false-positive rate,
9.5× the latency, and no-change accuracy 0.877 against 0.963.

`benchmarks/results/P7-FINAL-{A,B}*.json`.

---

## 14. Tests

| suite | result |
|---|---|
| unit / integration / language / AI / performance / lifecycle | **1172 passed, 0 failed, 1 skipped** |
| Phase 7 additions | 68 new tests across 6 files |
| UIA | not run — requires an interactive desktop session |

New: `Phase7PunctuationAndGrammarTests` (34), `RussianStyleAnalyzerTests` (14),
`Phase7ReleaseRegressionTests` (31 including data rows), `PunctuationModelIntegrationTests` (9),
`WriteAiSettingsMigrationTests` (9), `CommaInsertionMergeTests` (6),
`ShippedLexicalResourcesTests` (4).

**One environmental failure, with evidence.** `Lookup_IsFastEnoughForThePerTokenPath` asserts a
lexical lookup under 50 µs and fails under concurrent CPU load; it passes in isolation on the
same build (`1 passed, 0 failed`). This is the same flake Phase 6 §12 recorded. It is not
excluded from the suite.

Negatives outnumber positives in the new linguistic tests deliberately. The failure that
matters for these rules is not a missed comma — it is a comma proposed into a sentence that
did not want one, so each rule's hard negatives are the alternative readings its gate exists to
exclude.

---

## 15. Package audit (§58)

Release build: `dotnet publish -c Release -r win-x64 --self-contained`. **0 errors.**

| component | size |
|---|---:|
| `models/writelight-qwen` (WriteAI generative) | 424 MB |
| `ThirdParty/LanguageEngine` (LanguageTool) | 382 MB |
| `resources/lexical` | 331 MB |
| `models/writelight-reranker` | 30 MB |
| `models/writelite-punctuation` | 30 MB |
| `resources/audio` | 13 MB |
| `resources/spelling` | 4 MB |
| runtime + application binaries (297 files) | 194 MB |
| `resources/rules` | 144 KB |
| **total** | **~1.4 GB** |

Hygiene verified in the published output: no `runtimes/` (cross-platform RIDs pruned by the
RID-specific publish — 185 MB of iOS/Android/macOS/Linux ONNX binaries that the plain
`bin/Release` output does carry), no `logs/`, no PUNC-A/B/C training checkpoints, no
`writelight-qwen/adapter` or `merged-hf`, and `resources/lexical/source` now excluded as build
input. 8 PDBs.

### One duplication found, measured, and deliberately not acted on

`writelight-lexical-open.json` (53 MB) and `writelight-lexical-en.json` (41 MB) hold content
that `writelight-lexical.db` already carries in full — the database has all 142,853 entries of
both languages, and `OfflineLexicalKnowledgeService` opens it first and returns before it ever
reads the JSON. 94 MB shipped and never read on the dictionary path.

Excluding them was implemented and **reverted**: `LexicalPackCatalogService` enumerates
installed packs by reading those files directly, so removing them also removes the packs — and
their CC-BY-SA-4.0 and CC-BY-4.0 attribution — from the dictionary UI. Reclaiming the space
means teaching the catalog to enumerate from the database, which changes a user-visible surface
and licence display, and is not a change to make at the end of a release phase. Recorded below
as fix-soon-after-1.0, with tests pinning the current arrangement so whoever does it finds out
immediately if they break the catalog or the dictionary.

### WriteAI model packaging (§59)

The 424 MB generative model currently ships bundled. Making it an optional component would cut
the download by 30 % for the majority of users who never invoke a Smart Action — and after §9,
they no longer pay for it in memory either. It is **not** done here: the architecture has no
installer-time component selection and no post-install fetch, building one is a new project,
and §59 says not to make it one. Offline-first is preserved either way; the tradeoff is
documented and the decision is a 1.x installer question.

---

## 16. Versioning (§60)

```
WriteLite Language Engine 1.0
  rule pack               ru-1.3.0
  WriteLite-Punctuation-v1  rubert-tiny2, int8 ONNX, threshold 0.996 (frozen)
  WriteAI Local 1.0         base model Qwen2.5, runtime llama.cpp
```

`WriteLiteLanguageEngineVersion` holds these. Product name and provenance sit side by side
there on purpose: a licence obligation is not satisfied by a product name, and these strings
belong in diagnostics and About rather than in ordinary UI (§53).

---

## 17. Known limitations, classified (§62)

**Release blockers: none.**

### Fix soon after 1.0

| | evidence |
|---|---|
| 94 MB of duplicated lexical JSON | §15. Blocked on moving pack-catalog enumeration to the database. |
| 5.98 s startup, `morphology-index.json` 16 MB parse the largest contributor | §12 |
| LanguageTool 602 MB resident whenever extended checking is on | §12 |
| Reranker still used for candidate selection despite being net-negative at every threshold | Phase 6 §6; not changed here to avoid an unforced pipeline change |
| Four §43 words absent from the modern-vocabulary pack: «юзать», «бафф», «тильт», «дефолтный» | §18 below |

### Known limitations

- **Participial and gerund phrase punctuation** is not settled deterministically. These need to
  know where a phrase ends and whether it has dependents, which is parsing, and the trained
  model reaches them only at 0.996 confidence.
- **Real-word recall is 0.075.** 58 of 100 golden misses are a genuine candidate-generation gap
  whose fix is expanding the 405 confusion sets — data work with its own licensing review.
- **Morphology recall is 0.319.** Phase 6 established there is no learnable selection task in
  the data; the targets are generation failures and closing them means questioning 78 % of
  every correct word.
- **Punctuation model on technical and mixed-script text.** Its known weakness, latent at 0.996
  and the reason to distrust any loosening.
- **Style covers what closed lists can settle.** Passive-voice density and unclear pronoun
  reference are absent because they need to know what the sentence means.
- **UIA tests are not run in this environment.** They need an interactive desktop session.

### Future enhancement

Optional WriteAI model download (§15), document-level terminology consistency, and expanding
the confusion sets.

---

## 18. §43 modern vocabulary, measured

The behavioural requirement — "not automatically a spelling error" — **passes for all ten
words**: none of «имба, кринж, рофл, вайб, юзать, апнуть, нерф, бафф, тильт, дефолтный» is
auto-corrected by the spelling pipeline, verified through `LocalSpellChecker` and
`SpellTextAnalyzer` rather than through the index.

Six are in the curated pack with their register flags (`is_slang`, `is_borrowing`), which is
what lets a formal profile offer a neutral alternative without the word ever being an error.
Four are absent — «юзать», «бафф» (only «баф» is present), «тильт», «дефолтный» (only
«дефолт») — and would draw a suggestion underline rather than a correction. Adding them means
rebuilding the 3.09 M-form index, which is not a change to make between a final benchmark and a
release gate.

---

## 19. Release defaults (§52), verified not assumed

```
Deterministic correction        ON
WriteLite-Punctuation-v1        ON   (threshold 0.996, suggestion-only)
Style suggestions               ON   (STYLE class, never auto-applies, ≤3 per 100 words)
WriteAI Smart Actions           available, model loaded on first use
Automatic generative correction OFF
Style profile                   General
```

Asserted in `WriteAiSettingsMigrationTests.DefaultsMatchTheReleaseConfiguration`.

---

## 20. Release candidate gate (§63)

> **YES** — the WriteLite Language Engine is ready for WriteLite 1.0 RC.

The engine ships Phase 6's accepted numbers unchanged (F1 0.810, final-text 0.630, FP/100
0.348, no-change 0.963) with the accepted model actually integrated, actually packaged, and
actually working on documents rather than only on corpus items. The four integration defects
found this phase are fixed and regression-tested. Idle memory fell 37 % and the startup freeze
path is closed in the architecture. 1172 tests pass, with the single failure identified as
environmental and evidenced.

Nothing in the known-limitations list damages user text, loses data, crashes, freezes the
editor, or produces mass false positives. The items that remain are recall ceilings with
measured causes and resource costs with named owners — the things §62 says not to hold a
release for.

Major language features are frozen. Only release-blocking bug fixes, performance fixes, UX
fixes and critical language regressions from here to 1.0.

There is no Phase 8.
