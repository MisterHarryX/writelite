"""Extract every Russian surface form from the OpenCorpora dictionary.

Source: OpenCorpora 0.92 (rev. 417150) as compiled into `pymorphy3-dicts-ru`.
Licence: CC BY-SA (data); the wrapper package is MIT. Attribution and
share-alike are recorded in `resources/lexical/sources.manifest.json`.

The dictionary is walked at the paradigm level rather than through
`MorphAnalyzer.parse`, which would re-run disambiguation 5.1 million times.
`words.dawg` maps a surface form to the (paradigm_id, index) pairs that
produce it, and the paradigm tables give the tag and the normal form
directly -- the same data `parse` would return, minus the ranking.

Output is a TSV (form, lemma, pos, flags, grammemes) written streaming, so
the 5.1M rows never sit in memory at once.
"""

from __future__ import annotations

import argparse
import io
import os
import sys
import time
from collections import Counter

import pymorphy3

# Grammemes that make a form unsuitable for a writing assistant's lexicon:
# they mark entries OpenCorpora itself flags as non-standard.
REJECT_GRAMMEMES = frozenset({
    "Erro",   # explicitly marked as an error form in the source corpus
    "Dist",   # distorted / colloquially deformed
})

# Grammemes that we want to surface as lexicon metadata.
FLAG_GRAMMEMES = {
    "Name": "is_proper_name",
    "Surn": "is_proper_name",
    "Patr": "is_proper_name",
    "Geox": "is_proper_name",
    "Orgn": "is_proper_name",
    "Trad": "is_proper_name",
    "Abbr": "is_abbreviation",
    "Init": "is_abbreviation",
    "Slng": "is_slang",
    "Arch": "is_archaic",
    "Litr": "is_literary",
    "Infr": "is_informal",
}

POS_ORDER = [
    "NOUN", "ADJF", "ADJS", "COMP", "VERB", "INFN", "PRTF", "PRTS", "GRND",
    "NUMR", "ADVB", "NPRO", "PRED", "PREP", "CONJ", "PRCL", "INTJ", "UNKN",
]
POS_CODE = {p: i for i, p in enumerate(POS_ORDER)}


def iter_forms(morph):
    """Yield (form, lemma, pos, flagset, grammeme_string) for every entry."""
    dictionary = morph.dictionary
    build_tag_info = dictionary.build_tag_info
    build_normal_form = dictionary.build_normal_form

    # Tags repeat heavily (5532 distinct tags over 5.1M forms), so decoding a
    # tag's grammemes once and caching pays for itself many times over.
    tag_cache: dict[tuple, tuple] = {}

    # `iteritems` yields one (word, (paradigm_id, index)) pair per analysis, so
    # a homograph shows up as several adjacent entries with the same key. The
    # DAWG iterates in sorted key order, so grouping is just a change-detect on
    # the word, and the union of every analysis becomes one lexicon row.
    current_word = None
    current: list[tuple] = []

    def flush(word, analyses):
        if not analyses:
            return None
        pos = analyses[0][0]
        flags = frozenset().union(*(a[1] for a in analyses))
        lemma = analyses[0][2]
        # A handful of forms carry dozens of analyses; four is enough to keep
        # case/number/gender available for agreement scoring without turning
        # the TSV into a multi-gigabyte file.
        tags = "|".join(list(dict.fromkeys(a[3] for a in analyses))[:4])
        return word, lemma, pos, flags, tags

    for word, (para_id, idx) in dictionary.words.iteritems(""):
        if word != current_word:
            row = flush(current_word, current) if current_word is not None else None
            if row is not None:
                yield row
            current_word = word
            current = []

        key = (para_id, idx)
        cached = tag_cache.get(key)
        if cached is None:
            tag = build_tag_info(para_id, idx)
            grammemes = frozenset(tag.grammemes)
            if grammemes & REJECT_GRAMMEMES:
                cached = ()
            else:
                cached = (
                    tag.POS or "UNKN",
                    frozenset(FLAG_GRAMMEMES[g] for g in grammemes if g in FLAG_GRAMMEMES),
                    str(tag),
                )
            tag_cache[key] = cached
        if not cached:
            continue
        current.append((cached[0], cached[1], build_normal_form(para_id, idx, word), cached[2]))

    row = flush(current_word, current) if current_word is not None else None
    if row is not None:
        yield row


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--out",
        default=os.path.join("ai", "data", "lexical", "opencorpora-forms.tsv"),
    )
    parser.add_argument("--limit", type=int, default=0, help="stop after N forms (smoke test)")
    args = parser.parse_args()

    os.makedirs(os.path.dirname(args.out), exist_ok=True)

    started = time.time()
    morph = pymorphy3.MorphAnalyzer()
    meta = dict(morph.dictionary.meta)
    pos_counts: Counter[str] = Counter()
    flag_counts: Counter[str] = Counter()
    lemmas: set[str] = set()
    written = 0

    with io.open(args.out, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("form\tlemma\tpos\tflags\ttag\n")
        for form, lemma, pos, flags, tag in iter_forms(morph):
            handle.write(f"{form}\t{lemma}\t{pos}\t{','.join(sorted(flags))}\t{tag}\n")
            pos_counts[pos] += 1
            for flag in flags:
                flag_counts[flag] += 1
            lemmas.add(lemma)
            written += 1
            if args.limit and written >= args.limit:
                break

    elapsed = time.time() - started
    report = {
        "source": "OpenCorpora via pymorphy3-dicts-ru",
        "source_version": meta.get("source_version"),
        "source_revision": meta.get("source_revision"),
        "forms": written,
        "lemmas": len(lemmas),
        "pos_counts": dict(pos_counts.most_common()),
        "flag_counts": dict(flag_counts.most_common()),
        "seconds": round(elapsed, 1),
        "output": args.out,
    }
    import json

    with io.open(args.out + ".stats.json", "w", encoding="utf-8") as handle:
        json.dump(report, handle, ensure_ascii=False, indent=2)
    print(json.dumps({k: v for k, v in report.items() if k != "pos_counts"}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
