"""Measure the reranker: the fine-tuned encoder, and the exported artefact.

Two things are worth measuring separately. The PyTorch checkpoint says whether
the training worked. The quantised ONNX graph says whether what actually ships
still agrees with it — int8 weights are a real change, and a reranker that
disagrees with its own evaluation is worse than no reranker.

The headline number is real-word accuracy: given a sentence and a confusable
alternative, does the model prefer the correct member? That is the capability
the lexical layer structurally cannot have.
"""

from __future__ import annotations

import argparse
import io
import json
import os
import sys
import time
from collections import defaultdict

import numpy as np

DEFAULT_MODEL = os.path.join("models", "writelight-reranker")
DEFAULT_DATA = os.path.join("ai", "data", "errors")


def load_jsonl(path: str):
    with io.open(path, encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if line:
                yield json.loads(line)


def evaluate_torch(model_dir: str, records: list[dict], batch_size: int = 64) -> dict:
    import torch
    from transformers import AutoModelForSequenceClassification, AutoTokenizer

    source = os.path.join(model_dir, "hf")
    tokenizer = AutoTokenizer.from_pretrained(source)
    model = AutoModelForSequenceClassification.from_pretrained(source).eval()

    scores = np.zeros(len(records))
    with torch.no_grad():
        for start in range(0, len(records), batch_size):
            chunk = records[start:start + batch_size]
            encoded = tokenizer(
                [r["sentence"] for r in chunk],
                padding=True, truncation=True, max_length=64, return_tensors="pt",
            )
            logits = model(**encoded).logits
            probabilities = torch.softmax(logits, dim=-1)[:, 1]
            scores[start:start + len(chunk)] = probabilities.numpy()

    return summarise(records, scores)


def evaluate_onnx(model_dir: str, records: list[dict], batch_size: int = 64) -> dict:
    import onnxruntime as ort
    from transformers import AutoTokenizer

    tokenizer = AutoTokenizer.from_pretrained(os.path.join(model_dir, "hf"))
    session = ort.InferenceSession(
        os.path.join(model_dir, "model.onnx"),
        providers=["CPUExecutionProvider"],
    )
    names = {i.name for i in session.get_inputs()}

    scores = np.zeros(len(records))
    latencies: list[float] = []
    for start in range(0, len(records), batch_size):
        chunk = records[start:start + batch_size]
        encoded = tokenizer(
            [r["sentence"] for r in chunk],
            padding="max_length", truncation=True, max_length=64, return_tensors="np",
        )
        feed = {k: v.astype(np.int64) for k, v in encoded.items() if k in names}
        began = time.perf_counter()
        logits = session.run(None, feed)[0]
        latencies.append((time.perf_counter() - began) * 1000 / len(chunk))
        shifted = logits - logits.max(axis=-1, keepdims=True)
        exponentiated = np.exp(shifted)
        scores[start:start + len(chunk)] = (exponentiated / exponentiated.sum(axis=-1, keepdims=True))[:, 1]

    result = summarise(records, scores)
    result["msPerSentence"] = round(float(np.mean(latencies)), 3)
    return result


def summarise(records: list[dict], scores: np.ndarray) -> dict:
    labels = np.array([int(r["label"]) for r in records])
    predictions = (scores >= 0.5).astype(int)

    true_positive = int(((predictions == 1) & (labels == 1)).sum())
    false_positive = int(((predictions == 1) & (labels == 0)).sum())
    false_negative = int(((predictions == 0) & (labels == 1)).sum())
    precision = true_positive / max(true_positive + false_positive, 1)
    recall = true_positive / max(true_positive + false_negative, 1)

    # Pairwise accuracy: within each (correct, corrupted) pair, does the correct
    # member score higher? This is the decision the pipeline actually makes, and
    # unlike thresholded accuracy it does not depend on calibration.
    pairs: dict[str, dict[int, float]] = defaultdict(dict)
    categories: dict[str, dict[str, dict[int, float]]] = defaultdict(lambda: defaultdict(dict))
    for record, score in zip(records, scores):
        key = record.get("pair_id")
        if key is None:
            continue
        pairs[key][int(record["label"])] = float(score)
        categories[record.get("category", "other")][key][int(record["label"])] = float(score)

    def pairwise(source) -> tuple[int, int]:
        correct = total = 0
        for values in source.values():
            if 0 in values and 1 in values:
                total += 1
                correct += int(values[1] > values[0])
        return correct, total

    correct, total = pairwise(pairs)
    per_category = {}
    for category, group in categories.items():
        category_correct, category_total = pairwise(group)
        if category_total:
            per_category[category] = {
                "pairs": category_total,
                "accuracy": round(category_correct / category_total, 4),
            }

    return {
        "examples": len(records),
        "accuracy": round(float((predictions == labels).mean()), 4),
        "precision": round(precision, 4),
        "recall": round(recall, 4),
        "f1": round(2 * precision * recall / max(precision + recall, 1e-9), 4),
        "pairwiseAccuracy": round(correct / max(total, 1), 4),
        "pairs": total,
        "byCategory": per_category,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", default=DEFAULT_MODEL)
    parser.add_argument("--data", default=DEFAULT_DATA)
    parser.add_argument("--split", default="test")
    parser.add_argument("--out", default=None)
    parser.add_argument("--skip-torch", action="store_true")
    args = parser.parse_args()

    path = os.path.join(args.data, f"rerank_{args.split}.jsonl")
    records = list(load_jsonl(path))
    if not records:
        raise SystemExit(f"no records in {path}")

    report = {"split": args.split, "records": len(records), "model": args.model}
    if not args.skip_torch:
        report["torch"] = evaluate_torch(args.model, records)
    if os.path.exists(os.path.join(args.model, "model.onnx")):
        report["onnxInt8"] = evaluate_onnx(args.model, records)

    out = args.out or os.path.join("ai", "outputs", "reranker", f"eval-{args.split}.json")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with io.open(out, "w", encoding="utf-8") as handle:
        json.dump(report, handle, ensure_ascii=False, indent=2)

    print(json.dumps({
        k: ({kk: vv for kk, vv in v.items() if kk != "byCategory"} if isinstance(v, dict) else v)
        for k, v in report.items()
    }, ensure_ascii=False))
    for backend in ("torch", "onnxInt8"):
        if backend in report:
            print(backend, "by category:",
                  json.dumps(report[backend]["byCategory"], ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
