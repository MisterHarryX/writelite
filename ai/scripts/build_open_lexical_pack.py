#!/usr/bin/env python3
"""Build the Russian-only OpenRussian morphology/lexical pack.

Network access is used only by this development-time builder.  Every upstream
file is pinned to an immutable commit and verified before parsing.  The shipped
application never downloads dictionary data.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import io
import json
import re
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CORE_PATH = ROOT / "resources" / "lexical" / "writelight-lexical-core.json"
OUT_PATH = ROOT / "resources" / "lexical" / "writelight-lexical-open.json"
MORPHOLOGY_PATH = ROOT / "resources" / "lexical" / "morphology-index.json"

OPENRUSSIAN_COMMIT = "50e210c4803237779cb562bc1abcea529066031c"
OPENRUSSIAN_REPOSITORY = "https://github.com/Badestrand/russian-dictionary"
OPENRUSSIAN_RAW = (
    "https://raw.githubusercontent.com/Badestrand/russian-dictionary/"
    f"{OPENRUSSIAN_COMMIT}"
)
SOURCE_FILES = {
    "nouns.csv": ("noun", "c388f9e6dde51932832be8d7e9afdbe9f0acee72fcf70677f0fe25ea61293c84"),
    "verbs.csv": ("verb", "8659de6799b949fb35f080b08088fb7d347ed300490954ebb380a31642646e1d"),
    "adjectives.csv": ("adj", "89ab5d10dcd2f21f6b485de704aef372f32251468e3aa96f18b21e259f26b80a"),
    "others.csv": ("unknown", "9f22a16b17fc9a564298112168b667fced11aafb254ccebf5b7544fc37cdaa92"),
}
FORM_COLUMNS = {
    "noun": (
        "sg_nom", "sg_gen", "sg_dat", "sg_acc", "sg_inst", "sg_prep",
        "pl_nom", "pl_gen", "pl_dat", "pl_acc", "pl_inst", "pl_prep",
    ),
    "verb": (
        "partner", "imperative_sg", "imperative_pl",
        "past_m", "past_f", "past_n", "past_pl",
        "presfut_sg1", "presfut_sg2", "presfut_sg3",
        "presfut_pl1", "presfut_pl2", "presfut_pl3",
    ),
    "adj": tuple(
        f"decl_{gender}_{case}"
        for gender in ("m", "f", "n", "pl")
        for case in ("nom", "gen", "dat", "acc", "inst", "prep")
    ) + ("comparative", "superlative", "short_m", "short_f", "short_n", "short_pl"),
    "unknown": (),
}
USER_AGENT = "WriteLite verified lexical builder/2.0"


def _download_or_read(name: str, expected_sha256: str, cache_dir: Path | None) -> bytes:
    cache_path = cache_dir / name if cache_dir else None
    if cache_path and cache_path.exists():
        data = cache_path.read_bytes()
    else:
        last_error: Exception | None = None
        for attempt in range(3):
            try:
                request = urllib.request.Request(
                    f"{OPENRUSSIAN_RAW}/{name}",
                    headers={"User-Agent": USER_AGENT},
                )
                with urllib.request.urlopen(request, timeout=90) as response:
                    data = response.read()
                break
            except Exception as error:  # network is development-time only
                last_error = error
                if attempt < 2:
                    time.sleep(attempt + 1)
        else:
            raise RuntimeError(f"download failed for {name}") from last_error

    actual = hashlib.sha256(data).hexdigest()
    if actual != expected_sha256:
        raise RuntimeError(f"checksum mismatch for {name}: {actual}")
    if cache_path and not cache_path.exists():
        cache_path.parent.mkdir(parents=True, exist_ok=True)
        cache_path.write_bytes(data)
    return data


def _clean_form(value: str | None) -> str:
    if not value:
        return ""
    value = value.replace("'", "").replace("\u0301", "").strip()
    value = re.sub(r"\s+", " ", value)
    return value.strip(" *")


def _split_values(value: str | None, max_items: int = 16) -> list[str]:
    if not value:
        return []
    result: list[str] = []
    for part in re.split(r"[,;/]", value):
        candidate = _clean_form(part)
        if candidate and candidate not in result:
            result.append(candidate)
        if len(result) >= max_items:
            break
    return result


def _parse_source(name: str, pos: str, data: bytes) -> list[dict]:
    text = data.decode("utf-8-sig")
    rows = csv.DictReader(io.StringIO(text), delimiter="\t")
    entries: list[dict] = []
    for row in rows:
        lemma = _clean_form(row.get("bare"))
        if not lemma or len(lemma) > 96 or not any("а" <= ch.lower() <= "я" or ch.lower() == "ё" for ch in lemma):
            continue
        forms: list[str] = []
        for column in FORM_COLUMNS[pos]:
            for form in _split_values(row.get(column)):
                if form.casefold() != lemma.casefold() and form not in forms:
                    forms.append(form)
        entries.append({
            "lemma": lemma,
            "language": "ru",
            "pos": pos,
            "inflections": forms,
            "synonyms": [],
            "antonyms": [],
            "definitions": [],
            "examples": [],
        })
    return entries


def _merge(existing: dict, incoming: dict) -> dict:
    for field in ("inflections", "definitions", "synonyms", "antonyms", "examples"):
        combined = list(existing.get(field) or [])
        seen = {
            json.dumps(item, ensure_ascii=False, sort_keys=True)
            if isinstance(item, dict) else str(item).casefold()
            for item in combined
        }
        for item in incoming.get(field) or []:
            key = json.dumps(item, ensure_ascii=False, sort_keys=True) if isinstance(item, dict) else str(item).casefold()
            if key not in seen:
                combined.append(item)
                seen.add(key)
        existing[field] = combined
    if existing.get("pos") in (None, "", "unknown") and incoming.get("pos"):
        existing["pos"] = incoming["pos"]
    return existing


def _compact_bytes(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--cache-dir", type=Path, help="optional verified source cache")
    args = parser.parse_args()

    core = json.loads(CORE_PATH.read_text(encoding="utf-8"))
    by_key: dict[str, dict] = {}
    for entry in core["entries"]:
        if entry.get("language") != "ru":
            raise RuntimeError("core pack contains a non-Russian entry")
        by_key[entry["lemma"].casefold().replace("ё", "е")] = entry

    counts: dict[str, int] = {}
    for name, (pos, expected_sha256) in SOURCE_FILES.items():
        data = _download_or_read(name, expected_sha256, args.cache_dir)
        parsed = _parse_source(name, pos, data)
        counts[name] = len(parsed)
        for entry in parsed:
            key = entry["lemma"].casefold().replace("ё", "е")
            by_key[key] = _merge(by_key[key], entry) if key in by_key else entry

    entries = sorted(by_key.values(), key=lambda item: item["lemma"].casefold())
    document = {
        "manifest": {
            "formatVersion": 2,
            "packId": "writelight-lexical-open",
            "packVersion": "4.0.0-openrussian-ru",
            "displayName": "Русский морфологический пакет WriteLite",
            "license": "CC-BY-SA-4.0",
            "licenseNote": (
                "OpenRussian contributors, CC BY-SA 4.0; commit "
                f"{OPENRUSSIAN_COMMIT}. Добавлены оригинальные статьи WriteLite CC0. "
                "Пакет не содержит статей словаря Ожегова."
            ),
            "source": f"{OPENRUSSIAN_REPOSITORY}/tree/{OPENRUSSIAN_COMMIT}",
            "isDemo": False,
            "entryCount": len(entries),
            "maxBytes": 64 * 1024 * 1024,
        },
        "entries": entries,
    }
    encoded = _compact_bytes(document) + b"\n"
    if len(encoded) > document["manifest"]["maxBytes"]:
        raise RuntimeError(f"pack is too large: {len(encoded)} bytes")
    OUT_PATH.write_bytes(encoded)

    morphology = [
        {"lemma": e["lemma"], "language": "ru", "pos": e["pos"], "forms": e["inflections"]}
        for e in entries
    ]
    MORPHOLOGY_PATH.write_bytes(_compact_bytes(morphology) + b"\n")
    print(f"path={OUT_PATH}")
    print(f"entries={len(entries)}")
    print(f"bytes={len(encoded)}")
    print(f"sha256={hashlib.sha256(encoded).hexdigest()}")
    print(f"sources={json.dumps(counts, sort_keys=True)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
