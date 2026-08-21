#!/usr/bin/env python3
"""Build the Russian <-> English translation index.

Development-time ETL only; the shipped application never downloads anything.

Why this exists
---------------
WriteLite ships a Russian pack and an English pack, and both are good at their own
language, but nothing connected them: there was no data anywhere in the product
saying that "красивый" and "beautiful" are the same idea. The dictionary page needs
that link to offer English translations, and translations are the one kind of
dictionary content that absolutely must not be produced by a model — a fabricated
translation is indistinguishable from a real one until it embarrasses someone.

Source   : OpenRussian, the same pinned commit the Russian lexical pack is built
           from, `translations_en` column.
Licence  : CC-BY-SA-4.0. Attribution to OpenRussian contributors is written into
           the output manifest and is already recorded in sources.manifest.json.
Output   : resources/lexical/ru-en-translations.db

SQLite rather than JSON, for the same reason the lexical packs moved to SQLite
(docs/LEXICAL_STORAGE_BENCHMARK.md): as JSON this index is 4.4 MB on disk and
roughly twenty on the heap, which would hand back a good part of the 230 MB that
migration saved. As an indexed table it costs a file handle.

Both directions are stored. The reverse map is built here rather than in the
application because inverting forty thousand entries at startup would cost more
than the whole dictionary page is allowed to.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import csv
import json
import re
import sqlite3
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT_PATH = ROOT / "resources" / "lexical" / "ru-en-translations.db"

OPENRUSSIAN_COMMIT = "50e210c4803237779cb562bc1abcea529066031c"
OPENRUSSIAN_RAW = (
    "https://raw.githubusercontent.com/Badestrand/russian-dictionary/"
    f"{OPENRUSSIAN_COMMIT}"
)
SOURCE_FILES = {
    "nouns.csv": "c388f9e6dde51932832be8d7e9afdbe9f0acee72fcf70677f0fe25ea61293c84",
    "verbs.csv": "8659de6799b949fb35f080b08088fb7d347ed300490954ebb380a31642646e1d",
    "adjectives.csv": "89ab5d10dcd2f21f6b485de704aef372f32251468e3aa96f18b21e259f26b80a",
    "others.csv": "9f22a16b17fc9a564298112168b667fced11aafb254ccebf5b7544fc37cdaa92",
}
SOURCE_ID = "openrussian-50e210c4"
USER_AGENT = "WriteLite verified lexical builder/2.0"

# Caps. A word with thirty glosses is a dictionary artefact, not a useful answer,
# and the card only has room to show a handful anyway.
MAX_PER_LEMMA = 8
MAX_REVERSE = 10
MAX_TRANSLATION_CHARS = 40

RE_RU_LEMMA = re.compile(r"^[а-яёА-ЯЁ][а-яёА-ЯЁ\-]{0,47}$")
RE_EN_TERM = re.compile(r"^[a-zA-Z][a-zA-Z '\-]{0,39}$")

# OpenRussian glosses carry editorial asides: "to go (on foot)", "bank [of a river]".
RE_PARENTHETICAL = re.compile(r"\s*[\(\[][^)\]]*[\)\]]")


def _fold(value: str) -> str:
    return value.strip().casefold().replace("ё", "е")


def _download_or_read(name: str, expected_sha256: str, cache_dir: Path | None) -> bytes:
    cache_path = cache_dir / name if cache_dir else None
    if cache_path and cache_path.exists():
        data = cache_path.read_bytes()
    else:
        last_error: Exception | None = None
        for attempt in range(3):
            try:
                request = urllib.request.Request(
                    f"{OPENRUSSIAN_RAW}/{name}", headers={"User-Agent": USER_AGENT}
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

        if cache_path:
            cache_path.parent.mkdir(parents=True, exist_ok=True)
            cache_path.write_bytes(data)

    digest = hashlib.sha256(data).hexdigest()
    if digest != expected_sha256:
        raise RuntimeError(
            f"{name}: sha256 mismatch\n  expected {expected_sha256}\n  actual   {digest}"
        )
    return data


def _clean_translations(raw: str) -> list[str]:
    """Split one `translations_en` cell into individual English terms."""
    if not raw:
        return []

    out: list[str] = []
    for piece in raw.split(","):
        term = RE_PARENTHETICAL.sub("", piece).strip().strip(".;:")
        if not term or len(term) > MAX_TRANSLATION_CHARS:
            continue
        if not RE_EN_TERM.match(term):
            continue
        if term.casefold() not in {t.casefold() for t in out}:
            out.append(term)
    return out


def build(cache_dir: Path | None) -> dict:
    forward: dict[str, list[str]] = {}
    reverse: dict[str, list[str]] = {}
    seen_pairs: set[tuple[str, str]] = set()
    stats = {"rows": 0, "lemmas": 0, "pairs": 0, "skipped_lemma": 0, "no_translation": 0}

    for name, sha in SOURCE_FILES.items():
        data = _download_or_read(name, sha, cache_dir)
        reader = csv.DictReader(io.StringIO(data.decode("utf-8")), delimiter="\t")
        for row in reader:
            stats["rows"] += 1
            lemma = (row.get("bare") or "").strip()
            if not lemma or not RE_RU_LEMMA.match(lemma):
                stats["skipped_lemma"] += 1
                continue

            terms = _clean_translations(row.get("translations_en") or "")
            if not terms:
                stats["no_translation"] += 1
                continue

            key = _fold(lemma)
            bucket = forward.setdefault(key, [])
            for term in terms:
                if len(bucket) >= MAX_PER_LEMMA:
                    break
                if term.casefold() in {t.casefold() for t in bucket}:
                    continue
                bucket.append(term)

                pair = (key, term.casefold())
                if pair in seen_pairs:
                    continue
                seen_pairs.add(pair)
                stats["pairs"] += 1

                back = reverse.setdefault(term.casefold(), [])
                if len(back) < MAX_REVERSE and lemma not in back:
                    back.append(lemma)

    forward = {k: v for k, v in forward.items() if v}
    stats["lemmas"] = len(forward)
    stats["english_terms"] = len(reverse)

    return {
        "manifest": {
            "formatVersion": 1,
            "packId": "writelight-translations-ru-en",
            "packVersion": f"1.0.0-openrussian-{OPENRUSSIAN_COMMIT[:8]}",
            "displayName": "Переводы RU ↔ EN",
            "license": "CC-BY-SA-4.0",
            "licenseNote": (
                "OpenRussian contributors, CC BY-SA 4.0; "
                f"commit {OPENRUSSIAN_COMMIT}. Столбец translations_en."
            ),
            "source": f"https://github.com/Badestrand/russian-dictionary/tree/{OPENRUSSIAN_COMMIT}",
            "sourceId": SOURCE_ID,
            "entryCount": len(forward),
        },
        "ru": dict(sorted(forward.items())),
        "en": dict(sorted(reverse.items())),
    }, stats


def _write_database(document: dict, out: Path) -> None:
    if out.exists():
        out.unlink()

    connection = sqlite3.connect(out)
    try:
        connection.executescript(
            """
            PRAGMA journal_mode = OFF;
            PRAGMA synchronous = OFF;

            CREATE TABLE manifest (key TEXT PRIMARY KEY, value TEXT NOT NULL);

            -- One row per (headword, gloss). `source` is the language of the
            -- headword, so a single table serves both directions.
            --
            -- WITHOUT ROWID: the primary key is the whole access path, so the rows
            -- live in the B-tree itself. A rowid table plus a covering index would
            -- store every pair twice and cost about 6 MB more on disk.
            CREATE TABLE translation (
                source     TEXT NOT NULL,
                normalized TEXT NOT NULL,
                ordinal    INTEGER NOT NULL,
                value      TEXT NOT NULL,
                PRIMARY KEY (source, normalized, ordinal)
            ) WITHOUT ROWID;
            """
        )

        connection.executemany(
            "INSERT INTO manifest (key, value) VALUES (?, ?)",
            [(key, str(value)) for key, value in document["manifest"].items()],
        )

        rows = []
        for source, table in (("ru", document["ru"]), ("en", document["en"])):
            for normalized, values in table.items():
                for ordinal, value in enumerate(values):
                    rows.append((source, normalized, ordinal, value))

        connection.executemany(
            "INSERT INTO translation (source, normalized, ordinal, value) VALUES (?, ?, ?, ?)",
            rows,
        )

        connection.executescript("VACUUM;")
        connection.commit()
    finally:
        connection.close()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cache", type=Path, default=ROOT / "ai" / "data" / "lexical" / "openrussian")
    parser.add_argument("--out", type=Path, default=OUT_PATH)
    args = parser.parse_args()

    document, stats = build(args.cache)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    _write_database(document, args.out)

    stats_path = args.out.with_suffix(".stats.json")
    stats_path.write_text(json.dumps(stats, ensure_ascii=False, indent=2), encoding="utf-8")

    size_mb = args.out.stat().st_size / 1024 / 1024
    print(f"wrote {args.out} ({size_mb:.1f} MB)")
    print(json.dumps(stats, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
