#!/usr/bin/env python3
"""Extract Russian definitions, synonyms and antonyms from a ruwiktionary dump.

Development-time ETL only; the shipped application never reads a dump.  The
dump is pinned to a dated Wikimedia export and verified by SHA-1 (published
upstream) and SHA-256 (recorded in resources/lexical/sources.manifest.json)
before a single page is parsed.

Output is an intermediate JSONL layer -- one record per Russian lemma -- that
build_open_lexical_pack.py merges into the shipped pack.  Nothing here writes
into resources/ directly.

Licence: ruwiktionary text is CC-BY-SA-4.0, compatible with the existing
OpenRussian CC-BY-SA-4.0 layer.  Every emitted record carries sourceId
"ruwiktionary-<date>" so provenance survives the merge.
"""
from __future__ import annotations

import argparse
import bz2
import hashlib
import json
import re
import sys
import time
import xml.etree.ElementTree as ET
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import wikitext_ru as W  # noqa: E402

DUMP_DATE = "20260801"
DUMP_URL = (f"https://dumps.wikimedia.org/ruwiktionary/{DUMP_DATE}/"
            f"ruwiktionary-{DUMP_DATE}-pages-articles.xml.bz2")
EXPECTED_SHA1 = "334c0872597fbd1901e995becf927059706fb50a"
EXPECTED_SHA256 = "4d261710f3c377675a1b17314886c1b6c407c804ed9cd40b89a027895dd9ab93"
EXPECTED_BYTES = 333956701
SOURCE_ID = f"ruwiktionary-{DUMP_DATE}"

MAX_DEFINITIONS = 6
MAX_DEFINITION_CHARS = 320
MAX_EXAMPLES_PER_SENSE = 1
MAX_EXAMPLE_CHARS = 200
MAX_LINKS_PER_SENSE = 8

DEFINITION_SECTIONS = {"Значение", "Значения"}
SYNONYM_SECTIONS = {"Синонимы"}
ANTONYM_SECTIONS = {"Антонимы"}
MORPHOLOGY_PREFIX = "Морфологические"

# A lemma we are willing to index: Cyrillic, single token, optional hyphen.
RE_LEMMA = re.compile(r"^[А-Яа-яЁё][А-Яа-яЁё\-']{0,47}$")


def verify_dump(path: Path, skip: bool) -> dict[str, str | int]:
    size = path.stat().st_size
    if skip:
        return {"bytes": size, "sha1": "skipped", "sha256": "skipped"}
    h1, h256 = hashlib.sha1(), hashlib.sha256()
    with path.open("rb") as handle:
        while True:
            chunk = handle.read(1 << 22)
            if not chunk:
                break
            h1.update(chunk)
            h256.update(chunk)
    sha1, sha256 = h1.hexdigest(), h256.hexdigest()
    if size != EXPECTED_BYTES:
        raise RuntimeError(f"dump size mismatch: {size} != {EXPECTED_BYTES}")
    if sha1 != EXPECTED_SHA1:
        raise RuntimeError(f"dump sha1 mismatch: {sha1}")
    if sha256 != EXPECTED_SHA256:
        raise RuntimeError(f"dump sha256 mismatch: {sha256}")
    return {"bytes": size, "sha1": sha1, "sha256": sha256}


def parse_page(title: str, text: str) -> dict | None:
    """Build one lexical record from a page, or None if it has no Russian data."""
    russian = W.language_section(text, "ru")
    if russian is None:
        return None

    pos: str | None = None
    definitions: list[dict] = []
    synonym_lines: list[list[str]] = []
    antonym_lines: list[list[str]] = []

    for _, name, body in W.iter_sections(russian):
        if name.startswith(MORPHOLOGY_PREFIX):
            pos = pos or W.parse_pos(body)
        elif name in DEFINITION_SECTIONS and not definitions:
            for line in W.numbered_lines(body):
                if len(definitions) >= MAX_DEFINITIONS:
                    break
                payload = line.lstrip("# ").strip()
                label = W.extract_label(line)
                text_value = W.strip_markup(payload)
                if not text_value or text_value.lower() in W.EMPTY_MARKERS:
                    continue
                if len(text_value) > MAX_DEFINITION_CHARS:
                    text_value = text_value[:MAX_DEFINITION_CHARS].rsplit(" ", 1)[0] + "…"
                examples = [
                    e[:MAX_EXAMPLE_CHARS]
                    for e in W.extract_examples(line, limit=MAX_EXAMPLES_PER_SENSE)
                ]
                definitions.append({
                    "senseId": str(len(definitions) + 1),
                    "text": text_value,
                    "label": label,
                    "examples": examples,
                })
        elif name in SYNONYM_SECTIONS and not synonym_lines:
            synonym_lines = [W.split_sense_links(l, MAX_LINKS_PER_SENSE)
                             for l in W.numbered_lines(body)]
        elif name in ANTONYM_SECTIONS and not antonym_lines:
            antonym_lines = [W.split_sense_links(l, MAX_LINKS_PER_SENSE)
                             for l in W.numbered_lines(body)]

    # ruwiktionary aligns synonym/antonym list position with sense position.
    def aligned(lines: list[list[str]]) -> list[dict]:
        out: list[dict] = []
        seen: set[str] = set()
        for index, values in enumerate(lines):
            sense_id = str(index + 1)
            for value in values:
                key = value.casefold().replace("ё", "е")
                if key in seen or key == title.casefold().replace("ё", "е"):
                    continue
                seen.add(key)
                out.append({"value": value, "senseId": sense_id})
        return out

    synonyms = aligned(synonym_lines)
    antonyms = aligned(antonym_lines)
    if not definitions and not synonyms and not antonyms and not pos:
        return None

    return {
        "lemma": title,
        "language": "ru",
        "pos": pos,
        "definitions": definitions,
        "synonyms": synonyms,
        "antonyms": antonyms,
        "sourceId": SOURCE_ID,
    }


def iter_pages(path: Path):
    """Stream (title, text) for main-namespace, non-redirect pages."""
    with bz2.open(path, "rb") as stream:
        context = ET.iterparse(stream, events=("end",))
        for _, element in context:
            tag = element.tag.rsplit("}", 1)[-1]
            if tag != "page":
                continue
            ns = element.findtext("{*}ns")
            title = element.findtext("{*}title") or ""
            redirect = element.find("{*}redirect")
            text = element.findtext("{*}revision/{*}text") or ""
            element.clear()
            if ns != "0" or redirect is not None or not text:
                continue
            yield title, text


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dump", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--limit", type=int, default=0, help="stop after N emitted records")
    parser.add_argument("--skip-verify", action="store_true",
                        help="development shortcut; never use for a release build")
    args = parser.parse_args()

    checks = verify_dump(args.dump, args.skip_verify)
    print(f"dump={args.dump.name} bytes={checks['bytes']} sha256={checks['sha256']}", flush=True)

    args.out.parent.mkdir(parents=True, exist_ok=True)
    started = time.time()
    scanned = skipped_title = no_russian = emitted = 0
    with_definitions = with_synonyms = with_antonyms = with_pos = 0
    definition_total = synonym_total = antonym_total = example_total = 0

    with args.out.open("w", encoding="utf-8", newline="\n") as handle:
        for title, text in iter_pages(args.dump):
            scanned += 1
            if scanned % 250_000 == 0:
                print(f"  scanned={scanned} emitted={emitted} "
                      f"elapsed={time.time() - started:.0f}s", flush=True)
            if not RE_LEMMA.match(title):
                skipped_title += 1
                continue
            if "{{-ru-}}" not in text:
                no_russian += 1
                continue
            record = parse_page(title, text)
            if record is None:
                no_russian += 1
                continue
            handle.write(json.dumps(record, ensure_ascii=False) + "\n")
            emitted += 1
            if record["definitions"]:
                with_definitions += 1
                definition_total += len(record["definitions"])
                example_total += sum(len(d["examples"]) for d in record["definitions"])
            if record["synonyms"]:
                with_synonyms += 1
                synonym_total += len(record["synonyms"])
            if record["antonyms"]:
                with_antonyms += 1
                antonym_total += len(record["antonyms"])
            if record["pos"]:
                with_pos += 1
            if args.limit and emitted >= args.limit:
                break

    stats = {
        "dump": args.dump.name,
        "dumpUrl": DUMP_URL,
        "dumpSha256": checks["sha256"],
        "sourceId": SOURCE_ID,
        "pagesScanned": scanned,
        "skippedByTitle": skipped_title,
        "skippedNoRussian": no_russian,
        "records": emitted,
        "recordsWithDefinitions": with_definitions,
        "recordsWithSynonyms": with_synonyms,
        "recordsWithAntonyms": with_antonyms,
        "recordsWithPos": with_pos,
        "definitions": definition_total,
        "synonyms": synonym_total,
        "antonyms": antonym_total,
        "examples": example_total,
        "seconds": round(time.time() - started, 1),
    }
    args.out.with_suffix(".stats.json").write_text(
        json.dumps(stats, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(stats, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
