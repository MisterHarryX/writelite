#!/usr/bin/env python3
"""Compare correction quality on held-out chat test set (baseline rules vs model JSON)."""

from __future__ import annotations

import argparse
import json
import re
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from writelight_ai.metrics import evaluate_pairs  # noqa: E402


def rules_baseline(source: str, row: dict) -> str:
    text = source
    for a, b in [
        ("небыл", "не был"),
        ("небыла", "не была"),
        ("вообщем", "в общем"),
        ("здраствуйте", "здравствуйте"),
        ("I has", "I have"),
        ("work good", "works well"),
        ("dont ", "don't "),
        ("recieve", "receive"),
    ]:
        text = text.replace(a, b)
        text = text.replace(a.capitalize(), b.capitalize())
    if text and text[0].islower():
        text = text[0].upper() + text[1:]
    if text and text[-1].isalnum():
        text += "."
    return text


def extract_corrected(model_out: str, fallback: str) -> str:
    s = model_out.strip()
    # strip markdown fences
    s = re.sub(r"^```(?:json)?\s*", "", s)
    s = re.sub(r"\s*```$", "", s)
    try:
        obj = json.loads(s)
        if isinstance(obj, dict) and "correctedText" in obj:
            return str(obj["correctedText"])
    except Exception:
        # try find first {...}
        m = re.search(r"\{.*\}", s, flags=re.S)
        if m:
            try:
                obj = json.loads(m.group(0))
                if isinstance(obj, dict) and "correctedText" in obj:
                    return str(obj["correctedText"])
            except Exception:
                pass
    return fallback


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--dataset", type=Path, default=ROOT / "data" / "chat" / "test.jsonl")
    p.add_argument("--adapter-dir", type=Path, default=None)
    p.add_argument("--base-model", type=str, default="Qwen/Qwen2.5-0.5B-Instruct")
    p.add_argument("--output", type=Path, default=ROOT / "outputs" / "hybrid_compare.json")
    p.add_argument("--limit", type=int, default=100)
    args = p.parse_args()

    if not args.dataset.exists():
        print("Missing test set", file=sys.stderr)
        return 2

    rows = []
    with args.dataset.open(encoding="utf-8") as f:
        for i, line in enumerate(f):
            if i >= args.limit:
                break
            obj = json.loads(line)
            rows.append(
                {
                    "source": obj.get("source") or json.loads(obj["messages"][1]["content"]).get("text"),
                    "target": obj.get("target")
                    or json.loads(obj["messages"][2]["content"]).get("correctedText"),
                    "language": obj.get("language", "und"),
                }
            )

    report = {"n": len(rows), "baselines": {}}

    # Rules only
    report["baselines"]["rules"] = evaluate_pairs(rows, rules_baseline)

    # Optional neural
    if args.adapter_dir and args.adapter_dir.exists():
        try:
            import torch
            from peft import PeftModel
            from transformers import AutoModelForCausalLM, AutoTokenizer
            from writelight_ai.prompts import SYSTEM_PROMPT, user_payload

            tok = AutoTokenizer.from_pretrained(args.adapter_dir, trust_remote_code=True)
            if tok.pad_token is None:
                tok.pad_token = tok.eos_token
            base = AutoModelForCausalLM.from_pretrained(
                args.base_model,
                trust_remote_code=True,
                torch_dtype=torch.float16 if torch.cuda.is_available() else torch.float32,
                device_map="auto" if torch.cuda.is_available() else None,
            )
            model = PeftModel.from_pretrained(base, str(args.adapter_dir))
            model.eval()

            def predict(source: str, row: dict) -> str:
                messages = [
                    {"role": "system", "content": SYSTEM_PROMPT},
                    {"role": "user", "content": user_payload(source, row.get("language"))},
                ]
                prompt = tok.apply_chat_template(messages, tokenize=False, add_generation_prompt=True)
                inputs = tok(prompt, return_tensors="pt")
                if torch.cuda.is_available():
                    inputs = {k: v.to(model.device) for k, v in inputs.items()}
                with torch.no_grad():
                    out = model.generate(**inputs, max_new_tokens=256, do_sample=False, pad_token_id=tok.pad_token_id)
                gen = tok.decode(out[0][inputs["input_ids"].shape[-1] :], skip_special_tokens=True)
                return extract_corrected(gen, rules_baseline(source, row))

            report["baselines"]["writelight_qwen"] = evaluate_pairs(rows, predict)

            def hybrid(source: str, row: dict) -> str:
                # Prefer model when protected tokens preserved and rewrite sane
                hyp = predict(source, row)
                # trivial hybrid: if model destroys URL-like tokens, fall back
                for token in re.findall(r"https?://\S+|[\w.+-]+@[\w.-]+|[A-Za-z]:\\[^\s]+", source):
                    if token not in hyp:
                        return rules_baseline(source, row)
                return hyp

            report["baselines"]["hybrid"] = evaluate_pairs(rows, hybrid)
        except Exception as ex:  # noqa: BLE001
            report["neural_error"] = str(ex)

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps(report, indent=2, ensure_ascii=False)[:4000])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
