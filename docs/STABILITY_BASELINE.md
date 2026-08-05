# Stability baseline

Date: 2026-07-19
Branch: `fix/stability-ui-dictionaries`

## Reproducible command line baseline

`dotnet clean`, restore, Release build and Release tests were run before the
stability changes. The build completed without errors; the baseline suite had
334 passed and 3 skipped tests. The skips require a live local AI host and are
intentional environment-dependent tests.

The working tree contained pre-existing local model archives, model folders and
runtime logs. They are deliberately not part of this branch or release commit.

## Scope of the baseline

The baseline confirms compilation and deterministic tests. After the stability
changes, the full Release suite completed with **336 passed, 3 skipped** (339
total); the 3 skips require an opt-in live AI host. It is not evidence
of a 15-minute interactive host-application run, RAM profiling or an installed
Word/Edge/Chrome scenario. Those must be recorded separately on a desktop with
the required applications available.
