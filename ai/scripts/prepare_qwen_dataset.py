#!/usr/bin/env python3
"""Build WriteLite-Qwen chat dataset (≥10k by default)."""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from writelight_ai.build_chat_dataset import build_chat_dataset  # noqa: E402
from writelight_ai.prompts import MODEL_VERSION_PLACEHOLDER  # noqa: E402


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--seed-dir", type=Path, default=ROOT / "data" / "seed")
    p.add_argument("--output-dir", type=Path, default=ROOT / "data" / "chat")
    p.add_argument("--seed", type=int, default=42)
    p.add_argument("--target-size", type=int, default=12_000)
    p.add_argument("--model-version", type=str, default=MODEL_VERSION_PLACEHOLDER)
    args = p.parse_args()

    counts = build_chat_dataset(
        seed_dir=args.seed_dir,
        output_dir=args.output_dir,
        seed=args.seed,
        target_size=args.target_size,
        model_version=args.model_version,
    )
    print("WriteLite-Qwen chat dataset:", counts)
    print("Output:", args.output_dir)
    if counts["all"] < 10_000:
        print("WARNING: dataset smaller than 10_000", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
