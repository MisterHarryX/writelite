# WriteLite — pre-release audit

**Date:** 28 August 2026 · **Branch:** `clean-source-upload` · **Platform:** Windows 11 Pro 26200, .NET 10, WPF

---

## Scope of this audit, stated honestly

The brief asked for twelve phases. This pass went deep on the ones with named user-facing
defects and named missing features — startup stability, the double-click card, the external
write path, the English dictionary, reader pagination, book covers, hotkeys, and the editor's
"is it working?" state — and shallow on the rest. Where a section below says *not audited*,
nothing was measured and no claim is made. That is a smaller report than the brief describes,
and it is the part that is true.

Everything numeric here was measured on this machine during this session. Nothing is estimated.

---

## Project overview

A Windows desktop assistant for Russian and English text, working entirely on the machine —
no cloud, no account, no text leaving the process. Two surfaces:

- **A system-wide field monitor.** Watches the focused editable control through UI Automation,
  underlines findings, and writes corrections back into the host application.
- **Its own environment.** Rich-text editor, book reader, notes board, document converter and
  dictionary.

17 projects; ~59 000 lines in `WriteLite.App` alone. Language engine frozen at 1.0, rule pack
`ru-1.6.0` (106 rules, 78 of them regex).

## Architecture summary

```
keystroke
   ↓  (in-app editor)                        (other applications)
EditorPage.Editor_TextChanged            TextFieldMonitor ← UIA focus/text events
   ↓ 220 ms debounce                          ↓ debounce
DocumentTextIndex.Build   (dispatcher)    AutomationTextTarget.TryReadTextAsync
   ↓ flat string                              ↓ flat string
        ┌──────────── shared analysis stack ────────────┐
        │ Fast lane      rules + spelling      ~0.4 ms  │
        │ Deterministic  + LanguageTool, style, ONNX    │
        │ Deep           WriteAI (Qwen), opt-in         │
        └───────────────────────────────────────────────┘
   ↓ per-lane publish
issues → sidebar / underlines / correction card
   ↓ user applies
CanonicalCorrection → TextTargetCapabilities → ExternalTextWriter → verify by re-read
```

The write path is capability-driven end to end: what a control *can do* selects the strategy,
and every write is verified by re-reading the control rather than trusting the strategy's
own report.

---

## Problems found and fixed

### P0 — release blockers

**None found.** No crash, data-loss or privacy defect was identified. One latent
startup-failure path was closed (below) that had not yet been observed in the field.

### P1 — critical

| # | Problem | Root cause | Status |
|---|---|---|---|
| 1 | A machine under load could fail the launch with an unhandled `RegexMatchTimeoutException` | `RuleCatalog.LoadDefault()` ran all 265 embedded rule self-tests at startup, against patterns carrying the 100 ms *runtime* match timeout. That timeout exists to stop a pathological user input hanging the analyzer; applied to a 30-character test string it measures machine load. `RegexMatchTimeoutException` is not an `ArgumentException` and was caught nowhere above the call. | **Fixed** |
| 2 | ~460 ms of blocking work on the dispatcher during startup, done twice | `LoadDefault()` was called once for the orchestrator's analyzer and once for the monitor's fast analyzer, on the UI thread, each time re-parsing the pack and re-compiling 78 patterns to produce an identical immutable object. | **Fixed** |
| 3 | The double-click dictionary card closed itself in Discord | The card was invalidated whenever the monitor's `(target, generation, textVersion)` triple stopped matching. Two of those three are not statements about the text: the monitor restamps every republished snapshot with the live version and republishes on `SetPopupInteractionOpen` — which is called to open this very card — and it publishes a `null` snapshot while switching fields. | **Fixed** |
| 4 | Correction apply latency regressed 21× on ordinary text boxes | An in-flight fix for a real Discord defect (`ValuePattern.SetValue` corrupts Slate/Electron composers) demoted the whole-value write for *every* control. Correct diagnosis, blunt remedy. `FieldMonitorApplyTests` measured p50 apply rising from under 300 ms to 383.6 ms. | **Fixed** |
| 5 | Clean text sat under a spinner for the whole model lane | `SetAnalyzing(false)` ran only in the pass's `finally`. On text *with* findings the panel filled and cleared the state incidentally; on text with none there was nothing to clear it, so a document with nothing wrong showed "checking…" for as long as the model lane took and only then said it was fine. | **Fixed** |

### P2 — important

| # | Problem | Status |
|---|---|---|
| 6 | The double-click card labelled every word `RU`, including English ones | **Fixed** |
| 7 | The English dictionary had no lemma resolution: `boxes` and `carried` reported "not in dictionary" while `box` and `carry` are in the pack. Double-clicking running prose lands on an inflected form most of the time, so most of the English pack was unreachable. | **Fixed** |
| 8 | The card offered no translations, though the same `TranslationIndex` was already open for the dictionary page | **Fixed** |
| 9 | The card was a dead end — no route to the full article | **Fixed** |
| 10 | No click-outside dismissal; the card could only be closed by Escape (which needed focus it did not have) or by the × | **Fixed** |
| 11 | A page jump landed showing the *previous* page — found while building pagination; the lead-in scroll meant for highlights leaves the viewport a third of a screen before the target, and the reading position is the character at the top of the viewport | **Fixed** |
| 12 | Rapid presses of a page arrow advanced one page — each press re-derived the position from a scroll that had not settled | **Fixed** |
| 13 | The rule-pack test was a known flake, failing under parallel load | **Fixed** (validation now uses a 5 s timeout; load can no longer masquerade as a bad pattern) |

### P3 — polish

Not systematically swept. The Release build of the solution reports 140 warnings (the README
records 131 before this session; the difference is not attributable without a per-warning diff,
which was not done). 14 of them are in the App project, including three unused fields on
`HybridTextAnalysisService` and one on `ReadingPage` — pre-existing, harmless, and left alone
rather than touched late in a release cycle.

---

## Features added

| Feature | Brief § | What was built |
|---|---|---|
| Reader pagination | §8 | `ReadingPagination` — a virtual page model, plus `‹ [7] / 41 ›` with jump-to-page in the reader toolbar and a shortcut on each arrow |
| Custom book covers | §9 | `BookCoverStore` — covers copied, re-encoded and owned by the application; shown on the shelf card; set, replaced and removed from the book's context menu |
| Hotkeys | §10 | `ShortcutRegistry` + `GlobalHotkeyService` + a settings section that lists every shortcut in the product, edits them by capture, refuses invalid and conflicting bindings, and resets |
| English in the double-click card | §5 | Lemma resolution, correct language label, a translations tab, and a route into the full dictionary article |

### The pagination model, documented

The reader is a continuously scrolling flow, so there is no rendered page to count, and the
formats it opens have no page of their own worth borrowing — TXT and DOCX have none, EPUB has
them only where a publisher marked them, and a PDF's pages belong to the file's layout rather
than to the text extracted from it. Deriving a number from the viewport would produce a page
count that changes when the window is resized or the type made bigger: a number that means
nothing between two sessions and nothing at all between two people reading the same book.

So a page here is **a fixed span of the document's own text, about 1 800 characters, ending at
a paragraph boundary** — the classic manuscript page, which is what "page 124 of 367" has always
meant for a text with no typeset pages. One unbroken block falls back to cutting at the target
so a chapter exported without paragraph marks cannot collapse the book into one page.

What this buys: the division depends on the text and nothing else. Window size, font size, line
spacing and measure cannot change the page count or move the reader between pages. Two people
reading the same file are on the same page. Restoring after a restart lands on the right page,
because the stored position is a character offset and this is a function of character offsets.

What it does not claim: these are not the publisher's page numbers and will not agree where a
source has its own. That is stated rather than hidden.

---

## Measurements

All measured on this machine, this session. `tools/startlat` is new and is in the repository.

### Startup components

| Operation | Before | After | How measured |
|---|---:|---:|---|
| `RuleCatalog.LoadDefault`, second and later calls | 177 ms | **0 ms** | `tools/startlat`, Release |
| `RuleCatalog.LoadDefault`, first call | 462 ms, on the dispatcher | 320 ms, **on a worker** overlapped with the Hunspell load | `tools/startlat` + code path |
| First `RuleBasedAnalyzer.Analyze` after start | 176 ms | **43 ms** | `tools/startlat`, Release |
| `RuleCatalog.Warm` (the deferred regex JIT, paid in the background) | — | 93 ms | `tools/startlat`, Release |

The 176 → 43 ms figure is the one that matters for feel: `RegexOptions.Compiled` emits its code
on a pattern's *first match*, not at construction, and the load-time self-tests used to pay that
by accident. Removing them moved 176 ms onto whatever sentence the user typed first. `Warm()`
now pays it deliberately, once, on the background thread that startup is already waiting on.

**End-to-end startup was not measured cleanly enough to claim a delta.** Four Release launches
were logged (settings-loaded → double-click hook live): 4.83 s, 4.22 s, 3.51 s before the change
and 3.58 s after. The ranges overlap and the machine was running a test suite during part of it.
The component measurements above are the defensible ones.

### Correction apply

| Path | Before this session | In-flight (uncommitted) state | After |
|---|---:|---:|---:|
| Field apply, WPF TextBox, p50 | < 300 ms | 383.6 ms | **18.0 ms** |
| Field apply, p95 | — | 932.8 ms | **55.7 ms** |

Measured by `FieldMonitorApplyTests`, which drives the production
`CorrectionApplicationService` against a real automation element.

### Editor input path

| Operation | Measured | Target | Verdict |
|---|---:|---:|---|
| `DocumentTextIndex.Build`, 20 000 chars (the only per-pass dispatcher work) | **0.23 ms** p50 | < 16 ms (one frame) | Comfortable |
| `DocumentTextIndex.Build`, 200 000 chars | **2.90 ms** p50 | < 220 ms | Comfortable |
| Growth, 20 k → 200 k | 0.24 → 2.83 ms | linear | Linear |
| Debounce before a check starts | 220 ms | ≤ 1 s | Within target |
| Fast lane, steady state | 0.4 ms | — | — |

**This is a negative finding worth stating plainly.** The obvious suspect for "the input field
feels slow" — projecting the rich document to a flat string on the UI thread — is not the cause.
It costs a quarter of a millisecond on an ordinary document. Typing is answered by WPF and never
waits for WriteLite.

What *was* wrong was the reporting rather than the work: the panel went on saying it was
checking long after it had the answer (P1 #5). That is a perception defect, and it is fixed.

### Not measured

Overlay appearance latency, dictionary lookup latency end to end, reader open time, and memory
growth over repeated open/close cycles. Each needs instrumentation that does not exist yet, and
inventing numbers for them would be worse than the gap. Listed under remaining risks.

---

## Application compatibility

The write path now orders its strategies **per control** rather than by a fixed ranking:

- **Default: cheapest first.** A window message or a pattern call changes the control directly;
  a clipboard paste borrows a shared system resource and synthesises input, so it goes last.
  Measured through this path, a WPF text box takes a pattern write in 28 ms and a selected paste
  in 241 ms; a browser field, 27 ms against 1 152 ms.
- **The exception is by scope.** `EM_REPLACESEL` and a selected paste replace the span the
  correction names. `ValuePattern.SetValue` replaces the entire value. Those are the same thing
  for a control whose value *is* its text, and very different for one that keeps a document
  model behind it — Slate, Quill, ProseMirror, and so every Electron composer. There the
  whole-value write leaves the model describing text that is no longer there: the text reads
  back correctly, so the write verifies, and Backspace and Delete stop working. That was the
  reported Discord defect.

The discriminator is `FrameworkId`, not an application name. Every Chromium embedding — Chrome,
Edge, Electron, CEF — reports `Chrome`, and reports it because of how the control is built,
which is exactly what the decision turns on. An application built on Electron tomorrow is
covered without being named. The whole-value route is **demoted, not removed**: a Chromium field
with no selectable text still gets it, because for that control it is the only route there is.

`IsOffscreen` is no longer consulted for editability (in-flight work, retained). Discord's
composer reports `IsOffscreen=true` while the user is typing into it, because Chromium marks the
composer bar offscreen when its layout box runs past the client area. Believing the flag dropped
the field entirely.

**Verified live this session:** the application starts, the low-level double-click hook installs,
and the new global hotkey registers (`event=global-hotkeys-registered count=1`).
**Not verified live:** the Discord double-click and write paths. Per the session's agreed limits,
no live application the user had open was driven. That is the largest remaining risk and is
listed as such.

---

## Security and privacy

Reviewed the new code against the existing contract (technical events only; never field text,
never the word looked up, never a file path, never exception text).

- Every new log line carries an exception *type name* or a count. No user text, no paths.
- The one value-carrying line is `global-hotkey-rejected gesture=Ctrl+Alt+W` — a configuration
  value the user chose, comparable to the `profile=Quality` already logged. Acceptable.
- **The global hotkey is registered, not hooked.** `RegisterHotKey` asks Windows to report one
  specific chord. A keyboard hook would have seen every keystroke on the desktop — including
  into password fields and other people's messages — to answer the same question. For an
  application whose premise is that text stays on the machine, that difference is the argument.
- Cover images are copied into `%LOCALAPPDATA%\WriteLite\reading\covers` and re-encoded. Input
  is size-capped at 64 MB before any decoder touches it, and a decoder failure is a message
  rather than an exception reaching the user.
- No credentials, keys or endpoints were added. No new network access. No new shell execution.

`PrivacyScanTests` continues to pass.

---

## Tests

**Baseline at the start of this session:** 1 416 passed · 1 failed · 1 skipped. The failure was
the rule-pack regex flake.

**After this session:** **1 505 passed · 0 failed · 3 skipped** (Debug, full solution, 2 m 28 s).
The three skipped need a live local Qwen server and skip themselves.

### Added — 73 tests

| File | Tests | Covers |
|---|---:|---|
| `Lexical/LexicalCardLifecycleTests.cs` | 7 | When the dictionary card survives a snapshot and when it goes away; click-inside vs click-outside |
| `Lexical/EnglishCardLookupTests.cs` | 7 | English lemma resolution against the shipped pack; that a miss stays a miss and a short word is never reduced |
| `Reading/ReadingPaginationTests.cs` | 11 | The page division: coverage, monotonicity, paragraph breaks, one unbroken block, malformed break lists, clamping |
| `Reading/ReaderPageNavigationTests.cs` | 7 | Page navigation through the real control, including that type size and window size cannot change the page count |
| `Reading/BookCoverStoreTests.cs` | 16 | PNG, JPEG, huge images, odd ratios, transparency, corrupt files, empty files, replace, remove, and that a failed replacement keeps the existing cover |
| `Settings/ShortcutRegistryTests.cs` | 27 | Uniqueness, no shipped collisions, matching, rebinding, conflicts, invalid combinations, persistence, cross-version settings files |
| `Settings/ShortcutSettingsSectionTests.cs` | 6 | That the page is generated from the registry rather than hand-written |
| `Documents/EditorInputLatencyTests.cs` | 4 | The dispatcher cost of the document projection, and that it grows linearly |
| `Documents/EditorAnalysingStateTests.cs` | 2 | That clean text is told it is clean as soon as the deterministic lane answers |

Two existing tests were updated: `RussianRulePackTests` now calls the validating loader
explicitly, and `TextTargetCapabilityPolicyTests` gained the framework-aware ordering cases.

### One test that had to be checked twice

`CleanTextStopsSayingCheckingAsSoonAsTheDeterministicLaneAnswers` was written, passed, and then
**passed again with the fix reverted** — because the revert had only removed half of it. Reverting
the other half made it fail at every sample from 250 ms to 2 s, which is what confirmed both the
defect and the fix. Recorded here because a test that passes before the fix is not evidence of
anything, and the first bisect said it was.

---

## Remaining risks

1. **The Discord path is not verified live.** The write-ordering fix and the card-lifecycle fix
   are argued from measurement, logs and unit tests, and the read path was reasoned from a
   previous session's captured log. Neither was exercised against a running Discord in this
   session. This is the single largest gap.
2. **Real GUI interaction is not automatically tested.** Unchanged from before; mouse and
   keyboard against live external applications remain manual.
3. **Overlay, dictionary and reader latency are unmeasured**, and are therefore unstated rather
   than estimated.
4. **The global hotkey can be silently unavailable.** If another application holds `Ctrl+Alt+W`,
   registration is refused, logged, and the shortcut does nothing. The settings page does not
   yet show that it failed — the user has to notice. Listed as post-release work.
5. **The registry does not yet drive every shortcut.** The editor, the window and the reader are
   registry-driven; the notes board, the dictionary page and the word manager still have local
   handlers not represented in it. They are documented as absent rather than shown wrongly.
6. **140 Release warnings** across the solution, 14 of them in the App project. Pre-existing debt; not triaged this session.
7. **94 MB of duplicated lexical JSON** ships because the pack catalog reads those files
   directly. Pre-existing and pinned by `ShippedLexicalResourcesTests`.
8. **Licence still unchosen.** There is no `LICENSE` file. This is a release blocker for
   *publishing*, though not a technical one.

---

## Release readiness

| Category | Weight | Score | Why |
|---|---:|---:|---:|
| Core functionality | 20 | 18 | Everything the product claims works, and is measured. Two feature gaps the brief named are now closed. |
| Stability | 20 | 17 | 1 499 tests green; a latent startup-failure path closed. Live external-application behaviour still unverified. |
| Performance | 15 | 13 | Apply 21× faster than the in-flight state; first check 4× faster; input path measured and comfortable. Several latencies still unmeasured. |
| UX | 15 | 12 | The spinner defect and the vanishing card are fixed; pagination, covers and hotkeys added. No systematic consistency or responsive sweep was done. |
| Cross-application compatibility | 10 | 7 | The ordering is now principled and per-control, but the application it was written for was not exercised live. |
| Code maintainability | 5 | 4 | Nine scattered shortcut switches replaced by one registry; the write path's ordering rationale is written down where it is decided. |
| Error handling | 5 | 4 | New surfaces return typed failures with messages. No systematic sweep of the existing swallowed-exception sites. |
| Security / privacy | 5 | 5 | New code adds no text to logs, no network, no shell. The hotkey deliberately avoids a keyboard hook. |
| Test coverage | 5 | 4 | 73 tests added on the changed surfaces. GUI-against-real-applications remains untested. |
| **Total** | **100** | **84** | |

### Would I approve this build for public release?

**YES, WITH MINOR KNOWN ISSUES** — with one condition that is not technical.

The technical issues that remain are known, written down, and none of them loses user data,
crashes the application or leaks text. The largest, that the Discord fixes are argued rather
than demonstrated, is a confidence gap rather than a defect: it needs half an hour with Discord
open and the compatibility log tailing, and it should happen before the build ships.

The condition is **the licence**. There is no `LICENSE` file, and the README says so. Publishing
a build of code that is "all rights reserved" with third-party components under LGPL-2.1+,
Apache-2.0, MIT and CC-BY-SA-4.0 inside it is not a release-readiness question I can settle from
the code, and it has to be settled before anything is published.

### Next ten, post-release

1. Verify the double-click and write paths live against Discord, Slack and VS Code, with the
   compatibility log open. Turn what is learned into `fieldmatrix` rows.
2. Instrument overlay appearance and dictionary lookup so those rows of the latency table stop
   saying *unmeasured*.
3. Show a failed global-hotkey registration in the settings page instead of only in the log.
4. Bring the notes board, dictionary page and word manager into the shortcut registry.
5. Give the reader a keyboard route to jump-to-page (the box needs a shortcut of its own).
6. Add covers to the "continue reading" card, which still shows none.
7. Measure memory across repeated open/close of the overlay, books and settings — the resource
   audit the brief asks for was not performed.
8. Reclaim the 94 MB of duplicated lexical JSON by teaching the pack catalog to read the database.
9. Triage the 140 Release warnings, starting with the App project.
10. Decide the licence and add the file.
