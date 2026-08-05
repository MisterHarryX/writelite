#!/usr/bin/env python3
"""Quantize exported ONNX encoder with ONNX Runtime dynamic quantization when available."""

from __future__ import annotations

import argparse
import json
from pathlib import Path


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--model-dir", type=Path, default=Path("../models/writelight-gec"))
    args = p.parse_args()
    src = args.model_dir / "encoder.onnx"
    dst = args.model_dir / "encoder.int8.onnx"
    if not src.exists():
        print("No encoder.onnx — skip quantization (Lite profile still works).")
        report = {"status": "skipped", "reason": "encoder.onnx missing"}
        (args.model_dir / "quantize_report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        return 0
    try:
        from onnxruntime.quantization import quantize_dynamic, QuantType

        quantize_dynamic(str(src), str(dst), weight_type=QuantType.QInt8)
        report = {"status": "ok", "output": str(dst)}
    except Exception as ex:  # noqa: BLE001
        report = {"status": "failed", "error": str(ex)}
    args.model_dir.mkdir(parents=True, exist_ok=True)
    (args.model_dir / "quantize_report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(report)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
