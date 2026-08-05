#!/usr/bin/env python3
"""Export a fine-tuned seq2seq model folder to ONNX (best-effort) + manifest for WriteLite."""

from __future__ import annotations

import argparse
import json
import shutil
import sys
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--model-dir", type=Path, required=True)
    p.add_argument("--output-dir", type=Path, default=ROOT.parent / "models" / "writelight-gec")
    p.add_argument("--model-version", type=str, default="writelight-gec-mt5-small-1.0.0")
    args = p.parse_args()

    args.output_dir.mkdir(parents=True, exist_ok=True)
    if not args.model_dir.exists():
        print("Model dir missing:", args.model_dir)
        return 2

    # Copy HF files as a portable pack even if ONNX export fails.
    pack_hf = args.output_dir / "hf"
    if pack_hf.exists():
        shutil.rmtree(pack_hf)
    shutil.copytree(args.model_dir, pack_hf)

    onnx_ok = False
    onnx_error = None
    try:
        from transformers import AutoModelForSeq2SeqLM, AutoTokenizer
        import torch

        tok = AutoTokenizer.from_pretrained(args.model_dir)
        model = AutoModelForSeq2SeqLM.from_pretrained(args.model_dir)
        model.eval()
        # Encoder export is a simplified path; full encoder-decoder ONNX is complex.
        # We write a placeholder note and keep HF weights for now.
        note = args.output_dir / "ONNX_EXPORT_NOTES.txt"
        note.write_text(
            "Full encoder-decoder ONNX export for mT5 is environment-specific.\n"
            "HF weights are packaged under hf/ for conversion with optimum/onnxruntime-tools.\n"
            "WriteLite Lite profile does not require ONNX.\n",
            encoding="utf-8",
        )
        # Try a minimal encoder export if possible.
        try:
            dummy = tok("gec: hello", return_tensors="pt")
            encoder = model.get_encoder()
            torch.onnx.export(
                encoder,
                (dummy["input_ids"], dummy["attention_mask"]),
                str(args.output_dir / "encoder.onnx"),
                input_names=["input_ids", "attention_mask"],
                output_names=["last_hidden_state"],
                dynamic_axes={
                    "input_ids": {0: "batch", 1: "seq"},
                    "attention_mask": {0: "batch", 1: "seq"},
                    "last_hidden_state": {0: "batch", 1: "seq"},
                },
                opset_version=14,
            )
            onnx_ok = True
        except Exception as ex:  # noqa: BLE001
            onnx_error = str(ex)
    except Exception as ex:  # noqa: BLE001
        onnx_error = str(ex)

    manifest = {
        "modelVersion": args.model_version,
        "schemaVersion": 1,
        "family": "writelight-gec",
        "exportedAt": datetime.now(timezone.utc).isoformat(),
        "hfPath": "hf",
        "onnxEncoder": "encoder.onnx" if onnx_ok else None,
        "onnxOk": onnx_ok,
        "onnxError": onnx_error,
        "license": "Apache-2.0 (base mT5) + WriteLite fine-tune weights",
    }
    (args.output_dir / "model.manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print("Export complete:", args.output_dir)
    print(json.dumps(manifest, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
