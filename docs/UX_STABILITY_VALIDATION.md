# UX-stable validation

Date: 2026-07-19  
Branch: `fix/languagetool-inspired-ux-stability`

## Automated validation

- Clean Release build: successful, 0 errors, 17 existing test-analyzer warnings.
- Full suite: 353 passed, 3 skipped, 0 failed (356 total).
- UIA: 3 passed, 0 failed.
- Stress: 2 passed, 0 failed.
- Published lifecycle smoke: exit code 0, residual processes 0.

Lifecycle suites must run sequentially. Running the full suite and the UIA filter at the same time makes two independent test processes compete for the same llama-server lifecycle and creates a false residual-process failure.

## Real UI validation

The Release UI was driven through Windows UI Automation:

- Main navigation opened the new editor workspace.
- `Привет как тваи дела` produced two local cards.
- `тваи → твои` was applied with one click.
- The remaining punctuation card stayed visible after the targeted replacement.
- The editor disabled WPF/system spell checking, updated the word/issue counters, and exposed accessible names.
- Settings and diagnostics rendered without binding or accessibility-tree errors.

## Release

- Framework-dependent: `artifacts/release-ux-stable-framework-dependent/WriteLite/WriteLite.exe`
- Files: 1867
- Size: 2726.93 MB
- Requires the .NET 10 Desktop Runtime.

Self-contained restore remains blocked by repeated TLS/decryption failures while downloading the official .NET 10.0.9 runtime packages from NuGet. It is not reported as ready.

## Screenshots

Screenshots are stored under `artifacts/ui-validation/`. They contain only WriteLite and synthetic test text.

## Not claimed

- No 20-minute memory soak was completed in this iteration.
- No full Edge/Chrome/Word interaction matrix was completed.
- The external overlay click limitation of the automation driver remains documented in `UX_STABILITY_BASELINE.md`.
