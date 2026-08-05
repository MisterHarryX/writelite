"""Dataset preparation: seed load, synthetic corruption, splits, dedupe."""

from __future__ import annotations

import hashlib
import json
import random
from pathlib import Path
from typing import Any, Iterable

from .errors import corrupt_text


def read_jsonl(path: Path) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    with path.open("r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            rows.append(json.loads(line))
    return rows


def write_jsonl(path: Path, rows: Iterable[dict[str, Any]]) -> int:
    path.parent.mkdir(parents=True, exist_ok=True)
    n = 0
    with path.open("w", encoding="utf-8") as f:
        for row in rows:
            f.write(json.dumps(row, ensure_ascii=False) + "\n")
            n += 1
    return n


def fingerprint(source: str, target: str) -> str:
    h = hashlib.sha256()
    h.update(source.encode("utf-8"))
    h.update(b"\0")
    h.update(target.encode("utf-8"))
    return h.hexdigest()[:16]


def load_seed(seed_dir: Path) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for name in ("ru_clean.jsonl", "en_clean.jsonl"):
        p = seed_dir / name
        if p.exists():
            rows.extend(read_jsonl(p))
    return rows


def build_pairs(
    clean_rows: list[dict[str, Any]],
    seed: int = 42,
    errors_per_sentence: int = 2,
    include_clean_identity: bool = True,
) -> list[dict[str, Any]]:
    rng = random.Random(seed)
    out: list[dict[str, Any]] = []
    for row in clean_rows:
        text = row["text"]
        lang = row.get("language", "und")
        license_id = row.get("license", "Apache-2.0")
        base_id = row.get("id", fingerprint(text, text))

        if include_clean_identity:
            out.append(
                {
                    "id": f"{base_id}-clean",
                    "language": lang,
                    "source": text,
                    "target": text,
                    "issues": [],
                    "sourceType": "clean-identity",
                    "license": license_id,
                    "split": "pending",
                }
            )

        for k in range(errors_per_sentence):
            corrupted, applied = corrupt_text(text, lang, random.Random(seed + k * 10007 + hash(base_id) % 100000), max_errors=3)
            if corrupted == text:
                continue
            issues = [
                {
                    "type": a.error_type,
                    "original": a.original_span,
                    "replacement": a.corrupted_span,  # note: generator view; train uses source→target
                    "error_id": a.error_id,
                }
                for a in applied
            ]
            out.append(
                {
                    "id": f"{base_id}-err{k}-{fingerprint(corrupted, text)}",
                    "language": lang,
                    "source": corrupted,
                    "target": text,
                    "issues": issues,
                    "sourceType": "synthetic",
                    "license": license_id,
                    "split": "pending",
                }
            )
        _ = rng  # reserved for future sampling
    return out


def dedupe(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    seen: set[str] = set()
    out: list[dict[str, Any]] = []
    for r in rows:
        fp = fingerprint(r["source"], r["target"])
        if fp in seen:
            continue
        seen.add(fp)
        out.append(r)
    return out


def assign_splits(
    rows: list[dict[str, Any]],
    seed: int = 42,
    train_ratio: float = 0.8,
    val_ratio: float = 0.1,
) -> list[dict[str, Any]]:
    """Split by target fingerprint to reduce leakage of same clean sentence across splits."""
    rng = random.Random(seed)
    by_target: dict[str, list[dict[str, Any]]] = {}
    for r in rows:
        key = hashlib.sha256(r["target"].encode("utf-8")).hexdigest()
        by_target.setdefault(key, []).append(r)

    keys = list(by_target.keys())
    rng.shuffle(keys)
    n = len(keys)
    n_train = int(n * train_ratio)
    n_val = int(n * val_ratio)
    train_keys = set(keys[:n_train])
    val_keys = set(keys[n_train : n_train + n_val])
    # rest test

    out: list[dict[str, Any]] = []
    for key, group in by_target.items():
        if key in train_keys:
            split = "train"
        elif key in val_keys:
            split = "validation"
        else:
            split = "test"
        for r in group:
            r = dict(r)
            r["split"] = split
            out.append(r)
    return out


def prepare_dataset(
    seed_dir: Path,
    output_dir: Path,
    seed: int = 42,
    errors_per_sentence: int = 3,
) -> dict[str, int]:
    clean = load_seed(seed_dir)
    pairs = build_pairs(clean, seed=seed, errors_per_sentence=errors_per_sentence)
    pairs = dedupe(pairs)
    pairs = assign_splits(pairs, seed=seed)

    counts = {"train": 0, "validation": 0, "test": 0, "all": len(pairs)}
    for split in ("train", "validation", "test"):
        subset = [r for r in pairs if r["split"] == split]
        write_jsonl(output_dir / f"{split}.jsonl", subset)
        counts[split] = len(subset)
    write_jsonl(output_dir / "all.jsonl", pairs)

    # provenance
    meta = {
        "seed": seed,
        "counts": counts,
        "source": str(seed_dir).replace("\\", "/"),
        "license_note": "See ai/data/licenses.json",
    }
    output_dir.mkdir(parents=True, exist_ok=True)
    (output_dir / "dataset_meta.json").write_text(json.dumps(meta, indent=2), encoding="utf-8")
    return counts
