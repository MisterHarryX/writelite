# Manual stability validation

## Status

Not completed in this automated workspace. No claim is made that Notepad,
Edge, Chrome or Word completed a 15-minute release-build validation here.

## Required desktop checklist

1. Start the self-contained `WriteLite.exe`, type in an editable Notepad field
   and verify exact underline/click/popup/replacement.
2. Repeat in installed browsers and Word if available; confirm static,
   read-only and password controls receive no overlay.
3. Change focus rapidly, close the host during analysis, and verify the popup
   disappears and WriteLite remains running.
4. Repeat lexical double-click while editing; alter text before lookup returns
   and verify the old card is cancelled.
5. Verify protected paths, URLs, email, versions, IPs, commands and JSON are
   neither underlined nor changed.

Use the log folder only for safe event codes; do not attach user text to a bug
report.
