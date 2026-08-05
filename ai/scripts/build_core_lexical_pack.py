#!/usr/bin/env python3
"""Build the deterministic Russian-only WriteLite explanatory core.

The source file is hand-authored CC0 data.  This builder rejects non-Russian
entries, duplicate lemmas and obsolete cross-language fields before producing
the runtime pack and morphology side-car.  It performs no network requests.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE_PATH = ROOT / "resources" / "lexical" / "source" / "core-ru.entries.json"
OUT_PATH = ROOT / "resources" / "lexical" / "writelight-lexical-core.json"
MORPHOLOGY_PATH = ROOT / "resources" / "lexical" / "morphology-index.json"

FORMAT_VERSION = 2
PACK_VERSION = "4.0.0-ru-cc0"
SOURCE_URL = "https://github.com/OpenAI/WriteLite"
ALLOWED_FIELDS = {
    "lemma",
    "language",
    "pos",
    "inflections",
    "synonyms",
    "antonyms",
    "definitions",
    "examples",
}


def _compact_bytes(value: object) -> bytes:
    return json.dumps(
        value,
        ensure_ascii=False,
        separators=(",", ":"),
        sort_keys=False,
    ).encode("utf-8")


def _validate_and_normalize(raw_entries: object) -> list[dict]:
    if not isinstance(raw_entries, list) or not raw_entries:
        raise ValueError("core source must be a non-empty JSON array")

    entries: list[dict] = []
    seen: set[str] = set()
    for index, raw in enumerate(raw_entries):
        if not isinstance(raw, dict):
            raise ValueError(f"entry {index} is not an object")
        unknown = set(raw) - ALLOWED_FIELDS
        if unknown:
            raise ValueError(f"entry {index} has unsupported fields: {sorted(unknown)}")

        lemma = str(raw.get("lemma") or "").strip()
        if not lemma:
            raise ValueError(f"entry {index} has no lemma")
        if raw.get("language") != "ru":
            raise ValueError(f"entry {lemma!r} is not Russian")
        key = lemma.casefold().replace("ё", "е")
        if key in seen:
            raise ValueError(f"duplicate lemma: {lemma}")
        seen.add(key)

        entry = {
            "lemma": lemma,
            "language": "ru",
            "pos": str(raw.get("pos") or "unknown"),
            "inflections": list(dict.fromkeys(raw.get("inflections") or [])),
            "synonyms": raw.get("synonyms") or [],
            "antonyms": raw.get("antonyms") or [],
            "definitions": raw.get("definitions") or [],
            "examples": raw.get("examples") or [],
        }
        entries.append(entry)

    entries.sort(key=lambda item: item["lemma"].casefold())
    return entries


def _write_morphology(entries: list[dict]) -> None:
    morphology = [
        {
            "lemma": entry["lemma"],
            "language": "ru",
            "pos": entry["pos"],
            "forms": entry["inflections"],
        }
        for entry in entries
    ]
    MORPHOLOGY_PATH.write_bytes(_compact_bytes(morphology) + b"\n")


def main() -> int:
    raw_entries = json.loads(SOURCE_PATH.read_text(encoding="utf-8"))
    entries = _validate_and_normalize(raw_entries)
    document = {
        "manifest": {
            "formatVersion": FORMAT_VERSION,
            "packId": "writelight-lexical-core",
            "packVersion": PACK_VERSION,
            "displayName": "Толковый словарь WriteLite: базовый русский пакет",
            "license": "CC0-1.0",
            "licenseNote": (
                "Оригинальные статьи WriteLite; ограниченный справочный корпус. "
                "Не является словарём Ожегова."
            ),
            "source": SOURCE_URL,
            "isDemo": False,
            "entryCount": len(entries),
            "maxBytes": 64 * 1024 * 1024,
        },
        "entries": entries,
    }
    encoded = _compact_bytes(document) + b"\n"
    OUT_PATH.write_bytes(encoded)
    _write_morphology(entries)
    print(f"path={OUT_PATH}")
    print(f"entries={len(entries)}")
    print(f"bytes={len(encoded)}")
    print(f"sha256={hashlib.sha256(encoded).hexdigest()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
