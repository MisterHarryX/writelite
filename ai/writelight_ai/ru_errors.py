#!/usr/bin/env python3
"""Seeded, configurable Russian error generator for WriteLite.

This module is the corruption engine behind ``ai/scripts/build_ru_error_corpus.py``.
It is a much stronger replacement for :mod:`writelight_ai.errors` (which stays in
place for the legacy chat dataset) and is Russian-specific throughout.

Design rules
------------
* **Deterministic.**  ``corrupt(sentence, seed)`` depends only on its arguments
  and the generator configuration -- never on ``hash()`` ordering, dict order of
  external data, or wall-clock state.
* **Edits are planned over the original string, then applied left to right.**
  This makes overlap handling trivial and lets every :class:`Corruption` carry an
  exact span into the *corrupted* text while still remembering the correct token.
* **Every corruption is verified.**  A candidate edit is rejected if the
  replacement equals the original (case-insensitively for word edits).  Word-level
  edits are additionally classified against the OpenCorpora dictionary through
  ``pymorphy3.MorphAnalyzer.word_is_known`` into ``non_word`` (the corrupted token
  is not a Russian word at all -- a spell checker can catch it) and ``real_word``
  (the corrupted token *is* a real Russian word -- only context can catch it).

Reconstruction
--------------
``Corruption.span`` indexes the corrupted text, ``Corruption.original`` holds the
correct text for that span, so::

    corrected = apply_corrections(result.text, result)
    assert corrected == sentence

Licensing: the keyboard layout, homoglyph and orthographic rule tables in this
file are authored in-repo and carry the repository licence.  ``pymorphy3`` and
its OpenCorpora dictionary are used at runtime only (LGPL/CC-BY-SA data, not
redistributed from here).
"""

from __future__ import annotations

import functools
import hashlib
import json
import math
import random
import re
import unicodedata
from dataclasses import dataclass, field, replace as _dc_replace
from pathlib import Path
from typing import Iterable, Iterator, Sequence

__all__ = [
    "GENERATOR_VERSION",
    "RU_KEYBOARD_ROWS",
    "EN_KEYBOARD_ROWS",
    "RU_KEY_NEIGHBOURS",
    "EN_KEY_NEIGHBOURS",
    "RU_LETTER_NEIGHBOURS",
    "RU_TO_EN_LAYOUT",
    "EN_TO_RU_LAYOUT",
    "HOMOGLYPHS_CYR_TO_LAT",
    "HOMOGLYPHS_LAT_TO_CYR",
    "ERROR_TYPES",
    "DEFAULT_PROBABILITIES",
    "Corruption",
    "CorruptionResult",
    "ErrorConfig",
    "RussianErrorGenerator",
    "apply_corrections",
    "to_ru_layout",
    "to_en_layout",
    "default_confusion_path",
    "load_confusion_sets",
]

GENERATOR_VERSION = "ru-errors-1.0.0"


def stable_seed(*parts: object) -> int:
    """Process-independent seed from arbitrary parts.

    ``hash()`` is randomised per interpreter run, so every seed derivation in
    this module and in the corpus builder goes through blake2b instead.
    """
    h = hashlib.blake2b(digest_size=8)
    for p in parts:
        h.update(repr(p).encode("utf-8"))
        h.update(b"\x00")
    return int.from_bytes(h.digest(), "big")

# ---------------------------------------------------------------------------
# Keyboard geometry
# ---------------------------------------------------------------------------
#
# Physical ANSI stagger, in key units.  Row 0 (the digit row) starts flush at
# x=0; the Tab row is pushed right by the 1.5u Tab key, the home row by the
# 1.75u Caps key and the bottom row by the 2.25u Shift key.  Key centres are
# therefore ``row_offset + index + 0.5`` which is what makes the diagonal
# neighbours come out asymmetric in the way real keyboards are.

_ROW_OFFSETS: tuple[float, ...] = (0.0, 1.5, 1.75, 2.25)

#: Standard Russian ЙЦУКЕН layout, top row first.  Includes ё (on the ``~``
#: key), х/ъ (top row tail), ж/э (home row tail) and б/ю (bottom row tail).
RU_KEYBOARD_ROWS: tuple[str, ...] = (
    "ё1234567890-=",
    "йцукенгшщзхъ",
    "фывапролджэ",
    "ячсмитьбю.",
)

#: Standard US QWERTY layout in the same physical positions.
EN_KEYBOARD_ROWS: tuple[str, ...] = (
    "`1234567890-=",
    "qwertyuiop[]",
    "asdfghjkl;'",
    "zxcvbnm,./",
)

# Practical additions to the purely geometric adjacency.  ё sits on the ``~``
# key, which is physically next to nothing but ``1`` and Tab, yet ё/й slips are
# extremely common in real Russian typing (both are "the odd key at the far
# left"), and many ЙЦУКЕН variants relocate ё next to it.  Added explicitly and
# symmetrically rather than by loosening the distance threshold, which would
# have polluted every other key.
_EXTRA_ADJACENCY: tuple[tuple[str, str], ...] = (
    ("ё", "й"),
    ("ё", "1"),
    ("ё", "е"),
)

_NEIGHBOUR_RADIUS = 1.3


def _key_positions(rows: Sequence[str]) -> dict[str, tuple[float, float]]:
    pos: dict[str, tuple[float, float]] = {}
    for r, row in enumerate(rows):
        offset = _ROW_OFFSETS[r]
        for c, ch in enumerate(row):
            pos.setdefault(ch, (offset + c + 0.5, float(r)))
    return pos


def _build_neighbours(
    rows: Sequence[str],
    extra: Sequence[tuple[str, str]] = (),
) -> dict[str, tuple[str, ...]]:
    """Euclidean adjacency over the physical key grid (always symmetric)."""
    pos = _key_positions(rows)
    keys = sorted(pos)
    out: dict[str, set[str]] = {k: set() for k in keys}
    for i, a in enumerate(keys):
        ax, ay = pos[a]
        for b in keys[i + 1 :]:
            bx, by = pos[b]
            if math.hypot(ax - bx, ay - by) <= _NEIGHBOUR_RADIUS:
                out[a].add(b)
                out[b].add(a)
    for a, b in extra:
        if a in out and b in out:
            out[a].add(b)
            out[b].add(a)
    return {k: tuple(sorted(v)) for k, v in sorted(out.items())}


#: Full physical adjacency of the Russian layout, digits and punctuation
#: included (typing ``1`` for ``ё`` is a real slip).
RU_KEY_NEIGHBOURS: dict[str, tuple[str, ...]] = _build_neighbours(
    RU_KEYBOARD_ROWS, _EXTRA_ADJACENCY
)

#: Full physical adjacency of the QWERTY layout.
EN_KEY_NEIGHBOURS: dict[str, tuple[str, ...]] = _build_neighbours(EN_KEYBOARD_ROWS)


def _letters_only(nb: dict[str, tuple[str, ...]]) -> dict[str, tuple[str, ...]]:
    keys = {k for k in nb if k.isalpha()}
    return {
        k: tuple(x for x in nb[k] if x in keys)
        for k in sorted(keys)
    }


#: Cyrillic-letter-only view of :data:`RU_KEY_NEIGHBOURS`; this is what the
#: ``keyboard_neighbour`` corruption uses so it never injects a digit mid-word.
RU_LETTER_NEIGHBOURS: dict[str, tuple[str, ...]] = _letters_only(RU_KEY_NEIGHBOURS)


def _build_layout_map() -> dict[str, str]:
    out: dict[str, str] = {}
    for ru_row, en_row in zip(RU_KEYBOARD_ROWS, EN_KEYBOARD_ROWS):
        for ru_ch, en_ch in zip(ru_row, en_row):
            out.setdefault(ru_ch, en_ch)
            if ru_ch.isalpha() and en_ch.isalpha():
                out.setdefault(ru_ch.upper(), en_ch.upper())
    return out


#: ЙЦУКЕН -> QWERTY, keyed by the character the Russian layout produces.
RU_TO_EN_LAYOUT: dict[str, str] = _build_layout_map()

#: QWERTY -> ЙЦУКЕН, the exact inverse of :data:`RU_TO_EN_LAYOUT`.
EN_TO_RU_LAYOUT: dict[str, str] = {v: k for k, v in RU_TO_EN_LAYOUT.items()}


def to_en_layout(text: str) -> str:
    """Retype ``text`` as if the ЙЦУКЕН keys had been read as QWERTY."""
    return "".join(RU_TO_EN_LAYOUT.get(ch, ch) for ch in text)


def to_ru_layout(text: str) -> str:
    """Retype ``text`` as if the QWERTY keys had been read as ЙЦУКЕН."""
    return "".join(EN_TO_RU_LAYOUT.get(ch, ch) for ch in text)


# ---------------------------------------------------------------------------
# Homoglyphs
# ---------------------------------------------------------------------------

_HOMOGLYPH_PAIRS: tuple[tuple[str, str], ...] = (
    ("а", "a"),
    ("е", "e"),
    ("о", "o"),
    ("р", "p"),
    ("с", "c"),
    ("х", "x"),
    ("у", "y"),
    ("к", "k"),
    ("м", "m"),
    ("т", "t"),
    ("в", "b"),
    ("н", "h"),
)

HOMOGLYPHS_CYR_TO_LAT: dict[str, str] = {}
for _cyr, _lat in _HOMOGLYPH_PAIRS:
    HOMOGLYPHS_CYR_TO_LAT[_cyr] = _lat
    HOMOGLYPHS_CYR_TO_LAT[_cyr.upper()] = _lat.upper()
del _cyr, _lat

#: Latin -> Cyrillic.  Note this is not a perfect inverse in the uppercase
#: direction on purpose: ``В`` and ``Н`` map from ``B``/``H`` while lowercase
#: ``b``/``h`` map back to ``в``/``н``.
HOMOGLYPHS_LAT_TO_CYR: dict[str, str] = {v: k for k, v in HOMOGLYPHS_CYR_TO_LAT.items()}


# ---------------------------------------------------------------------------
# Morphology (pymorphy3) -- lazily initialised, cached
# ---------------------------------------------------------------------------

_MORPH = None


def _morph():
    global _MORPH
    if _MORPH is None:  # pragma: no branch - trivial memoisation
        import pymorphy3

        _MORPH = pymorphy3.MorphAnalyzer()
    return _MORPH


@functools.lru_cache(maxsize=1 << 18)
def is_known_word(word: str) -> bool:
    """True when OpenCorpora knows ``word`` as a Russian surface form."""
    w = word.strip().lower()
    if not w:
        return False
    if not _CYR_RE.search(w):
        return False
    try:
        return bool(_morph().word_is_known(w))
    except Exception:  # pragma: no cover - dictionary failure must not crash
        return False


@functools.lru_cache(maxsize=1 << 16)
def _lexeme_forms(word: str) -> tuple[tuple[str, str, str], ...]:
    """``((surface_form, pos, tag_string), ...)`` for the best parse of ``word``.

    Restricted to the *same part of speech* as the input, so a corruption is a
    wrong case/number/gender/person/tense of the same word -- an agreement
    error -- rather than a jump to a participle or gerund, which reads as a
    different word entirely and is not the error class we are modelling.
    """
    try:
        parses = _morph().parse(word.lower())
    except Exception:  # pragma: no cover
        return ()
    if not parses:
        return ()
    best = parses[0]
    pos = best.tag.POS
    if pos not in _INFLECTABLE_POS:
        return ()
    try:
        lexeme = best.lexeme
    except Exception:  # pragma: no cover
        return ()
    return tuple(
        (f.word, str(f.tag.POS), str(f.tag)) for f in lexeme if f.tag.POS == pos
    )


_INFLECTABLE_POS = frozenset({"NOUN", "ADJF", "VERB", "PRTF", "NUMR", "ADJS"})


# ---------------------------------------------------------------------------
# Text helpers
# ---------------------------------------------------------------------------

_CYR_RE = re.compile(r"[А-Яа-яЁё]")
_WORD_RE = re.compile(r"[А-Яа-яЁёA-Za-z]+(?:[-'][А-Яа-яЁёA-Za-z]+)*")
_PROTECT_RE = re.compile(
    r"(?:https?://\S+|www\.\S+|\S+@\S+\.\S+|[A-Za-z]:\\\S+|\d[\d.,:/-]*)"
)
_VOWELS = "аеёиоуыэюя"
_CONSONANTS = "бвгджзйклмнпрстфхцчшщ"


def _protected_spans(text: str) -> list[tuple[int, int]]:
    return [(m.start(), m.end()) for m in _PROTECT_RE.finditer(text)]


def _word_spans(text: str, min_len: int = 1) -> list[tuple[int, int]]:
    prot = _protected_spans(text)
    out = []
    for m in _WORD_RE.finditer(text):
        if len(m.group(0)) < min_len:
            continue
        if any(m.start() < e and m.end() > s for s, e in prot):
            continue
        out.append((m.start(), m.end()))
    return out


def _cyrillic_word_spans(text: str, min_len: int = 1) -> list[tuple[int, int]]:
    return [
        (s, e)
        for s, e in _word_spans(text, min_len)
        if _CYR_RE.search(text[s:e]) and text[s:e].isalpha()
    ]


def _match_case(source: str, target: str) -> str:
    """Give ``target`` the capitalisation pattern of ``source``."""
    if not source or not target:
        return target
    if source.isupper() and len(source) > 1:
        return target.upper()
    if source[:1].isupper():
        return target[:1].upper() + target[1:]
    return target


# ---------------------------------------------------------------------------
# Public data structures
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class Corruption:
    """A single introduced error.

    ``start``/``end`` index the **corrupted** text and delimit ``replacement``;
    ``original`` is the correct text that belonged there, so the sentence is
    repaired by substituting ``original`` back into the span.
    """

    error_type: str
    start: int
    end: int
    original: str
    replacement: str
    category: str  # "non_word" | "real_word" | "not_lexical"
    detail: str = ""

    @property
    def span(self) -> tuple[int, int]:
        return (self.start, self.end)

    def as_record(self) -> dict:
        return {
            "start": self.start,
            "end": self.end,
            "original": self.original,
            "replacement": self.replacement,
            "type": self.error_type,
        }


class CorruptionResult(list):
    """``list[Corruption]`` that also carries the texts it relates.

    ``corrupt()`` is specified to return a list of corruptions; this subclass
    keeps that contract while making the corrupted text reachable without a
    second call.
    """

    def __init__(
        self,
        corruptions: Iterable[Corruption] = (),
        *,
        text: str = "",
        target: str = "",
    ) -> None:
        super().__init__(corruptions)
        self.text = text  # corrupted sentence  (== model input / "source")
        self.target = target  # correct sentence   (== model output / "target")

    #: Alias matching the corpus record field name.
    @property
    def source(self) -> str:
        return self.text

    @property
    def changed(self) -> bool:
        return bool(self) and self.text != self.target

    def expected_correction(self) -> str:
        return apply_corrections(self.text, self)

    def __repr__(self) -> str:  # pragma: no cover - debugging aid
        return f"CorruptionResult({list(self)!r}, text={self.text!r})"


def apply_corrections(text: str, corruptions: Iterable[Corruption]) -> str:
    """Undo ``corruptions`` in ``text``, yielding the correct sentence."""
    out = []
    cursor = 0
    for c in sorted(corruptions, key=lambda c: c.start):
        if c.start < cursor:
            continue
        out.append(text[cursor : c.start])
        out.append(c.original)
        cursor = c.end
    out.append(text[cursor:])
    return "".join(out)


ERROR_TYPES: tuple[str, ...] = (
    # character level
    "char_delete",
    "char_insert",
    "char_replace",
    "char_transpose",
    "char_double",
    "char_undouble",
    "keyboard_neighbour",
    # layout / encoding
    "layout_word",
    "layout_partial",
    "homoglyph",
    # spacing
    "space_delete",
    "space_insert",
    # punctuation
    "punct_missing_comma",
    "punct_extra_comma",
    "punct_dash",
    "punct_terminal",
    # capitalisation
    "caps_sentence_start",
    "caps_proper_noun",
    "caps_allcaps",
    # morphology
    "morph_inflect",
    # Russian orthography
    "ortho_tsya",
    "ortho_nn",
    "ortho_ne_ni",
    "ortho_pre_pri",
    "ortho_prefix_zs",
    "ortho_signs",
    "ortho_zhi_shi",
    "ortho_cha_shcha",
    "ortho_chu_shchu",
    "ortho_unstressed_vowel",
    "ortho_voicing",
    "ortho_yo",
    # lexical confusion (real-word errors)
    "confusion_pair",
)

#: Relative weights, not absolute frequencies: the generator picks how many
#: errors to inject, then draws types without replacement using these.
DEFAULT_PROBABILITIES: dict[str, float] = {
    "char_delete": 0.55,
    "char_insert": 0.35,
    "char_replace": 0.30,
    "char_transpose": 0.50,
    "char_double": 0.30,
    "char_undouble": 0.25,
    "keyboard_neighbour": 0.70,
    "layout_word": 0.18,
    "layout_partial": 0.12,
    "homoglyph": 0.20,
    "space_delete": 0.35,
    "space_insert": 0.25,
    "punct_missing_comma": 0.60,
    "punct_extra_comma": 0.35,
    "punct_dash": 0.20,
    "punct_terminal": 0.30,
    "caps_sentence_start": 0.40,
    "caps_proper_noun": 0.25,
    "caps_allcaps": 0.10,
    "morph_inflect": 0.75,
    "ortho_tsya": 0.70,
    "ortho_nn": 0.45,
    "ortho_ne_ni": 0.40,
    "ortho_pre_pri": 0.35,
    "ortho_prefix_zs": 0.40,
    "ortho_signs": 0.30,
    "ortho_zhi_shi": 0.40,
    "ortho_cha_shcha": 0.35,
    "ortho_chu_shchu": 0.35,
    "ortho_unstressed_vowel": 0.55,
    "ortho_voicing": 0.45,
    "ortho_yo": 0.08,
    "confusion_pair": 0.85,
}


@dataclass
class ErrorConfig:
    """Every error type is individually probability-configurable."""

    probabilities: dict[str, float] = field(
        default_factory=lambda: dict(DEFAULT_PROBABILITIES)
    )
    #: P(number of errors) for a sentence that is being corrupted at all.
    error_count_weights: dict[int, float] = field(
        default_factory=lambda: {1: 0.58, 2: 0.28, 3: 0.10, 4: 0.04}
    )
    #: Minimum gap between two edits, in characters of the original sentence.
    min_edit_gap: int = 1
    #: Give up after this many failed type draws.
    max_attempts: int = 40
    #: Reject a word-level edit whose corrupted token is a known word, unless
    #: the error type is inherently a real-word error.  Off by default: real
    #: word errors are exactly what we want the reranker to learn.
    reject_real_words: bool = False

    def enabled(self) -> list[str]:
        return [t for t in ERROR_TYPES if self.probabilities.get(t, 0.0) > 0.0]

    def with_only(self, *types: str) -> "ErrorConfig":
        """Copy with all but ``types`` disabled -- handy for tests."""
        probs = {t: (self.probabilities.get(t, 0.5) if t in types else 0.0) for t in ERROR_TYPES}
        return _dc_replace(self, probabilities=probs)


@dataclass
class _Edit:
    """A planned edit expressed over the *original* sentence."""

    start: int
    end: int
    original: str
    replacement: str
    error_type: str
    detail: str = ""
    #: Word span used for dictionary classification (defaults to the edit span).
    word_start: int | None = None
    word_end: int | None = None


# ---------------------------------------------------------------------------
# Orthographic rule tables
# ---------------------------------------------------------------------------

_ZS_PREFIXES: tuple[tuple[str, str], ...] = (
    ("без", "бес"),
    ("раз", "рас"),
    ("роз", "рос"),
    ("из", "ис"),
    ("воз", "вос"),
    ("вз", "вс"),
    ("низ", "нис"),
    ("чрез", "чрес"),
    ("через", "черес"),
)

_SIGN_PREFIXES: tuple[str, ...] = (
    "об", "под", "раз", "рас", "с", "пред", "из", "сверх", "меж", "от", "в", "пере", "без",
)

_VOICING_PAIRS: tuple[tuple[str, str], ...] = (
    ("д", "т"),
    ("б", "п"),
    ("г", "к"),
    ("з", "с"),
    ("в", "ф"),
    ("ж", "ш"),
)
_VOICING_MAP: dict[str, str] = {}
for _a, _b in _VOICING_PAIRS:
    _VOICING_MAP[_a] = _b
    _VOICING_MAP[_b] = _a
del _a, _b

_ZHI_SHI = (("жи", "жы"), ("ши", "шы"), ("ци", "цы"))
_CHA_SHCHA = (("ча", "чя"), ("ща", "щя"), ("чо", "чё"))
_CHU_SHCHU = (("чу", "чю"), ("щу", "щю"))

#: Subordinators that require a preceding comma in Russian; deleting that comma
#: is the single most common punctuation error in casual writing.
_COMMA_TRIGGERS: tuple[str, ...] = (
    "который", "которая", "которое", "которые", "которых", "которым", "которой",
    "что", "чтобы", "потому", "если", "когда", "пока", "хотя", "чем", "как",
    "где", "куда", "кто", "поэтому", "значит", "но", "а", "или", "зато", "однако",
)

_DASHES = ("—", "–", "-", "−")


# ---------------------------------------------------------------------------
# Confusion sets
# ---------------------------------------------------------------------------


def default_confusion_path() -> Path:
    """``ai/data/errors/ru_confusion_pairs.json`` relative to this file."""
    return Path(__file__).resolve().parents[1] / "data" / "errors" / "ru_confusion_pairs.json"


def load_confusion_sets(path: Path | str | None = None) -> list[dict]:
    p = Path(path) if path is not None else default_confusion_path()
    if not p.exists():
        return []
    with p.open(encoding="utf-8") as fh:
        data = json.load(fh)
    sets = data["sets"] if isinstance(data, dict) else data
    return [s for s in sets if len(s.get("members", ())) >= 2]


def _index_confusions(
    sets: Sequence[dict],
) -> tuple[dict[str, tuple[tuple[str, ...], str]], dict[str, tuple[tuple[str, tuple[str, ...], str], ...]]]:
    """Build the single-word and multi-word lookups.

    Returns ``(words, phrases)`` where ``words`` maps a lowercased one-word
    member to ``(alternatives, kind)`` and ``phrases`` maps the lowercased
    *first* word of a multi-word member to the phrases starting with it,
    longest first.  Ordering is fully deterministic.
    """
    words: dict[str, tuple[tuple[str, ...], str]] = {}
    phrase_rows: list[tuple[str, tuple[str, ...], str]] = []
    for s in sets:
        members = [str(m) for m in s["members"]]
        kind = str(s.get("kind", "paronym"))
        for m in members:
            others = tuple(x for x in members if x.lower() != m.lower())
            if not others:
                continue
            key = m.lower()
            if " " in key:
                phrase_rows.append((key, others, kind))
                continue
            if key in words:  # merge duplicates across sets, stay deterministic
                merged = tuple(sorted(set(words[key][0]) | set(others)))
                words[key] = (merged, words[key][1])
            else:
                words[key] = (others, kind)

    phrases: dict[str, list[tuple[str, tuple[str, ...], str]]] = {}
    for row in sorted(phrase_rows, key=lambda r: (-len(r[0]), r[0])):
        phrases.setdefault(row[0].split(" ", 1)[0], []).append(row)
    return words, {k: tuple(v) for k, v in sorted(phrases.items())}


# ---------------------------------------------------------------------------
# The generator
# ---------------------------------------------------------------------------


class RussianErrorGenerator:
    """Seeded Russian error generator.

    >>> gen = RussianErrorGenerator()
    >>> res = gen.corrupt("Он говорит, что придёт завтра.", seed=7)
    >>> res.expected_correction() == res.target
    True
    """

    version = GENERATOR_VERSION

    def __init__(
        self,
        config: ErrorConfig | None = None,
        confusion_sets: Sequence[dict] | None = None,
        *,
        load_confusions: bool = True,
    ) -> None:
        self.config = config or ErrorConfig()
        if confusion_sets is None and load_confusions:
            confusion_sets = load_confusion_sets()
        self.confusion_sets = list(confusion_sets or [])
        self._conf_words, self._conf_phrases = _index_confusions(self.confusion_sets)

    # -- public API --------------------------------------------------------

    def corrupt(self, sentence: str, seed: int = 0) -> CorruptionResult:
        """Corrupt ``sentence`` deterministically for ``seed``.

        Returns a :class:`CorruptionResult` (a ``list[Corruption]``) whose
        ``.text`` is the corrupted sentence and ``.target`` the input.
        """
        rng = random.Random(stable_seed(seed, sentence))
        edits = self._plan(sentence, rng)
        return self._materialise(sentence, edits)

    def corrupt_n(self, sentence: str, seed: int, n_errors: int) -> CorruptionResult:
        """Corrupt with an exact error budget (used by the corpus builder)."""
        rng = random.Random(stable_seed(seed, sentence, n_errors))
        edits = self._plan(sentence, rng, n_errors=n_errors)
        return self._materialise(sentence, edits)

    # -- planning ----------------------------------------------------------

    def _plan(self, text: str, rng: random.Random, n_errors: int | None = None) -> list[_Edit]:
        cfg = self.config
        if n_errors is None:
            counts = sorted(cfg.error_count_weights)
            weights = [cfg.error_count_weights[c] for c in counts]
            n_errors = rng.choices(counts, weights=weights, k=1)[0]
        if n_errors <= 0:
            return []

        pool = [t for t in cfg.enabled()]
        weights = [cfg.probabilities[t] for t in pool]
        edits: list[_Edit] = []
        taken: list[tuple[int, int]] = []
        attempts = 0
        while len(edits) < n_errors and attempts < cfg.max_attempts and pool:
            attempts += 1
            etype = rng.choices(pool, weights=weights, k=1)[0]
            handler = getattr(self, "_e_" + etype, None)
            if handler is None:  # pragma: no cover - guarded by ERROR_TYPES
                continue
            edit = handler(text, rng)
            if edit is None:
                continue
            if not self._accept(text, edit, taken):
                continue
            edits.append(edit)
            taken.append((edit.start - cfg.min_edit_gap, edit.end + cfg.min_edit_gap))
        edits.sort(key=lambda e: e.start)
        return edits

    def _accept(self, text: str, edit: _Edit, taken: Sequence[tuple[int, int]]) -> bool:
        if edit.start < 0 or edit.end > len(text) or edit.start > edit.end:
            return False
        if text[edit.start : edit.end] != edit.original:
            return False
        if edit.replacement == edit.original:
            return False
        if edit.original and edit.replacement and edit.original.isalpha():
            if edit.replacement.lower() == edit.original.lower() and edit.error_type not in (
                "caps_sentence_start",
                "caps_proper_noun",
                "caps_allcaps",
            ):
                return False
        for s, e in taken:
            if edit.start < e and edit.end > s:
                return False
        if self.config.reject_real_words and self._classify(edit) == "real_word":
            return False
        return True

    def _classify(self, edit: _Edit) -> str:
        """``non_word`` / ``real_word`` / ``not_lexical`` for a planned edit."""
        text = edit.replacement
        if not _CYR_RE.search(text) and not re.search(r"[A-Za-z]", text):
            return "not_lexical"
        tokens = [t for t in _WORD_RE.findall(text) if t]
        if not tokens:
            return "not_lexical"
        for tok in tokens:
            if not _CYR_RE.search(tok):
                return "non_word"  # Latin residue: layout / homoglyph slip
            if not is_known_word(tok):
                return "non_word"
        return "real_word"

    # -- materialisation ---------------------------------------------------

    def _materialise(self, text: str, edits: Sequence[_Edit]) -> CorruptionResult:
        out: list[str] = []
        corruptions: list[Corruption] = []
        cursor = 0
        delta = 0
        for e in edits:
            out.append(text[cursor : e.start])
            new_start = e.start + delta
            out.append(e.replacement)
            new_end = new_start + len(e.replacement)
            delta += len(e.replacement) - len(e.original)
            cursor = e.end
            corruptions.append(
                Corruption(
                    error_type=e.error_type,
                    start=new_start,
                    end=new_end,
                    original=e.original,
                    replacement=e.replacement,
                    category=self._classify(e),
                    detail=e.detail,
                )
            )
        out.append(text[cursor:])
        return CorruptionResult(corruptions, text="".join(out), target=text)

    # ------------------------------------------------------------------
    # Character-level errors
    # ------------------------------------------------------------------

    def _pick_word(
        self,
        text: str,
        rng: random.Random,
        min_len: int = 3,
        cyrillic: bool = True,
    ) -> tuple[int, int, str] | None:
        spans = (
            _cyrillic_word_spans(text, min_len)
            if cyrillic
            else _word_spans(text, min_len)
        )
        if not spans:
            return None
        s, e = rng.choice(spans)
        return s, e, text[s:e]

    def _e_char_delete(self, text: str, rng: random.Random) -> _Edit | None:
        got = self._pick_word(text, rng, 4)
        if not got:
            return None
        s, e, w = got
        i = rng.randrange(1, len(w) - 1)
        return _Edit(s, e, w, w[:i] + w[i + 1 :], "char_delete", detail=w[i])

    def _e_char_insert(self, text: str, rng: random.Random) -> _Edit | None:
        got = self._pick_word(text, rng, 3)
        if not got:
            return None
        s, e, w = got
        i = rng.randrange(1, len(w))
        base = w[i - 1].lower()
        pool = RU_LETTER_NEIGHBOURS.get(base) or tuple(_VOWELS)
        ch = rng.choice(pool)
        return _Edit(s, e, w, w[:i] + ch + w[i:], "char_insert", detail=ch)

    def _e_char_replace(self, text: str, rng: random.Random) -> _Edit | None:
        got = self._pick_word(text, rng, 3)
        if not got:
            return None
        s, e, w = got
        i = rng.randrange(1, len(w))
        old = w[i].lower()
        alphabet = _VOWELS if old in _VOWELS else _CONSONANTS
        ch = rng.choice([c for c in alphabet if c != old])
        return _Edit(s, e, w, w[:i] + ch + w[i + 1 :], "char_replace", detail=f"{old}>{ch}")

    def _e_char_transpose(self, text: str, rng: random.Random) -> _Edit | None:
        got = self._pick_word(text, rng, 4)
        if not got:
            return None
        s, e, w = got
        cands = [i for i in range(1, len(w) - 2) if w[i] != w[i + 1]]
        if not cands:
            return None
        i = rng.choice(cands)
        new = w[:i] + w[i + 1] + w[i] + w[i + 2 :]
        return _Edit(s, e, w, new, "char_transpose", detail=w[i : i + 2])

    def _e_char_double(self, text: str, rng: random.Random) -> _Edit | None:
        got = self._pick_word(text, rng, 3)
        if not got:
            return None
        s, e, w = got
        cands = [
            i
            for i in range(1, len(w))
            if w[i].isalpha() and w[i - 1] != w[i] and (i + 1 >= len(w) or w[i + 1] != w[i])
        ]
        if not cands:
            return None
        i = rng.choice(cands)
        return _Edit(s, e, w, w[: i + 1] + w[i] + w[i + 1 :], "char_double", detail=w[i])

    def _e_char_undouble(self, text: str, rng: random.Random) -> _Edit | None:
        got = self._pick_word(text, rng, 4)
        if not got:
            return None
        s, e, w = got
        cands = [i for i in range(len(w) - 1) if w[i] == w[i + 1] and w[i].isalpha()]
        if not cands:
            return None
        i = rng.choice(cands)
        return _Edit(s, e, w, w[:i] + w[i + 1 :], "char_undouble", detail=w[i])

    def _e_keyboard_neighbour(self, text: str, rng: random.Random) -> _Edit | None:
        got = self._pick_word(text, rng, 3)
        if not got:
            return None
        s, e, w = got
        cands = [i for i, ch in enumerate(w) if RU_LETTER_NEIGHBOURS.get(ch.lower())]
        if not cands:
            return None
        i = rng.choice(cands)
        old = w[i]
        nb = RU_LETTER_NEIGHBOURS[old.lower()]
        ch = rng.choice(nb)
        if old.isupper():
            ch = ch.upper()
        return _Edit(s, e, w, w[:i] + ch + w[i + 1 :], "keyboard_neighbour", detail=f"{old}>{ch}")

    # ------------------------------------------------------------------
    # Layout / homoglyph
    # ------------------------------------------------------------------

    def _e_layout_word(self, text: str, rng: random.Random) -> _Edit | None:
        spans = _word_spans(text, 3)
        if not spans:
            return None
        rng.shuffle(spans)
        for s, e in spans:
            w = text[s:e]
            if _CYR_RE.search(w):
                new = to_en_layout(w)
            else:
                new = to_ru_layout(w)
            if new != w and all(ch not in " \t" for ch in new):
                return _Edit(s, e, w, new, "layout_word", detail="ru>en" if _CYR_RE.search(w) else "en>ru")
        return None

    def _e_layout_partial(self, text: str, rng: random.Random) -> _Edit | None:
        got = self._pick_word(text, rng, 4)
        if not got:
            return None
        s, e, w = got
        cut = rng.randrange(1, len(w))
        # Only the tail (or head) slips out of layout -- the classic
        # "noticed halfway through" typo.
        if rng.random() < 0.5:
            new = w[:cut] + to_en_layout(w[cut:])
            detail = f"tail@{cut}"
        else:
            new = to_en_layout(w[:cut]) + w[cut:]
            detail = f"head@{cut}"
        if new == w:
            return None
        return _Edit(s, e, w, new, "layout_partial", detail=detail)

    def _e_homoglyph(self, text: str, rng: random.Random) -> _Edit | None:
        got = self._pick_word(text, rng, 3)
        if not got:
            return None
        s, e, w = got
        cands = [i for i, ch in enumerate(w) if ch in HOMOGLYPHS_CYR_TO_LAT]
        if not cands:
            return None
        i = rng.choice(cands)
        new = w[:i] + HOMOGLYPHS_CYR_TO_LAT[w[i]] + w[i + 1 :]
        return _Edit(s, e, w, new, "homoglyph", detail=f"{w[i]}>{HOMOGLYPHS_CYR_TO_LAT[w[i]]}")

    # ------------------------------------------------------------------
    # Spacing
    # ------------------------------------------------------------------

    def _e_space_delete(self, text: str, rng: random.Random) -> _Edit | None:
        cands = [
            m.start()
            for m in re.finditer(r"(?<=[А-Яа-яЁё]) (?=[А-Яа-яЁё])", text)
        ]
        if not cands:
            return None
        i = rng.choice(cands)
        ws = text.rfind(" ", 0, i) + 1
        we = text.find(" ", i + 1)
        we = len(text) if we < 0 else we
        left, right = text[ws:i], text[i + 1 : we]
        if not (left and right):
            return None
        merged = left + right
        return _Edit(ws, we, text[ws:we], merged, "space_delete", detail=f"{left}+{right}")

    def _e_space_insert(self, text: str, rng: random.Random) -> _Edit | None:
        spans = [(s, e) for s, e in _cyrillic_word_spans(text, 6)]
        if not spans:
            return None
        s, e = rng.choice(spans)
        w = text[s:e]
        cut = rng.randrange(2, len(w) - 1)
        return _Edit(s, e, w, w[:cut] + " " + w[cut:], "space_insert", detail=f"@{cut}")

    # ------------------------------------------------------------------
    # Punctuation
    # ------------------------------------------------------------------

    def _e_punct_missing_comma(self, text: str, rng: random.Random) -> _Edit | None:
        pat = r",(\s+)(?=(?:" + "|".join(_COMMA_TRIGGERS) + r")\b)"
        cands = [m for m in re.finditer(pat, text, flags=re.IGNORECASE)]
        if not cands:
            return None
        m = rng.choice(cands)
        return _Edit(m.start(), m.start() + 1, ",", "", "punct_missing_comma", detail=m.group(1))

    def _e_punct_extra_comma(self, text: str, rng: random.Random) -> _Edit | None:
        cands = [
            m.start()
            for m in re.finditer(r"(?<=[А-Яа-яЁё]) (?=[А-Яа-яЁё])", text)
        ]
        if not cands:
            return None
        i = rng.choice(cands)
        return _Edit(i, i, "", ",", "punct_extra_comma")

    def _e_punct_dash(self, text: str, rng: random.Random) -> _Edit | None:
        cands: list[_Edit] = []
        for m in re.finditer(r"\s(—|–)\s", text):
            cands.append(_Edit(m.start() + 1, m.end() - 1, m.group(1), "-", "punct_dash", detail="dash>hyphen"))
        for m in re.finditer(r"(?<=[А-Яа-яЁё])-(?=[А-Яа-яЁё])", text):
            cands.append(_Edit(m.start(), m.end(), "-", "—", "punct_dash", detail="hyphen>dash"))
        for m in re.finditer(r"\s-\s", text):
            cands.append(_Edit(m.start() + 1, m.end() - 1, "-", "—", "punct_dash", detail="hyphen>dash"))
        if not cands:
            return None
        return rng.choice(cands)

    def _e_punct_terminal(self, text: str, rng: random.Random) -> _Edit | None:
        m = re.search(r"([.!?…]+)\s*$", text)
        if not m:
            return None
        if rng.random() < 0.75:
            return _Edit(m.start(1), m.end(1), m.group(1), "", "punct_terminal", detail="dropped")
        alt = {".": "!", "!": ".", "?": ".", "…": "."}.get(m.group(1)[0], ".")
        return _Edit(m.start(1), m.end(1), m.group(1), alt, "punct_terminal", detail="swapped")

    # ------------------------------------------------------------------
    # Capitalisation
    # ------------------------------------------------------------------

    def _e_caps_sentence_start(self, text: str, rng: random.Random) -> _Edit | None:
        for i, ch in enumerate(text):
            if ch.isalpha():
                if ch.isupper():
                    return _Edit(i, i + 1, ch, ch.lower(), "caps_sentence_start")
                return None
        return None

    def _e_caps_proper_noun(self, text: str, rng: random.Random) -> _Edit | None:
        spans = _cyrillic_word_spans(text, 2)
        cands = [
            (s, e)
            for s, e in spans
            if s > 0 and text[s].isupper() and not text[s:e].isupper()
        ]
        if not cands:
            return None
        s, e = rng.choice(cands)
        w = text[s:e]
        return _Edit(s, e, w, w[0].lower() + w[1:], "caps_proper_noun")

    def _e_caps_allcaps(self, text: str, rng: random.Random) -> _Edit | None:
        spans = _cyrillic_word_spans(text, 3)
        cands = [(s, e) for s, e in spans if not text[s:e].isupper()]
        if not cands:
            return None
        s, e = rng.choice(cands)
        w = text[s:e]
        return _Edit(s, e, w, w.upper(), "caps_allcaps")

    # ------------------------------------------------------------------
    # Morphology -- wrong but real inflected form of the same lemma
    # ------------------------------------------------------------------

    def _e_morph_inflect(self, text: str, rng: random.Random) -> _Edit | None:
        spans = _cyrillic_word_spans(text, 3)
        if not spans:
            return None
        rng.shuffle(spans)
        for s, e in spans[:12]:
            w = text[s:e]
            forms = _lexeme_forms(w)
            if len(forms) < 2:
                continue
            low = w.lower()
            alts = sorted({f for f, _, _ in forms if f != low})
            if not alts:
                continue
            new = _match_case(w, rng.choice(alts))
            if new == w:
                continue
            tags = {f: t for f, _, t in forms}
            return _Edit(
                s, e, w, new, "morph_inflect",
                detail=f"{tags.get(low, '?')}>{tags.get(new.lower(), '?')}",
            )
        return None

    # ------------------------------------------------------------------
    # Russian orthography -- rule driven
    # ------------------------------------------------------------------

    def _word_rule(
        self,
        text: str,
        rng: random.Random,
        etype: str,
        rule,
        min_len: int = 3,
        sample: int = 14,
    ) -> _Edit | None:
        """Apply ``rule(word, rng) -> (new_word, detail) | None`` to a word."""
        spans = _cyrillic_word_spans(text, min_len)
        if not spans:
            return None
        rng.shuffle(spans)
        for s, e in spans[:sample]:
            w = text[s:e]
            got = rule(w, rng)
            if got is None:
                continue
            new, detail = got
            if new and new != w:
                return _Edit(s, e, w, new, etype, detail=detail)
        return None

    @staticmethod
    def _rule_tsya(w: str, rng: random.Random):
        low = w.lower()
        if low.endswith("ться"):
            return w[:-4] + _preserve(w[-4:], "тся"), "ться>тся"
        if low.endswith("тся"):
            return w[:-3] + _preserve(w[-3:], "ться"), "тся>ться"
        return None

    @staticmethod
    def _rule_nn(w: str, rng: random.Random):
        low = w.lower()
        i = low.find("нн")
        if i >= 0:
            return w[:i] + w[i + 1 :], "нн>н"
        m = re.search(r"(ан|ян|ен|ён|ин|он|ун)(ый|ая|ое|ые|ый|ого|ому|ым|ых|о|а|ы)$", low)
        if m:
            j = m.start(1) + 1  # index of the н
            return w[: j + 1] + "н" + w[j + 1 :], "н>нн"
        return None

    @staticmethod
    def _rule_ne_ni(w: str, rng: random.Random):
        low = w.lower()
        if low in ("не", "ни"):
            return _match_case(w, "ни" if low == "не" else "не"), f"{low}>swap"
        for a, b in (("не", "ни"), ("ни", "не")):
            if low.startswith(a) and len(low) > 4:
                return w[: 0] + _match_case(w, b + low[2:]), f"{a}>{b} prefix"
        return None

    @staticmethod
    def _rule_pre_pri(w: str, rng: random.Random):
        low = w.lower()
        if len(low) < 6:
            return None
        if low.startswith("пре"):
            return _match_case(w, "при" + low[3:]), "пре>при"
        if low.startswith("при"):
            return _match_case(w, "пре" + low[3:]), "при>пре"
        return None

    @staticmethod
    def _rule_prefix_zs(w: str, rng: random.Random):
        low = w.lower()
        for a, b in _ZS_PREFIXES:
            if low.startswith(a) and len(low) > len(a) + 2:
                return _match_case(w, b + low[len(a) :]), f"{a}>{b}"
            if low.startswith(b) and len(low) > len(b) + 2:
                return _match_case(w, a + low[len(b) :]), f"{b}>{a}"
        return None

    @staticmethod
    def _rule_signs(w: str, rng: random.Random):
        low = w.lower()
        i = low.find("ъ")
        if i > 0:
            return w[:i] + ("Ь" if w[i].isupper() else "ь") + w[i + 1 :], "ъ>ь"
        for p in _SIGN_PREFIXES:
            j = len(p)
            if low.startswith(p) and low[j : j + 1] == "ь" and low[j + 1 : j + 2] in "еёюя":
                return w[:j] + ("Ъ" if w[j].isupper() else "ъ") + w[j + 1 :], "ь>ъ"
        # generic: ь before a soft vowel deep inside a word
        m = [k for k in range(1, len(low) - 1) if low[k] == "ь" and low[k + 1] in "еёюя"]
        if m:
            k = rng.choice(m)
            return w[:k] + ("Ъ" if w[k].isupper() else "ъ") + w[k + 1 :], "ь>ъ"
        return None

    @staticmethod
    def _make_digraph_rule(pairs):
        def rule(w: str, rng: random.Random):
            low = w.lower()
            hits = [(i, a, b) for a, b in pairs for i in _find_all(low, a)]
            if not hits:
                return None
            i, a, b = rng.choice(sorted(hits))
            return w[:i] + _preserve(w[i : i + len(a)], b) + w[i + len(a) :], f"{a}>{b}"

        return rule

    @staticmethod
    def _rule_unstressed_vowel(w: str, rng: random.Random):
        """Swap о<->а or е<->и in a syllable that is unlikely to be stressed.

        OpenCorpora carries no stress marks, so "unstressed" is approximated:
        the word must have at least two vowels and we never touch the last
        vowel (which in Russian carries the stress far more often than chance)
        nor a single-vowel word.  This reproduces the аканье/иканье spelling
        errors that dominate real Russian typing without claiming to know the
        true stress position.
        """
        low = w.lower()
        vpos = [i for i, ch in enumerate(low) if ch in _VOWELS]
        if len(vpos) < 2:
            return None
        swap = {"о": "а", "а": "о", "е": "и", "и": "е"}
        cands = [i for i in vpos[:-1] if low[i] in swap]
        if not cands:
            return None
        i = rng.choice(cands)
        ch = swap[low[i]]
        if w[i].isupper():
            ch = ch.upper()
        return w[:i] + ch + w[i + 1 :], f"{low[i]}>{swap[low[i]]}"

    @staticmethod
    def _rule_voicing(w: str, rng: random.Random):
        """Swap a paired consonant at word end or before another consonant."""
        low = w.lower()
        cands = []
        for i, ch in enumerate(low):
            if ch not in _VOICING_MAP:
                continue
            nxt = low[i + 1] if i + 1 < len(low) else ""
            at_end = i == len(low) - 1 or (nxt in "ьъ" and i + 2 >= len(low))
            before_cons = nxt in _CONSONANTS
            if at_end or before_cons:
                cands.append(i)
        if not cands:
            return None
        i = rng.choice(cands)
        ch = _VOICING_MAP[low[i]]
        if w[i].isupper():
            ch = ch.upper()
        return w[:i] + ch + w[i + 1 :], f"{low[i]}>{_VOICING_MAP[low[i]]}"

    @staticmethod
    def _rule_yo(w: str, rng: random.Random):
        low = w.lower()
        i = low.find("ё")
        if i >= 0:
            return w[:i] + ("Е" if w[i].isupper() else "е") + w[i + 1 :], "ё>е"
        cands = _find_all(low, "е")
        if not cands:
            return None
        i = rng.choice(cands)
        cand = w[:i] + ("Ё" if w[i].isupper() else "ё") + w[i + 1 :]
        # only claim it is an error if the ё-form is a real word
        return (cand, "е>ё") if is_known_word(cand) else None

    def _e_ortho_tsya(self, t, r):
        return self._word_rule(t, r, "ortho_tsya", self._rule_tsya, 4)

    def _e_ortho_nn(self, t, r):
        return self._word_rule(t, r, "ortho_nn", self._rule_nn, 4)

    def _e_ortho_ne_ni(self, t, r):
        return self._word_rule(t, r, "ortho_ne_ni", self._rule_ne_ni, 2)

    def _e_ortho_pre_pri(self, t, r):
        return self._word_rule(t, r, "ortho_pre_pri", self._rule_pre_pri, 6)

    def _e_ortho_prefix_zs(self, t, r):
        return self._word_rule(t, r, "ortho_prefix_zs", self._rule_prefix_zs, 5)

    def _e_ortho_signs(self, t, r):
        return self._word_rule(t, r, "ortho_signs", self._rule_signs, 4)

    def _e_ortho_zhi_shi(self, t, r):
        return self._word_rule(t, r, "ortho_zhi_shi", self._make_digraph_rule(_ZHI_SHI), 3)

    def _e_ortho_cha_shcha(self, t, r):
        return self._word_rule(t, r, "ortho_cha_shcha", self._make_digraph_rule(_CHA_SHCHA), 3)

    def _e_ortho_chu_shchu(self, t, r):
        return self._word_rule(t, r, "ortho_chu_shchu", self._make_digraph_rule(_CHU_SHCHU), 3)

    def _e_ortho_unstressed_vowel(self, t, r):
        return self._word_rule(t, r, "ortho_unstressed_vowel", self._rule_unstressed_vowel, 4)

    def _e_ortho_voicing(self, t, r):
        return self._word_rule(t, r, "ortho_voicing", self._rule_voicing, 4)

    def _e_ortho_yo(self, t, r):
        return self._word_rule(t, r, "ortho_yo", self._rule_yo, 3)

    # ------------------------------------------------------------------
    # Lexical confusion (real-word errors)
    # ------------------------------------------------------------------

    def _confusion_candidates(self, text: str) -> list[tuple[int, int, tuple[str, ...], str]]:
        """All confusable single words and phrases in ``text``.

        Deterministic order (by position, then by length) so that the only
        randomness in :meth:`_e_confusion_pair` comes from the supplied RNG.
        """
        out: list[tuple[int, int, tuple[str, ...], str]] = []
        if not (self._conf_words or self._conf_phrases):
            return out
        low = text.lower()
        for s, e in _word_spans(text, 2):
            w = low[s:e]
            for phrase, others, kind in self._conf_phrases.get(w, ()):
                pe = s + len(phrase)
                if low[s:pe] == phrase and (pe == len(text) or not text[pe].isalpha()):
                    out.append((s, pe, others, kind))
            hit = self._conf_words.get(w)
            if hit is not None:
                out.append((s, e, hit[0], hit[1]))
        out.sort(key=lambda r: (r[0], r[1]))
        return out

    def _e_confusion_pair(self, text: str, rng: random.Random) -> _Edit | None:
        cands = self._confusion_candidates(text)
        if not cands:
            return None
        s, e, alts, kind = rng.choice(cands)
        w = text[s:e]
        new = _match_case(w, rng.choice(sorted(alts)))
        if new == w:
            return None
        return _Edit(s, e, w, new, "confusion_pair", detail=kind)

    # ------------------------------------------------------------------
    # Introspection
    # ------------------------------------------------------------------

    def confusable_words(self) -> frozenset[str]:
        """Every lowercased single-word member of the confusion sets."""
        return frozenset(self._conf_words)

    def confusable_first_words(self) -> frozenset[str]:
        """Single words plus the first word of every multi-word member."""
        return frozenset(self._conf_words) | frozenset(self._conf_phrases)

    def has_confusable(self, text: str) -> bool:
        return bool(self._confusion_candidates(text))


def _find_all(hay: str, needle: str) -> list[int]:
    out, i = [], hay.find(needle)
    while i >= 0:
        out.append(i)
        i = hay.find(needle, i + 1)
    return out


def _preserve(source: str, target: str) -> str:
    """Copy the per-character case of ``source`` onto ``target``."""
    out = []
    for i, ch in enumerate(target):
        out.append(ch.upper() if i < len(source) and source[i].isupper() else ch)
    return "".join(out)


def normalise(text: str) -> str:
    """Shared normalisation used by both the generator and the corpus builder."""
    t = unicodedata.normalize("NFC", text)
    t = t.replace(" ", " ").replace(" ", " ").replace("​", "")
    t = t.replace("«", '"').replace("»", '"')
    t = t.replace("“", '"').replace("”", '"').replace("„", '"')
    t = t.replace("‘", "'").replace("’", "'")
    t = t.replace("–", "—").replace("−", "—")
    t = re.sub(r"\s+", " ", t)
    return t.strip()


if __name__ == "__main__":  # pragma: no cover - manual smoke check
    import sys

    gen = RussianErrorGenerator()
    demo = sys.argv[1:] or [
        "Он сказал, что придёт завтра вечером.",
        "Мне нужно учиться, чтобы сдать экзамен по математике.",
        "Наша компания представила новый отчёт для клиентов.",
    ]
    for si, sent in enumerate(demo):
        for seed in range(3):
            res = gen.corrupt(sent, seed=seed + si * 10)
            assert res.expected_correction() == res.target
            print(f"[{seed}] {res.text}")
            for c in res:
                print(f"    {c.error_type:<24} {c.original!r} -> {c.replacement!r} [{c.category}]")
