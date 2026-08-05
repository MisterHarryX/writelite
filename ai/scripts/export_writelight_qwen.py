#!/usr/bin/env python3
"""
Export WriteLite-Qwen pack for local desktop runtime.

Steps:
1) Merge LoRA adapter into base (optional, PEFT merge).
2) Write model.manifest.json for C# host.
3) Optionally attempt GGUF conversion if llama.cpp convert tools exist.

End-user runtime does not need Python: C# host loads GGUF or talks to bundled llama-server.
"""

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
    p.add_argument("--adapter-dir", type=Path, required=True)
    p.add_argument("--base-model", type=str, default="Qwen/Qwen2.5-0.5B-Instruct")
    p.add_argument("--output-dir", type=Path, default=ROOT.parent / "models" / "writelight-qwen")
    p.add_argument("--model-version", type=str, default="WriteLite-Qwen-0.6B-GEC-1.0.0-dev")
    p.add_argument("--merge", action="store_true", help="Merge LoRA into base FP16/BF16")
    p.add_argument("--profile", type=str, default="Lite", choices=["Lite", "Quality"])
    args = p.parse_args()

    if not args.adapter_dir.exists():
        print("Adapter missing:", args.adapter_dir, file=sys.stderr)
        return 2

    args.output_dir.mkdir(parents=True, exist_ok=True)
    adapter_out = args.output_dir / "adapter"
    if adapter_out.exists():
        shutil.rmtree(adapter_out)
    shutil.copytree(args.adapter_dir, adapter_out)

    merged_ok = False
    merged_error = None
    if args.merge:
        try:
            import torch
            from peft import PeftModel
            from transformers import AutoModelForCausalLM, AutoTokenizer

            print("Merging LoRA into base…")
            tok = AutoTokenizer.from_pretrained(args.base_model, trust_remote_code=True)
            base = AutoModelForCausalLM.from_pretrained(
                args.base_model,
                trust_remote_code=True,
                dtype=torch.float16 if torch.cuda.is_available() else torch.float32,
                device_map="cpu",
            )
            model = PeftModel.from_pretrained(base, str(args.adapter_dir))
            merged = model.merge_and_unload()
            merged_dir = args.output_dir / "merged-hf"
            if merged_dir.exists():
                shutil.rmtree(merged_dir)
            merged_dir.mkdir(parents=True, exist_ok=True)
            merged.save_pretrained(str(merged_dir), safe_serialization=True)
            tok.save_pretrained(str(merged_dir))
            merged_ok = True
            print("Merged HF model:", merged_dir)
        except Exception as ex:  # noqa: BLE001
            merged_error = str(ex)
            print("Merge failed:", ex, file=sys.stderr)

    manifest = {
        "modelVersion": args.model_version,
        "schemaVersion": 1,
        "family": "WriteLite-Qwen",
        "baseModel": args.base_model,
        "profile": args.profile,
        "runtime": "llama.cpp-gguf-or-hf-adapter",
        "exportedAt": datetime.now(timezone.utc).isoformat(),
        "paths": {
            "adapter": "adapter",
            "mergedHf": "merged-hf" if merged_ok else None,
            "gguf": "model.gguf" if (args.output_dir / "model.gguf").exists() else None,
        },
        "inference": {
            "temperature": 0.0,
            "maxNewTokens": 96,
            "thinking": False,
            "systemPromptFixed": True,
        },
        "mergeOk": merged_ok,
        "mergeError": merged_error,
        "license": "Apache-2.0 (Qwen base) + WriteLite fine-tune adapter",
    }
    (args.output_dir / "model.manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")

    # Prompt template for C# host
    from writelight_ai.prompts import SYSTEM_PROMPT  # noqa: E402

    (args.output_dir / "system_prompt.txt").write_text(SYSTEM_PROMPT, encoding="utf-8")
    print(json.dumps(manifest, indent=2))
    print("Pack ready:", args.output_dir)
    return 0
    print("Note: convert merged-hf → GGUF with llama.cpp convert script when available.")
    return 0


if __name__ == "__main__":
    sys.path.insert(0, str(ROOT))
    raise SystemExit(main())
