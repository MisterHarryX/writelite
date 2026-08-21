"""Refuses to let a training run start on data that overlaps the golden benchmark.

The exporter already blocks golden content on the way out (`tools/trainexport/GoldenGuard.cs`),
and `GoldenDatasetIntegrityTests` checks the written artefacts. This is the third check and it
sits at the last possible moment: immediately before a model sees an example.

That placement is the point. The first two guard the pipeline that produced today's files;
this one guards the file actually being read, whatever produced it — a hand-edited jsonl, a
copy from another machine, a future exporter with a new bug. A model that has seen the
benchmark cannot be un-trained, and there is no way to tell after the fact which items it saw,
so the failure has to be loud and it has to be fatal. `verify_or_die` raises SystemExit; there
is deliberately no flag to downgrade it to a warning.

The fingerprint matches the C# implementation exactly: letters and digits only, lowercased,
ё folded to е, everything else collapsed to a single space. Re-punctuating or re-casing a
golden sentence therefore does not smuggle it past.
"""

from __future__ import annotations

import hashlib
import io
import json
import os


def fingerprint(sentence: str) -> str:
    """Content hash that survives re-casing, re-spacing and re-punctuation."""
    out: list[str] = []
    last_was_space = True
    for ch in sentence:
        if ch.isalpha() or ch.isdigit():
            lowered = ch.lower()
            out.append("е" if lowered == "ё" else lowered)
            last_was_space = False
        elif not last_was_space:
            out.append(" ")
            last_was_space = True
    return hashlib.sha256("".join(out).strip().encode("utf-8")).hexdigest()


def load_golden(path: str) -> tuple[set[str], set[str]]:
    """Returns (content fingerprints, ids) for every golden sentence, both forms."""
    fingerprints: set[str] = set()
    ids: set[str] = set()
    with io.open(path, encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            record = json.loads(line)
            if record.get("id"):
                ids.add(record["id"])
            for field in ("source", "target"):
                if record.get(field):
                    fingerprints.add(fingerprint(record[field]))
    return fingerprints, ids


def verify_or_die(
    records: list[dict],
    golden_path: str,
    text_field: str = "sentence",
    what: str = "training data",
) -> dict:
    """Aborts the process if any record overlaps golden. Returns guard stats when clean."""
    if not os.path.exists(golden_path):
        raise SystemExit(
            f"golden corpus not found at {golden_path}; refusing to train without a leakage guard"
        )

    fingerprints, ids = load_golden(golden_path)
    leaks: list[str] = []
    for record in records:
        text = record.get(text_field, "")
        if fingerprint(text) in fingerprints or record.get("id") in ids:
            leaks.append(record.get("id", "<no id>"))
            if len(leaks) >= 10:
                break

    if leaks:
        raise SystemExit(
            f"GOLDEN LEAKAGE in {what}: {len(leaks)}+ records overlap the frozen benchmark "
            f"by normalised content fingerprint or id. First offenders: {leaks}. "
            "Training is aborted. The golden set is evaluation-only; every published quality "
            "number is a statement about those exact items."
        )

    return {
        "goldenPath": golden_path,
        "goldenFingerprints": len(fingerprints),
        "goldenIds": len(ids),
        "recordsChecked": len(records),
        "leaks": 0,
    }
