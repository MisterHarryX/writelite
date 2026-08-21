# The frozen Russian benchmark

`ru_frozen_v1.jsonl` is WriteLite's golden set: 841 hand-built Russian items across
14 categories, and the only corpus that published quality numbers are measured
against. This file explains what protects it and how to change it.

## Why it is frozen

Every benchmark number in `docs/` is a statement about *these exact 841 items*. If the
corpus changes, those numbers stop being comparable to anything measured after the
change — including the before/after tables that justify shipping a model. Freezing is
what makes "we improved punctuation from 0.26 to 0.41" a claim rather than a feeling.

## Hash policy — byte-exact LF

The recorded `sha256` covers the file's **exact bytes**: UTF-8, LF line endings, one
JSON object per line, trailing newline. There is no normalisation step at verification
time; the bytes either match or they do not.

Three things keep that true:

| Mechanism | What it prevents |
|---|---|
| `build.py` writes in binary mode with `"\n"` joins | The generator can never emit CRLF |
| `.gitattributes` marks `*.jsonl -text` | Git's `text=auto` cannot convert on checkout |
| `GoldenDatasetIntegrityTests.Golden_MatchesItsPinnedHash` | Drift fails the build, and says which kind it is |

This policy was adopted after a real incident. On 2026-08-12 the working copy had CRLF
line endings, so the file hashed to `392f60ce…` while its manifest recorded `16adfd7c…`.
The content was byte-identical once normalised — nothing was lost — but nothing in the
repository would have told the difference between that and genuine tampering, because
nothing checked. The test now distinguishes the two cases and prints the right fix for
each.

## Current version

| | |
|---|---|
| name | `ru_frozen_v1` |
| version | 1 |
| items | 841 |
| sha256 | `16adfd7c763d8c17c696040a90178a8e7e9c83f5d6d09f99eb017538ec91293d` |
| generator | `ai/data/benchmark/_gen/build.py` |

The hash and version are pinned **twice**: in `ru_frozen_v1.meta.json`, and as constants
in `tests/WriteLite.Tests/Language/GoldenDatasetIntegrityTests.cs`. That duplication is
deliberate. If the test read the expected hash from the manifest it would agree with
whatever the manifest said, so regenerating the corpus would update both and the test
would pass while the benchmark silently changed. Changing the corpus has to be an
explicit edit in two places, in two languages, by someone who meant it.

## Reproducing it

```bash
python ai/data/benchmark/_gen/build.py
```

Deterministic: same inputs produce byte-identical output. Re-running against an
unmodified corpus rewrites the same bytes and exits 0. The generator verifies every
error span (`source[start:end] == original`), rejects overlapping spans, rejects
duplicate sources, and bounds every item at 3–30 words.

## Repairing drift

When the content is intact but the bytes are not canonical — the CRLF case above:

```bash
python ai/data/benchmark/_gen/build.py --force
```

`--force` is only for this. It prints the before and after hashes so the change is
visible in the log.

## Changing the benchmark

You do not change `v1`. Without `--force`, the generator refuses to overwrite it with
different content and tells you so:

```
refusing to overwrite a frozen corpus with different content.
```

To add or fix items, create a v2:

1. Copy `_gen/build.py` to `_gen/build_v2.py` and edit that.
2. Emit `ru_frozen_v2.jsonl` and `ru_frozen_v2.meta.json`.
3. Register v2's pinned hash in `GoldenDatasetIntegrityTests`.
4. **Leave v1 in place.** Published results are measured against it, and deleting it
   retroactively invalidates every table that cites it.
5. Re-run the benchmark on both and report them separately until v1 is retired.

## Separation from training data

The golden set must never reach a training run. Two mechanisms enforce this:

- `ai/scripts/build_ru_error_corpus.py` excludes any sentence appearing in the frozen
  benchmark when it builds `ai/data/errors/`.
- `GoldenDatasetIntegrityTests.TrainingSplits_ShareNoItemWithGolden` checks the result,
  comparing whitespace-collapsed strings across *every* string field of
  `train/validation/test` and `rerank_train/rerank_validation/rerank_test`.
- `GoldenDatasetIntegrityTests.TrainingConfigs_DoNotReferenceGoldenData` fails if any
  `ai/configs/*.yaml` mentions `ru_frozen`, `benchmark` or `golden`.

Verified 2026-08-12: **0 overlapping items across all six splits** (24 344 train,
1 973 validation, 2 004 test, 14 682 rerank-train, 1 214 rerank-validation,
1 240 rerank-test).

## The four roles

| Split | Path | May a model train on it? |
|---|---|---|
| train | `ai/data/errors/train.jsonl` | yes |
| validation | `ai/data/errors/validation.jsonl` | for early stopping / tuning only |
| test | `ai/data/errors/test.jsonl` | no — held out for model-level evaluation |
| **golden** | `ai/data/benchmark/ru_frozen_v1.jsonl` | **no — product-level reporting only** |

`test` and `golden` are not the same thing and are not interchangeable. `test` measures
the model; `golden` measures the assembled product through `langbench`, including the
rule, dictionary and LanguageTool layers a model never sees.
