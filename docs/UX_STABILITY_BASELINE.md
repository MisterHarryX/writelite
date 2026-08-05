# WriteLite UX and stability baseline

Captured on 2026-07-19 before the LanguageTool-inspired editor workspace iteration.

## Repository state

- Base branch: `fix/stability-ui-dictionaries`
- Iteration branch: `fix/languagetool-inspired-ux-stability`
- Baseline commit: `6c2a19b`
- Existing user model, archive, log, and release artifacts were intentionally left untracked or unstaged.

## Verified baseline

- Release build: successful, 0 errors, 17 analyzer warnings in tests.
- Full suite: 349 passed, 3 skipped, 0 failed.
- UIA filter: 3 passed, 0 failed.
- Stress filter: 2 passed, 0 failed.
- Framework-dependent lifecycle smoke: exit code 0, no residual WriteLite or llama-server process.
- Notepad: exact WriteLite geometry created for `тваи` at UTF-16 start 11, length 4; selective hit-test returned a match over the word.

## Baseline gaps

- The “Проверка текста” navigation destination was informational only, not an editor.
- No three-pane review workspace or virtualized recommendation list existed in the main window.
- A 20-minute memory soak had not been completed.
- A physical overlay click could not be delivered by the Windows automation driver because it targeted the host editor HWND.
- Edge, Chrome, and Word had not completed the full interaction matrix.
- Self-contained .NET 10.0.9 restore was blocked by repeated official-feed TLS/ZIP download failures.
