#!/usr/bin/env python3
"""
Import a confirmed public-domain historical Russian dictionary edition (e.g. Dal)
into WriteLite historical pack format.

LEGAL:
  - Only use confirmed public-domain historical editions.
  - Do NOT scrape modern commercial sites or unknown GitHub dumps.
  - Provide --source-url and --edition metadata; they are stored in the manifest.
  - Output is NEVER labeled as modern normative Russian.

Example (after you legally obtain a PD text file):

  python ai/scripts/import_dal_dictionary.py \\
    --input path/to/dal_pd.txt \\
    --edition "Толковый словарь живого великорусского языка, изд. 2, 1880–1882" \\
    --source-url "https://example.org/pd-source" \\
    --license "Public Domain" \\
    --output resources/lexical/historical

This script does not download by default. Pass --download-url only for known PD URLs
you have verified yourself.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import urllib.request
from datetime import datetime, timezone
from pathlib import Path


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def parse_simple_articles(text: str) -> list[dict]:
    """
    Very conservative parser: lines starting with ALL-CAPS / Title lemma + dash definition.
    Real Dal XML/TEI sources should replace this with a dedicated parser per edition.
    """
    entries: list[dict] = []
    # Pattern: LEMMA — definition...
    for line in text.splitlines():
        line = line.strip()
        if not line or len(line) < 8:
            continue
        m = re.match(r"^([А-ЯЁA-Z][А-Яа-яЁёA-Za-z\-]{1,40})\s*[—\-–:]\s+(.+)$", line)
        if not m:
            continue
        lemma = m.group(1).lower()
        definition = m.group(2).strip()
        if len(definition) < 12:
            continue
        entries.append(
            {
                "lemma": lemma,
                "pos": "unknown",
                "definition": definition,
                "historical": True,
            }
        )
    return entries


def main() -> int:
    p = argparse.ArgumentParser(description="Import PD historical dictionary into WriteLite pack")
    p.add_argument("--input", type=Path, help="Local PD text file")
    p.add_argument("--download-url", type=str, default=None, help="Optional verified PD URL")
    p.add_argument("--edition", type=str, required=True)
    p.add_argument("--source-url", type=str, required=True)
    p.add_argument("--license", type=str, default="Public Domain")
    p.add_argument("--output", type=Path, default=Path("resources/lexical/historical"))
    p.add_argument("--author", type=str, default="В. И. Даль")
    args = p.parse_args()

    args.output.mkdir(parents=True, exist_ok=True)
    raw_path = args.output / "source.raw.txt"

    if args.download_url:
        print("Downloading", args.download_url)
        urllib.request.urlretrieve(args.download_url, raw_path)
        input_path = raw_path
    elif args.input:
        input_path = args.input
    else:
        print("Provide --input or --download-url")
        return 2

    if not input_path.exists():
        print("Input missing:", input_path)
        return 2

    text = input_path.read_text(encoding="utf-8", errors="replace")
    entries = parse_simple_articles(text)
    if not entries:
        print(
            "No articles parsed. For a real edition, extend the parser or supply "
            "pre-normalized JSONL. Writing empty pack is refused."
        )
        return 3

    entries_path = args.output / "historical.entries.jsonl"
    with entries_path.open("w", encoding="utf-8") as f:
        for e in entries:
            e["era"] = args.edition
            f.write(json.dumps(e, ensure_ascii=False) + "\n")

    digest = sha256_file(entries_path)
    manifest = {
        "formatVersion": 1,
        "packId": "historical-dal-pd",
        "packVersion": "import-1",
        "displayName": "Historical Russian dictionary (PD import)",
        "editionLabel": args.edition,
        "author": args.author,
        "license": args.license,
        "sourceUrl": args.source_url,
        "retrievedAt": datetime.now(timezone.utc).isoformat(),
        "entryCount": len(entries),
        "sha256": digest,
        "isHistorical": True,
        "isDal": "даль" in args.edition.lower() or "dal" in args.edition.lower(),
        "modernNormative": False,
        "notes": "Entries are historical. Do not present as sole modern meaning.",
    }
    (args.output / "historical.manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(f"Wrote {len(entries)} entries → {entries_path}")
    print("Manifest:", args.output / "historical.manifest.json")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
