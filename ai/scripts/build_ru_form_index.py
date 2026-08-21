"""Build WriteLite's runtime Russian surface-form index.

Produces a memory-mappable directed acyclic word graph plus a parallel
metadata array, replacing "load a dictionary into a HashSet" with a
structure that also supports approximate matching.

Why a DAFSA and not SymSpell: at three million forms a SymSpell deletion
index needs tens of millions of entries and gigabytes of RAM, which is not
acceptable in a desktop app that currently idles at 35 MB. A minimal DAFSA
stores the same three million forms in tens of megabytes, is memory-mapped
so it does not count against the working set, and a Levenshtein automaton
walks it directly for edit-distance-2 candidate generation.

Outputs (all under resources/lexical/):
  ru-forms.wldawg   graph + alphabet + per-arc subtree word counts
  ru-forms.meta.bin fixed 12-byte record per word id (pos, flags, freq, lemma, tag)
  ru-forms.strings  lemma and tag string tables
  ru-forms.stats.json  composition report

Word ids are a minimal perfect hash: the id of a form is its lexicographic
rank, computed during traversal from the per-arc subtree counts, so the
metadata array needs no keys of its own.

Sources and licences are recorded in resources/lexical/sources.manifest.json.
"""

from __future__ import annotations

import argparse
import io
import json
import os
import struct
import sys
import time
from collections import Counter

MAGIC = b"WLDAWG\x01\x00"
ARC_SIZE = 12
META_SIZE = 12
NO_TARGET = 0xFFFFFFFF

FLAG_LAST_ARC = 1 << 0
FLAG_WORD_END = 1 << 1

META_PROPER = 1 << 0
META_ABBREV = 1 << 1
META_SLANG = 1 << 2
META_OBSCENE = 1 << 3
META_BORROWING = 1 << 4
META_INFORMAL = 1 << 5
META_ARCHAIC = 1 << 6
META_NON_CYRILLIC = 1 << 7

FLAG_NAMES = {
    "is_proper_name": META_PROPER,
    "is_abbreviation": META_ABBREV,
    "is_slang": META_SLANG,
    "is_obscene": META_OBSCENE,
    "is_borrowing": META_BORROWING,
    "is_informal": META_INFORMAL,
    "is_archaic": META_ARCHAIC,
}

POS_ORDER = [
    "UNKN", "NOUN", "ADJF", "ADJS", "COMP", "VERB", "INFN", "PRTF", "PRTS",
    "GRND", "NUMR", "ADVB", "NPRO", "PRED", "PREP", "CONJ", "PRCL", "INTJ",
]
POS_CODE = {p: i for i, p in enumerate(POS_ORDER)}

# Maps the JSON `pos` vocabulary used by the curated modern-vocabulary pack
# onto OpenCorpora part-of-speech codes.
JSON_POS = {
    "noun": "NOUN", "verb": "INFN", "adjective": "ADJF", "adverb": "ADVB",
    "interjection": "INTJ", "particle": "PRCL", "abbreviation": "NOUN",
    "proper_name": "NOUN", "phrase": "UNKN",
}

CYRILLIC = set("абвгдеёжзийклмнопрстуфхцчшщъыьэюя-")


class Dafsa:
    """Incremental minimal-DAFSA construction (Daciuk et al.).

    Input words must arrive in ascending lexicographic order; the builder
    then only ever needs to minimise the suffix that the previous word and
    the current word do not share.
    """

    def __init__(self) -> None:
        self.children: list[list] = [[]]   # node -> [[label, target], ...]
        self.final = bytearray([0])
        self._register: dict[tuple, int] = {}
        self._unchecked: list[tuple[int, str, int]] = []
        self._previous = ""

    def _new_node(self) -> int:
        self.children.append([])
        self.final.append(0)
        return len(self.children) - 1

    def _signature(self, node: int) -> tuple:
        return (self.final[node], tuple((c[0], c[1]) for c in self.children[node]))

    def _minimize(self, down_to: int) -> None:
        for i in range(len(self._unchecked) - 1, down_to - 1, -1):
            parent, label, child = self._unchecked[i]
            signature = self._signature(child)
            existing = self._register.get(signature)
            if existing is not None:
                for arc in self.children[parent]:
                    if arc[0] == label:
                        arc[1] = existing
                        break
            else:
                self._register[signature] = child
            self._unchecked.pop()

    def insert(self, word: str) -> None:
        if word <= self._previous:
            raise ValueError(f"input must be strictly ascending: {word!r} after {self._previous!r}")
        common = 0
        limit = min(len(word), len(self._previous))
        while common < limit and word[common] == self._previous[common]:
            common += 1

        self._minimize(common)

        node = self._unchecked[-1][2] if self._unchecked else 0
        for char in word[common:]:
            child = self._new_node()
            self.children[node].append([char, child])
            self._unchecked.append((node, char, child))
            node = child

        self.final[node] = 1
        self._previous = word

    def finish(self) -> None:
        self._minimize(0)
        self._register.clear()

    def reachable_nodes(self) -> int:
        seen = set()
        stack = [0]
        while stack:
            node = stack.pop()
            if node in seen:
                continue
            seen.add(node)
            for _, target in self.children[node]:
                stack.append(target)
        return len(seen)


def serialise(dafsa: Dafsa, path: str) -> tuple[int, int, int]:
    """Flatten the graph into the on-disk arc array and return its shape."""
    # Nodes are laid out as contiguous runs of arcs, so a node is identified
    # by the index of its first arc; the last arc of a run carries FLAG_LAST_ARC.
    order: list[int] = []
    seen = set()
    stack = [0]
    while stack:
        node = stack.pop()
        if node in seen:
            continue
        seen.add(node)
        order.append(node)
        for _, target in dafsa.children[node]:
            if target not in seen:
                stack.append(target)

    # Root first, then any node with arcs. Leaves need no arc slot at all.
    order.sort(key=lambda n: (n != 0, n))
    arc_offset: dict[int, int] = {}
    cursor = 0
    for node in order:
        if node != 0 and not dafsa.children[node]:
            continue
        arc_offset[node] = cursor
        cursor += len(dafsa.children[node])
    arc_count = cursor

    # Subtree word counts, computed bottom-up over the DAG with an explicit
    # stack (three million forms means recursion would blow the stack).
    words_from: dict[int, int] = {}
    stack = [(0, False)]
    while stack:
        node, expanded = stack.pop()
        if node in words_from:
            continue
        if not expanded:
            stack.append((node, True))
            for _, target in dafsa.children[node]:
                if target not in words_from:
                    stack.append((target, False))
            continue
        total = 0
        for _, target in dafsa.children[node]:
            total += dafsa.final[target] + words_from.get(target, 0)
        words_from[node] = total

    alphabet = sorted({label for node in order for label, _ in dafsa.children[node]})
    alphabet_index = {ch: i for i, ch in enumerate(alphabet)}

    header = struct.pack(
        "<8sIIIIII",
        MAGIC,
        len(alphabet),
        arc_count,
        words_from[0],
        arc_offset[0],
        META_SIZE,
        0,
    )

    arcs = bytearray(arc_count * ARC_SIZE)
    for node in order:
        if node != 0 and not dafsa.children[node]:
            continue
        base = arc_offset[node]
        children = sorted(dafsa.children[node], key=lambda a: a[0])
        for i, (label, target) in enumerate(children):
            flags = 0
            if i == len(children) - 1:
                flags |= FLAG_LAST_ARC
            if dafsa.final[target]:
                flags |= FLAG_WORD_END
            target_arc = arc_offset.get(target, NO_TARGET)
            if not dafsa.children[target]:
                target_arc = NO_TARGET
            subtree = dafsa.final[target] + words_from.get(target, 0)
            struct.pack_into(
                "<HHII", arcs, (base + i) * ARC_SIZE,
                alphabet_index[label], flags, target_arc, subtree,
            )

    with io.open(path, "wb") as handle:
        handle.write(header)
        handle.write(struct.pack(f"<{len(alphabet)}H", *(ord(c) for c in alphabet)))
        handle.write(arcs)

    return arc_count, words_from[0], len(alphabet)


def word_ids_in_order(words: list[str]) -> dict[str, int]:
    """Lexicographic rank == the id the C# reader derives from subtree counts."""
    return {word: i for i, word in enumerate(words)}


def load_opencorpora(path: str):
    with io.open(path, encoding="utf-8") as handle:
        header = handle.readline()
        if not header.startswith("form\t"):
            raise ValueError(f"unexpected header in {path}: {header!r}")
        for line in handle:
            parts = line.rstrip("\n").split("\t")
            if len(parts) < 5:
                continue
            form, lemma, pos, flags, tag = parts[0], parts[1], parts[2], parts[3], parts[4]
            yield form, lemma, pos, flags.split(",") if flags else [], tag.split("|")[0]


def load_modern_vocab(path: str, inflect: bool):
    """Curated modern vocabulary, expanded through pymorphy3 where declinable."""
    if not os.path.exists(path):
        return
    with io.open(path, encoding="utf-8") as handle:
        entries = json.load(handle)

    morph = None
    if inflect:
        try:
            import pymorphy3

            morph = pymorphy3.MorphAnalyzer()
        except Exception:
            morph = None

    for entry in entries:
        lemma = (entry.get("lemma") or "").strip()
        if not lemma:
            continue
        pos = JSON_POS.get(entry.get("pos", ""), "NOUN")
        flags = list(entry.get("flags") or [])
        register = entry.get("register")
        if register == "obscene" and "is_obscene" not in flags:
            flags.append("is_obscene")
        if register in ("slang", "informal") and "is_slang" not in flags:
            flags.append("is_slang")

        surfaces = {lemma.lower()}
        if morph is not None and entry.get("inflect"):
            parsed = morph.parse(lemma.lower())
            if parsed:
                for form in parsed[0].lexeme:
                    surfaces.add(form.word)
        for surface in surfaces:
            yield surface, lemma.lower(), pos, flags, ""


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--forms", default="ai/data/lexical/opencorpora-forms.tsv")
    parser.add_argument("--modern", default="resources/lexical/ru-modern-vocab.json")
    parser.add_argument("--frequency", default="resources/lexical/ru-frequency.tsv")
    parser.add_argument("--out-dir", default="resources/lexical")
    parser.add_argument("--prefix", default="ru-forms")
    parser.add_argument("--no-inflect-modern", action="store_true")
    args = parser.parse_args()

    started = time.time()
    os.makedirs(args.out_dir, exist_ok=True)

    # ---- collect ---------------------------------------------------------
    records: dict[str, tuple[str, str, int, str]] = {}
    source_counts = Counter()

    def add(surface, lemma, pos, flags, tag, source):
        surface = surface.strip().lower()
        if not surface or len(surface) > 48:
            return
        bits = 0
        for name in flags:
            bits |= FLAG_NAMES.get(name, 0)
        if any(ch not in CYRILLIC and not ch.isdigit() for ch in surface):
            bits |= META_NON_CYRILLIC
        existing = records.get(surface)
        if existing is None:
            records[surface] = (lemma, pos, bits, tag)
            source_counts[source] += 1
        else:
            # Keep the first (dictionary) analysis but union the flag bits, so a
            # word that OpenCorpora knows and the curated pack marks as slang
            # ends up carrying both facts.
            records[surface] = (existing[0], existing[1], existing[2] | bits, existing[3])

    for form, lemma, pos, flags, tag in load_opencorpora(args.forms):
        add(form, lemma, pos, flags, tag, "opencorpora")
    for form, lemma, pos, flags, tag in load_modern_vocab(args.modern, not args.no_inflect_modern):
        add(form, lemma, pos, flags, tag, "modern-vocab")

    # ---- frequency -------------------------------------------------------
    frequency: dict[str, int] = {}
    if os.path.exists(args.frequency):
        with io.open(args.frequency, encoding="utf-8") as handle:
            for rank, line in enumerate(handle, start=1):
                word, _, _count = line.rstrip("\n").partition("\t")
                word = word.strip().lower()
                if word and word not in frequency:
                    frequency[word] = min(rank, 65535)

    words = sorted(records)
    ids = word_ids_in_order(words)

    # ---- graph -----------------------------------------------------------
    dafsa = Dafsa()
    for word in words:
        dafsa.insert(word)
    dafsa.finish()

    dawg_path = os.path.join(args.out_dir, args.prefix + ".wldawg")
    arc_count, word_count, alphabet_size = serialise(dafsa, dawg_path)
    if word_count != len(words):
        raise AssertionError(f"graph holds {word_count} words, expected {len(words)}")

    # ---- string tables ---------------------------------------------------
    lemma_ids: dict[str, int] = {}
    tag_ids: dict[str, int] = {"": 0}
    for lemma, _pos, _bits, tag in records.values():
        if lemma not in lemma_ids:
            lemma_ids[lemma] = len(lemma_ids)
        if tag not in tag_ids:
            tag_ids[tag] = len(tag_ids)

    def write_string_table(handle, table: dict[str, int]):
        ordered = sorted(table, key=table.get)
        blob = "\n".join(ordered).encode("utf-8")
        handle.write(struct.pack("<I", len(ordered)))
        handle.write(struct.pack("<I", len(blob)))
        handle.write(blob)

    strings_path = os.path.join(args.out_dir, args.prefix + ".strings")
    with io.open(strings_path, "wb") as handle:
        handle.write(b"WLSTR\x01\x00\x00")
        write_string_table(handle, lemma_ids)
        write_string_table(handle, tag_ids)

    # ---- metadata --------------------------------------------------------
    meta = bytearray(len(words) * META_SIZE)
    pos_counts = Counter()
    flag_counts = Counter()
    with_frequency = 0
    for word in words:
        lemma, pos, bits, tag = records[word]
        rank = frequency.get(word, 0)
        if rank:
            with_frequency += 1
        struct.pack_into(
            "<BBHIHH", meta, ids[word] * META_SIZE,
            POS_CODE.get(pos, 0), bits, rank, lemma_ids[lemma], tag_ids[tag], 0,
        )
        pos_counts[pos] += 1
        for name, bit in FLAG_NAMES.items():
            if bits & bit:
                flag_counts[name] += 1
        if bits & META_NON_CYRILLIC:
            flag_counts["non_cyrillic"] += 1

    meta_path = os.path.join(args.out_dir, args.prefix + ".meta.bin")
    with io.open(meta_path, "wb") as handle:
        handle.write(meta)

    stats = {
        "generatedAt": time.strftime("%Y-%m-%d"),
        "surfaceForms": len(words),
        "lemmas": len(lemma_ids),
        "distinctTags": len(tag_ids),
        "arcs": arc_count,
        "alphabetSize": alphabet_size,
        "graphNodes": dafsa.reachable_nodes(),
        "formsBySource": dict(source_counts),
        "formsByPos": dict(pos_counts.most_common()),
        "formsByFlag": dict(flag_counts.most_common()),
        "formsWithFrequency": with_frequency,
        "bytes": {
            "wldawg": os.path.getsize(dawg_path),
            "meta": os.path.getsize(meta_path),
            "strings": os.path.getsize(strings_path),
        },
        "buildSeconds": round(time.time() - started, 1),
    }
    stats_path = os.path.join(args.out_dir, args.prefix + ".stats.json")
    with io.open(stats_path, "w", encoding="utf-8") as handle:
        json.dump(stats, handle, ensure_ascii=False, indent=2)

    print(json.dumps({k: v for k, v in stats.items() if k not in ("formsByPos", "formsByFlag")}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
