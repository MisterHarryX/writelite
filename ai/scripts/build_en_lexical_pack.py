#!/usr/bin/env python3
"""Build the English lexical pack from Open English WordNet (WN-LMF XML).

Development-time ETL only; the shipped application never downloads anything.
The source release is pinned and verified by SHA-256 before parsing.

Source   : Open English WordNet 2024, https://en-word.net/
Licence  : CC BY 4.0, and the underlying Princeton WordNet License.
           Attribution to BOTH the Open English WordNet team and Princeton
           WordNet is required and is written into the pack manifest.

Output   : resources/lexical/writelight-lexical-en.json
           (same document shape as the Russian packs so one loader serves both)
"""
from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import re
import time
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT_PATH = ROOT / "resources" / "lexical" / "writelight-lexical-en.json"

SOURCE_URL = "https://en-word.net/static/english-wordnet-2024.xml.gz"
SOURCE_SHA256 = "e1f633b0a93758cae34ea27c44c4dad310a8af2467b155f99dd6673af697e875"
SOURCE_BYTES = 12912118
SOURCE_VERSION = "2024"
SOURCE_ID = "oewn-2024"

POS_MAP = {"n": "noun", "v": "verb", "a": "adj", "s": "adj", "r": "adv"}

MAX_DEFINITIONS = 6
MAX_DEFINITION_CHARS = 320
MAX_EXAMPLES_PER_SENSE = 1
MAX_EXAMPLE_CHARS = 200
MAX_SYNONYMS = 12
MAX_ANTONYMS = 8

# Single-token English headwords: letters, optional internal hyphen/apostrophe.
RE_LEMMA = re.compile(r"^[A-Za-z][A-Za-z'\-]{0,47}$")

USER_AGENT = "WriteLite verified lexical builder/2.0"


def download_or_read(cache: Path | None) -> bytes:
    if cache and cache.exists():
        data = cache.read_bytes()
    else:
        request = urllib.request.Request(SOURCE_URL, headers={"User-Agent": USER_AGENT})
        with urllib.request.urlopen(request, timeout=300) as response:
            data = response.read()
        if cache:
            cache.parent.mkdir(parents=True, exist_ok=True)
            cache.write_bytes(data)
    digest = hashlib.sha256(data).hexdigest()
    if len(data) != SOURCE_BYTES or digest != SOURCE_SHA256:
        raise RuntimeError(f"source checksum mismatch: bytes={len(data)} sha256={digest}")
    return data


def local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def parse_wordnet(xml_bytes: bytes) -> tuple[dict, dict]:
    """Return (synsets, entries) keyed by their WN-LMF ids."""
    synsets: dict[str, dict] = {}
    entries: dict[str, dict] = {}

    root = ET.fromstring(gzip.decompress(xml_bytes))
    for lexicon in root:
        if local(lexicon.tag) != "Lexicon":
            continue
        for node in lexicon:
            name = local(node.tag)
            if name == "LexicalEntry":
                lemma_node = node.find("{*}Lemma")
                if lemma_node is None:
                    continue
                senses = []
                for sense in node.findall("{*}Sense"):
                    antonyms = [
                        rel.get("target")
                        for rel in sense.findall("{*}SenseRelation")
                        if rel.get("relType") == "antonym" and rel.get("target")
                    ]
                    senses.append({
                        "id": sense.get("id"),
                        "synset": sense.get("synset"),
                        "antonyms": antonyms,
                    })
                entries[node.get("id")] = {
                    "lemma": lemma_node.get("writtenForm") or "",
                    "pos": lemma_node.get("partOfSpeech") or "",
                    "senses": senses,
                }
            elif name == "Synset":
                definition = node.findtext("{*}Definition") or ""
                examples = [e.text.strip() for e in node.findall("{*}Example")
                            if e.text and e.text.strip()]
                synsets[node.get("id")] = {
                    "definition": " ".join(definition.split()),
                    "examples": examples,
                    "members": (node.get("members") or "").split(),
                    "pos": node.get("partOfSpeech") or "",
                }
    return synsets, entries


def build_entries(synsets: dict, entries: dict) -> tuple[list[dict], dict[str, int]]:
    # Map a sense id back to the lemma that owns it, for antonym resolution.
    sense_owner: dict[str, str] = {}
    for entry in entries.values():
        for sense in entry["senses"]:
            if sense["id"]:
                sense_owner[sense["id"]] = entry["lemma"]

    by_key: dict[str, dict] = {}
    stats = {"skippedMultiword": 0, "skippedShape": 0}

    for entry in entries.values():
        lemma = entry["lemma"].strip()
        if " " in lemma or "_" in lemma:
            stats["skippedMultiword"] += 1
            continue
        if not RE_LEMMA.match(lemma):
            stats["skippedShape"] += 1
            continue
        pos = POS_MAP.get(entry["pos"])
        if pos is None:
            continue

        key = lemma.casefold()
        record = by_key.get(key)
        if record is None:
            record = {
                "lemma": lemma,
                "language": "en",
                "pos": pos,
                "inflections": [],
                "synonyms": [],
                "antonyms": [],
                "definitions": [],
                "examples": [],
            }
            by_key[key] = record

        for sense in entry["senses"]:
            synset = synsets.get(sense["synset"] or "")
            if synset is None:
                continue
            sense_id = str(len(record["definitions"]) + 1)

            if synset["definition"] and len(record["definitions"]) < MAX_DEFINITIONS:
                text = synset["definition"]
                if len(text) > MAX_DEFINITION_CHARS:
                    text = text[:MAX_DEFINITION_CHARS].rsplit(" ", 1)[0] + "…"
                record["definitions"].append({
                    "text": text,
                    "pos": POS_MAP.get(synset["pos"], pos),
                    "senseId": sense_id,
                    "sourceId": SOURCE_ID,
                })
                for example in synset["examples"][:MAX_EXAMPLES_PER_SENSE]:
                    record["examples"].append({
                        "text": example[:MAX_EXAMPLE_CHARS],
                        "senseId": sense_id,
                    })

            for member_id in synset["members"]:
                member = entries.get(member_id)
                if member is None:
                    continue
                value = member["lemma"].replace("_", " ").strip()
                if not value or value.casefold() == key or " " in value:
                    continue
                if len(record["synonyms"]) >= MAX_SYNONYMS:
                    break
                if any(s["value"].casefold() == value.casefold() for s in record["synonyms"]):
                    continue
                record["synonyms"].append({
                    "value": value,
                    "pos": pos,
                    "senseId": sense_id,
                    "relevance": 0.7,
                    "sourceId": SOURCE_ID,
                })

            for target in sense["antonyms"]:
                value = (sense_owner.get(target) or "").replace("_", " ").strip()
                if not value or value.casefold() == key or " " in value:
                    continue
                if len(record["antonyms"]) >= MAX_ANTONYMS:
                    break
                if any(a["value"].casefold() == value.casefold() for a in record["antonyms"]):
                    continue
                record["antonyms"].append({
                    "value": value,
                    "pos": pos,
                    "senseId": sense_id,
                    "relevance": 0.8,
                    "sourceId": SOURCE_ID,
                })

    ordered = sorted(by_key.values(), key=lambda item: item["lemma"].casefold())
    return ordered, stats


def compact(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--cache", type=Path, help="verified source cache path")
    parser.add_argument("--out", type=Path, default=OUT_PATH)
    args = parser.parse_args()

    started = time.time()
    raw = download_or_read(args.cache)
    print(f"source bytes={len(raw)} sha256={SOURCE_SHA256}", flush=True)

    synsets, entries = parse_wordnet(raw)
    print(f"parsed synsets={len(synsets)} lexicalEntries={len(entries)} "
          f"({time.time() - started:.0f}s)", flush=True)

    records, stats = build_entries(synsets, entries)

    document = {
        "manifest": {
            "formatVersion": 2,
            "packId": "writelight-lexical-en",
            "packVersion": f"1.0.0-oewn-{SOURCE_VERSION}",
            "displayName": "WriteLite English lexical pack",
            "language": "en",
            "license": "CC-BY-4.0",
            "licenseNote": (
                "Open English Wordnet 2024, CC BY 4.0 "
                "(https://creativecommons.org/licenses/by/4.0). "
                "Derived from the Princeton University WordNet database under the "
                "WordNet License; attribution is required to both the Open English "
                "WordNet team and Princeton WordNet."
            ),
            "source": SOURCE_URL,
            "sourceSha256": SOURCE_SHA256,
            "isDemo": False,
            "entryCount": len(records),
            "maxBytes": 64 * 1024 * 1024,
        },
        "entries": records,
    }

    encoded = compact(document) + b"\n"
    if len(encoded) > document["manifest"]["maxBytes"]:
        raise RuntimeError(f"pack is too large: {len(encoded)} bytes")
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_bytes(encoded)

    with_definitions = sum(1 for r in records if r["definitions"])
    with_synonyms = sum(1 for r in records if r["synonyms"])
    with_antonyms = sum(1 for r in records if r["antonyms"])
    report = {
        "path": str(args.out),
        "entries": len(records),
        "bytes": len(encoded),
        "sha256": hashlib.sha256(encoded).hexdigest(),
        "entriesWithDefinitions": with_definitions,
        "entriesWithSynonyms": with_synonyms,
        "entriesWithAntonyms": with_antonyms,
        "definitions": sum(len(r["definitions"]) for r in records),
        "synonyms": sum(len(r["synonyms"]) for r in records),
        "antonyms": sum(len(r["antonyms"]) for r in records),
        "examples": sum(len(r["examples"]) for r in records),
        "skipped": stats,
        "seconds": round(time.time() - started, 1),
    }
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
