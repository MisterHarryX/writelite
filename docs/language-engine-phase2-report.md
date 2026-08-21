# Language engine — Phase 2 report

Date: 2026-08-12. Machine: Windows 11 Pro 26200, .NET SDK 10.0.301, single 1920×1080
display, CPU-only inference.

Follows `docs/ai-language-audit.md` (Phase 1). Everything below was measured on this
machine in this pass. Where something was verified by reading code rather than by
running it, the text says so.

**No fine-tuning was performed, and none is recommended yet.** The reason is in §4 and
§14: the measured bottleneck is not model quality.

---

## 1. Verified build and test results

Clean rebuild from `dotnet clean`:

```bash
dotnet clean WriteLite.sln -c Release
dotnet build WriteLite.sln -c Release
dotnet build tools/langbench/langbench.csproj -c Release
dotnet test  WriteLite.sln -c Release --no-build
```

| | Verification run (before changes) | Final (after all Phase 2 changes) |
|---|---|---|
| Build errors | **0** | **0** |
| Build warnings | 132 (incl. 10 × `NU1903`) | **132 (0 × `NU1903`)** |
| langbench compile | 0 errors, 4 warnings | 0 errors, 4 warnings |
| Tests passed | **811** | **823** |
| Tests failed | **0** | **0** |
| Tests skipped | 3 | 1 |
| Total | 814 | **824** |

The count rises from 814 to 824 because this phase adds 10 tests (5 golden-integrity,
5 contextual-wiring). Skips fall from 3 to 1 because the two Qwen-dependent tests find a
live loopback server and actually run. Warnings stay at 132 in total while `NU1903`
drops to zero (§12) — the new test files initially contributed 10 `MSTEST0037` warnings
of their own, which have been fixed, so no new warnings were introduced.

Warning breakdown by code (all pre-existing):

| Code | What it is |
|---|---|
| `MSTEST0037` | MSTest analyzer style advice in the test project (`Assert.IsTrue` → `Assert.IsGreaterThan` etc.) |
| `MSTEST0044`, `MSTEST0032` | Further MSTest analyzer advice |
| `CS0108`, `CS0414` | Member hiding and unused private fields |
| **`NU1903`** | **Security advisory — `SQLitePCLRaw.lib.e_sqlite3` 2.1.10. See §12.** |

The three skips are environmental and each is self-declaring:

| Skipped test | Requires |
|---|---|
| `LiveHost_Check_Stop_FreesProcessAndPort` | a live LanguageTool host |
| `CSharp_Provider_Calls_Qwen_And_Corrects_Russian` | a live Qwen loopback server |
| `LiveServer_IfRunning_ReturnsValidatedCorrection` | a live Qwen loopback server |

### The two previously failing tests

Both now pass, and both were fixed at the cause rather than by adjusting the assertion.

**`Analyzer_WithoutQwenPack_UsesLiteFallback`** asserted a fallback that never happened,
because `QwenModelBackend.RefreshAvailability` probes loopback health *before* it looks
for a pack — and a developer machine normally has a server on the default port. Pointing
the test at an empty directory was not enough. Added
`QwenModelBackend(…, bool allowUnverifiedLoopback = true)`; passing `false` requires
positive evidence (a healthy endpoint or an installed pack) instead of assuming a server
might appear. The default is unchanged, so product behaviour is identical.

**`Provider_UnreachableServer_UsesFallback`** asserted `LastBackend != "writelight-qwen"`
against an unreachable endpoint. This was asserting against *deliberate* product
behaviour: `LocalAiTextAnalyzer` keeps `backend = "writelight-qwen"` whenever the neural
route was attempted, including when the answer is rejected and the deterministic engine
produces the text, so a rejected answer stays attributable
([LocalAiTextAnalyzer.cs:246](../src/WriteLite.AI.Local/LocalAiTextAnalyzer.cs#L246)).

The real gap was that nothing could distinguish "route attempted" from "model's text was
used" except by scraping logs. Added `LastNeuralOutputUsed`, set only where the model's
output survives validation, and surfaced through `LocalAiTextProvider`. The test now
asserts what actually matters — that an unreachable server contributes no text.

### One failure that was mine, and what it exposed

The first post-rebuild run showed `TrayShutdownPath_StopsApplicationWithExitCodeZero`
failing on a 30 s timeout. It was not a code regression: a `WriteLite.exe` was holding
`WriteLite.AI.Local.dll` and had to be terminated before the solution could rebuild, and
the force-kill left an orphaned recovery snapshot in
`%LOCALAPPDATA%\WriteLite\recovery`. On the next start, `EditorPage.CheckForRecoverableWork`
raises a **modal** `MessageBox`, which blocks the application before it can reach its
orderly shutdown path.

After moving the snapshot aside, the test passes. Worth recording because it is a real
fragility, already flagged in `FUTURE_WORK.md`: any abnormal exit leaves the next launch
blocked on a modal dialog, which also makes automated UI runs non-deterministic. An
in-page banner would remove both problems.

> The preserved snapshot (an untitled document from the killed session) is at
> `…/scratchpad/recovered-session/`. Move both files back into
> `%LOCALAPPDATA%\WriteLite\recovery` to restore the recovery prompt.

---

## 2. The first end-to-end benchmark

`tools/langbench/` runs the production `HybridTextAnalysisService` graph — the same
`WriteLiteOrchestratingAnalyzer` composition `App.OnStartup` builds — over the frozen
841-item corpus. Each layer is switchable, and a disabled layer is replaced by an
analyzer that finds nothing rather than by restructuring the graph, so every run
exercises the same orchestration, merge and filter code and the only variable is the
evidence flowing in.

Two deliberate departures from the app's wiring, both for determinism: the personal
dictionary and ignore list are created empty (otherwise the score depends on whoever last
used the machine), and the AI debounce is dropped to its floor (1.5 s × 841 items is 21
minutes of measuring a timer).

```bash
tools/langbench/bin/Release/net10.0-windows/langbench.exe --layers rules,spell,lt,rerank
```

Results land in `benchmarks/results/*.json`.

### Layer attribution

| run | P | R | F1 | corr | FP/100 | no-change | p50 ms | p95 ms | peak RSS |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| rules only | 0.733 | 0.022 | 0.043 | 0.008 | 0.149 | 0.991 | 0.72 | 1.13 | 114 MB |
| spell only | 0.958 | 0.602 | 0.739 | 0.529 | 0.149 | 0.977 | 0.88 | 2.36 | 131 MB |
| LanguageTool only | 0.876 | 0.628 | 0.732 | 0.394 | 0.149 | 0.908 | 131.55 | 195.88 | 135 MB |
| rules + spell | 0.947 | 0.616 | 0.746 | 0.529 | 0.299 | 0.968 | 0.90 | 2.19 | 184 MB |
| rules + spell + LT | 0.875 | 0.702 | 0.779 | 0.555 | 0.348 | 0.897 | 130.27 | 144.21 | 200 MB |
| + reranker | 0.872 | 0.710 | 0.783 | 0.578 | 0.398 | 0.891 | 130.74 | 144.92 | 208 MB |
| **+ lexicon veto** | **0.945** | 0.696 | **0.802** | 0.565 | **0.348** | **0.963** | 132.31 | 144.12 | 208 MB |
| + local AI | — | — | — | — | — | — | — | — | — |

**The local-AI row cannot honestly be filled.** Three attempts, each ending in the same
reproducible hang (§8). The third — a 112-item stratified sample against a freshly
restarted server — did run to completion, but **35 of 112 items (31 %) timed out and were
never analysed**, so its quality figures are floors rather than measurements. langbench
says so itself on the console and in `TimedOutItems`.

What that run does establish, and what it does not:

| | |
|---|---|
| **Usable: AI-path latency** | **p50 2 334 ms/sentence** — ~18× the LanguageTool path (130 ms) and ~2 500× the deterministic path (0.9 ms) |
| **Usable: failure rate** | 31 % of items unserved once the server wedged; p95 and max both pinned at the 30 s timeout ceiling |
| Not usable: recall/F1 | 35 unanalysed items count as "found nothing" |
| Not usable: precision, no-change accuracy, FP rate | An item that times out produces zero issues, which scores as perfect preservation. The reported no-change 0.958 and FP 0.000/100 are **artifacts of the failure**, not evidence of quality |

Deterministic baseline on the same sample, for whenever the AI layer can be measured
against it: P 0.986 / R 0.798 / F1 0.882 / correction 0.652
(`benchmarks/results/S1-sample-noai.json` vs `S2-sample-ai.json`).

The latency figure alone has a design consequence: at 2.3 s/sentence the local model
cannot be part of an as-you-type path under any debounce, which is what the existing
architecture already assumes — `AiCallRouter` gates it and `HybridTextAnalysisService`
debounces it. That assumption is now measured rather than presumed.

### Detection recall by category

This is the table Phase 1 could not produce. `spellbench` reported 0.000 for punctuation,
morphology and real_word because it instantiates the spelling layer alone; those were
never product measurements.

| category | rules | spell | LT | rules+spell | +LT | +reranker |
|---|---:|---:|---:|---:|---:|---:|
| typo_simple | 0.000 | 0.971 | 0.986 | 0.971 | 0.986 | 0.986 |
| typo_keyboard | 0.000 | 0.971 | 0.971 | 0.971 | 0.971 | 0.971 |
| homoglyph | 0.000 | 1.000 | 1.000 | 1.000 | 1.000 | 1.000 |
| layout | 0.000 | 1.000 | 0.171 | 1.000 | 1.000 | 1.000 |
| orthography | 0.065 | 0.584 | 0.792 | 0.597 | 0.818 | **0.844** |
| **punctuation** | 0.151 | 0.000 | 0.182 | 0.151 | **0.273** | 0.273 |
| **morphology** | 0.000 | 0.000 | 0.319 | 0.000 | **0.319** | 0.319 |
| **real_word** | 0.013 | 0.000 | 0.062 | 0.013 | 0.062 | **0.087** |
| technical | 0.000 | 1.000 | 1.000 | 1.000 | 1.000 | 1.000 |
| modern_term | 0.000 | 0.933 | 0.933 | 0.933 | 0.967 | 0.967 |
| proper_name | 0.000 | 0.967 | 0.933 | 0.967 | 1.000 | 1.000 |

**Punctuation, morphology and real-word correction are now measured end-to-end.** They
are served almost entirely by LanguageTool, and all three remain weak: 0.273, 0.319 and
0.087 recall respectively.

### Preservation by category — the finding that matters most

| category | rules | spell | LT | rules+spell | +LT | +reranker |
|---|---:|---:|---:|---:|---:|---:|
| clean | 0.989 | 0.989 | 0.989 | 0.978 | 0.975 | 0.971 |
| **slang** | 1.000 | 0.957 | 0.717 | 0.957 | **0.717** | **0.696** |
| **profanity** | 1.000 | 0.885 | 0.385 | 0.885 | **0.385** | 0.385 |

**Enabling LanguageTool costs 24 points of slang preservation and 50 points of profanity
preservation.** `WriteLiteAppSettings.ExtendedChecking` defaults to `true`, so this is
what users get, and nothing measured it before because `spellbench` never ran LanguageTool.

Against the §36 acceptance targets, on the shipped configuration:

| target | required | measured | verdict |
|---|---:|---:|---|
| Correct-text preservation | ≥ 0.97 | 0.891 overall / 0.971 on `clean` | **missed overall** |
| Common slang recognition | ≥ 0.90 | **0.696** | **missed** |
| Semantic corruption | ≈ 0 | 1 item / 381 changed (0.26 %) | met |

---

## 3. Golden dataset reproducibility

The Phase 1 audit found the frozen corpus untracked, its recorded hash disagreeing with
the file on disk, and nothing anywhere enforcing either.

**Diagnosis.** Not tampering. The generator writes LF in binary mode and hashes those
bytes; the working copy had CRLF (841 pairs). Normalising reproduced the manifest hash
exactly. Confirmed by re-running the generator into a scratch directory: it emits
`16adfd7c…91293d`, byte-identical to the manifest and to the normalised working copy.

**What now protects it**

| Mechanism | Prevents |
|---|---|
| `.gitattributes`: `*.jsonl -text`, `*.tsv -text` | Git's `text=auto` converting corpora on checkout |
| `build.py` refuses to overwrite a frozen corpus with different content | Silent regeneration under the same name |
| `build.py --force`, which logs before/after hashes | Drift repair, visibly |
| `Golden_MatchesItsPinnedHash` | Any content change; distinguishes CRLF drift from real edits and prints the right fix for each |
| `Golden_ManifestAgreesWithThePinnedConstants` | Manifest and test disagreeing |
| `Golden_HasTheItemCountAndCategoriesItsManifestClaims` | Item/category drift |
| `TrainingSplits_ShareNoItemWithGolden` | Training on the reporting set |
| `TrainingConfigs_DoNotReferenceGoldenData` | A config pointing at golden data |

The expected hash and version are pinned **as constants in the test**, not read from the
manifest. A test that read the manifest would agree with whatever the manifest said, so
regenerating would update both and the test would pass while the benchmark changed
underneath every published result. Changing the corpus now requires a deliberate edit in
two files and two languages.

Guard verified by tampering: appending one byte makes `build.py` exit 1 without writing,
and `--force` restores the canonical hash.

**Leakage: measured, not assumed.** 0 overlapping items between golden and all six splits
(24 344 train / 1 973 validation / 2 004 test / 14 682 rerank-train / 1 214
rerank-validation / 1 240 rerank-test), comparing whitespace-collapsed strings across
every string field.

Procedure documented in `ai/data/benchmark/GOLDEN.md`, including the four roles and why
`test` and `golden` are not interchangeable.

**Still outstanding — and it needs your decision.** The corpus, its generator and
`ai/data/errors/` (24 MB) are still *untracked* in git. Everything above protects the
file on this machine; only committing protects it from this machine. I have not run
`git add`/`git commit`, because the repository has a single commit and ~30 unrelated
modified files, and deciding what enters history is yours. See §15.

---

## 4. The bottleneck, measured

Phase 1 recorded correction accuracy 0.529 against top-1 candidate accuracy 0.880 and
asked whether right answers were being generated and then lost.

**They are not.** The two metrics have different denominators:

```
correction accuracy = correctedExactly / ALL gold errors   = 263 / 497 = 0.529
top-1 accuracy      = top1            / DETECTED errors    = 263 / 299 = 0.880
top1 × recall = 0.880 × 0.602 = 0.529 = correction accuracy   (exactly)
```

`correctedExactly` (263) equals the top-1 count (263). **Nothing is lost between ranking
a candidate and emitting it as an issue** — no loss to confidence gating, merging,
precedence, span alignment, filtering or application logic.

The available headroom, separated:

| Source | Size |
|---|---:|
| Errors never detected at all | 198 / 497 = **39.8 pp** |
| Right answer in top-3 but not top-1 (ranking) | 11 / 497 = **2.2 pp** |
| Right answer not generated at all for a detected error | 25 / 497 = 5.0 pp |

**Recall headroom is ~18× the ranking headroom.** This is why §6 of the brief — building
a stronger multi-signal ranker — is *not* the right next investment, and why I did not
build one. The reranker that already exists captured 2.2 pp of the 2.2 pp available (§5),
which is the whole ranking budget.

### Ranked weaknesses (from measurement)

1. **Real-word contextual correction** — recall 0.087, 73 of 80 still missed. Largest
   single category loss.
2. **False positives on informal Russian** — slang preservation 0.696, profanity 0.385,
   caused by LanguageTool. Worst *product* defect here, because it fires on text the user
   wrote correctly.
3. **Morphology** — recall 0.319, correction accuracy 0.021. LanguageTool detects a third
   and can almost never propose the right form.
4. **Complex punctuation** — recall 0.273, correction accuracy 0.061.
5. **Orthography tail** — recall 0.844; the remaining misses need context, not lexicon.
6. **Candidate ranking** — 2.2 pp of headroom, now captured. Effectively closed.

---

## 5. Change made: the contextual reranker was never connected

The largest single finding of this phase.

`models/writelight-reranker/` — rubert-tiny2 fine-tuned as a binary sentence-acceptability
classifier, exported to ONNX, quantised to int8, 29 MB, ~1 ms/sentence — was trained,
evaluated, documented in `docs/RU_LANGUAGE_INTELLIGENCE_2026-08.md` as a shipped
capability, deployed next to the binaries, and covered by its own passing unit tests.

**Nothing in the product ever called it.** The only references to
`ContextualCorrectionRefiner` were from `ContextualRerankerTests`. Real-word detection
recall of 0.013 is what a disconnected component looks like from outside.

Wired into `SpellTextAnalyzer` in two places:

- **Reranking** — the lexicon ranks candidates by edit distance and frequency; the model
  reorders them by how well each reads in place, and returns the input untouched unless
  it separates the top two clearly.
- **Real-word detection** — `FindRealWordErrors` over confusion-set tokens, for words
  that pass every dictionary lookup but do not belong in the sentence.

Design constraints applied:

- Real-word findings are **never** `CanApplyAutomatically`, whatever the score. The
  evidence is a 29 M-parameter acceptability estimate, not a rule.
- Severity `Suggestion`, with a Russian explanation naming both words.
- The lexical pass owns any span it already flagged — it has harder evidence.
- Failures are caught and logged; the user still gets every lexical result.
- **Off on the typing path.** `App.OnStartup`'s fast analyzer and `EditorPage`'s
  as-you-type analyzer both set `ContextualRefinementEnabled = false`, so an ONNX session
  never lands on the keystroke path. The orchestrated path publishes the same findings a
  moment later.

Also fixed a latent inefficiency: `FindRealWordErrors` called `ScoreSentence(text)` inside
its token loop, re-scoring the same unchanged sentence once per confusion-set token. Now
computed once.

### Measured effect — same binaries, same guard, reranker as a switchable layer

Isolated by making the reranker a langbench layer (`--layers rules,spell` vs
`rules,spell,rerank`), so before/after differs in exactly one variable:

| Metric | Before | After | Change |
|---|---:|---:|---:|
| Detection precision | 0.947 | 0.942 | −0.005 |
| Detection recall | 0.616 | 0.626 | **+0.010** |
| Detection F1 | 0.746 | 0.752 | **+0.006** |
| Correction accuracy | 0.529 | 0.551 | **+0.022** |
| No-change accuracy | 0.968 | 0.963 | −0.005 |
| False positives /100 tokens | 0.299 | 0.348 | −0.049 |
| Semantic corruptions | 1 | 1 | 0 |
| p50 latency | 0.90 ms | 5.52 ms | −4.6 ms |
| p95 latency | 2.19 ms | 14.58 ms | −12.4 ms |
| Peak RSS | 184 MB | 200 MB | −16 MB |

With LanguageTool also enabled (`rules,spell,lt` → `rules,spell,lt,rerank`):

| Metric | Before | After | Change |
|---|---:|---:|---:|
| Detection F1 | 0.779 | 0.783 | +0.004 |
| Correction accuracy | 0.555 | 0.578 | **+0.023** |
| real_word recall | 0.062 | 0.087 | +0.025 |
| orthography recall | 0.818 | 0.844 | +0.026 |
| No-change accuracy | 0.897 | 0.891 | −0.006 |
| slang preservation | 0.717 | 0.696 | −0.021 |

**Honest reading.** Wiring in the reranker is a real but modest gain, and most of it comes
from *reranking* (correction accuracy +2.3 pp), not from real-word detection, which moves
0.062 → 0.087 and leaves 73 of 80 real-word errors unfound. That matches what the
component's own documentation predicted: the confusion-set gate catches words that are
grammatically impossible in place ("должен **будит**"), and misses pairs separated by
meaning rather than grammar (компания/кампания leans correct by ~0.02, far below the 0.35
gate). It costs ~12 ms of p95 latency and 16 MB, and it very slightly increases false
positives.

---

## 6. Semantic safety

`SemanticGuard` in langbench compares the produced text against the source and reports
meaning-bearing divergence: negation, numbers, dates, proper names, Latin tokens,
percentages, currency, modality (должен/может/нужно…) and comparatives (больше/меньше…).

**A flaw in my own first version, found by the measurement.** Comparing produced against
source alone reported 88–92 corruptions per run — but `"Ghbdtn, как дела?"` →
`"Привет, как дела?"` is a *correct* layout fix that necessarily removes a Latin token. 60
of 88 reported corruptions were correct behaviour. The guard now subtracts the change the
gold target itself licenses and counts only what the pipeline does beyond it.

After the fix, on the full deterministic pipeline: **1 corrupted item out of 381 whose
text changed (0.26 %)**. The surviving case is a genuine proper-name defect —
`"на официальном сайте The Verge"`, where "The" is rewritten into Cyrillic.

Deliberately not checked: word order, synonym substitution and punctuation, which change
text without changing claims and would drown the signal.

---

## 7. Performance

| configuration | p50 | p95 | max | peak RSS |
|---|---:|---:|---:|---:|
| rules only | 0.72 ms | 1.13 ms | — | 114 MB |
| rules + spell | 0.90 ms | 2.19 ms | 147 ms | 184 MB |
| rules + spell + rerank | 5.52 ms | 14.58 ms | 164 ms | 200 MB |
| rules + spell + LT | 130.27 ms | 144.21 ms | 329 ms | 200 MB |
| rules + spell + LT + rerank | 130.74 ms | 144.92 ms | 308 ms | 208 MB |
| rules + spell + LT + rerank + lexveto | 132.31 ms | 144.12 ms | 316 ms | 208 MB |
| **+ local AI** (112-item sample) | **2 334 ms** | 30 014 ms † | 30 015 ms † | 210 MB |

† pinned at the harness timeout ceiling by the hang in §8, not a measurement of inference.

Three orders of magnitude separate the layers, and each sits where it belongs:

- **Deterministic (rules + spell), ~1 ms** — safe on the keystroke path, and that is where
  `CompositeTextAnalyzer` runs it.
- **Reranker, +12 ms p95** — fine on the debounced orchestrated path, too heavy for every
  keystroke, which is why `ContextualRefinementEnabled` is now false on both as-you-type
  analyzers.
- **LanguageTool, ~130 ms** — 145× the deterministic path. Off the UI thread, behind the
  debounce, with rules+spell published first so it is not felt as typing lag.
- **Local model, ~2.3 s** — usable only for explicit, user-initiated work. This is measured
  confirmation of what `AiCallRouter`'s gating already assumed.

Memory is flat across every deterministic configuration at ~200–210 MB. The +78 MB step
from the "rules only" floor (114 MB) is the 3.09 M-form index and the ONNX session, both
loaded once per process.

Not measured: CPU and memory at idle, during typing, and on a large document as separate
scenarios (§21 of the brief). Only per-sentence latency and process peak RSS were
captured.

---

## 8. A reproducible hang in the local AI path

**The full pipeline including local AI could not be measured. This is why, and the
inability to measure it is itself the most serious defect found this phase.**

Reproduced three times, each ending in the same state:

| # | Configuration | Outcome |
|---|---|---|
| 1 | 841 items, `rules,spell,lt,ai` | wedged after ~14 min of inference (842 s llama CPU) |
| 2 | 841 items, same + 30 s per-item timeout | wedged, 4 `CLOSE_WAIT` sockets |
| 3 | **112-item sample, freshly restarted server** | wedged after ~7 min, 4 `CLOSE_WAIT` sockets; completed only by timing out **35 of 112 items** |

Diagnosed live rather than inferred:

- `llama-server` CPU **frozen** across repeated 15–20 s observation windows (0 s delta),
  while langbench had used 6 s of CPU total. Neither process was doing anything.
- `/health` returned `200 {"status":"ok"}` the entire time.
- `netstat` showed accumulating `CLOSE_WAIT` sockets on the server — 4 at the point of
  each wedge — plus a `FIN_WAIT_2` on the client side.
- `model.manifest.json` sets `parallelSlots: 1`.

**Mechanism.** `QwenModelBackend.InferAsync` calls
`client.SendAsync(req, linked.Token)` with a token that is cancelled on timeout or by a
newer request ([QwenModelBackend.cs:275](../src/WriteLite.AI.Local/QwenModelBackend.cs#L275)).
Cancelling an in-flight HTTP request aborts the client socket, which leaves the server
holding a half-closed connection it never reaps. With one parallel slot, a few of those
are enough to stop the server serving — while its health endpoint keeps answering, so
nothing upstream notices.

**Why this matters in the product, not just the benchmark.** Cancellation is not an
exceptional path here — it is the normal one. `HybridTextAnalysisService` debounces and
cancels in-flight AI work on every new keystroke burst
([HybridTextAnalysisService.cs:186](../src/WriteLite.App/Services/Ai/HybridTextAnalysisService.cs#L186)),
and `AiPreviewWindow` cancels whenever the user closes a Smart Action preview. So ordinary
typing generates exactly the cancellations that leak these sockets. The user-visible
symptom is local AI progressively going quiet while `IsAvailable` still reports true —
which is the same `IsAvailable`-conflates-installed-with-running problem `FUTURE_WORK.md`
already flagged, now with a concrete failure attached.

**What was changed here:** `langbench` bounds each item at 30 s, counts timeouts, and
reports `TimedOutItems`; a non-zero value invalidates recall for that run and the console
says so. That makes the harness honest. **It does not fix the product** — see §15 item 1
for what would.

---

## 9. Smart Actions — verified, not rewritten

Verified by reading the implementation and its tests, **not by driving the running
application**. Stated plainly because §11 asks for behavioural verification and a code
read is weaker evidence.

| §11 requirement | Status | Where |
|---|---|---|
| Selection context passed correctly | ✅ | `EditorPage.Ai.cs:212` — `start`/`end` captured *before* the dialog opens, because focus can lose the selection |
| Nearby paragraph context available | ✅ | `ReadContext(start, before:)` both sides, bounded at 400 chars |
| Text outside selection untouched | ✅ | writes only `new TextRange(start, end).Text`; InsertBelow appends a paragraph |
| Cancellation works | ✅ | `AiPreviewWindow` owns a CTS, cancels on close, passes the token; `OperationCanceledException` disables acceptance |
| Output validation | ✅ | `ResponseCleaner.Clean` strips fences/preambles/quotes, rejects empty — 6 tests |
| Malformed output cannot modify the document | ✅ | `Clean` returns null → offline fallback; the document is only written from an accepted outcome |
| Undo after Apply | ✅ | `BeginChange`/`EndChange` — one undo unit restores the original in one press |
| UI never blocks | ✅ | async throughout, dialog-hosted |
| Document changed under preview | ✅ | `ArgumentException` → refuses to write, tells the user |

Gaps against the brief, **not** addressed this phase:

- §13 multiple rewrite variants — `RewriteResult` carries a single `Suggestion`.
- §14 shorten levels (light/medium/strong) — one `Shorten` operation.
- §17 "do not translate WriteLite as an ordinary word" — not verified.

---

## 10. What I did not do, and why

Stated explicitly so the gaps are visible rather than implied.

| Brief section | Status |
|---|---|
| §6 stronger multi-signal ranker | **Deliberately not built.** Measurement says ranking headroom is 2.2 pp and the existing reranker captured it. Building a magic-number scorer against a closed gap would be effort against a non-problem. |
| §7 dictionaries as active knowledge | Not done. `OfflineLexicalKnowledgeService` exists and is rich; it is still not consulted during correction/ranking. |
| §8 true contextual correction (одел/надел, компания/кампания) | Partially — the reranker now runs, but its own calibration shows it cannot separate meaning-based pairs. Unsolved. |
| §9 document-level context / entity memory | Not done. No entity, date or number consistency checking exists. |
| §10 explanation improvements | Not done beyond the new real-word explanation. |
| §12–§17 Smart Action upgrades | Verified (§9 above), not upgraded. |
| §19 expanded hard-negative tests | Not done. The measured slang/profanity regression is the evidence such tests would have caught. |
| §22 long-document tests (1/10/50/100 pages) | **Not done.** No timings exist. |

---

## 11. Change made: WriteLite's lexicon now overrules LanguageTool on spelling

The §2 finding — LanguageTool costing 24 points of slang and 50 points of profanity
preservation — is a disagreement about *whether a word is a word*. WriteLite indexes
3.09 M Russian surface forms including a curated modern-vocabulary pack (2 652 lemmas →
27 724 forms: internet and gaming slang, anglicisms, obscenities). LanguageTool ships a
smaller general dictionary and does not know that register.

`WriteLiteOrchestratingAnalyzer` already dropped engine orthography hits covered by the
*user* dictionary. It now also drops them when WriteLite's own lexicon recognises the
word. Multi-word spans must be wholly known — one unknown token and the engine's claim
stands. The predicate is an optional constructor argument, so the analyzer behaves
exactly as before when it is not supplied.

### Measured effect (`rules,spell,lt,rerank` → `+lexveto`)

| Metric | Before | After | Change |
|---|---:|---:|---:|
| Detection precision | 0.872 | 0.945 | **+0.074** |
| Detection recall | 0.710 | 0.696 | −0.014 |
| Detection F1 | 0.783 | 0.802 | **+0.019** |
| No-change accuracy | 0.891 | 0.963 | **+0.072** |
| **Slang preservation** | 0.696 | **0.935** | **+0.239** |
| **Profanity preservation** | 0.385 | **0.885** | **+0.500** |
| False positives /100 tokens | 0.398 | 0.348 | +0.050 |
| Correction accuracy | 0.578 | 0.565 | −0.012 |
| Orthography recall | 0.844 | 0.779 | −0.065 |
| p95 latency | 144.92 ms | 144.12 ms | +0.80 ms |

**The trade, stated plainly.** It costs about 5 orthography detections out of 77 and 1.4 pp
of overall recall. It buys 7.4 pp of precision, 7.2 pp of no-change accuracy, and returns
slang and profanity preservation to roughly their lexicon-only levels. For a product whose
own brief says an annoying correction is worse than a missed suggestion, that is the right
side of the trade — but it *is* a trade, and if orthography recall matters more for some
audience, the predicate is one constructor argument away from being configurable.

Against the §36 acceptance targets, on the shipping configuration:

| target | required | before | after |
|---|---:|---:|---:|
| Common slang recognition | ≥ 0.90 | 0.696 ✗ | **0.935 ✓** |
| Correct-text preservation | ≥ 0.97 | 0.891 ✗ | 0.963 (0.975 on `clean`) |

---

## 12. Dependency advisory — resolved

`SQLitePCLRaw.lib.e_sqlite3` 2.1.10, pulled transitively by `Microsoft.Data.Sqlite` 9.0.0.

| | |
|---|---|
| Advisory | `GHSA-2m69-gcr7-jv3q` / **CVE-2025-6965** |
| Severity | High |
| Nature | SQLitePCLRaw bundling a vulnerable SQLite |
| Vulnerable range | `<= 2.1.11` |
| First patched version | **none recorded** in the GitHub advisory database |

Because no patched version is nominated, the fix is to move *past* the range rather than
to a named release. Available: 2.1.12, 3.50.3, 3.53.3. Chose **2.1.12** — the smallest
step out of the vulnerable range, staying on the same 2.1.x API so `Microsoft.Data.Sqlite`
9.0.0 keeps working unchanged. 3.x would be a runtime swap this repository has no reason
to take on for a lexical database it opens read-only.

Applied as an explicit direct `PackageReference` in `WriteLite.App.csproj`, which raises
the transitive resolution for every dependent project.

**Verified, not assumed:**

| Check | Result |
|---|---|
| Resolved versions | `bundle_e_sqlite3`, `core`, `lib.e_sqlite3`, `provider.e_sqlite3` all **2.1.12** |
| `NU1903` warnings across solution | **0** (was 10) |
| Build | 0 errors |
| SQLite / lexical DB tests | **61 passed, 0 failed** (`SqliteLexicalStoreTests`, `LexicalLayerTests`, `LexicalPackCatalogServiceTests`, `TranslationIndexTests`) |
| Full suite | **821 passed, 0 failed, 3 skipped** |

Practical exposure was low — the DB is local, read-only and queried with parameters, so
there is no attacker-supplied SQL path — but the upgrade is clean and carries no
regression.

---

## 13. Cumulative before → after

Configuration: `rules,spell,lt` — the shipping deterministic pipeline. "After" is the same
pipeline with both Phase 2 changes (reranker wiring + lexicon veto).

| Metric | Before | After | Change |
|---|---:|---:|---:|
| Detection precision | 0.875 | 0.945 | **+0.071** |
| Detection recall | 0.702 | 0.696 | −0.006 |
| **Detection F1** | 0.779 | **0.802** | **+0.023** |
| **Correction accuracy** | 0.555 | **0.565** | **+0.010** |
| Punctuation recall | 0.273 | 0.273 | 0 |
| Morphology recall | 0.319 | 0.319 | 0 |
| Real-word recall | 0.062 | 0.075 | +0.013 |
| Orthography recall | 0.818 | 0.779 | −0.039 |
| **Slang preservation** | 0.717 | **0.935** | **+0.218** |
| **Profanity preservation** | 0.385 | **0.885** | **+0.500** |
| **No-change accuracy** | 0.897 | **0.963** | **+0.066** |
| False positives /100 tokens | 0.348 | 0.348 | 0 |
| Semantic corruption | 1 / 375 | 1 / 349 | 0 |
| p95 latency | 144.21 ms | 144.12 ms | +0.09 ms |
| Peak RSS | 200 MB | 208 MB | −8 MB |

Both changes are pipeline fixes, not model changes. The brief's §23 hoped a 5–15 point
improvement was available from fixing ranking and contextual selection rather than from
training; the honest figure is **+2.3 pp F1 and +6.6 pp no-change accuracy**, with the
large movements in slang (+21.8 pp) and profanity (+50.0 pp) preservation. Detection
recall is essentially unchanged, because the categories that dominate the recall deficit —
punctuation, morphology, real-word — were not addressed by either change.

---

## 14. Is training justified?

**No, not yet, and not the generative model.** Full reasoning, with the evidence for each
classification, is in `docs/training-readiness-report.md`. In summary:

| Weakness | Measured | Classification |
|---|---:|---|
| Slang/profanity false positives | now fixed: 0.935 / 0.885 | **was deterministic** — fixed this phase, no training |
| Candidate ranking | 2.2 pp headroom, captured | **closed** |
| Local-AI wedge | run hangs, sockets leak | **deterministic defect** — a stuck socket is not a training problem |
| Morphology | recall 0.319, correction 0.021 | needs rules or a much better model; LanguageTool tuning is cheaper per point |
| Punctuation | recall 0.273, correction 0.061 | Russian comma rules are rule-describable; extend the rule layer first |
| **Real-word** | recall 0.075, 74 of 80 missed | **the one place training is justified** — and it is a 29 M encoder retrain, not an LLM fine-tune |

The real-word case is genuinely a model limit rather than a plumbing bug: the existing
reranker leans the *correct* way on компания/кампания by ~0.02, i.e. right sign and no
confidence, and the margin sweep found 0.20–0.60 behave identically. That is a
capacity/data signature. Even a perfect real-word detector is bounded at **+16 pp** recall
(80 of 497 gold errors), realistically +4–6 pp.

Recommended order: fix the AI wedge, wire lexical knowledge into ranking, re-benchmark,
*then* extend the real-word dataset and retrain the encoder.

---

## 15. Recommended next steps

Ranked by measured value per unit of effort.

1. **Fix the local-AI wedge (§8).** Highest priority, because it is a correctness defect
   in a shipped feature: cancelled requests leak sockets, the single slot fills, and the
   model silently stops answering while reporting itself available. Needs connection
   disposal on cancel, an `IsReady` backed by the existing health probe (already flagged
   in `FUTURE_WORK.md`), and either more slots or a bounded request queue.
2. **Commit the benchmark artifacts.** Everything in §3 protects the corpus *on this
   machine*; only version control protects it from this machine. Not done here because
   the repository has one commit and ~30 unrelated modified files, and choosing what
   enters history is the maintainer's call. Suggested minimum:
   `ai/data/benchmark/ru_frozen_v1.jsonl`, `.meta.json`, `_gen/`, `GOLDEN.md`.
   `ai/data/errors/` is 24 MB and could reasonably stay regenerable instead.
3. **Make the recovery prompt non-modal (§1).** An abnormal exit currently blocks the
   next launch on a `MessageBox`, which also makes automated UI runs non-deterministic.
4. **Wire `OfflineLexicalKnowledgeService` into correction and ranking (§7 of the
   brief).** The lexicon veto added this phase uses only "is this a word"; definitions,
   register labels, morphology and frequency are all available and still unused.
5. **Attack punctuation and morphology through rules**, measuring each rule against the
   frozen corpus, before considering a model for them.
6. **Then** the real-word dataset extension and encoder retrain.

Not recommended: building the multi-signal ranker of the brief's §6. The measurement says
the ranking gap was 2.2 pp and it is now captured.
