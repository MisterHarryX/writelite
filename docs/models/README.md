# Model cards

Every model WriteLite ships has a card here. A weights file with no card is not a shippable
artefact: without the training data, the split it was evaluated on and the licence of what it
was built from, nobody — including a later version of this project — can say whether a number
measured against it means anything.

Each card states, at minimum:

```
name          the deployed artefact's identity, including version
task          the one question the model answers
architecture  base model, parameter count, and what was changed
training data path, hash, split sizes, provenance and licence
validation    held-out results, on the split used to choose hyperparameters
test          held-out results, on a split touched once
golden        results on the frozen benchmark, measured after thresholds were frozen
size          on-disk bytes, RAM at inference, CPU latency
licence       of the artefact and of everything it derives from
```

Golden results are reported separately from validation and test on purpose. The frozen
benchmark is the acceptance gate and is evaluated once, after every choice has been made; a
card that reports only a golden number is hiding whether that number was chased.

| card | model | status |
|---|---|---|
| [writelite-punctuation-v1.md](writelite-punctuation-v1.md) | `WriteLite-Punctuation-v1` | see card |
| — | `writelight-reranker` (`reranker-001`) | shipped since Phase 3; see `docs/language-engine-phase6-training-report.md` §5 for its measured failure mode on candidate selection |
