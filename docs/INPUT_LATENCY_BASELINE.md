# Input latency baseline

Branch: `fix/input-latency-inline-corrections`.

Before this iteration the built-in editor's `TextChanged` handler synchronously split the complete document to count words, copied the complete text, mutated cancellation state, and entered an async whole-document analysis flow on every edit. The external UI Automation monitor could synchronously call `Dispatcher.Invoke` while sampling keyboard activity. Overlay snapshots recalculated all UIA rectangles even when fast and deep stages described the same target and text.

The last clean Release baseline before this branch was 353 passed, 3 skipped and 0 failed tests; UIA 3/3 and Stress 2/2. This records the code-path baseline. Runtime percentiles are recorded after deterministic instrumentation is available, so no synthetic pre-change percentile is presented as measured.

Target thresholds: input handler p50 < 3 ms, p95 < 8 ms, no UI-thread block > 50 ms; fast analysis after 120-220 ms; deep analysis after 700-1200 ms.
