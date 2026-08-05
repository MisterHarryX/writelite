#!/usr/bin/env python3
"""Fine-tune a seq2seq GEC model (mT5 by default). Supports smoke mode."""

from __future__ import annotations

import argparse
import json
import random
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))


def load_config(path: Path) -> dict:
    text = path.read_text(encoding="utf-8")
    try:
        import yaml  # type: ignore

        return yaml.safe_load(text)
    except Exception:
        # Minimal fallback for smoke YAML without PyYAML (key: value only).
        cfg: dict = {}
        for line in text.splitlines():
            line = line.strip()
            if not line or line.startswith("#") or ":" not in line:
                continue
            key, val = line.split(":", 1)
            key = key.strip()
            val = val.strip().strip('"').strip("'")
            if val.lower() in ("true", "false"):
                cfg[key] = val.lower() == "true"
            elif val.lower() == "null":
                cfg[key] = None
            else:
                try:
                    if "." in val:
                        cfg[key] = float(val)
                    else:
                        cfg[key] = int(val)
                except ValueError:
                    cfg[key] = val
        return cfg


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", type=Path, default=ROOT / "configs" / "smoke.yaml")
    parser.add_argument("--smoke", action="store_true", help="Force tiny run even if config is full")
    args = parser.parse_args()
    cfg = load_config(args.config)
    if args.smoke:
        cfg["smoke"] = True
        cfg["max_train_steps"] = min(int(cfg.get("max_train_steps") or 8), 8)
        cfg["num_train_epochs"] = 1

    seed = int(cfg.get("seed", 42))
    random.seed(seed)

    dataset_dir = (ROOT / cfg.get("dataset_dir", "data/processed")).resolve()
    output_dir = (ROOT / cfg.get("output_dir", "outputs/run")).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)

    train_path = dataset_dir / "train.jsonl"
    if not train_path.exists():
        print("Missing dataset. Run: python scripts/prepare_dataset.py")
        return 2

    # Prefer transformers Trainer when available; otherwise write a baseline checkpoint stub.
    try:
        import torch
        from datasets import load_dataset
        from transformers import (
            AutoModelForSeq2SeqLM,
            AutoTokenizer,
            DataCollatorForSeq2Seq,
            Seq2SeqTrainer,
            Seq2SeqTrainingArguments,
            set_seed,
        )
    except Exception as ex:  # noqa: BLE001
        print("Transformers/torch not available — writing baseline stub checkpoint:", ex)
        stub = {
            "model_name": cfg.get("model_name"),
            "status": "stub-no-torch",
            "note": "Install requirements.txt and re-run for real training.",
        }
        (output_dir / "checkpoint-stub.json").write_text(json.dumps(stub, indent=2), encoding="utf-8")
        (output_dir / "train_metrics.json").write_text(
            json.dumps({"status": "skipped", "reason": str(ex)}, indent=2), encoding="utf-8"
        )
        return 0

    set_seed(seed)
    model_name = cfg["model_name"]
    print("Loading model:", model_name)
    tokenizer = AutoTokenizer.from_pretrained(model_name)
    model = AutoModelForSeq2SeqLM.from_pretrained(model_name)

    data_files = {
        "train": str(dataset_dir / "train.jsonl"),
        "validation": str(dataset_dir / "validation.jsonl"),
    }
    raw = load_dataset("json", data_files=data_files)

    prefix = "gec: "
    max_source = int(cfg.get("max_source_length", 128))
    max_target = int(cfg.get("max_target_length", 128))

    def preprocess(batch):
        inputs = [prefix + s for s in batch["source"]]
        model_inputs = tokenizer(inputs, max_length=max_source, truncation=True)
        labels = tokenizer(text_target=batch["target"], max_length=max_target, truncation=True)
        model_inputs["labels"] = labels["input_ids"]
        return model_inputs

    tokenized = raw.map(preprocess, batched=True, remove_columns=raw["train"].column_names)

    if cfg.get("smoke"):
        tokenized["train"] = tokenized["train"].select(range(min(16, len(tokenized["train"]))))
        tokenized["validation"] = tokenized["validation"].select(range(min(8, len(tokenized["validation"]))))

    collator = DataCollatorForSeq2Seq(tokenizer=tokenizer, model=model)
    use_fp16 = bool(cfg.get("fp16")) and torch.cuda.is_available()
    use_bf16 = bool(cfg.get("bf16")) and torch.cuda.is_available()

    training_args = Seq2SeqTrainingArguments(
        output_dir=str(output_dir),
        eval_strategy="steps" if not cfg.get("smoke") else "no",
        learning_rate=float(cfg.get("learning_rate", 3e-5)),
        per_device_train_batch_size=int(cfg.get("train_batch_size", 2)),
        per_device_eval_batch_size=int(cfg.get("eval_batch_size", 2)),
        gradient_accumulation_steps=int(cfg.get("gradient_accumulation_steps", 1)),
        num_train_epochs=float(cfg.get("num_train_epochs", 1)),
        max_steps=int(cfg["max_train_steps"]) if cfg.get("max_train_steps") else -1,
        warmup_ratio=float(cfg.get("warmup_ratio", 0.0)),
        fp16=use_fp16,
        bf16=use_bf16,
        logging_steps=int(cfg.get("logging_steps", 10)),
        save_steps=int(cfg.get("save_steps", 500)),
        eval_steps=int(cfg.get("eval_steps", 500)),
        save_total_limit=2,
        predict_with_generate=True,
        generation_max_length=max_target,
        report_to=[],
        seed=seed,
        load_best_model_at_end=not bool(cfg.get("smoke")),
        metric_for_best_model="loss",
    )

    trainer = Seq2SeqTrainer(
        model=model,
        args=training_args,
        train_dataset=tokenized["train"],
        eval_dataset=tokenized.get("validation"),
        processing_class=tokenizer,
        data_collator=collator,
    )

    resume = cfg.get("resume_from_checkpoint")
    train_result = trainer.train(resume_from_checkpoint=resume if resume else None)
    trainer.save_model(str(output_dir / "best"))
    tokenizer.save_pretrained(str(output_dir / "best"))

    metrics = dict(train_result.metrics)
    metrics["model_name"] = model_name
    metrics["smoke"] = bool(cfg.get("smoke"))
    metrics["device"] = "cuda" if torch.cuda.is_available() else "cpu"
    (output_dir / "train_metrics.json").write_text(json.dumps(metrics, indent=2), encoding="utf-8")
    print("Training complete:", metrics)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
