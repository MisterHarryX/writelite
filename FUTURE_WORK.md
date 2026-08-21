# Future work

Things noticed during the stability pass that were deliberately **not** done, because
the pass was about making the existing build stable rather than about changing what it
does. Each entry says what is wrong, why it was left, and what fixing it would involve.

## Accessibility

### Reading library cards are invisible to assistive technology

`ReadingPage.BuildProjectCard` creates a `Border` with a `MouseLeftButtonUp` handler and
sets `AutomationProperties.Name` on it. WPF gives a plain `Border` no automation peer, so
the card never appears in the UI Automation tree at all and the name is never read by
anything. The card is also unreachable by keyboard: it is not focusable, and there is no
`Enter`/`Space` activation path.

This was found while writing the UI stress driver, which cannot locate the cards by name
and has to click the counts line inside them instead — the same limitation a screen
reader user would hit, except they have no cursor to fall back on.

Fixing it properly means making the card a `Button` with a card `ControlTemplate`, which
brings focus, keyboard activation and an automation peer at once. That is a change to
what the control *is*, so it did not belong in a stability pass.

The same applies to the note cards on the notes board and to the mark rows in the
reader's side panel.

### The reader's canvas reports the whole book as its bounds

The reading canvas is a `RichTextBox` with scrolling disabled inside an outer
`ScrollViewer`, so its automation bounding rectangle is the height of the entire laid-out
document — over eight hundred thousand pixels for a three-thousand-paragraph book, with a
negative top once the reader is any distance in. Every automation client has to intersect
that with the window to find out where the text actually is.

A `TextPattern`-aware surface would report visible ranges properly. Worth doing if the
reader ever needs to be usable with a screen reader.

## Performance

### The reader lays out the whole book at once

`ReadingDocument.Build` produces one `FlowDocument` containing every paragraph, and WPF
lays all of it out. Applying typography is now proportional to the block count rather
than to the character count (measured: ~60 ms for 6081 blocks against ~490 ms for the
rebuild it replaced), but it is still proportional to the whole book rather than to what
is on screen.

Virtualising it would mean either `FlowDocumentPageViewer`-style pagination — which
changes the reading model from a continuous scroll to pages — or building the document in
chunks around the viewport, which would invalidate the offset map's current assumption
that every run exists at all times. Both are redesigns, and the measurement does not
currently justify one: 60 ms is inside the band where a type change reads as immediate.

Revisit if books substantially larger than a few megabytes become a normal case.

### Importing a large book is slow

Opening the 1.5 MB / 3000-paragraph stress fixture takes several seconds. This is
`DocumentSerializer` parsing, and it correctly runs off the dispatcher — the window stays
responsive throughout and the reader sees a loading state. It is unchanged by this pass
and is not a stability defect, but it is the largest single wait in the reading flow.

### The process is System-DPI-aware, not PerMonitorV2

Measured at runtime with `GetWindowDpiAwarenessContext` against the shipped Release
build: the shell window reports `DPI_AWARENESS_SYSTEM_AWARE`. WriteLite ships no
application manifest and does not set `ApplicationHighDpiMode`, so it takes the default.

`ShellWindowBehavior` was written as though the application were PerMonitorV2 — it
carries a `WM_DPICHANGED` handler whose only purpose is to re-ask for the maximised
frame after a move between differently scaled monitors, and its comment asserted
PerMonitorV2 outright. That comment has been corrected; the handler has been left, since
it is the right answer if the manifest is ever added and costs nothing while the message
never arrives.

What system awareness costs today: moving the window to a monitor with a different scale
gets a bitmap stretch from Windows rather than a re-layout (soft text, correct geometry),
and a scale change made while the application is running is not picked up until it
restarts. Nothing is clipped or unreachable in either case, because every measurement in
the placement path comes from `GetMonitorInfo` for the monitor the window is on rather
than from a DIP constant.

Switching to PerMonitorV2 is a one-line manifest change, but it makes WPF re-run layout
on every DPI change, and that is exactly the sort of change that needs to be exercised at
100 %, 125 %, 150 % and 175 % and across two monitors of different scale before it can be
trusted. The machine this pass ran on is a single 1920 × 1080 display at 96 DPI, so that
verification was not possible here and the change was deliberately not made — trading a
known-good behaviour for an untested one is not an improvement.

## Behaviour

### Escape closes the book from anywhere in the shell

`MainWindow.OnEscape` gives the reader first refusal on Escape whenever a book is open.
That is the right precedence, but it means an Escape that was meant for something the
shell cannot see — a popup that has already closed itself, for instance — puts the reader
back in the library. Worth narrowing to "Escape while focus is inside the reader".

### The document-recovery prompt is modal at startup

`EditorPage.CheckForRecoverableWork` shows a `MessageBox` as soon as the shell is up when
a previous session left an unsaved document. It blocks the whole application until it is
answered, and after any abnormal exit it is the first thing the user sees. An in-page
banner offering the same choice would be less intrusive and would not block automation.

### `QwenModelBackend.IsAvailable` conflates "installed" with "running"

The backend reports itself available when the model pack is on disk, or optimistically
when the endpoint is loopback, regardless of whether anything is serving inference. Every
caller therefore has to discover the truth by making a request and inspecting which
backend answered — which is what `StudyCardDraftService` now does.

A separate `IsReady`, backed by the health probe it already has, would let callers ask the
question directly instead of inferring it. The probe is currently synchronous
(`ProbeHealth` calls `GetAsync(...).GetAwaiter().GetResult()`), so exposing it would want
an async form first.

## Tests

### Two pre-existing tests depend on the model pack being absent

`QwenRouterAndParseTests.Analyzer_WithoutQwenPack_UsesLiteFallback` and
`LocalAiAnalyzerTests.Provider_UnreachableServer_UsesFallback` both assert that the
analyzer falls back when no Qwen pack is present. On a machine where the pack *is*
present under `models/writelight-qwen` — which is the normal developer setup — the
backend reports itself available and both tests fail.

They were failing before this pass and are unrelated to it. Fixing them means pointing
the backend at an empty model directory for the duration of the test rather than letting
it discover the real one.
