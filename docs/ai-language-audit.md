# WriteLite language intelligence — audit (Phase 1)

Date: 2026-08-12. Machine: Windows 11 Pro 26200, .NET SDK 10.0.301.

Everything below was verified on this machine in this pass unless the line says
otherwise. Where a claim comes from an existing document rather than from a
command I ran, it is marked *(from docs, not re-verified)*.

**Headline: this is not a greenfield project.** A large part of what the brief
asks for already exists and works. The audit's job was to find out *which* part,
and the answer is that the deterministic Russian spelling stack is strong and
measured, while the layers above it — grammar, punctuation, document context,
and the end-to-end evaluation that would prove any of them — are where the real
gaps are.

---

## 1. Control run: does the existing thing still work?

Run before touching anything, per §54.

```bash
dotnet build WriteLite.sln -c Release
```

Succeeded. 0 errors, 4 warnings — all four are the same `NU1903` advisory for
`SQLitePCLRaw.lib.e_sqlite3` 2.1.10 (known vulnerability, high severity). That
is a real supply-chain finding and is listed under risks below.

```bash
dotnet test WriteLite.sln -c Release --no-build
```

**811 passed, 2 failed, 1 skipped, 814 total, 1 m 28 s.**

The two failures are pre-existing and already documented in `FUTURE_WORK.md`:

| Test | Why it fails |
|---|---|
| `QwenRouterAndParseTests.Analyzer_WithoutQwenPack_UsesLiteFallback` | Asserts the analyzer falls back to `lite` when no Qwen pack exists. The pack *is* present at `models/writelight-qwen`, so the backend reports itself available and the assertion fails. |
| `LocalAiAnalyzerTests.Provider_UnreachableServer_UsesFallback` | Same root cause — asserts `LastBackend != "writelight-qwen"`. |

Both are test-isolation defects, not product defects: they discover the real
model directory instead of being pointed at an empty one. They are the baseline
I will hold the rest of the work against — **no new failures are acceptable**,
and these two should be fixed as part of Phase 2 rather than carried.

The skipped test is `LiveHost_Check_Stop_FreesProcessAndPort` (needs a live
LanguageTool host).

---

## 2. What is actually installed

### 2.1 Local generative model

`models/writelight-qwen/model.manifest.json`:

| Field | Value |
|---|---|
| Model version | `WriteLite-Qwen-0.6B-GEC-1.0.1` |
| Base model | `Qwen2.5-0.5B-Instruct` (Apache-2.0) |
| Format | GGUF, `writelight-qwen-q4_k_m.gguf` — Q4_K_M |
| Also on disk | LoRA adapter + merged HF weights (`merged-hf/`, gitignored) |
| Runtime | `llama.cpp` server, launched as a child process, loopback only |
| Endpoint | `http://127.0.0.1:8742`, OpenAI-shaped `/chat/completions` |
| **Context** | **768 tokens** |
| Batch / micro-batch | 256 / 128 tokens, 1 parallel slot |
| Memory budget | 5120 MiB, CPU only |
| Inference | temperature 0.0, `maxNewTokens` 96, fixed system prompt (rev. 3) |
| Weights SHA-256 | `a25b3ab9…aa390` |
| Pack size on disk | 2.3 GB (dominated by `merged-hf/`; the shipped GGUF is far smaller) |

The 768-token context is the single most consequential number in this table. It
is small enough that §7 (full-document context) and §8 (hierarchical retrieval)
cannot be served by this model without the chunking layer the brief describes —
which is the correct architectural answer anyway, but it means the constraint is
hard, not a preference.

`models/writelight-qwen-polish-staging/` is a second 2.3 GB pack that nothing in
`src/` references. Dead weight; candidate for deletion after confirming with the
owner.

`models/writelight-gec/` contains only a README — the directory is a stub.

### 2.2 Contextual reranker

`models/writelight-reranker/` — `cointegrated/rubert-tiny2` (MIT, 29 M params)
fine-tuned as a **binary sentence-acceptability classifier**, exported to ONNX,
dynamically quantised to int8. 29.4 MB, ~1 ms per sentence on CPU. Training
config and metrics in `experiments/reranker-001/result.json`; artefact SHA-256
recorded there.

This is a good design decision that predates this audit and should be kept: a
binary encoder *structurally cannot* rewrite the user's text, which removes an
entire class of semantic-corruption failure (§27) rather than mitigating it.

### 2.3 Tokenizer

`WordPieceTokenizer` (190 lines) in `WriteLite.AI.Local`, driven by
`models/writelight-reranker/vocab.txt` (83 828 tokens) — this serves the
reranker. The Qwen path does not tokenize in-process; it posts text to the
llama.cpp server and lets the runtime tokenize.

### 2.4 Rule engine

**LanguageTool 6.4 is embedded and shipped**:
`src/WriteLite.App/ThirdParty/LanguageEngine/6.4/languagetool-server.jar` +
`languagetool.jar`, launched by `WriteLiteLanguageEngineHost` as
`org.languagetool.server.HTTPServer` on `127.0.0.1:18081` (searches 40 ports
upward if busy), wrapped in a Windows Job Object so it dies with the app.

Note on defaults, because it is easy to misread: `WriteLiteLanguageOptions.EnableEngine`
defaults to `false`, but `App.xaml.cs:157` sets it from `_settings.ExtendedChecking`,
which defaults to **`true`**. So the engine *is* live in the shipped app. The
library-level `false` is just a safe default for direct construction.

### 2.5 Spelling and morphology

- **Form index** — `resources/lexical/ru-forms.wldawg`, a minimal DAFSA holding
  **3 091 067 surface forms in 4.7 MB** (verified: the benchmark run reports
  `DictionaryWordCount = 3091067`). Word ids are lexicographic rank recovered
  from per-arc subtree counts, so the 37 MB metadata array (`ru-forms.meta.bin`)
  is memory-mapped rather than resident.
- **Candidate generation** — Levenshtein automaton walking the DAFSA
  (`RussianCandidateGenerator`), scored by `RussianCandidateScorer` on first-letter
  agreement, length ratio, ЙЦУКЕН key adjacency, Russian confusion patterns
  (unstressed vowels, voiced/voiceless pairs, тся/ться, doubled consonants),
  corpus frequency, and register flags.
- **Hunspell** — LibreOffice ru_RU retained as a second opinion behind
  `CompositeSpellingLexicon`.
- **Frequency** — `ru-frequency.tsv`, 10.8 MB, OpenSubtitles-2018 ranks.
- **Modern vocabulary** — `ru-modern-vocab.json`, 2 652 lemmas → 27 724 forms of
  tech/brand/gaming/internet slang, authored in-repo under CC0-1.0.

### 2.6 Lexical knowledge

`OfflineLexicalKnowledgeService` (502 lines) + `SqliteLexicalStore` over
`resources/lexical/writelight-lexical.db` (152 MB). Supporting packs:
`writelight-lexical-open.json` (55 MB), `writelight-lexical-en.json` (42 MB),
`morphology-index.json` (16 MB), `ru-en-translations.db` (6.6 MB).

The brief's §5 `ILexicalKnowledgeService` is **already implemented** as
`ILexicalServices.cs` + `OfflineLexicalKnowledgeService`, with morphology
(`RuleBasedMorphologyService`), syntactic role (`SyntacticRoleAnalyzer`),
translations (`TranslationIndex`) and contextual ranking
(`ContextualLexicalRanker`). This does not need to be built; it needs to be
*consulted by the correction pipeline* more than it currently is.

### 2.7 Data provenance

`resources/lexical/sources.manifest.json` (28 KB) records name, source, licence,
version, hash and entry counts, and is enforced by `LexicalProvenanceTests`.
This satisfies §21 in substance. It is **not** at the path the brief names
(`data/provenance-manifest.json`) — I recommend keeping the existing path and
its passing test rather than moving it to match the brief.

Licences in play: OpenCorpora CC BY-SA 3.0, WriteLite Modern Vocabulary CC0-1.0,
FrequencyWords MIT, LibreOffice Hunspell BSD-like, rubert-tiny2 MIT, Qwen2.5
Apache-2.0. Nothing scraped without licence *(from docs + manifest, spot-checked
not exhaustively re-verified)*.

---

## 3. The pipeline as it actually runs

```
typing
  └─ DebouncedTextAnalyzer
       └─ HybridTextAnalysisService              (Services/Ai)
            ├─ local: WriteLiteOrchestratingAnalyzer
            │    ├─ RuleBasedAnalyzer            (resources/rules/ru/*.json)
            │    ├─ SpellTextAnalyzer → LocalSpellChecker
            │    │     ├─ RussianFormIndexLexicon (DAFSA + automaton + scorer)
            │    │     ├─ HunspellSpellingLexicon
            │    │     ├─ UserDictionaryService
            │    │     └─ ContextualCorrectionRefiner → RussianContextualReranker (ONNX int8)
            │    └─ WriteLiteLanguageEngine       (LanguageTool 6.4, loopback Java)
            └─ ai:    AiTextAnalysisService → LocalAiTextProvider
                       └─ LocalAiTextAnalyzer     (WriteLite.AI.Local)
                            ├─ AiCallRouter        — decides IF the LLM runs at all
                            ├─ LocalCorrectionEngine (Lite, deterministic, always available)
                            ├─ QwenModelBackend    → llama.cpp loopback
                            ├─ TextDiffBuilder     — offsets recomputed, never trusted from the model
                            └─ AiResultValidator   — rejects malformed / excessive rewrites
                 ↓
        WriteLiteIssueMerger  (local outranks AI: sourceRank 0/1 vs tertiary)
                 ↓
        IssueRenderingPipeline.Filter
                 ↓
                 UI
```

Selection actions run on a separate path: `TextRewriteService` →
`QwenModelBackend.CompleteAsync`, with `OfflineRewriteFallback` when the model
is unreachable.

**This is already the hybrid architecture §6 asks for.** Deterministic layers
answer cheap questions; the LLM is gated behind a router; model output is
re-diffed and validated rather than applied. The brief's §6, §24, §26 and §45
are structurally satisfied. What is missing is measurement of whether the
*whole* chain is any good — see §5 below.

---

## 4. Measured baseline

### 4.1 Frozen benchmark

`ai/data/benchmark/ru_frozen_v1.jsonl` — 841 items, 14 categories, 5 478 tokens.

```bash
dotnet build tools/spellbench/spellbench.csproj -c Release
tools/spellbench/bin/Release/net10.0-windows/spellbench.exe
```

Reproduced exactly, this run → `ai/outputs/benchmark/audit-2026-08-12.json`:

| Metric | Value |
|---|---|
| detection precision | 0.958 |
| detection recall | 0.602 |
| detection F1 | 0.739 |
| correction accuracy | 0.529 |
| top-1 accuracy | 0.880 |
| top-3 recall | 0.916 |
| false positives / 100 clean tokens | 0.149 |
| false positives / sentence | 0.011 |
| no-change accuracy (clean+slang+profanity) | 0.977 |
| slang preservation | 0.963 |
| profanity preservation | 0.926 |
| ms/sentence mean / p50 / p95 / max | 1.73 / 0.08 / 2.54 / 172.8 |
| dictionary load | 492 ms |
| peak working set | 121.8 MB (managed heap 43.5 MB) |

Identical to the numbers in `docs/RU_LANGUAGE_INTELLIGENCE_2026-08.md`, so that
document is honest and the harness is deterministic.

### 4.2 Per category — this is where the work is

| category | n | P | R | F1 | correction | no-change |
|---|---:|---:|---:|---:|---:|---:|
| typo_simple | 70 | 1.000 | 0.971 | **0.986** | 0.900 | — |
| typo_keyboard | 35 | 1.000 | 0.971 | **0.986** | 0.886 | — |
| homoglyph | 30 | 1.000 | 1.000 | **1.000** | 0.933 | — |
| layout | 35 | 1.000 | 1.000 | **1.000** | 0.800 | — |
| proper_name | 30 | 0.967 | 0.967 | 0.967 | 0.600 | — |
| technical | 30 | 0.909 | 1.000 | 0.952 | 0.967 | — |
| modern_term | 30 | 0.966 | 0.933 | 0.949 | 0.800 | — |
| orthography | 76 | 1.000 | 0.584 | 0.738 | 0.545 | — |
| clean | 277 | — | — | — | — | 0.989 |
| slang | 46 | — | — | — | — | 0.957 |
| profanity | 26 | — | — | — | — | 0.885 |
| **morphology** | 46 | 0.000 | 0.000 | **0.000** | 0.000 | — |
| **punctuation** | 30 | 0.000 | 0.000 | **0.000** | 0.000 | — |
| **real_word** | 80 | 0.000 | 0.000 | **0.000** | 0.000 | — |

**The three zeros must not be read as "the product scores zero here."** They are
a harness limitation: `spellbench` instantiates `LocalSpellChecker` +
`SpellTextAnalyzer` directly and never invokes LanguageTool, the rule catalog,
the reranker or the LLM. A spelling pass cannot see a missing comma or a wrong
case ending by construction.

What they honestly mean is worse in a different way: **for the three categories
the brief cares most about, WriteLite currently has no end-to-end measurement at
all.** The reranker has its own numbers (0.873 pairwise accuracy int8, ~1 ms)
*(from docs)*, but nothing measures the assembled product.

Against §36's targets, the only claims I can currently defend are spelling
(0.986 F1 on simple typos, 0.880 top-1 overall) and correct-text preservation
(0.977). Punctuation, grammar and slang-in-context are **unmeasured**, and
"unmeasured" is the correct word — not "failing", and certainly not "passing".

---

## 5. Gaps, ranked by what they cost

### G1 — No end-to-end benchmark (blocks §34, §36, §37, §38)
`spellbench` measures one layer. There is no harness that runs
`HybridTextAnalysisService` (rules + LT + spell + reranker + LLM) over the frozen
corpus and reports precision/recall/FP-rate/semantic-corruption for the thing the
user actually experiences. **Without this, no claim about improvement can be
made and no fine-tuning decision can be justified.** This is the first thing to
build.

### G2 — No document-level context or entity memory (§7, §8, §32)
Verified by search: there is no entity store, no name/date/number consistency
checking, nothing that would flag «основана в 2018» against «с момента основания
в 2021». `SemanticTextChunker` (205 lines) exists and is the right seed, but it
chunks for the LT client, not for a retrieval-backed document model. The
768-token Qwen context makes this mandatory, not optional.

### G3 — Error taxonomy is narrower than the brief (§3)
`AiIssueType` has 14 members; the brief names 21. Missing and genuinely useful:
`CASE_GOVERNMENT`, `TAUTOLOGY`, `CLARITY`, `FORMALITY`, `COLLOQUIAL`, `SLANG`,
`AMBIGUITY`, `SEMANTIC_INCONSISTENCY`, `FACTUAL_INCONSISTENCY`,
`NAME_INCONSISTENCY`, `DATE_INCONSISTENCY`, `NUMBER_INCONSISTENCY`. Note
`AiIssueType` is a wire enum with explicit numbering and `Other = 14` — extend by
adding new values, never by renumbering.

`TextIssue` (the UI-facing record) is in better shape: it already carries
`Explanation`, `RuleId`, `Confidence`, `Severity` and both a coarse
`IssueCategory` and a fine `LinguisticIssueCategory`. §4 (explanations) and §25
(confidence) are structurally in place; what is untested is whether the
explanations are *good* — no test asserts explanation quality or non-vagueness.

### G4 — No semantic regression checking (§27)
`AiResultValidator` rejects excessive rewrites by length/shape. Nothing
specifically detects **negation flips**, number changes, name changes or date
changes between original and corrected text. «Я не поддерживаю» → «Я поддерживаю»
is the failure mode the brief calls critical, and there is currently no guard
aimed at it. This is cheap to build and belongs before any model change.

### G5 — Model lifecycle has no unload (§29)
Searched: no idle-timeout unload path exists. `QwenModelBackend` starts a
llama.cpp child process and keeps it until the app exits.
`QwenModelBackend.IsAvailable` also conflates "pack on disk" with "server
answering" — already flagged in `FUTURE_WORK.md`, and it is the direct cause of
the two failing tests in §1.

### G6 — Smart Actions: backend ahead of the brief, UI unverified (§9, §42)
`RewriteOperation` already covers Rewrite/ImproveStyle/Formal/Casual/Simplify/
Shorten/Expand/Grammar/Explain/Translate/Custom — a superset of the seven the
brief requires. `AiPreviewWindow` exists for preview/apply. Two shortfalls:
`RewriteResult` returns a **single** suggestion where §11 asks for multiple
variants, and I have not yet verified the toolbar/loading/cancel/undo behaviour
against §42 — that needs a run of the app, not a code read.

### G7 — Golden set is not frozen in any enforceable sense (§35)
`ru_frozen_v1.meta.json` records
`sha256 = 16adfd7c…91293d`, but the file on disk hashes to `392f60ce…39ef7`.
Diagnosed: **not corruption** — the manifest hash was taken over LF content and
the working copy is CRLF (841 CRLF pairs; normalising to LF reproduces
`16adfd7c…` exactly). Two real problems remain:
1. Nothing *enforces* the hash — no test asserts it, and the two conventions
   silently disagree.
2. `ai/data/benchmark/ru_frozen_v1.jsonl`, its generator `_gen/`, and the whole
   of `ai/data/errors/` (train/validation/test, 24 MB) are **untracked in git** —
   not ignored, just never committed. The repository has exactly one commit and
   ~30 modified files. The "frozen golden set" currently exists only on this
   machine.

### G8 — `SQLitePCLRaw.lib.e_sqlite3` 2.1.10 has a known high-severity advisory
`GHSA-2m69-gcr7-jv3q`, surfaced on every build. Needs a version bump and a
regression run.

### G9 — Dead artefacts
`models/writelight-qwen-polish-staging/` (2.3 GB, unreferenced),
`models/writelight-gec/` (README only), a stray empty `ReaderView.xaml.cs` at the
repo root, and `qwen_server.pid`.

---

## 6. Strengths worth protecting

Listed because §1 says not to replace working infrastructure blindly, and
several of these would be easy to lose by accident.

1. **The DAFSA form index.** 3.09 M forms in 4.7 MB with a memory-mapped
   metadata array, and `verify_ru_form_index.py` independently checks
   enumeration order, reachability, id-equals-rank and edit distances against a
   separate Damerau-Levenshtein implementation. SymSpell was rejected on
   arithmetic *(from docs)* and that reasoning holds.
2. **False-positive discipline.** 0.149 FP per 100 clean tokens, 0.977 no-change
   accuracy, 0.963 slang preservation, 0.926 profanity preservation. This is the
   hardest property to earn and the easiest to destroy by adding an eager LLM
   layer. Any change that moves these down is a regression regardless of what it
   improves.
3. **The reranker cannot rewrite text.** Binary classifier by construction.
4. **Offsets are never trusted from the model.** `TextDiffBuilder` recomputes
   them; `AiResultValidator` gates the result.
5. **Privacy is tested, not asserted.** `PrivacyScanTests`,
   `WriteLiteLoopbackGuard`, and a test that `WriteLite.Language.Russian`
   references no networking assembly.
6. **Provenance is enforced by a test**, not by a README.

---

## 7. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Untracked benchmark + training data (G7) | Total loss of reproducibility if this machine dies | Commit `ru_frozen_v1.*`, `_gen/`, and `ai/data/errors/` **before** any further work |
| Adding an LLM layer to correction raises false positives | Destroys the product's best property | End-to-end harness (G1) must land first and gate every change |
| 768-token context | Silent truncation on real documents | Chunking + retrieval (G2) before any long-document claim |
| Semantic inversion (G4) | Critical, user-invisible | Dedicated negation/number/name/date guard + tests |
| `SQLitePCLRaw` advisory (G8) | Security | Bump + regression run |
| Fine-tuning without a measured deficit | Wasted days, §38 violation | Do not train until G1 produces per-category numbers for the full pipeline |

---

## 8. Recommended plan

Ordered so that every later step is measurable by an earlier one. Steps 1–3 are
prerequisites for any statement about quality; step 7 is explicitly gated.

1. **Protect what exists.** Commit the untracked benchmark, generator and
   training splits. Add a test asserting the frozen corpus hash under a single
   stated normalisation (LF), so G7 can never recur silently.
2. **Fix the two failing tests** by pointing the backend at an empty model
   directory, and split `IsAvailable` into `IsInstalled` / `IsReadyAsync` (G5),
   which is the actual root cause.
3. **Build the end-to-end harness** (G1) — `langbench`, running
   `HybridTextAnalysisService` over `ru_frozen_v1`, reporting per-category
   precision/recall/F1, FP rate, no-change accuracy, semantic-corruption rate,
   latency and RAM to `benchmarks/results/*.json`. This produces the first
   honest punctuation/morphology/real-word numbers the project has ever had.
4. **Semantic regression guard** (G4) — negation, numbers, names, dates,
   quantities — wired into `AiResultValidator` and into the harness as a
   first-class metric.
5. **Extend the taxonomy** (G3) additively, and add tests that every emitted
   correction carries a non-vague, non-empty Russian explanation.
6. **Document context engine** (G2) — chunking, entity/date/number memory,
   retrieval into the 768-token window, with the consistency checks from §7
   emitting the new `*_INCONSISTENCY` types at *suggestion* severity, never as
   automatic rewrites.
7. **Only then** decide about training. Step 3 will say which categories are
   genuinely weak end-to-end. If the weakness is punctuation, the cheapest fix
   is likely LanguageTool rule tuning plus targeted rules, not a fine-tune. If
   it is real-word/context, a fine-tune or a stronger reranker is justified —
   and `docs/training-readiness-report.md` (§56) gets written first, with the
   measured deficit as its justification.

The brief asks for `benchmarks/writelite-language-benchmark/`. I recommend the
harness live at `tools/langbench/` alongside the existing `spellbench`/`lexbench`
and write results to `benchmarks/results/`, reusing `ai/data/benchmark/ru_frozen_v1.jsonl`
rather than creating a second corpus. Two corpora would guarantee divergence, and
the existing one is already 841 items across the categories the brief names.

---

## 9. What Phase 1 did not cover

Stated so the gaps in the audit itself are visible:

- **UI behaviour was read, not run.** §42/§43 (toolbar, loading state, cancel,
  preview, apply, undo; correction card showing original/replacement/reason/
  category) were verified as code paths, not as observed behaviour.
- **Long-document performance (§49) is unmeasured.** No 10/50/100-page timings
  exist. `FUTURE_WORK.md` records that a 1.5 MB import takes "several seconds"
  off-dispatcher, but that is the reader, not the language pipeline.
- **Explanation quality (§4) is unassessed.** The field exists and is populated;
  whether the text is linguistically accurate and useful has not been judged.
- **The LLM's own correction quality is unmeasured** — that is exactly G1.
- **`writelight-lexical.db` (152 MB) coverage was not profiled.** I confirmed it
  loads and is queried; I did not measure how often lookups miss.
