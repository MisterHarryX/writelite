#!/usr/bin/env python3
"""Evaluate a checkpoint or identity/baseline corrector on the test split."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from writelight_ai.dataset import read_jsonl  # noqa: E402
from writelight_ai.metrics import evaluate_pairs  # noqa: E402


def baseline_predict(source: str, row: dict) -> str:
    """Very small rule baseline for offline evaluation without a neural model."""
    text = source
    # mirror a few Lite rules
    replacements = {
        "небыл": "не был",
        "вообщем": "в общем",
        "здраствуйте": "здравствуйте",
        "извените": "извините",
        "I has": "I have",
        "recieve": "receive",
        "seperate": "separate",
        "definately": "definitely",
        "becuase": "because",
        "tommorow": "tomorrow",
    }
    for a, b in replacements.items():
        text = text.replace(a, b)
        text = text.replace(a.capitalize(), b.capitalize())
    if text and text[0].islower():
        text = text[0].upper() + text[1:]
    if text and text[-1].isalnum():
        text = text + ("?" if text.lower().startswith(("how", "what", "как", "что")) else ".")
    return text


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--dataset", type=Path, default=ROOT / "data" / "processed" / "test.jsonl")
    p.add_argument("--model-dir", type=Path, default=None, help="HF seq2seq checkpoint directory")
    p.add_argument("--output", type=Path, default=ROOT / "outputs" / "eval_report.json")
    p.add_argument("--baseline", action="store_true", help="Use rule baseline instead of neural model")
    args = p.parse_args()

    if not args.dataset.exists():
        print("Dataset missing. Run prepare_dataset.py first.")
        return 2

    rows = read_jsonl(args.dataset)
    predict = baseline_predict

    if args.model_dir and not args.baseline:
        try:
            from transformers import AutoModelForSeq2SeqLM, AutoTokenizer
            import torch

            tok = AutoTokenizer.from_pretrained(args.model_dir)
            model = AutoModelForSeq2SeqLM.from_pretrained(args.model_dir)
            model.eval()
            device = "cuda" if torch.cuda.is_available() else "cpu"
            model.to(device)

            def predict(source: str, row: dict) -> str:  # noqa: F811
                inputs = tok("gec: " + source, return_tensors="pt", truncation=True, max_length=256).to(device)
                with torch.no_grad():
                    out = model.generate(**inputs, max_length=256)
                return tok.decode(out[0], skip_special_tokens=True)
        except Exception as ex:  # noqa: BLE001
            print("Failed to load model, falling back to baseline:", ex)
            predict = baseline_predict

    report = evaluate_pairs(rows, predict)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps(report["overall"], indent=2, ensure_ascii=False))
    print("Wrote", args.output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
