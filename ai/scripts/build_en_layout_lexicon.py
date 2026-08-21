"""Builds the compact English lexicon used for Latin-target keyboard-layout recovery.

WriteLite's layout converter maps ЙЦУКЕН to QWERTY and back. Until Phase 5 it only ever
resolved the result against the *Russian* lexicon, so a Russian-looking token that was
really an English word typed on the wrong layout could not be recovered: "пшерги" was
answered with "перги" rather than "github". Resolving against English needs an English
word list on the spelling path, and the shipped English pack
(resources/lexical/writelight-lexical-en.json, Open English WordNet 2024) is 42 MB of
synsets, definitions and examples — far too much to open while someone is typing.

This script reduces that pack to the only thing the layout path asks of it — "is this
string an English word" — as a newline-delimited, sorted, lowercase list. It also folds in
two hand-maintained lists that WordNet structurally does not contain:

  * function words ("the", "and", "of"), which a lexical database of content words omits;
  * technical and product vocabulary ("github", "npm", "kubernetes"), which is what
    WriteLite users actually type in the middle of Russian prose.

The two are kept in separate files because they are not equally strong evidence. Technical
vocabulary raises a layout candidate's rank — a Russian speaker writing about "github" is
common. A function word does not: it only makes the candidate available, because short
function words are exactly the length at which a Cyrillic string collides with an English
word by accident.

Provenance and licence
----------------------
Open English WordNet 2024, CC BY 4.0, attribution to the Open English WordNet team and to
Princeton WordNet. The derived list is a membership set — no definitions, no relations —
and carries the same attribution through resources/lexical/en-layout-words.stats.json and
docs/LEXICAL_DATA_LICENSES.md.

The curated additions are original to this project and are kept in a separate input file so
that the WordNet-derived and WriteLite-authored parts of the output stay separable.

    python ai/scripts/build_en_layout_lexicon.py
"""

from __future__ import annotations

import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
PACK = ROOT / "resources" / "lexical" / "writelight-lexical-en.json"
FUNCTION_WORDS = ROOT / "resources" / "lexical" / "en-layout-function-words.txt"
TECHNICAL = ROOT / "resources" / "lexical" / "en-layout-technical.txt"
OUT = ROOT / "resources" / "lexical" / "en-layout-words.txt"
STATS = ROOT / "resources" / "lexical" / "en-layout-words.stats.json"

# The layout converter only produces characters that exist on a QWERTY keyboard, so a word
# it can never yield is dead weight in the output.
MIN_LENGTH = 2
MAX_LENGTH = 24


def usable(word: str) -> bool:
    return (
        MIN_LENGTH <= len(word) <= MAX_LENGTH
        and word.isascii()
        and word.isalpha()
    )


def read_wordnet() -> set[str]:
    if not PACK.exists():
        sys.exit(f"English lexical pack not found: {PACK}")

    data = json.loads(PACK.read_text(encoding="utf-8"))
    words: set[str] = set()
    for entry in data.get("entries", []):
        for raw in [entry.get("lemma", "")] + [
            inflection if isinstance(inflection, str) else inflection.get("value", "")
            for inflection in (entry.get("inflections") or [])
        ]:
            candidate = (raw or "").strip().lower()
            if usable(candidate):
                words.add(candidate)
    return words


def read_list(path: pathlib.Path) -> set[str]:
    if not path.exists():
        sys.exit(f"curated list not found: {path}")

    words: set[str] = set()
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.split("#", 1)[0].strip().lower()
        if line and usable(line):
            words.add(line)
    return words


def main() -> None:
    wordnet = read_wordnet()
    function_words = read_list(FUNCTION_WORDS)
    technical = read_list(TECHNICAL)
    curated = function_words | technical
    combined = sorted(wordnet | curated)

    OUT.write_text("\n".join(combined) + "\n", encoding="utf-8")
    STATS.write_text(
        json.dumps(
            {
                "totalWords": len(combined),
                "fromWordnet": len(wordnet),
                "fromFunctionWords": len(function_words),
                "fromTechnical": len(technical),
                "curatedNotInWordnet": len(curated - wordnet),
                "minLength": MIN_LENGTH,
                "maxLength": MAX_LENGTH,
                "purpose": "Latin-target keyboard-layout recovery (membership only)",
                "sources": [
                    {
                        "name": "Open English WordNet 2024",
                        "license": "CC BY 4.0",
                        "attribution": "Open English WordNet team; Princeton University WordNet",
                        "via": "resources/lexical/writelight-lexical-en.json",
                    },
                    {
                        "name": "WriteLite curated function words",
                        "license": "CC0-1.0",
                        "attribution": "WriteLite project",
                        "via": "resources/lexical/en-layout-function-words.txt",
                    },
                    {
                        "name": "WriteLite curated technical and product vocabulary",
                        "license": "CC0-1.0",
                        "attribution": "WriteLite project",
                        "via": "resources/lexical/en-layout-technical.txt",
                    },
                ],
            },
            ensure_ascii=False,
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )

    print(f"{len(combined)} words -> {OUT}")
    print(f"  wordnet {len(wordnet)}, function {len(function_words)}, technical {len(technical)} "
          f"({len(curated - wordnet)} curated words not already present)")


if __name__ == "__main__":
    main()
