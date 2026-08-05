#!/usr/bin/env python3
"""
Fixed WriteLite GEC benchmark suite.

Runs a deterministic rule baseline (and optional neural model) against
ai/data/benchmark/fixed_suite.jsonl and writes a machine-readable report.

Metrics: precision/recall/F0.5 (sentence-level change agreement), false fixes,
protected-token damage, latency, auto-apply safety share.

Does not require GPU. Does not log raw user text beyond the fixed suite.

Usage:
  python ai/scripts/run_fixed_benchmark.py
  python ai/scripts/run_fixed_benchmark.py --model-dir path/to/hf
"""

from __future__ import annotations

import argparse
import json
import statistics
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from writelight_ai.metrics import evaluate_pairs  # noqa: E402


def baseline_predict(source: str, row: dict) -> str:
    text = source
    replacements = {
        "дила": "дела",
        "небыл": "не был",
        "небыла": "не была",
        "вообщем": "в общем",
        "I has": "I have",
        "recieve": "receive",
        "seperate": "separate",
        "definately": "definitely",
        "очень очень": "очень",
    }
    for a, b in replacements.items():
        text = text.replace(a, b)
        text = text.replace(a.capitalize(), b.capitalize())
    # light punctuation
    if text.startswith("Когда ") and ", " not in text and " было " in text:
        text = text.replace(" пришёл было ", " пришёл, было ", 1)
    if text.startswith("However ") and not text.startswith("However,"):
        text = "However," + text[len("However") :]
    return text


def protected_ok(source: str, pred: str, must_not_change: list[str]) -> bool:
    for token in must_not_change or []:
        if token and token in source and token not in pred:
            return False
    return True


def main() -> int:
    p = argparse.ArgumentParser(description="WriteLite fixed benchmark")
    p.add_argument(
        "--suite",
        type=Path,
        default=ROOT / "data" / "benchmark" / "fixed_suite.jsonl",
    )
    p.add_argument("--output", type=Path, default=ROOT / "outputs" / "fixed_benchmark_report.json")
    p.add_argument("--model-dir", type=Path, default=None)
    p.add_argument("--baseline-only", action="store_true")
    args = p.parse_args()

    if not args.suite.exists():
        print("Suite missing:", args.suite)
        return 2

    rows = []
    with args.suite.open(encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            rows.append(json.loads(line))

    predict = baseline_predict
    backend = "rule-baseline"

    if args.model_dir and not args.baseline_only:
        try:
            from transformers import AutoModelForSeq2SeqLM, AutoTokenizer
            import torch

            tok = AutoTokenizer.from_pretrained(args.model_dir)
            model = AutoModelForSeq2SeqLM.from_pretrained(args.model_dir)
            model.eval()
            device = "cuda" if torch.cuda.is_available() else "cpu"
            model.to(device)
            backend = f"hf:{args.model_dir.name}"

            def predict(source: str, row: dict) -> str:  # noqa: F811
                inputs = tok("gec: " + source, return_tensors="pt", truncation=True, max_length=256).to(device)
                with torch.no_grad():
                    out = model.generate(**inputs, max_length=256)
                return tok.decode(out[0], skip_special_tokens=True)

        except Exception as ex:  # noqa: BLE001
            print("Model load failed, using baseline:", ex)
            predict = baseline_predict
            backend = "rule-baseline-fallback"

    latencies = []
    protected_damage = 0
    false_fixes = 0
    auto_safe = 0
    pairs = []

    for row in rows:
        source = row["source"]
        target = row["target"]
        t0 = time.perf_counter()
        pred = predict(source, row)
        latencies.append((time.perf_counter() - t0) * 1000)
        pairs.append({"source": source, "target": target, "prediction": pred, **row})

        if not protected_ok(source, pred, row.get("must_not_change") or []):
            protected_damage += 1
        if source == target and pred != source:
            false_fixes += 1
        # heuristic: auto-safe if prediction equals target or is identity on clean
        if pred == target or (source == target and pred == source):
            auto_safe += 1

    # reuse library metrics on list-of-dict shape expected by evaluate_pairs
    eval_rows = [{"source": r["source"], "target": r["target"]} for r in rows]

    def pred_fn(source: str, row: dict) -> str:
        for p in pairs:
            if p["source"] == source:
                return p["prediction"]
        return source

    report = evaluate_pairs(eval_rows, pred_fn)
    report.update(
        {
            "backend": backend,
            "suite": str(args.suite),
            "n": len(rows),
            "false_fixes": false_fixes,
            "protected_token_damage": protected_damage,
            "auto_apply_safe_share": auto_safe / max(1, len(rows)),
            "latency_ms": {
                "p50": statistics.median(latencies) if latencies else 0,
                "p95": sorted(latencies)[max(0, int(len(latencies) * 0.95) - 1)] if latencies else 0,
                "mean": statistics.mean(latencies) if latencies else 0,
            },
            "categories": {},
        }
    )

    by_cat: dict[str, list] = {}
    for p in pairs:
        by_cat.setdefault(p.get("category", "other"), []).append(p)
    for cat, items in by_cat.items():
        ok = sum(1 for i in items if i["prediction"] == i["target"])
        report["categories"][cat] = {"n": len(items), "exact_match": ok / max(1, len(items))}

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({k: report[k] for k in report if k != "details"}, ensure_ascii=False, indent=2))
    print("Wrote", args.output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
