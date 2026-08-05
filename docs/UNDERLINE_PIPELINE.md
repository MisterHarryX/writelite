# Inline underline pipeline

Only exact orthography, punctuation and grammar spans are candidates for the
inline overlay. Before rendering, WriteLite verifies editability, current
target identity, text/generation/request state, UTF-16 span boundaries and
the original fragment. It requests UI Automation rectangles for each exact
span; no full-field fallback is allowed. If rectangles are unavailable, the
issue remains available through the indicator rather than producing a long,
incorrect line.

`InlineIssueGeometryBuilder` separates per-issue geometry and special-cases
zero-length punctuation as a compact marker. The overlay is selectively
clickable: `HTCLIENT` is returned only within the precise error hit area and
the rest is `HTTRANSPARENT`, preserving host caret, selection and scrolling.

Protected-token filtering and duplicate/overlap processing live in the
analysis path. A lexical replacement is revalidated against target, generation
and text version before the one chosen range is changed.
