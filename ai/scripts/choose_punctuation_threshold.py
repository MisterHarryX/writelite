"""Chooses and freezes the punctuation model's acceptance threshold, on validation only.

A classifier's argmax is a 0.5 threshold, and 0.5 is a statement about the *training* prior.
Training is balanced roughly 50/50 because an uncapped corpus is 95 % NO_CHANGE and teaches a
model to answer NO_CHANGE unconditionally. Production is the uncapped distribution, and it
carries an asymmetry the training loss never saw: WriteLite runs continuously while somebody
writes, so a comma proposed into correct text costs far more than a comma missed.

That asymmetry has a number attached. The deterministic pipeline sits at 0.348 false positives
per 100 clean tokens, and Phase 6's acceptance criteria say it must stay close to it. This
script converts a per-boundary false-positive rate into that product figure, so a threshold is
chosen against the metric the product is judged on rather than against F1.

The threshold is written into the deployed artefact and frozen **before** the golden benchmark
is evaluated. Choosing it afterwards would be tuning against the acceptance gate.

    python ai/scripts/choose_punctuation_threshold.py --experiment PUNC-A
"""

from __future__ import annotations

import argparse
import io
import json
import os

import numpy as np

#: Clean-text tokens and comma-free word boundaries in the frozen golden corpus's `clean`
#: category — the exact denominator langbench uses for FP/100. Counted from the corpus, not
#: from any model's behaviour on it, so this is a property of the benchmark rather than a
#: result. 277 items, 2010 word tokens, 1614 boundaries where a comma could be inserted.
GOLDEN_CLEAN_TOKENS = 2010
GOLDEN_CLEAN_BOUNDARIES = 1614

#: The deterministic pipeline's measured false-positive rate on clean text.
BASELINE_FP_PER_100 = 0.348


def metrics_at(probabilities: np.ndarray, labels: np.ndarray, threshold: float) -> dict:
    predictions = (probabilities[:, 1] >= threshold).astype(int)
    tp = int(((predictions == 1) & (labels == 1)).sum())
    fp = int(((predictions == 1) & (labels == 0)).sum())
    fn = int(((predictions == 0) & (labels == 1)).sum())
    tn = int(((predictions == 0) & (labels == 0)).sum())
    precision = tp / max(tp + fp, 1)
    recall = tp / max(tp + fn, 1)
    false_positive_rate = fp / max(tn + fp, 1)
    return {
        "threshold": round(threshold, 4),
        "precision": round(precision, 4),
        "recall": round(recall, 4),
        "f1": round(2 * precision * recall / max(precision + recall, 1e-9), 4),
        "specificity": round(tn / max(tn + fp, 1), 4),
        "falsePositiveRate": round(false_positive_rate, 5),
        # What this rate would mean on the frozen corpus's clean half, in the units the
        # product's acceptance criteria are written in.
        "projectedAddedFpPer100": round(
            false_positive_rate * GOLDEN_CLEAN_BOUNDARIES / GOLDEN_CLEAN_TOKENS * 100, 4
        ),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--experiment", required=True)
    parser.add_argument(
        "--fp-budget",
        type=float,
        default=0.05,
        help="how much FP/100 the punctuation layer may add on top of the deterministic 0.348",
    )
    args = parser.parse_args()

    root = os.path.join("models", "writelite-punctuation", args.experiment)
    probabilities = np.load(os.path.join(root, "validation_probabilities.npy"))
    labels = np.load(os.path.join(root, "validation_labels.npy"))

    grid = (
        [i / 100 for i in range(50, 100)]
        + [0.99, 0.992, 0.994, 0.995, 0.996, 0.997, 0.998, 0.999, 0.9995]
    )
    sweep = [metrics_at(probabilities, labels, t) for t in grid]

    # The chosen threshold is the one with the best recall among those whose projected
    # contribution stays inside the budget. Recall is what the layer exists to buy; the budget
    # is what it must not spend to get it.
    affordable = [row for row in sweep if row["projectedAddedFpPer100"] <= args.fp_budget]
    chosen = max(affordable, key=lambda row: row["recall"]) if affordable else None

    print(f"{args.experiment}: {len(labels)} validation boundaries")
    print(f"budget: +{args.fp_budget} FP/100 on top of the deterministic {BASELINE_FP_PER_100}")
    print()
    print(f"{'thr':>7} {'P':>7} {'R':>7} {'F1':>7} {'FPrate':>8} {'+FP/100':>9}")
    for row in sweep:
        if row["threshold"] < 0.9 and round(row["threshold"] * 100) % 5:
            continue
        mark = "  <-- chosen" if chosen and row["threshold"] == chosen["threshold"] else ""
        print(f"{row['threshold']:7.4f} {row['precision']:7.4f} {row['recall']:7.4f} "
              f"{row['f1']:7.4f} {row['falsePositiveRate']:8.5f} "
              f"{row['projectedAddedFpPer100']:9.4f}{mark}")

    print()
    if chosen is None:
        print("NO THRESHOLD MEETS THE BUDGET. The model cannot be deployed at this budget; "
              "the tightest available operating point is:")
        print(json.dumps(sweep[-1], indent=2))
    else:
        print("chosen:")
        print(json.dumps(chosen, indent=2))

    out = os.path.join("experiments", args.experiment, "threshold.json")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with io.open(out, "w", encoding="utf-8") as handle:
        json.dump({
            "experiment": args.experiment,
            "fpBudgetPer100": args.fp_budget,
            "baselineFpPer100": BASELINE_FP_PER_100,
            "chosen": chosen,
            "tightestAvailable": sweep[-1],
            "sweep": sweep,
            "frozenBeforeGoldenEvaluation": True,
        }, handle, ensure_ascii=False, indent=2)
    print(f"\nwritten: {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
