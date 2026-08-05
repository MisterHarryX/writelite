#!/usr/bin/env python3
"""End-to-end smoke: dataset → baseline eval (and optional tiny train if deps present)."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def run(cmd: list[str]) -> int:
    print(">", " ".join(cmd))
    return subprocess.call(cmd, cwd=str(ROOT))


def main() -> int:
    py = sys.executable
    code = run([py, "scripts/prepare_dataset.py", "--seed", "42"])
    if code != 0:
        return code
    code = run([py, "scripts/evaluate.py", "--baseline"])
    if code != 0:
        return code
    # Optional train — may skip if torch missing
    code = run([py, "scripts/train.py", "--config", "configs/smoke.yaml", "--smoke"])
    print("Smoke pipeline finished with train exit", code)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
