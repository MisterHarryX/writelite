# Field correction sprint — findings and evidence

Windows field monitor: malformed replacements and false read-only detection.
Measured on Windows 11 Pro 26200, .NET 10.0.301, Release build, 2026-08-20.

---

## 1. Root cause of malformed replacements

There were **two independent mechanisms**, both reproduced before any code changed
(`tools/fieldprobe`).

### 1a — The card corrupted correct answers (the visible defect)

`CorrectionCardText.VisualizeWhitespace` replaced **every** space in a displayed fragment
with `·`:

```csharp
.Replace(' ', '·')      // ← unconditional
```

That glyph exists so a spacing correction is not an invisible card. It is right for
`«  » → « »` and wrong for every correction whose replacement is more than one word. The
project has a curated table of exactly those (`LocalSpellChecker.StableCorrections`), so
the user was shown:

| what WriteLite would write | what the card drew |
| --- | --- |
| `вообщем → в общем` | `вообщем → в·общем` |
| `врятли → вряд ли` | `врятли → вряд·ли` |
| `потомучто → потому что` | `потомучто → потому·что` |
| `какбудто → как будто` | `какбудто → как·будто` |

That is the reported `робота·тет` shape: a correct replacement rendered as a word split by
a middle dot. The canonical replacement was never wrong; the card disagreed with it.

A second, smaller bug sat in the same method: `Shorten` called `Trim()` before the glyph
was applied, so a fragment that was *only* whitespace was deleted entirely. `«,» → «, »`
reached the card as `«,» → «,»` — the marker never rescued the case it was written for.

**Fixed at** `Services/CorrectionCardText.cs`. Whitespace is marked only where it is
genuinely invisible — a whitespace-only fragment, whitespace at a fragment edge that
differs from the other side, or a run of two or more spaces. A single interior space
between two visible characters is drawn as a space.

### 1b — Unvalidated word-splitting candidates (the latent corruption)

Hunspell's suggester tries splitting an unknown word in two and returns the result as an
ordinary candidate string containing a space. Recorded from the shipped `ru_RU` dictionary:

```
роботает        -> работает | ро ботает | проболтает | ботает | оботрет
Сечас           -> Счеса | Сеча | Сейчас | Се час | Сеча с | Сечка
пожалуста       -> пожалуйста | пожал уста | полупуста
непомню         -> напомню | не помню | попомню
необходимобыло  -> необходимо было | необходимость | необходимо | ...
```

`CorrectionCandidateValidityPolicy.FilterSuggestions` had no notion of these. A split could
become `suggestions[0]`, become the card's replacement, and be written into the field —
`роботает → ро ботает`, rendered `ро·ботает`. Nothing downstream would have objected.

**Fixed at** `src/WriteLite.Language.Core/CorrectionCandidateValidityPolicy.cs`. A split
candidate is admissible only on evidence: exactly two parts, a *pure* split (adding
whitespace and changing nothing else), each part at least three characters, and each part a
word the lexicon knows. The membership test is threaded through from the lexicon that
produced the candidate. The curated table bypasses the gate through
`FilterCuratedSuggestions`, because `вкурсе → в курсе` is a stated rule of Russian rather
than a ranker's guess, and its one-letter preposition would fail an evidentiary test for
good reasons that do not apply to it.

Measured after the fix — the dangerous splits are gone, the real ones survive:

```
роботает       -> работает | проболтает | ботает | оботрет        (ро ботает dropped)
Сечас          -> Счеса | Сеча | Сейчас | Сечка                   (Се час, Сеча с dropped)
непомню        -> напомню                                         (не помню dropped)
необходимобыло -> необходимо было | необходимость | ...           (kept — correct)
какбудто       -> как будто | будто                               (kept — correct)
```

### 1c — Offset drift on multiline RichEdit (would have corrupted text)

Not the reported symptom, but the same class and found while tracing it.
`Win32TextEdit.TryReplaceRange` sent the analyzer's offsets straight to `EM_SETSEL`. On a
classic multiline `EDIT` that is correct. On RichEdit it is not: the selection model counts
a line break as one character while the text the analyzer saw carries two. Every line above
the correction shifted the selection one character right. The failure is silent, grows with
document size, and is invisible in a single-line field.

**Fixed at** `Services/Win32SelectionOffsets.cs` + `Win32TextEdit.TryReplaceVerifiedRange`.
Rather than deciding per window class, the convention is **measured**: select everything,
read the end offset back, and compare it against the text length. A window whose answer
fits neither model is refused rather than guessed at.

---

## 2. Root cause of false read-only detection

Two causes, stacked.

### 2a — "Editable" meant "has a writable ValuePattern"

`EditableTextTargetPolicy.IsEditableTextTarget` ended in:

```csharp
// TextPattern by itself is read-only. It can support analysis, but it
// is never sufficient evidence that a correction may be written.
&& valueEditable;
```

and the write path refused anything the `ValuePatternTextAdapter` had not claimed:

```csharp
if (!snapshot.Target.IsEditable || !snapshot.Target.SupportsDirectWrite)
    return ...(snapshot.Target.IsEditable
        ? UnsupportedWritePattern
        : ReadOnly);           // ← "Поле только для чтения"
```

`SupportsDirectWrite` was `Adapter.SupportsDirectWrite` — a fact about which adapter
WriteLite picked for **reading**, not about the control. Every control that exposes text
through `TextPattern` and no writable value fell into `TextPatternReadOnlyAdapter`, whose
`ReplaceRangeAsync` returned *"The target field does not support safe direct replacement."*

Measured, that class of control is: **WPF `RichTextBox`, Microsoft Word, and every
Chromium/Electron surface that does not expose a value.** All of them are ones the user
types into freely.

### 2b — Keyboard focus was treated as evidence of editability

The same predicate required `hasKeyboardFocus`, and it was re-evaluated **at apply time**
through `TryValidateForWriteAsync`. `CorrectionPopupWindow` sets `ShowActivated = false`,
which stops it stealing focus when it *appears* — and does nothing when the user clicks a
button inside it. So pressing «Исправить» activated the popup, the target lost keyboard
focus, the live check said "not editable", and WriteLite told the user their field was
read-only. **WriteLite's own popup was the reason WriteLite could not write.**

### The fix

`Services/TextTargetCapabilities.cs` reads what the control says about itself in one pass,
and `TextTargetCapabilityPolicy` answers two separate questions:

- **Discovery** (`IsDiscoverableTarget`) — "is this the field the user is editing right
  now?" May require focus; that is what identifies the watched field.
- **Write** (`Evaluate`) — "may this control's text be changed?" Never considers focus.
  Returns `Editable`, `ReadOnly`, or **`Unavailable`** — the third value the old boolean
  could not express, and the reason a provider that timed out is now reported as
  "temporarily unavailable, try again" instead of "read-only".

`CorrectionPopupWindow` additionally now carries `WS_EX_NOACTIVATE`, so the situation stops
arising: the popup takes the click and the target keeps the caret.

---

## 3. Canonical replacement architecture

`src/WriteLite.Language.Core/CanonicalCorrection.cs`. One representation of "replace exactly
this with exactly that":

```
Start        int      UTF-16 code units
Length       int      0 for an insertion
Original     string   exactly text[Start .. Start+Length]
Replacement  string   exactly what will be written
```

An instance is only obtainable through `TryBind(text, …)`, which is handed the string the
correction is supposed to apply to. There is therefore no such thing as a canonical
correction that has not been checked against a real string. `TryBind` returns
`Bound` / `OutOfRange` / `OriginalMismatch` / `Malformed`, and additionally refuses an
offset that falls between the halves of a surrogate pair.

`ApplyTo(text)` is the only place the resulting string is computed. Display, dictionary and
apply all take the same bound value; nothing reconstructs a replacement from UI fragments.

`CorrectionDiff.Compute` produces the four display fragments and is decoration only. Its
invariant — `Prefix + ChangedOriginal + Suffix == Original` and
`Prefix + ChangedReplacement + Suffix == Replacement` — is asserted over the required
regression set, over 25 hand-written edit shapes, and over 4 800 generated string pairs
including emoji, so no boundary can land inside a surrogate pair or a combining mark.

---

## 4. Offset safety

No normalization happens between reading a field and analysing it: the string the adapter
returns is the string the analyzer sees, the string the correction binds to, and the string
the write is verified against. Analyzer offsets and control offsets are equal *by
construction* on that path.

Where they are not is the Win32 selection model, and that is handled by measurement rather
than assumption (§1c). Every write additionally proves its own range before touching
anything:

- **win32-edit** — reads the window's text with `WM_GETTEXT`, checks that the mapped span
  holds the recorded original, and only then sets the selection.
- **value-pattern** — re-binds the correction against the live value; a field that moved on
  produces no write.
- **selection-paste** — builds the range and compares `range.GetText(-1)` against the
  original before selecting. A provider that counts characters differently produces no
  write rather than a misplaced one.

Snapshot safety is unchanged in shape and stricter in enforcement: the apply path validates
target identity, generation, text version and full text before writing, and re-validates
after composing. A stale finding is rejected, never relocated.

---

## 5. External write strategies

`Services/Writing/ExternalTextWriter.cs`. Tried in order, least invasive first, and the
chain moves on whenever a write cannot be **verified by re-reading the control**:

| # | Strategy | Applies when | Notes |
| --- | --- | --- | --- |
| 1 | `win32-edit` | the control has a native edit-class window | `EM_SETSEL` + `EM_REPLACESEL`; keeps caret, scroll and usually undo |
| 2 | `value-pattern` | `ValuePattern` present and not read-only | one cross-process call; loses undo |
| 3 | `selection-paste` | `TextPattern` with selection, keyboard-focusable, value not read-only | selects the exact range, pastes, restores the clipboard |

Verification is the contract: a strategy reporting success is a claim, and
`ExternalTextWriter` re-reads the control after **every** attempt — including after a
strategy that threw — and only stops when what it reads is what the correction said it
would be.

Safety properties of strategy 3, which is the only one that leaves the control:

- The clipboard is captured as a full `IDataObject` before use and restored afterwards, on a
  short-lived STA thread so a clipboard round-trip cannot stall the UI.
- `Ctrl+V` is the only gesture the code can produce — `KeyboardInput` exposes no general
  send-keys facility, so no caller can send Enter.
- The paste is refused unless the foreground window belongs to the target's own process,
  checked twice: before putting the correction on the clipboard and again immediately
  before the keystroke.

---

## 6. Application / control compatibility matrix

Measured by `tools/fieldmatrix`, which drives the production `TextTargetCapabilities` →
`TextTargetCapabilityPolicy` → `ExternalTextWriter` path. "Verified" means the control was
read back and holds exactly the expected text. `ms` is from the first write attempt to the
verified read-back.

| Application / control | Technology | Read | Detect | Replace | Verified | Strategy | ms |
| --- | --- | ---: | ---: | ---: | ---: | --- | ---: |
| Notepad (Windows 11) | Win32 `RichEditD2DPT` | PASS | Editable | PASS | PASS | `win32-edit` | 53 |
| WinForms `TextBox` | Win32 `EDIT` | PASS | Editable | PASS | PASS | `win32-edit` | 9 |
| WPF `TextBox` | WPF | PASS | Editable | PASS | PASS | `value-pattern` | 28 |
| WPF `TextBox` multiline | WPF | PASS | Editable | PASS | PASS | `value-pattern` | 7 |
| WPF `RichTextBox` | WPF | PASS | Editable | PASS | PASS | `selection-paste` | 241 |
| Edge `<input>` | Chromium | PASS | Editable | PASS | PASS | `value-pattern` | 27 |
| Edge `<textarea>` | Chromium | PASS | Editable | PASS | PASS | `value-pattern` | 46 |
| Edge `contenteditable` | Chromium | PASS | Editable | PASS | PASS | `value-pattern` | 43 |
| Word, blank document | Office `_WwG` | PASS | Editable | PASS | PASS | `selection-paste` | 930 |
| Opera GX web text area | Chromium | PASS | Editable | PASS | PASS | `selection-paste` | 1152 |
| WPF `TextBox` `IsReadOnly` | WPF | PASS | **ReadOnly** | not attempted | n/a | none | — |
| WPF `PasswordBox` | WPF | PASS (empty) | **ReadOnly** | not attempted | n/a | none | — |

Of these, **WPF `RichTextBox`, Word and the Opera text area were all reported
«Поле только для чтения» before this sprint** — they have no writable `ValuePattern`.

### Not measured

- **Telegram Desktop, Discord, VS Code** — not measured. All three were running throughout;
  an earlier probe that measured "whatever has focus" produced rows naming applications it
  had never touched, so those rows were discarded rather than reported. VS Code's Monaco
  editor additionally does not expose a text field to automation unless accessibility mode
  is switched on in the editor.
- **UWP / WinUI text control** — no such application available on this machine.

---

## 7. Read-only validation

Positive evidence, not absence of a pattern:

- `WPF TextBox` with `IsReadOnly="True"` → capabilities `value=1 valueRO=1 text=1 select=1
  hwnd=0` → verdict **ReadOnly**, strategy plan **(none)**, no write attempted.
- `WPF PasswordBox` → `password=1` → verdict **ReadOnly**, no write attempted, and it is
  excluded from discovery so it is never decorated.
- A control that answers nothing at all → **Unavailable**, which produces
  «Поле ввода временно недоступно» with a retry, never a read-only claim.

The panel banner now reads «Поле доступно только для чтения. Исправления можно
скопировать.» and appears only for a genuine `ReadOnly` verdict.

`Services/CorrectionApplicationService.cs` distinguishes three failures the old code
collapsed into one: `ReadOnly` (copy offered), `UnsupportedWritePattern` — "WriteLite не
удалось изменить текст в этом поле" with copy **and** retry — and `TargetUnavailable`
(retry).

---

## 8. Tests

Full suite, Release:

```
Пройден!: не пройдено 0, пройдено 1406, пропущено 3, всего 1409  (2 m 13 s)
```

- **Passed** 1406 · **Failed** 0 · **Skipped** 3 (all pre-existing: they require a running
  Qwen server) · **Build errors** 0
- **Warnings** — **none introduced.** A full rebuild emits 12 warnings from the application
  project and 276 from the test project; every one is pre-existing and in a file this sprint
  did not touch (`WordChip`, `HybridTextAnalysisService`, `ReadingPage`, the morphology
  paradigms, and MSTest analyzer style suggestions such as `MSTEST0037`). Every file added
  or changed here compiles clean.
- Baseline before the sprint was 1319 passed / 2 failed — the two failures were the known
  rule-pack regex timeout flake under parallel load.

**87 new tests** in `tests/WriteLite.Tests/Corrections/`:

| File | Covers |
| --- | --- |
| `CanonicalCorrectionTests` | §5, §19, §21 — binding, staleness, surrogate boundaries, the six required words across 11 host contexts |
| `CorrectionDiffTests` | §4, §17, §20 — reconstruction over the required set, 25 edit shapes, 4 800 generated pairs, 800 emoji pairs, card rendering |
| `TextTargetCapabilityPolicyTests` | §9, §22 — editable/read-only/unavailable, focus loss, strategy planning |
| `ExternalTextWriterTests` | §11, §22 — chain fallthrough, unverifiable success, throwing strategy, non-destructive exhaustion, apply-all reduction |
| `SplitCandidateTests` | §2, §16, §18 — split admissibility, curated bypass, dictionary word resolution |
| `Win32SelectionOffsetTests` | §6, §21 — line-break conventions and offset mapping |
| `CorrectionDeduplicationTests` | §16 — one card per error, rebasing after apply, stale rejection, distinct alternatives kept |

---

## 9. Performance

Measured write-to-verified latency in real external controls (§6 table). Two tiers:

- **Direct paths** — `win32-edit` and `value-pattern`: **7–53 ms**. The correction is
  visible on the first read-back.
- **Paste path** — `selection-paste`: **241–1152 ms**. It carries a 120 ms settle delay
  before the clipboard goes back (the keystroke is handled on the target's own message
  loop), plus a clipboard capture and restore. It is only reached for controls that offer
  no other route.

No sleep in the apply path exceeds 150 ms. The verification back-off is 0/25/70/150 ms and
the first probe is immediate, so a well-behaved control pays nothing for it. The post-apply
re-read and re-analysis remain off the awaited path.

---

## 10. Remaining unsupported and unverified

- **Telegram, Discord, VS Code** — not measured (§6).
- **UWP / WinUI** — no test application available.
- **The paste path is slow** relative to the direct ones, and it is the path Word and WPF
  `RichTextBox` take. 241 ms is acceptable; the ~1 s cold figures are dominated by first-use
  clipboard cost and would repay attention if the paste path becomes common.
- **Clipboard restore is best-effort.** A delayed-rendering clipboard owner hands over a
  promise rather than data; capturing materialises what it offers at that moment. There is
  no way to preserve that from outside the owning process.
- **Undo after `selection-paste`** produces whatever undo unit the target application makes
  for a paste — usually correct, never guaranteed. `win32-edit` is reported as
  `undo=unreliable` on `RichEditD2DPT` (Windows 11 Notepad), unchanged from before.
- **Controls exposing neither a value, a text provider, nor an edit window** are
  `Unavailable` and are never written to. That is the intended safe failure.
