#!/usr/bin/env python3
"""Prepare train/validation/test JSONL from seed clean sentences."""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from writelight_ai.dataset import prepare_dataset  # noqa: E402


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--seed-dir", type=Path, default=ROOT / "data" / "seed")
    p.add_argument("--output-dir", type=Path, default=ROOT / "data" / "processed")
    p.add_argument("--seed", type=int, default=42)
    p.add_argument("--errors-per-sentence", type=int, default=3)
    args = p.parse_args()

    counts = prepare_dataset(
        seed_dir=args.seed_dir,
        output_dir=args.output_dir,
        seed=args.seed,
        errors_per_sentence=args.errors_per_sentence,
    )
    print("Dataset prepared:", counts)
    print("Output:", args.output_dir)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
