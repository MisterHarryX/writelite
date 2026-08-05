#!/usr/bin/env python3
"""
Real LoRA SFT for WriteLite-Qwen (no stubs).

Trains on chat JSONL produced by prepare_qwen_dataset.py.
Saves adapter + metrics + sample generations before/after.
"""

from __future__ import annotations

import argparse
import json
import random
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))


def load_config(path: Path) -> dict:
    text = path.read_text(encoding="utf-8")
    try:
        import yaml  # type: ignore

        return yaml.safe_load(text)
    except Exception:
        cfg: dict = {}
        for line in text.splitlines():
            line = line.strip()
            if not line or line.startswith("#") or ":" not in line:
                continue
            key, val = line.split(":", 1)
            key, val = key.strip(), val.strip().strip('"').strip("'")
            if val.startswith("["):
                cfg[key] = json.loads(val.replace("'", '"'))
            elif val.lower() in ("true", "false"):
                cfg[key] = val.lower() == "true"
            elif val.lower() == "null":
                cfg[key] = None
            else:
                try:
                    cfg[key] = float(val) if "." in val else int(val)
                except ValueError:
                    cfg[key] = val
        return cfg


CONTROL_PROMPTS = [
    ("ru", "привет как у тебя дела я сегодня небыл в школе"),
    ("ru", "Когда я пришел домой мама уже приготовила ужин"),
    ("en", "I has a new computer and it work good"),
    ("en", "where are you going i dont know"),
]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", type=Path, default=ROOT / "configs" / "qwen_smoke.yaml")
    parser.add_argument("--model", type=str, default=None, help="Override base model id")
    parser.add_argument("--smoke", action="store_true")
    args = parser.parse_args()
    cfg = load_config(args.config)
    if args.smoke:
        cfg["smoke"] = True
        cfg["max_train_steps"] = min(int(cfg.get("max_train_steps") or 30), 30)

    seed = int(cfg.get("seed", 42))
    random.seed(seed)

    try:
        import torch
        from datasets import load_dataset
        from peft import LoraConfig, TaskType, get_peft_model
        from transformers import (
            AutoModelForCausalLM,
            AutoTokenizer,
            DataCollatorForLanguageModeling,
            Trainer,
            TrainingArguments,
            set_seed,
        )
    except Exception as ex:  # noqa: BLE001
        print("FATAL: real training requires torch/transformers/peft:", ex, file=sys.stderr)
        print("Install: pip install -r requirements.txt", file=sys.stderr)
        return 3

    set_seed(seed)
    model_name = args.model or cfg.get("model_name") or "Qwen/Qwen2.5-0.5B-Instruct"
    dataset_dir = (ROOT / cfg.get("dataset_dir", "data/chat")).resolve()
    output_dir = (ROOT / cfg.get("output_dir", "outputs/qwen_smoke")).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)

    train_path = dataset_dir / "train.jsonl"
    val_path = dataset_dir / "validation.jsonl"
    if not train_path.exists():
        print("Missing dataset. Run: python scripts/prepare_qwen_dataset.py", file=sys.stderr)
        return 2

    device = "cuda" if torch.cuda.is_available() else "cpu"
    dtype = torch.bfloat16 if (device == "cuda" and cfg.get("bf16", True)) else (
        torch.float16 if (device == "cuda" and cfg.get("fp16")) else torch.float32
    )
    print(f"Loading base model: {model_name} device={device} dtype={dtype}")

    t0 = time.time()
    try:
        tokenizer = AutoTokenizer.from_pretrained(model_name, trust_remote_code=True)
        model = AutoModelForCausalLM.from_pretrained(
            model_name,
            trust_remote_code=True,
            torch_dtype=dtype if device == "cuda" else torch.float32,
            device_map="auto" if device == "cuda" else None,
        )
    except Exception as ex:  # noqa: BLE001
        alt = cfg.get("alt_model_name")
        if alt and alt != model_name:
            print(f"Primary model failed ({ex}); trying alt {alt}")
            model_name = alt
            tokenizer = AutoTokenizer.from_pretrained(model_name, trust_remote_code=True)
            model = AutoModelForCausalLM.from_pretrained(
                model_name,
                trust_remote_code=True,
                torch_dtype=dtype if device == "cuda" else torch.float32,
                device_map="auto" if device == "cuda" else None,
            )
        else:
            raise

    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token

    # --- baseline samples before training ---
    def generate_one(user_text: str, language: str) -> str:
        from writelight_ai.prompts import SYSTEM_PROMPT, user_payload

        messages = [
            {"role": "system", "content": SYSTEM_PROMPT},
            {"role": "user", "content": user_payload(user_text, language)},
        ]
        prompt = tokenizer.apply_chat_template(messages, tokenize=False, add_generation_prompt=True)
        inputs = tokenizer(prompt, return_tensors="pt")
        if device == "cuda":
            inputs = {k: v.to(model.device) for k, v in inputs.items()}
        with torch.no_grad():
            out = model.generate(
                **inputs,
                max_new_tokens=int(cfg.get("max_new_tokens", 256)),
                do_sample=False,
                temperature=None,
                top_p=None,
                pad_token_id=tokenizer.pad_token_id,
            )
        gen = out[0][inputs["input_ids"].shape[-1] :]
        return tokenizer.decode(gen, skip_special_tokens=True)

    before = []
    if cfg.get("generate_samples", True):
        for lang, text in CONTROL_PROMPTS:
            try:
                before.append({"language": lang, "text": text, "output": generate_one(text, lang)})
            except Exception as ex:  # noqa: BLE001
                before.append({"language": lang, "text": text, "error": str(ex)})
    (output_dir / "samples_before.json").write_text(json.dumps(before, indent=2, ensure_ascii=False), encoding="utf-8")

    # LoRA
    target_modules = cfg.get("lora_target_modules") or ["q_proj", "k_proj", "v_proj", "o_proj"]
    lora = LoraConfig(
        task_type=TaskType.CAUSAL_LM,
        r=int(cfg.get("lora_r", 16)),
        lora_alpha=int(cfg.get("lora_alpha", 32)),
        lora_dropout=float(cfg.get("lora_dropout", 0.05)),
        target_modules=target_modules,
        bias="none",
    )
    model = get_peft_model(model, lora)
    model.print_trainable_parameters()

    raw = load_dataset(
        "json",
        data_files={"train": str(train_path), "validation": str(val_path) if val_path.exists() else str(train_path)},
    )

    max_len = int(cfg.get("max_seq_length", 768))

    def to_text(example):
        messages = example["messages"]
        # Full supervised chat including assistant answer
        text = tokenizer.apply_chat_template(messages, tokenize=False, add_generation_prompt=False)
        return {"text": text}

    cols = raw["train"].column_names
    tokenized = raw.map(to_text, remove_columns=cols)

    def tokenize(batch):
        return tokenizer(
            batch["text"],
            truncation=True,
            max_length=max_len,
            padding=False,
        )

    tokenized = tokenized.map(tokenize, batched=True, remove_columns=["text"])

    if cfg.get("smoke"):
        n_train = min(256, len(tokenized["train"]))
        n_val = min(64, len(tokenized["validation"]))
        tokenized["train"] = tokenized["train"].shuffle(seed=seed).select(range(n_train))
        tokenized["validation"] = tokenized["validation"].shuffle(seed=seed).select(range(n_val))

    collator = DataCollatorForLanguageModeling(tokenizer=tokenizer, mlm=False)

    use_bf16 = bool(cfg.get("bf16")) and device == "cuda" and torch.cuda.is_bf16_supported()
    use_fp16 = bool(cfg.get("fp16")) and device == "cuda" and not use_bf16

    training_args = TrainingArguments(
        output_dir=str(output_dir),
        seed=seed,
        learning_rate=float(cfg.get("learning_rate", 2e-4)),
        per_device_train_batch_size=int(cfg.get("train_batch_size", 2)),
        per_device_eval_batch_size=int(cfg.get("eval_batch_size", 2)),
        gradient_accumulation_steps=int(cfg.get("gradient_accumulation_steps", 4)),
        num_train_epochs=float(cfg.get("num_train_epochs", 1)),
        max_steps=int(cfg["max_train_steps"]) if cfg.get("max_train_steps") else -1,
        warmup_ratio=float(cfg.get("warmup_ratio", 0.03)),
        logging_steps=int(cfg.get("logging_steps", 5)),
        eval_strategy="steps" if cfg.get("run_evaluation", True) else "no",
        eval_steps=int(cfg.get("eval_steps", 15)),
        save_steps=int(cfg.get("save_steps", 50)),
        save_total_limit=2,
        bf16=use_bf16,
        fp16=use_fp16,
        report_to=[],
        remove_unused_columns=False,
        load_best_model_at_end=bool(cfg.get("load_best_model_at_end", True)),
        metric_for_best_model="eval_loss",
        greater_is_better=False,
    )

    trainer = Trainer(
        model=model,
        args=training_args,
        train_dataset=tokenized["train"],
        eval_dataset=tokenized["validation"],
        data_collator=collator,
        processing_class=tokenizer,
    )

    print(
        f"Starting train: n_train={len(tokenized['train'])} n_val={len(tokenized['validation'])} "
        f"lora_r={lora.r} alpha={lora.lora_alpha}"
    )
    train_result = trainer.train(resume_from_checkpoint=cfg.get("resume_from_checkpoint") or None)
    metrics = dict(train_result.metrics)

    if cfg.get("run_evaluation", True):
        eval_metrics = trainer.evaluate()
        metrics.update({f"final_{k}": v for k, v in eval_metrics.items()})

    adapter_dir = output_dir / "adapter"
    trainer.save_model(str(adapter_dir))
    tokenizer.save_pretrained(str(adapter_dir))

    # After samples
    after = []
    if cfg.get("generate_samples", True):
        for lang, text in CONTROL_PROMPTS:
            try:
                after.append({"language": lang, "text": text, "output": generate_one(text, lang)})
            except Exception as ex:  # noqa: BLE001
                after.append({"language": lang, "text": text, "error": str(ex)})
    (output_dir / "samples_after.json").write_text(json.dumps(after, indent=2, ensure_ascii=False), encoding="utf-8")

    duration = time.time() - t0
    report = {
        "base_model": model_name,
        "model_version": cfg.get("model_version", "WriteLite-Qwen-0.6B-GEC-1.0.0-dev"),
        "device": device,
        "gpu_name": torch.cuda.get_device_name(0) if device == "cuda" else None,
        "n_train_examples": len(tokenized["train"]),
        "n_val_examples": len(tokenized["validation"]),
        "lora": {
            "r": lora.r,
            "alpha": lora.lora_alpha,
            "dropout": lora.lora_dropout,
            "target_modules": list(target_modules),
        },
        "train_metrics": metrics,
        "duration_sec": duration,
        "adapter_path": str(adapter_dir),
        "smoke": bool(cfg.get("smoke")),
        "dtype": str(dtype),
    }
    (output_dir / "train_report.json").write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps(report, indent=2, ensure_ascii=False))
    print("Saved adapter:", adapter_dir)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
