#!/usr/bin/env python3
"""Build the runtime SQLite lexical database from the JSON packs.

The JSON packs stay the build/export format; this produces the artifact the
application queries at runtime, so nothing has to be held in memory.

Schema
------
entry          one row per lemma, per language
form           every surface form (lemma + inflections), normalised for lookup
definition     senses, with the sourceId that says where each one came from
sense_link     synonyms and antonyms
definition_fts FTS5 index over definition text for full-text search

Lookup path is `form.normalized` -> `entry`, which is the query the UI issues on
every double-click, so that index is the one that has to be fast.

Usage:
  python ai/scripts/build_lexical_sqlite.py
  python ai/scripts/build_lexical_sqlite.py --out resources/lexical/writelight-lexical.db
"""
from __future__ import annotations

import argparse
import hashlib
import json
import sqlite3
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
LEXICAL_DIR = ROOT / "resources" / "lexical"
DEFAULT_OUT = LEXICAL_DIR / "writelight-lexical.db"

PACKS = (
    ("ru", "writelight-lexical-open.json"),
    ("ru", "writelight-lexical-core.json"),
    ("en", "writelight-lexical-en.json"),
)

SCHEMA = """
PRAGMA journal_mode = OFF;
PRAGMA synchronous = OFF;

CREATE TABLE meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE pack (
    pack_id      TEXT PRIMARY KEY,
    language     TEXT NOT NULL,
    pack_version TEXT NOT NULL,
    display_name TEXT,
    license      TEXT NOT NULL,
    license_note TEXT,
    source       TEXT,
    entry_count  INTEGER NOT NULL
);

CREATE TABLE entry (
    entry_id INTEGER PRIMARY KEY,
    lemma    TEXT NOT NULL,
    language TEXT NOT NULL,
    pos      TEXT NOT NULL,
    pack_id  TEXT NOT NULL REFERENCES pack(pack_id)
);

CREATE TABLE form (
    form_id    INTEGER PRIMARY KEY,
    entry_id   INTEGER NOT NULL REFERENCES entry(entry_id),
    surface    TEXT NOT NULL,
    normalized TEXT NOT NULL,
    is_lemma   INTEGER NOT NULL
);

CREATE TABLE definition (
    definition_id INTEGER PRIMARY KEY,
    entry_id      INTEGER NOT NULL REFERENCES entry(entry_id),
    ordinal       INTEGER NOT NULL,
    text          TEXT NOT NULL,
    pos           TEXT,
    label         TEXT,
    sense_id      TEXT,
    source_id     TEXT NOT NULL
);

CREATE TABLE example (
    example_id INTEGER PRIMARY KEY,
    entry_id   INTEGER NOT NULL REFERENCES entry(entry_id),
    text       TEXT NOT NULL,
    sense_id   TEXT
);

CREATE TABLE sense_link (
    link_id   INTEGER PRIMARY KEY,
    entry_id  INTEGER NOT NULL REFERENCES entry(entry_id),
    kind      TEXT NOT NULL,           -- 'synonym' | 'antonym'
    value     TEXT NOT NULL,
    pos       TEXT,
    relevance REAL NOT NULL DEFAULT 0.5,
    source_id TEXT
);
"""

# Created after the bulk insert: building indexes once at the end is much faster
# than maintaining them during ~2M row inserts.
INDEXES = """
CREATE INDEX idx_form_normalized ON form(normalized);
CREATE INDEX idx_form_entry      ON form(entry_id);
CREATE INDEX idx_entry_lemma     ON entry(lemma);
CREATE INDEX idx_entry_language  ON entry(language);
CREATE INDEX idx_entry_pos       ON entry(pos);
CREATE INDEX idx_definition_entry ON definition(entry_id);
CREATE INDEX idx_example_entry    ON example(entry_id);
CREATE INDEX idx_sense_link_entry ON sense_link(entry_id, kind);
CREATE INDEX idx_sense_link_value ON sense_link(value);
"""

FTS = """
CREATE VIRTUAL TABLE definition_fts USING fts5(
    text,
    content='definition',
    content_rowid='definition_id',
    tokenize='unicode61 remove_diacritics 0'
);
INSERT INTO definition_fts(rowid, text) SELECT definition_id, text FROM definition;
"""


def normalize(value: str, language: str) -> str:
    """Lookup key: case-folded, and yo-folded for Russian."""
    folded = value.strip().casefold()
    return folded.replace("ё", "е") if language == "ru" else folded


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT)
    parser.add_argument("--lexical-dir", type=Path, default=LEXICAL_DIR)
    args = parser.parse_args()

    started = time.time()
    if args.out.exists():
        args.out.unlink()
    args.out.parent.mkdir(parents=True, exist_ok=True)

    connection = sqlite3.connect(args.out)
    connection.executescript(SCHEMA)

    entry_id = form_id = definition_id = example_id = link_id = 0
    counts = {"entries": 0, "forms": 0, "definitions": 0, "examples": 0, "links": 0}
    seen_packs: set[str] = set()

    for language, filename in PACKS:
        path = args.lexical_dir / filename
        if not path.exists():
            print(f"skip (missing): {filename}")
            continue

        document = json.loads(path.read_text(encoding="utf-8"))
        manifest = document["manifest"]
        pack_id = manifest["packId"]
        if pack_id in seen_packs:
            continue
        seen_packs.add(pack_id)

        connection.execute(
            "INSERT INTO pack VALUES (?,?,?,?,?,?,?,?)",
            (pack_id, language, manifest["packVersion"], manifest.get("displayName"),
             manifest["license"], manifest.get("licenseNote"), manifest.get("source"),
             len(document["entries"])))

        entries, forms, definitions, examples, links = [], [], [], [], []
        for record in document["entries"]:
            entry_id += 1
            lemma = record["lemma"]
            pos = record.get("pos") or "unknown"
            entries.append((entry_id, lemma, language, pos, pack_id))

            form_id += 1
            forms.append((form_id, entry_id, lemma, normalize(lemma, language), 1))
            for inflection in record.get("inflections") or []:
                form_id += 1
                forms.append((form_id, entry_id, inflection, normalize(inflection, language), 0))

            for ordinal, definition in enumerate(record.get("definitions") or [], start=1):
                definition_id += 1
                definitions.append((
                    definition_id, entry_id, ordinal, definition["text"],
                    definition.get("pos"), definition.get("label"),
                    definition.get("senseId"),
                    definition.get("sourceId") or "writelight-cc0"))

            for example in record.get("examples") or []:
                example_id += 1
                examples.append((example_id, entry_id, example["text"], example.get("senseId")))

            for kind in ("synonym", "antonym"):
                for link in record.get(kind + "s") or []:
                    link_id += 1
                    links.append((
                        link_id, entry_id, kind, link["value"], link.get("pos"),
                        link.get("relevance", 0.5), link.get("sourceId")))

        connection.executemany("INSERT INTO entry VALUES (?,?,?,?,?)", entries)
        connection.executemany("INSERT INTO form VALUES (?,?,?,?,?)", forms)
        connection.executemany("INSERT INTO definition VALUES (?,?,?,?,?,?,?,?)", definitions)
        connection.executemany("INSERT INTO example VALUES (?,?,?,?)", examples)
        connection.executemany("INSERT INTO sense_link VALUES (?,?,?,?,?,?,?)", links)
        connection.commit()

        counts["entries"] += len(entries)
        counts["forms"] += len(forms)
        counts["definitions"] += len(definitions)
        counts["examples"] += len(examples)
        counts["links"] += len(links)
        print(f"{filename}: entries={len(entries)} forms={len(forms)} "
              f"definitions={len(definitions)} links={len(links)}", flush=True)

    print("building indexes…", flush=True)
    connection.executescript(INDEXES)
    print("building FTS5 index…", flush=True)
    connection.executescript(FTS)

    connection.executemany(
        "INSERT INTO meta VALUES (?,?)",
        [("schemaVersion", "1"),
         ("generatedAt", time.strftime("%Y-%m-%d")),
         ("entries", str(counts["entries"])),
         ("forms", str(counts["forms"]))])
    connection.commit()

    connection.execute("ANALYZE")
    connection.execute("VACUUM")
    connection.commit()
    connection.close()

    data = args.out.read_bytes()
    report = {
        "path": str(args.out),
        "bytes": len(data),
        "sha256": hashlib.sha256(data).hexdigest(),
        **counts,
        "seconds": round(time.time() - started, 1),
    }
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
