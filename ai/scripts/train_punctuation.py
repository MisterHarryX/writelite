"""Trains WriteLite's punctuation-decision classifier.

The task is one bit: given a comma-free Russian sentence and a word boundary in it, does a
comma belong there? Not "punctuate this sentence" — the position is given, the answer is
COMMA or NO_CHANGE, and nothing the model emits can reach the user's text except a comma at
an offset the caller already chose. That shape is deliberate. Phase 4 and Phase 5 measured a
generative model rewriting sentences at 0.115 then 0.211 precision against 0.958-0.972 for
every deterministic layer, and the largest single cluster of what remained was commas placed
in the wrong place. A classifier cannot place a comma in the wrong place, because it is not
the one choosing the place.

Base model: cointegrated/rubert-tiny2 (MIT, 29M parameters, hidden 312, 3 layers), the same
encoder the shipping contextual reranker uses. Reusing it is not laziness: the tokenizer, the
ONNX int8 export path and the C# inference runtime already exist and are already measured at
single-digit milliseconds on CPU, so a second head on the same architecture costs the product
one more 30 MB artefact and no new runtime.

Why this trains on CPU. Measured on the development machine (i5-12400F, 12 threads):
245 ms per step at batch 32, sequence 64 — 3.3 minutes per epoch over the 26k training
examples, about 13 minutes for a full run. The machine has an RTX 4070, and using it would
mean a ~3 GB CUDA wheel to save ten minutes per experiment. CUDA is used when it is already
present and skipped when it is not; nothing here requires it.

    python ai/scripts/train_punctuation.py --experiment PUNC-A --encoding segment
    python ai/scripts/train_punctuation.py --experiment PUNC-B --encoding marker
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import platform
import random
import sys
import time
from dataclasses import asdict, dataclass, field

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from golden_guard import verify_or_die  # noqa: E402

DEFAULT_BASE = os.path.join("ai", "models", "rubert-tiny2")
DEFAULT_DATA = os.path.join("ai", "data", "training", "punctuation_decision.jsonl")
DEFAULT_GOLDEN = os.path.join("ai", "data", "benchmark", "ru_frozen_v1.jsonl")
DEFAULT_OUT = os.path.join("models", "writelite-punctuation")

LABELS = ["NO_CHANGE", "COMMA"]

#: Marks the candidate boundary when ``--encoding marker`` is used. A character that cannot
#: occur in Russian text, so it never collides with something the user wrote, and a single
#: token after tokenisation.
MARKER = "¦"


@dataclass
class TrainingConfig:
    experiment: str = "PUNC-A"
    hypothesis: str = ""
    base_model: str = DEFAULT_BASE
    data: str = DEFAULT_DATA
    golden: str = DEFAULT_GOLDEN
    output_dir: str = DEFAULT_OUT
    encoding: str = "segment"          # segment | marker
    max_length: int = 64
    epochs: float = 4.0
    batch_size: int = 32
    eval_batch_size: int = 128
    learning_rate: float = 5e-5
    weight_decay: float = 0.01
    warmup_ratio: float = 0.06
    seed: int = 20260814
    max_train: int = 0                 # 0 = use everything
    comma_class_weight: float = 1.0    # >1 favours recall, <1 favours precision
    early_stopping_patience: int = 2
    export_onnx: bool = True


# ---------------------------------------------------------------------------
# data
# ---------------------------------------------------------------------------


def load_split(path: str, split: str, limit: int = 0) -> list[dict]:
    records: list[dict] = []
    with io.open(path, encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            record = json.loads(line)
            if record.get("split") != split:
                continue
            records.append(record)
            if limit and len(records) >= limit:
                break
    return records


def encode(record: dict, encoding: str) -> tuple[str, str | None]:
    """Turns {sentence, position} into what the encoder actually reads.

    Two ways to tell a sentence encoder *where* the question is, and they are the substance
    of the PUNC-A / PUNC-B comparison rather than a formatting detail:

    ``segment``
        Split at the boundary and hand the halves over as a sentence pair. BERT's segment
        embeddings then mark the split exactly, with no new vocabulary and no token spent on
        it. The cost is that the model sees two sequences with a [SEP] between them, which is
        a stronger break than a comma decision usually is.

    ``marker``
        Insert one character at the boundary and encode a single sequence. The left and right
        context stay adjacent in one stream, which is how the signal actually reads in Russian
        — «однако» after a comma versus «однако» opening a clause is a question about the
        immediate neighbours. The cost is one token of the budget and a symbol the pretrained
        model has never seen.
    """
    sentence = record["sentence"]
    position = record["position"]
    if encoding == "segment":
        return sentence[:position].strip(), sentence[position:].strip()
    if encoding == "marker":
        return f"{sentence[:position]} {MARKER} {sentence[position:]}".strip(), None
    raise SystemExit(f"unknown encoding: {encoding}")


def label_of(record: dict) -> int:
    return LABELS.index(record["correct"])


# ---------------------------------------------------------------------------
# metrics
# ---------------------------------------------------------------------------


def binary_metrics(predictions: np.ndarray, labels: np.ndarray) -> dict:
    """COMMA is the positive class throughout, so precision means 'commas I proposed'."""
    tp = int(((predictions == 1) & (labels == 1)).sum())
    fp = int(((predictions == 1) & (labels == 0)).sum())
    fn = int(((predictions == 0) & (labels == 1)).sum())
    tn = int(((predictions == 0) & (labels == 0)).sum())
    precision = tp / max(tp + fp, 1)
    recall = tp / max(tp + fn, 1)
    return {
        "accuracy": float((predictions == labels).mean()),
        "precision": precision,
        "recall": recall,
        "f1": 2 * precision * recall / max(precision + recall, 1e-9),
        # Specificity is the product-relevant one: of the boundaries that need no comma, how
        # many were left alone. A model can look strong on F1 and still insert commas across
        # clean text, and that is the failure mode this whole phase is guarding against.
        "specificity": tn / max(tn + fp, 1),
        "falsePositiveRate": fp / max(tn + fp, 1),
        "noChangeAccuracy": tn / max(tn + fp, 1),
        "truePositives": tp,
        "falsePositives": fp,
        "falseNegatives": fn,
        "trueNegatives": tn,
    }


def compute_metrics(eval_prediction):
    logits, labels = eval_prediction
    return binary_metrics(np.argmax(logits, axis=-1), labels)


def softmax(logits: np.ndarray) -> np.ndarray:
    shifted = logits - logits.max(axis=-1, keepdims=True)
    exponentiated = np.exp(shifted)
    return exponentiated / exponentiated.sum(axis=-1, keepdims=True)


def calibration_report(probabilities: np.ndarray, labels: np.ndarray, bins: int = 10) -> dict:
    """Expected calibration error plus the raw bins.

    Phase 3 shipped a pipeline in which every finding claimed a confidence of 1.000, which
    made the number useless for routing and for the UI. A probability that is going to be
    thresholded has to be checked rather than assumed, so the bins are reported alongside the
    single summary number and a badly calibrated model is visible even when its ECE is small.
    """
    confidence = probabilities.max(axis=-1)
    predictions = probabilities.argmax(axis=-1)
    correct = (predictions == labels).astype(float)

    edges = np.linspace(0.5, 1.0, bins + 1)
    ece = 0.0
    detail = []
    for low, high in zip(edges[:-1], edges[1:]):
        mask = (confidence >= low) & (confidence < high if high < 1.0 else confidence <= 1.0)
        count = int(mask.sum())
        if count == 0:
            continue
        mean_confidence = float(confidence[mask].mean())
        accuracy = float(correct[mask].mean())
        ece += (count / len(labels)) * abs(accuracy - mean_confidence)
        detail.append({
            "bin": f"[{low:.2f},{high:.2f})",
            "n": count,
            "meanConfidence": round(mean_confidence, 4),
            "accuracy": round(accuracy, 4),
        })

    return {"expectedCalibrationError": round(float(ece), 4), "bins": detail}


def sweep_threshold(probabilities: np.ndarray, labels: np.ndarray) -> list[dict]:
    """Precision/recall at every acceptance threshold on the COMMA probability.

    The classifier's argmax is a 0.5 threshold, and 0.5 is a statement about the training
    prior rather than about the product. Training is balanced roughly 50/50 because an
    uncapped corpus is 95 % NO_CHANGE and teaches a model to answer NO_CHANGE unconditionally;
    deployment is the uncapped distribution. The threshold is what reconciles the two, and it
    is chosen on validation and frozen before the golden set is touched.
    """
    rows = []
    for threshold in [i / 20 for i in range(1, 20)]:
        predictions = (probabilities[:, 1] >= threshold).astype(int)
        row = binary_metrics(predictions, labels)
        row["threshold"] = threshold
        rows.append(row)
    return rows


# ---------------------------------------------------------------------------
# training
# ---------------------------------------------------------------------------


def sha256_of(path: str) -> str:
    digest = hashlib.sha256()
    with io.open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def train(config: TrainingConfig) -> dict:
    import torch
    from datasets import Dataset
    from transformers import (
        AutoModelForSequenceClassification,
        AutoTokenizer,
        DataCollatorWithPadding,
        EarlyStoppingCallback,
        Trainer,
        TrainingArguments,
    )

    random.seed(config.seed)
    np.random.seed(config.seed)
    torch.manual_seed(config.seed)
    torch.use_deterministic_algorithms(False)  # cudnn determinism is not worth the throughput
    torch.set_num_threads(os.cpu_count() or 4)

    splits = {name: load_split(config.data, name, config.max_train if name == "train" else 0)
              for name in ("train", "validation", "test")}
    for name, records in splits.items():
        if not records:
            raise SystemExit(f"split '{name}' is empty in {config.data}")

    # §2: fail fast, before a single gradient step, on the file actually being read.
    guard = verify_or_die(
        [r for records in splits.values() for r in records],
        config.golden,
        what=f"punctuation_decision ({config.data})",
    )
    print(f"golden guard : {guard['recordsChecked']} records checked against "
          f"{guard['goldenFingerprints']} fingerprints / {guard['goldenIds']} ids — 0 leaks")

    tokenizer = AutoTokenizer.from_pretrained(config.base_model)
    if config.encoding == "marker":
        tokenizer.add_tokens([MARKER])

    model = AutoModelForSequenceClassification.from_pretrained(
        config.base_model,
        num_labels=2,
        id2label={i: name for i, name in enumerate(LABELS)},
        label2id={name: i for i, name in enumerate(LABELS)},
    )
    if config.encoding == "marker":
        model.resize_token_embeddings(len(tokenizer))

    def build(records: list[dict]):
        first, second = zip(*(encode(r, config.encoding) for r in records))
        columns = {"text": list(first), "labels": [label_of(r) for r in records]}
        if second[0] is not None:
            columns["text_pair"] = list(second)
        dataset = Dataset.from_dict(columns)
        remove = ["text"] + (["text_pair"] if "text_pair" in columns else [])
        return dataset.map(
            lambda batch: tokenizer(
                batch["text"],
                batch["text_pair"] if "text_pair" in columns else None,
                truncation=True,
                max_length=config.max_length,
            ),
            batched=True,
            remove_columns=remove,
        )

    datasets = {name: build(records) for name, records in splits.items()}

    class WeightedTrainer(Trainer):
        """Class weighting, so recall can be bought explicitly rather than by accident."""

        def compute_loss(self, model, inputs, return_outputs=False, **kwargs):
            labels = inputs.pop("labels")
            outputs = model(**inputs)
            weight = torch.tensor(
                [1.0, config.comma_class_weight],
                dtype=outputs.logits.dtype,
                device=outputs.logits.device,
            )
            loss = torch.nn.functional.cross_entropy(outputs.logits, labels, weight=weight)
            return (loss, outputs) if return_outputs else loss

    arguments = TrainingArguments(
        output_dir=os.path.join("ai", "outputs", config.experiment),
        num_train_epochs=config.epochs,
        per_device_train_batch_size=config.batch_size,
        per_device_eval_batch_size=config.eval_batch_size,
        learning_rate=config.learning_rate,
        weight_decay=config.weight_decay,
        warmup_ratio=config.warmup_ratio,
        eval_strategy="epoch",
        save_strategy="epoch",
        save_total_limit=1,
        load_best_model_at_end=True,
        metric_for_best_model="f1",
        greater_is_better=True,
        logging_steps=100,
        seed=config.seed,
        data_seed=config.seed,
        fp16=torch.cuda.is_available(),
        report_to=[],
        dataloader_num_workers=0,
    )

    trainer_class = WeightedTrainer if config.comma_class_weight != 1.0 else Trainer
    trainer = trainer_class(
        model=model,
        args=arguments,
        train_dataset=datasets["train"],
        eval_dataset=datasets["validation"],
        data_collator=DataCollatorWithPadding(tokenizer),
        compute_metrics=compute_metrics,
        callbacks=[EarlyStoppingCallback(early_stopping_patience=config.early_stopping_patience)],
    )

    started = time.time()
    trainer.train()
    training_seconds = time.time() - started

    results: dict = {"validation": {}, "test": {}}
    probabilities: dict = {}
    for name in ("validation", "test"):
        prediction = trainer.predict(datasets[name])
        labels = np.asarray(prediction.label_ids)
        probability = softmax(np.asarray(prediction.predictions))
        probabilities[name] = (probability, labels)
        results[name] = {
            "argmax": binary_metrics(probability.argmax(axis=-1), labels),
            "calibration": calibration_report(probability, labels),
            "n": int(len(labels)),
        }

    results["validation"]["thresholdSweep"] = sweep_threshold(*probabilities["validation"])

    output = os.path.join(config.output_dir, config.experiment)
    os.makedirs(output, exist_ok=True)
    model.save_pretrained(os.path.join(output, "hf"))
    tokenizer.save_pretrained(os.path.join(output, "hf"))

    np.save(os.path.join(output, "validation_probabilities.npy"), probabilities["validation"][0])
    np.save(os.path.join(output, "validation_labels.npy"), probabilities["validation"][1])
    np.save(os.path.join(output, "test_probabilities.npy"), probabilities["test"][0])
    np.save(os.path.join(output, "test_labels.npy"), probabilities["test"][1])

    return {
        "experiment": config.experiment,
        "hypothesis": config.hypothesis,
        "config": asdict(config),
        "dataset": {
            "path": config.data,
            "sha256": sha256_of(config.data),
            "counts": {name: len(records) for name, records in splits.items()},
            "commaShare": {
                name: round(sum(1 for r in records if r["correct"] == "COMMA") / len(records), 4)
                for name, records in splits.items()
            },
        },
        "goldenGuard": guard,
        "metrics": results,
        "trainingSeconds": round(training_seconds, 1),
        "parameters": int(sum(p.numel() for p in model.parameters())),
        "environment": {
            "device": "cuda" if __import__("torch").cuda.is_available() else "cpu",
            "python": platform.python_version(),
            "platform": platform.platform(),
            "cpuCount": os.cpu_count(),
        },
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    for name, value in asdict(TrainingConfig()).items():
        if isinstance(value, bool):
            parser.add_argument(f"--{name.replace('_', '-')}", type=lambda s: s.lower() != "false",
                                default=value)
        else:
            parser.add_argument(f"--{name.replace('_', '-')}", type=type(value), default=value)
    parsed = parser.parse_args()
    config = TrainingConfig(**{k: getattr(parsed, k) for k in asdict(TrainingConfig())})

    report = train(config)

    experiment_dir = os.path.join("experiments", config.experiment)
    os.makedirs(experiment_dir, exist_ok=True)
    with io.open(os.path.join(experiment_dir, "report.json"), "w", encoding="utf-8") as handle:
        json.dump(report, handle, ensure_ascii=False, indent=2)

    validation = report["metrics"]["validation"]["argmax"]
    test = report["metrics"]["test"]["argmax"]
    print()
    print(f"{config.experiment}  ({config.encoding}, {report['trainingSeconds'] / 60:.1f} min)")
    print(f"  validation : P={validation['precision']:.4f} R={validation['recall']:.4f} "
          f"F1={validation['f1']:.4f} specificity={validation['specificity']:.4f}")
    print(f"  test       : P={test['precision']:.4f} R={test['recall']:.4f} "
          f"F1={test['f1']:.4f} specificity={test['specificity']:.4f}")
    print(f"  calibration: ECE={report['metrics']['validation']['calibration']['expectedCalibrationError']:.4f}")
    print(f"  report     : {os.path.join(experiment_dir, 'report.json')}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
