"""Fine-tune and export WriteLite's Russian contextual reranker.

The reranker answers one question: does this sentence read as correct Russian?
Substituting each spelling candidate into the sentence and comparing the answer
is what lets WriteLite choose "компания" over "кампания" — a decision no amount
of edit distance or word frequency can make, because both are ordinary words.

Base model: cointegrated/rubert-tiny2 (MIT, 29M parameters, hidden size 312).
Chosen over fine-tuning the existing 0.5B generative model because a binary
encoder cannot rewrite the user's text, answers in single-digit milliseconds on
CPU, and quantises to roughly 30 MB.

Every run writes its full configuration and metrics to
`experiments/<name>/`, so a result can be reproduced or compared later.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import random
import sys
import time
from dataclasses import asdict, dataclass

import numpy as np

DEFAULT_BASE = os.path.join("ai", "models", "rubert-tiny2")
DEFAULT_DATA = os.path.join("ai", "data", "errors")
DEFAULT_OUT = os.path.join("models", "writelight-reranker")


@dataclass
class TrainingConfig:
    base_model: str = DEFAULT_BASE
    data_dir: str = DEFAULT_DATA
    output_dir: str = DEFAULT_OUT
    experiment: str = "reranker-001"
    max_length: int = 64
    epochs: float = 2.0
    batch_size: int = 32
    eval_batch_size: int = 64
    learning_rate: float = 3e-5
    weight_decay: float = 0.01
    warmup_ratio: float = 0.06
    seed: int = 20260809
    max_train: int = 0        # 0 = use everything
    fp16: bool = False


def sha256_of(path: str) -> str:
    digest = hashlib.sha256()
    with io.open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_pairs(path: str, limit: int = 0):
    """Reads {"sentence","label"} records; tolerates the corpus record shape too."""
    sentences: list[str] = []
    labels: list[int] = []
    with io.open(path, encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            record = json.loads(line)
            if "sentence" in record and "label" in record:
                sentences.append(record["sentence"])
                labels.append(int(record["label"]))
            elif "source" in record and "target" in record:
                # Corpus records: the target is correct by construction, and the
                # source is only a negative when it actually differs.
                sentences.append(record["target"])
                labels.append(1)
                if record["source"] != record["target"]:
                    sentences.append(record["source"])
                    labels.append(0)
            if limit and len(sentences) >= limit:
                break
    return sentences, labels


def compute_metrics(eval_prediction):
    logits, labels = eval_prediction
    predictions = np.argmax(logits, axis=-1)
    true_positive = int(((predictions == 1) & (labels == 1)).sum())
    false_positive = int(((predictions == 1) & (labels == 0)).sum())
    false_negative = int(((predictions == 0) & (labels == 1)).sum())
    precision = true_positive / max(true_positive + false_positive, 1)
    recall = true_positive / max(true_positive + false_negative, 1)
    return {
        "accuracy": float((predictions == labels).mean()),
        "precision": precision,
        "recall": recall,
        "f1": 2 * precision * recall / max(precision + recall, 1e-9),
    }


def train(config: TrainingConfig) -> dict:
    import torch
    from datasets import Dataset
    from transformers import (
        AutoModelForSequenceClassification,
        AutoTokenizer,
        DataCollatorWithPadding,
        Trainer,
        TrainingArguments,
    )

    random.seed(config.seed)
    np.random.seed(config.seed)
    torch.manual_seed(config.seed)

    train_path = os.path.join(config.data_dir, "rerank_train.jsonl")
    dev_path = os.path.join(config.data_dir, "rerank_validation.jsonl")
    test_path = os.path.join(config.data_dir, "rerank_test.jsonl")
    for path in (train_path, dev_path):
        if not os.path.exists(path):
            raise SystemExit(
                f"missing {path}. Build the corpus first: python ai/scripts/build_ru_error_corpus.py"
            )

    tokenizer = AutoTokenizer.from_pretrained(config.base_model)
    model = AutoModelForSequenceClassification.from_pretrained(config.base_model, num_labels=2)

    def build(path: str, limit: int = 0):
        sentences, labels = load_pairs(path, limit)
        dataset = Dataset.from_dict({"text": sentences, "labels": labels})
        return dataset.map(
            lambda batch: tokenizer(
                batch["text"], truncation=True, max_length=config.max_length
            ),
            batched=True,
            remove_columns=["text"],
        )

    train_dataset = build(train_path, config.max_train)
    dev_dataset = build(dev_path)

    arguments = TrainingArguments(
        output_dir=os.path.join("ai", "outputs", config.experiment),
        num_train_epochs=config.epochs,
        per_device_train_batch_size=config.batch_size,
        per_device_eval_batch_size=config.eval_batch_size,
        learning_rate=config.learning_rate,
        weight_decay=config.weight_decay,
        warmup_ratio=config.warmup_ratio,
        eval_strategy="epoch",
        save_strategy="no",
        logging_steps=100,
        seed=config.seed,
        fp16=config.fp16 and torch.cuda.is_available(),
        report_to=[],
        dataloader_num_workers=0,
    )

    trainer = Trainer(
        model=model,
        args=arguments,
        train_dataset=train_dataset,
        eval_dataset=dev_dataset,
        data_collator=DataCollatorWithPadding(tokenizer),
        compute_metrics=compute_metrics,
    )

    started = time.time()
    trainer.train()
    duration = time.time() - started

    metrics = {"validation": trainer.evaluate(dev_dataset)}
    if os.path.exists(test_path):
        metrics["test"] = trainer.evaluate(build(test_path))

    os.makedirs(config.output_dir, exist_ok=True)
    model.save_pretrained(os.path.join(config.output_dir, "hf"))
    tokenizer.save_pretrained(os.path.join(config.output_dir, "hf"))

    return {
        "metrics": metrics,
        "trainingSeconds": round(duration, 1),
        "trainExamples": len(train_dataset),
        "validationExamples": len(dev_dataset),
        "device": "cuda" if torch.cuda.is_available() else "cpu",
    }


def export_onnx(config: TrainingConfig) -> dict:
    """Traces the classifier to ONNX and quantises the weights to int8."""
    import torch
    from transformers import AutoModelForSequenceClassification, AutoTokenizer

    source = os.path.join(config.output_dir, "hf")
    tokenizer = AutoTokenizer.from_pretrained(source)
    model = AutoModelForSequenceClassification.from_pretrained(source).eval()

    onnx_path = os.path.join(config.output_dir, "model.fp32.onnx")
    sample = tokenizer(
        ["пример предложения"], return_tensors="pt",
        padding="max_length", truncation=True, max_length=config.max_length,
    )
    inputs = (sample["input_ids"], sample["attention_mask"], sample["token_type_ids"]) \
        if "token_type_ids" in sample else (sample["input_ids"], sample["attention_mask"])
    input_names = ["input_ids", "attention_mask"] + (["token_type_ids"] if len(inputs) == 3 else [])

    # The TorchScript exporter is preferred here over the dynamo path: dynamo
    # emits a graph whose shape annotations defeat onnxruntime's dynamic
    # quantiser ("Inferred shape and existing shape differ"), and int8 is the
    # difference between a 117 MB artefact and a 30 MB one. Fall back to dynamo
    # if the legacy exporter is gone.
    export_kwargs = dict(
        input_names=input_names,
        output_names=["logits"],
        dynamic_axes={name: {0: "batch"} for name in input_names + ["logits"]},
        opset_version=17,
        do_constant_folding=True,
    )
    try:
        torch.onnx.export(model, inputs, onnx_path, dynamo=False, **export_kwargs)
    except TypeError:
        torch.onnx.export(model, inputs, onnx_path, **export_kwargs)

    # torch's exporter spills weights into a sidecar `.onnx.data` file once the
    # graph is large enough. That is fine for Python, but the C# runtime ships a
    # single artefact, and a copied `.onnx` whose sidecar is named after the
    # original loads as an empty model rather than failing loudly. Re-save with
    # the tensors inlined before doing anything else.
    import onnx

    graph = onnx.load(onnx_path, load_external_data=True)
    inlined_path = os.path.join(config.output_dir, "model.inlined.onnx")
    onnx.save(graph, inlined_path, save_as_external_data=False)

    final_path = os.path.join(config.output_dir, "model.onnx")
    quantised = False
    quantisation_error = None
    try:
        from onnxruntime.quantization import QuantType, quantize_dynamic

        quantize_dynamic(inlined_path, final_path, weight_type=QuantType.QInt8)
        quantised = True
    except Exception as error:  # noqa: BLE001 - reported, not hidden
        # Without quantisation the fp32 graph still runs; it is four times the
        # size, not four times the error. But say so instead of pretending.
        import shutil

        shutil.copyfile(inlined_path, final_path)
        quantisation_error = f"{type(error).__name__}: {error}"

    # The C# runtime reads the plain vocab file, not tokenizer.json.
    vocab_path = os.path.join(config.output_dir, "vocab.txt")
    source_vocab = os.path.join(source, "vocab.txt")
    if os.path.exists(source_vocab):
        import shutil

        shutil.copyfile(source_vocab, vocab_path)
    else:
        with io.open(vocab_path, "w", encoding="utf-8", newline="\n") as handle:
            for token, _id in sorted(tokenizer.get_vocab().items(), key=lambda kv: kv[1]):
                handle.write(token + "\n")

    return {
        "onnx": final_path,
        "quantisedInt8": quantised,
        "quantisationError": quantisation_error,
        "bytesFp32": os.path.getsize(inlined_path),
        "bytesFinal": os.path.getsize(final_path),
        "vocabTokens": sum(1 for _ in io.open(vocab_path, encoding="utf-8")),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    for field, value in asdict(TrainingConfig()).items():
        parser.add_argument(f"--{field.replace('_', '-')}", type=type(value), default=value)
    parser.add_argument("--export-only", action="store_true")
    args = parser.parse_args()

    config = TrainingConfig(**{
        field: getattr(args, field) for field in asdict(TrainingConfig())
    })

    record: dict = {"config": asdict(config), "startedAt": time.strftime("%Y-%m-%dT%H:%M:%S")}
    if not args.export_only:
        record.update(train(config))
    record["export"] = export_onnx(config)

    # rubert-tiny2 is a cased model. Getting this wrong in the C# runtime would
    # not fail — it would quietly segment every capitalised word differently
    # from training — so the flag travels with the artefact.
    lowercase = False
    tokenizer_config = os.path.join(config.output_dir, "hf", "tokenizer_config.json")
    if os.path.exists(tokenizer_config):
        with io.open(tokenizer_config, encoding="utf-8") as handle:
            lowercase = bool(json.load(handle).get("do_lower_case", False))

    with io.open(os.path.join(config.output_dir, "reranker.json"), "w", encoding="utf-8") as handle:
        json.dump({
            "version": config.experiment,
            "baseModel": "cointegrated/rubert-tiny2",
            "baseModelLicense": "MIT",
            "maxLength": config.max_length,
            "lowercase": lowercase,
            "positiveLabelIndex": 1,
            "labels": {"0": "corrupted", "1": "correct"},
        }, handle, ensure_ascii=False, indent=2)

    experiment_dir = os.path.join("experiments", config.experiment)
    os.makedirs(experiment_dir, exist_ok=True)
    record["artifactSha256"] = sha256_of(record["export"]["onnx"])
    with io.open(os.path.join(experiment_dir, "result.json"), "w", encoding="utf-8") as handle:
        json.dump(record, handle, ensure_ascii=False, indent=2)

    print(json.dumps({
        "experiment": config.experiment,
        "metrics": record.get("metrics"),
        "export": record["export"],
        "trainingSeconds": record.get("trainingSeconds"),
        "device": record.get("device"),
    }, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
