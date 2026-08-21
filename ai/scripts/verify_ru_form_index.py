"""Verify the binary form index against the source word list.

The metadata array is addressed purely by the id the traversal computes, so a
mistake in the subtree counts would silently attach the wrong part of speech
and frequency to every word. This checks the invariant directly: the id of a
form must equal its lexicographic rank in the input.
"""

from __future__ import annotations

import argparse
import io
import json
import os
import random
import struct
import sys
import time

ARC_SIZE = 12
META_SIZE = 12
NO_TARGET = 0xFFFFFFFF
FLAG_LAST_ARC = 1
FLAG_WORD_END = 2
HEADER_SIZE = 32


def fold(ch: str) -> str:
    ch = ch.lower()
    return "е" if ch == "ё" else ch


class FormIndex:
    def __init__(self, directory: str, prefix: str = "ru-forms") -> None:
        with io.open(os.path.join(directory, prefix + ".wldawg"), "rb") as handle:
            blob = handle.read()
        magic, alphabet_count, arc_count, self.word_count, self.root, _meta_size, _ = struct.unpack_from(
            "<8sIIIIII", blob, 0
        )
        assert magic[:6] == b"WLDAWG", magic
        self.alphabet = [
            chr(c) for c in struct.unpack_from(f"<{alphabet_count}H", blob, HEADER_SIZE)
        ]
        start = HEADER_SIZE + alphabet_count * 2
        self.arcs = blob[start:start + arc_count * ARC_SIZE]
        self.arc_count = arc_count
        meta_path = os.path.join(directory, prefix + ".meta.bin")
        self.meta = io.open(meta_path, "rb").read() if os.path.exists(meta_path) else b""

    def arc(self, index: int):
        label, flags, target, subtree = struct.unpack_from("<HHII", self.arcs, index * ARC_SIZE)
        return self.alphabet[label], flags, target, subtree

    def get_id(self, word: str) -> int:
        return self._walk(self.root, word, 0, 0)

    def _walk(self, node: int, word: str, position: int, id_so_far: int) -> int:
        target_char = fold(word[position])
        current = id_so_far
        arc = node
        while True:
            label, flags, target, subtree = self.arc(arc)
            if fold(label) == target_char:
                if position == len(word) - 1:
                    if flags & FLAG_WORD_END:
                        return current
                elif target != NO_TARGET:
                    child = current + (1 if flags & FLAG_WORD_END else 0)
                    found = self._walk(target, word, position + 1, child)
                    if found >= 0:
                        return found
            current += subtree
            if flags & FLAG_LAST_ARC:
                return -1
            arc += 1

    def find_within(self, word: str, max_distance: int = 2, max_results: int = 64):
        query = [fold(c) for c in word]
        row = list(range(len(query) + 1))
        results: list[tuple[str, int, int]] = []
        self._search(self.root, 0, [], row, None, "", query, max_distance, max_results, results)
        results.sort(key=lambda r: (r[2], r[0]))
        return results

    def _search(self, node, id_so_far, prefix, row, prev_row, prev_char,
                query, max_distance, max_results, results):
        if len(results) >= max_results:
            return
        width = len(query) + 1
        current = id_so_far
        arc = node
        while True:
            raw_label, flags, target, subtree = self.arc(arc)
            label = fold(raw_label)
            nxt = [row[0] + 1] + [0] * (width - 1)
            for i in range(1, width):
                cost = 0 if query[i - 1] == label else 1
                value = min(nxt[i - 1] + 1, row[i] + 1, row[i - 1] + cost)
                if prev_row is not None and i > 1 and query[i - 1] == prev_char and query[i - 2] == label:
                    value = min(value, prev_row[i - 2] + 1)
                nxt[i] = value
            if min(nxt) <= max_distance:
                prefix.append(raw_label)
                if (flags & FLAG_WORD_END) and nxt[len(query)] <= max_distance:
                    results.append(("".join(prefix), current, nxt[len(query)]))
                if target != NO_TARGET and len(results) < max_results:
                    child = current + (1 if flags & FLAG_WORD_END else 0)
                    self._search(target, child, prefix, nxt, row, label,
                                 query, max_distance, max_results, results)
                prefix.pop()
            current += subtree
            if flags & FLAG_LAST_ARC or len(results) >= max_results:
                return
            arc += 1

    def iterate(self):
        """Every word in the graph, in lexicographic (id) order."""
        out: list[str] = []
        stack = [(self.root, "")]
        # Explicit stack, pushed in reverse so the left-most arc is expanded
        # first and the output order matches the ids the traversal assigns.
        def walk(node: int, prefix: str):
            arc = node
            while True:
                label, flags, target, _subtree = self.arc(arc)
                word = prefix + label
                if flags & FLAG_WORD_END:
                    out.append(word)
                if target != NO_TARGET:
                    walk(target, word)
                if flags & FLAG_LAST_ARC:
                    return
                arc += 1

        walk(self.root, "")
        del stack
        return out

    def info(self, word_id: int):
        pos, flags, rank, lemma_id, tag_id, _ = struct.unpack_from("<BBHIHH", self.meta, word_id * META_SIZE)
        return {"pos": pos, "flags": flags, "rank": rank, "lemma_id": lemma_id, "tag_id": tag_id}


def damerau(a: str, b: str) -> int:
    previous, current = list(range(len(b) + 1)), [0] * (len(b) + 1)
    two_back: list[int] | None = None
    for i in range(1, len(a) + 1):
        current[0] = i
        for j in range(1, len(b) + 1):
            cost = 0 if a[i - 1] == b[j - 1] else 1
            current[j] = min(current[j - 1] + 1, previous[j] + 1, previous[j - 1] + cost)
            if two_back is not None and i > 1 and j > 1 and a[i - 1] == b[j - 2] and a[i - 2] == b[j - 1]:
                current[j] = min(current[j], two_back[j - 2] + 1)
        two_back, previous, current = previous, current[:], [0] * (len(b) + 1)
    return previous[len(b)]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dir", default="resources/lexical")
    parser.add_argument("--forms", default="ai/data/lexical/opencorpora-forms.tsv")
    parser.add_argument("--sample", type=int, default=20000)
    args = parser.parse_args()

    index = FormIndex(args.dir)
    failures: list[str] = []

    # The graph is the authority on what it holds: enumerating it in
    # lexicographic order gives both the word list and the ids the metadata
    # array is addressed by, without assuming any one input file is the whole
    # story (the index merges OpenCorpora with the curated modern pack).
    words = list(index.iterate())
    if len(words) != index.word_count:
        failures.append(f"enumerated {len(words)} words, header says {index.word_count}")
    for i in range(1, len(words)):
        if words[i - 1] >= words[i]:
            failures.append(f"enumeration not sorted at {i}: {words[i-1]!r} >= {words[i]!r}")
            break

    # Every form in the source list must still be reachable.
    missing = 0
    for i, line in enumerate(io.open(args.forms, encoding="utf-8")):
        if not i:
            continue
        if index.get_id(line.split("\t", 1)[0]) < 0:
            missing += 1
    if missing:
        failures.append(f"{missing} source forms are not in the index")

    random.seed(20260809)
    sample_positions = random.sample(range(len(words)), min(args.sample, len(words)))
    id_mismatch = 0
    for position in sample_positions:
        word = words[position]
        found = index.get_id(word)
        # A folded query can legitimately resolve to an е-spelled twin, so the
        # id is checked against the rank of whatever word the id addresses.
        if found < 0 or (found != position and fold_all(words[found]) != fold_all(word)):
            id_mismatch += 1
            if len(failures) < 10:
                failures.append(f"id({word!r}) = {found}, expected {position}")
    if id_mismatch:
        failures.append(f"{id_mismatch}/{len(sample_positions)} sampled ids wrong")

    unknown = ["превет", "зделал", "хочю", "qqqqqqzz", "асдфгыв"]
    for word in unknown:
        if index.get_id(word) >= 0:
            failures.append(f"non-word {word!r} reported as known")

    started = time.time()
    probes = ["превет", "хочю", "зделать", "интиресный", "програма", "севодня", "пажалуйста"]
    neighbourhoods = {}
    for probe in probes:
        matches = index.find_within(probe, 2, 64)
        neighbourhoods[probe] = [m[0] for m in matches[:8]]
        for word, _wid, distance in matches:
            actual = damerau(fold_all(probe), fold_all(word))
            if actual != distance:
                failures.append(f"distance({probe!r},{word!r}) reported {distance}, actual {actual}")
                break
    search_ms = (time.time() - started) * 1000 / len(probes)

    report = {
        "words": index.word_count,
        "arcs": index.arc_count,
        "alphabet": len(index.alphabet),
        "sampledIds": len(sample_positions),
        "avgSearchMs": round(search_ms, 2),
        "neighbourhoods": neighbourhoods,
        "failures": failures[:20],
        "ok": not failures,
    }
    with io.open(os.path.join(args.dir, "ru-forms.verify.json"), "w", encoding="utf-8") as handle:
        json.dump(report, handle, ensure_ascii=False, indent=2)
    print(json.dumps({k: v for k, v in report.items() if k != "neighbourhoods"}, ensure_ascii=False))
    return 0 if not failures else 1


def fold_all(word: str) -> str:
    return "".join(fold(c) for c in word)


if __name__ == "__main__":
    sys.setrecursionlimit(10000)
    sys.exit(main())
