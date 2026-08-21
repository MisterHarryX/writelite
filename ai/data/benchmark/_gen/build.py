# -*- coding: utf-8 -*-
"""Builds ai/data/benchmark/ru_frozen_v1.jsonl and its meta sidecar.

Deterministic: same inputs -> byte-identical output (and therefore the same
sha256).  Every error span is verified with source[start:end] == original
before anything is written.
"""

import hashlib
import io
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
OUT_DIR = os.path.abspath(os.path.join(HERE, ".."))

import data_clean
import data_errors
import data_extra

FROZEN_DATE = "2026-08-09"

# category -> (list, kind)  kind in {"clean", "errors"}
CATEGORIES = [
    ("clean", data_clean.CLEAN, "clean"),
    ("typo_simple", data_errors.TYPO_SIMPLE, "errors"),
    ("typo_keyboard", data_errors.TYPO_KEYBOARD, "errors"),
    ("layout", data_errors.LAYOUT, "errors"),
    ("homoglyph", data_errors.HOMOGLYPH, "errors"),
    ("orthography", data_errors.ORTHOGRAPHY, "errors"),
    ("morphology", data_errors.MORPHOLOGY, "errors"),
    ("real_word", data_errors.REAL_WORD, "errors"),
    ("punctuation", data_errors.PUNCTUATION, "errors"),
    ("slang", data_extra.SLANG, "clean"),
    ("profanity", data_extra.PROFANITY, "clean"),
    ("proper_name", data_extra.PROPER_NAME, "errors"),
    ("modern_term", data_extra.MODERN_TERM, "errors"),
    ("technical", data_extra.TECHNICAL, "errors"),
]

LETTER = r"[^\W\d_]"


def occurrences(source, original):
    """Whole-token occurrences of `original` in `source`."""
    pattern = "(?<!" + LETTER + ")" + re.escape(original) + "(?!" + LETTER + ")"
    spans = [(m.start(), m.end()) for m in re.finditer(pattern, source, re.UNICODE)]
    if spans:
        return spans
    # Fall back to a raw substring search for originals that are not
    # letter-delimited (punctuation fragments such as "поэтому ,").
    return [(m.start(), m.end()) for m in re.finditer(re.escape(original), source)]


def build_error_item(item_id, category, entry):
    source, raw_errors = entry
    errors = []
    for raw in raw_errors:
        if len(raw) == 4:
            original, replacement, etype, occurrence = raw
        else:
            original, replacement, etype = raw
            occurrence = 1
        spans = occurrences(source, original)
        if len(spans) < occurrence:
            raise AssertionError(
                "%s: occurrence %d of %r not found in %r" % (item_id, occurrence, original, source))
        start, end = spans[occurrence - 1]
        assert source[start:end] == original, \
            "%s: span mismatch %r != %r" % (item_id, source[start:end], original)
        errors.append({
            "start": start,
            "end": end,
            "original": original,
            "replacement": replacement,
            "type": etype,
        })

    errors.sort(key=lambda e: e["start"])
    for a, b in zip(errors, errors[1:]):
        assert a["end"] <= b["start"], "%s: overlapping error spans" % item_id

    target = source
    for err in reversed(errors):
        target = target[:err["start"]] + err["replacement"] + target[err["end"]:]

    assert target != source, "%s: error item does not change the text" % item_id
    return {
        "id": item_id,
        "category": category,
        "source": source,
        "target": target,
        "errors": errors,
        "must_not_change": [],
    }


def build_clean_item(item_id, category, entry):
    if isinstance(entry, tuple):
        source, must_not_change = entry
    else:
        source, must_not_change = entry, []
    for token in must_not_change:
        assert token in source, "%s: must_not_change %r absent from source" % (item_id, token)
    return {
        "id": item_id,
        "category": category,
        "source": source,
        "target": source,
        "errors": [],
        "must_not_change": list(must_not_change),
    }


def main():
    items = []
    counts = {}
    seen = {}
    for category, entries, kind in CATEGORIES:
        for index, entry in enumerate(entries, start=1):
            item_id = "%s-%04d" % (category, index)
            if kind == "clean":
                item = build_clean_item(item_id, category, entry)
            else:
                item = build_error_item(item_id, category, entry)
            source = item["source"]
            if source in seen:
                raise AssertionError("duplicate source in %s and %s: %r" % (seen[source], item_id, source))
            seen[source] = item_id
            words = len(re.findall(r"\S+", source))
            assert 3 <= words <= 30, "%s: %d words out of range" % (item_id, words)
            items.append(item)
        counts[category] = len(entries)

    lines = [json.dumps(item, ensure_ascii=False, sort_keys=False) for item in items]
    payload = "\n".join(lines) + "\n"
    raw = payload.encode("utf-8")
    digest = hashlib.sha256(raw).hexdigest()

    jsonl_path = os.path.join(OUT_DIR, "ru_frozen_v1.jsonl")

    # A frozen corpus is only frozen if something refuses to overwrite it. Rebuilding
    # byte-identical output is harmless and stays silent; producing *different* bytes
    # under the same name is how a golden set stops being comparable to the results
    # already published against it, so that needs an explicit new version instead.
    if os.path.exists(jsonl_path):
        existing = io.open(jsonl_path, "rb").read()
        if existing != raw:
            existing_digest = hashlib.sha256(existing).hexdigest()
            if "--force" not in sys.argv:
                sys.stderr.write(
                    "refusing to overwrite a frozen corpus with different content.\n"
                    "  file        : %s\n"
                    "  on disk     : %s\n"
                    "  would write : %s\n"
                    "\n"
                    "ru_frozen_v1 is frozen. To change the benchmark, add a v2:\n"
                    "  1. copy this generator to build_v2.py and edit that\n"
                    "  2. write ru_frozen_v2.jsonl + ru_frozen_v2.meta.json\n"
                    "  3. register it in GoldenDatasetIntegrityTests\n"
                    "  4. keep v1 in place so published results stay comparable\n"
                    "\n"
                    "Pass --force only to repair a file that drifted from this "
                    "generator's own output (e.g. line-ending damage).\n"
                    % (jsonl_path, existing_digest, digest))
                return 1

            sys.stderr.write("--force: overwriting %s (%s -> %s)\n"
                             % (jsonl_path, existing_digest, digest))

    with io.open(jsonl_path, "wb") as handle:
        handle.write(raw)

    meta = {
        "name": "ru_frozen_v1",
        "version": 1,
        "date": FROZEN_DATE,
        "status": "FROZEN: do not modify; regenerate as v2 if changes are required.",
        "role": "golden",
        "hash_policy": (
            "sha256 over the file's exact bytes, which are UTF-8 with LF line endings. "
            "This generator writes LF in binary mode; .gitattributes marks *.jsonl -text "
            "so git never converts them. A CRLF copy hashes differently and is drift, "
            "not a new version — repair it with `python build.py --force`."
        ),
        "item_count": len(items),
        "sha256": digest,
        "file": "ru_frozen_v1.jsonl",
        "counts_by_category": counts,
        "error_item_count": sum(1 for i in items if i["errors"]),
        "clean_item_count": sum(1 for i in items if not i["errors"]),
        "error_span_count": sum(len(i["errors"]) for i in items),
        "generator": "ai/data/benchmark/_gen/build.py",
    }
    meta_path = os.path.join(OUT_DIR, "ru_frozen_v1.meta.json")
    with io.open(meta_path, "wb") as handle:
        handle.write((json.dumps(meta, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))

    print("items: %d" % len(items))
    print("sha256: %s" % digest)
    for category, _, _ in CATEGORIES:
        print("  %-14s %d" % (category, counts[category]))


if __name__ == "__main__":
    sys.exit(main() or 0)
