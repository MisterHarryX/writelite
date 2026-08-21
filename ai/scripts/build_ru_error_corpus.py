#!/usr/bin/env python3
"""Build the WriteLite Russian error-correction corpus.

Pipeline
--------
``RAW -> normalise -> dedup -> quality filter -> contamination filter ->
error synthesis -> hard negatives -> split``

Everything is seeded: rerunning with the same ``--seed`` and the same inputs
reproduces the outputs byte for byte (the ``sha256`` block in ``meta.json`` is
the check).  No stage depends on ``hash()``, on dict iteration order of loaded
JSON, or on wall-clock time.

Sources (both streamed, never read whole)
-----------------------------------------
``ai/data/lexical/ru-wiktionary-20260801.jsonl``
    Real usage sentences live in ``definitions[].examples[]``.
``ai/data/chat/*.jsonl``
    The ``target`` field of the WriteLite chat dataset -- already-correct
    Russian sentences.

Outputs (in ``ai/data/errors/``)
-------------------------------
``train.jsonl`` / ``validation.jsonl`` / ``test.jsonl``
    ``{"id", "source", "target", "errors": [{"start","end","original",
    "replacement","type"}], "domain", "synthetic"}``.
    ``source`` is what the model sees, ``target`` what it must produce, and
    every ``errors`` entry indexes ``source`` such that
    ``source[start:end] == replacement``.
``rerank_train.jsonl`` / ``rerank_validation.jsonl`` / ``rerank_test.jsonl``
    ``{"id", "sentence", "label", ...}`` -- a second view of the same corpus for
    a binary sentence-acceptability reranker.  ``label == 1`` is a correct
    Russian sentence, ``label == 0`` is the same sentence with exactly one wrong
    word substituted, so context is the only discriminator.
``meta.json``
    Counts, hashes, seed, generator version, contamination report.

Usage
-----
    python ai/scripts/build_ru_error_corpus.py
    python ai/scripts/build_ru_error_corpus.py --seed 7 --target-records 30000
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import random
import re
import sys
import unicodedata
from collections import Counter, defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Iterator, Sequence

_REPO = Path(__file__).resolve().parents[2]
if str(_REPO / "ai") not in sys.path:
    sys.path.insert(0, str(_REPO / "ai"))

from writelight_ai.ru_errors import (  # noqa: E402
    GENERATOR_VERSION,
    ErrorConfig,
    RussianErrorGenerator,
    apply_corrections,
    is_known_word,
    normalise,
    stable_seed,
)

BUILDER_VERSION = "ru-error-corpus-1.0.0"

# ---------------------------------------------------------------------------
# Default paths
# ---------------------------------------------------------------------------

DEFAULT_WIKTIONARY = _REPO / "ai" / "data" / "lexical" / "ru-wiktionary-20260801.jsonl"
DEFAULT_CHAT_DIR = _REPO / "ai" / "data" / "chat"
DEFAULT_OUT_DIR = _REPO / "ai" / "data" / "errors"
#: Written by a different pipeline.  This script only ever *reads* it.
DEFAULT_BENCHMARK = _REPO / "ai" / "data" / "benchmark" / "ru_frozen_v1.jsonl"

SPLITS = ("train", "validation", "test")
DEFAULT_SPLIT_RATIOS = (0.86, 0.07, 0.07)

DOMAINS = (
    "chat",
    "social",
    "formal",
    "school",
    "technical",
    "gaming",
    "business",
    "news",
    "docs",
)


# ---------------------------------------------------------------------------
# Quality filtering
# ---------------------------------------------------------------------------

_CYR = re.compile(r"[А-Яа-яЁё]")
_LAT = re.compile(r"[A-Za-z]")
_WORD = re.compile(r"[А-Яа-яЁёA-Za-z]+(?:[-'][А-Яа-яЁёA-Za-z]+)*")

#: Wiki/HTML/markdown residue and citation furniture that survives extraction.
_MARKUP = re.compile(
    r"(?:\{\{|\}\}|\[\[|\]\]|</?[a-zA-Z][^>]*>|&[a-z]{2,6};|&#\d+;|"
    r"==+|\|\||\{\||https?://|www\.|\[\d+\]|\.{4,}|~~|__|\*\*|"
    r"#REDIRECT|thumb\||px\|)"
)

#: Mojibake signatures.  A capital latin-1 lead byte (Ð/Ñ/Ã/Â) followed by a
#: latin-1 supplement character is the unmistakable footprint of UTF-8 Cyrillic
#: decoded through a single-byte codepage; U+FFFD is the footprint of the
#: reverse.  Control characters never belong in a running sentence.
#: Encoding-corruption detection, built from code points at import time so
#: that no control character ever appears literally in this source file.
#: U+FFFD is the footprint of bytes that failed to decode; a latin-1 lead
#: byte (C2/C3/D0/D1) followed by a latin-1 continuation byte is the
#: footprint of UTF-8 Cyrillic decoded through a single-byte codepage.
_REPLACEMENT_CHAR = chr(0xFFFD)
_CONTROL_CHARS = frozenset(
    chr(c) for c in range(0x20) if c not in (0x09, 0x0A, 0x0D)
) | {chr(0x7F)}
_MOJIBAKE_LEADS = frozenset(chr(c) for c in (0xC2, 0xC3, 0xD0, 0xD1))
_MOJIBAKE_TAILS = frozenset(chr(c) for c in range(0x80, 0xC0))


def has_encoding_corruption(text: str) -> bool:
    """Mojibake, replacement characters or stray control codes."""
    if _REPLACEMENT_CHAR in text:
        return True
    if any(ch in _CONTROL_CHARS for ch in text):
        return True
    return any(
        a in _MOJIBAKE_LEADS and b in _MOJIBAKE_TAILS
        for a, b in zip(text, text[1:])
    )


#: Characters that a plain running sentence should never contain: bracket
#: and pipe furniture left by markup, plus invisible formatting codepoints.
_BAD_CHARS = frozenset('{}[]<>|^`' + chr(0x5C)) | {
    chr(c) for c in (0x00AD, 0x200B, 0x200E, 0x200F, 0x202A, 0x202C, 0xFEFF)
}

_ELLIPSIS_LEAD = re.compile(r"^[\s\-—–•*….,;:!?)\]]+")


@dataclass
class FilterStats:
    seen: int = 0
    empty: int = 0
    too_short: int = 0
    too_long: int = 0
    not_cyrillic: int = 0
    markup: int = 0
    mojibake: int = 0
    bad_chars: int = 0
    bad_start: int = 0
    no_verb_material: int = 0
    duplicate: int = 0
    passed_cheap: int = 0
    unknown_tokens: int = 0
    contaminated: int = 0
    accepted: int = 0

    def as_dict(self) -> dict:
        return {k: getattr(self, k) for k in self.__dataclass_fields__}


def dedup_key(text: str) -> str:
    """Normalised identity of a sentence: case-, punctuation- and ё-insensitive.

    Used for deduplication *and* for the benchmark contamination check, so a
    benchmark item that differs only in punctuation or capitalisation still
    knocks the sentence out of the training corpus.
    """
    t = unicodedata.normalize("NFKC", text).lower().replace("ё", "е")
    t = re.sub(r"[^\w\s]", " ", t, flags=re.UNICODE)
    t = re.sub(r"\s+", " ", t).strip()
    return hashlib.blake2b(t.encode("utf-8"), digest_size=16).hexdigest()


def cheap_filter(text: str, stats: FilterStats, *, min_tokens: int, max_tokens: int) -> bool:
    """Everything that can be decided without touching the morphology engine."""
    if not text:
        stats.empty += 1
        return False
    toks = text.split()
    if len(toks) < min_tokens:
        stats.too_short += 1
        return False
    if len(toks) > max_tokens:
        stats.too_long += 1
        return False
    if _MARKUP.search(text):
        stats.markup += 1
        return False
    if has_encoding_corruption(text):
        stats.mojibake += 1
        return False
    if any(ch in _BAD_CHARS for ch in text):
        stats.bad_chars += 1
        return False

    letters = [c for c in text if c.isalpha()]
    if len(letters) < 15:
        stats.not_cyrillic += 1
        return False
    cyr = sum(1 for c in letters if _CYR.match(c))
    if cyr / len(letters) < 0.90:
        stats.not_cyrillic += 1
        return False

    if _ELLIPSIS_LEAD.match(text):
        stats.bad_start += 1
        return False
    if not (text[0].isalpha() or text[0] in "\"'("):
        stats.bad_start += 1
        return False

    words = [w for w in _WORD.findall(text) if _CYR.search(w)]
    if len(words) < min_tokens - 1:
        stats.no_verb_material += 1
        return False
    return True


def morph_filter(text: str, stats: FilterStats, *, max_unknown_ratio: float, max_unknown: int) -> bool:
    """Drop sentences with too many tokens OpenCorpora does not recognise."""
    words = [w for w in _WORD.findall(text) if _CYR.search(w) and len(w) > 1]
    if not words:
        stats.unknown_tokens += 1
        return False
    unknown = sum(1 for w in words if not is_known_word(w))
    if unknown > max_unknown or unknown / len(words) > max_unknown_ratio:
        stats.unknown_tokens += 1
        return False
    return True


# ---------------------------------------------------------------------------
# Domain labelling
# ---------------------------------------------------------------------------
#
# Keyword-driven with a structural fallback.  Deliberately conservative: a
# sentence only gets a specialist label when its lexis actually supports it,
# otherwise it falls back to the register buckets (chat / news / formal) that
# the surface form does support.  The realised distribution is recorded in
# meta.json rather than forced to be uniform.

_DOMAIN_LEXIS: dict[str, tuple[str, ...]] = {
    "gaming": (
        "игр", "геймер", "персонаж", "уровен", "квест", "босс", "катк", "лут",
        "респаун", "стрим", "донат", "прокачк", "мультиплеер", "консол",
        "джойстик", "шутер", "аркад", "приставк", "чит", "патч", "клан",
        "рейд", "скилл", "инвентар",
    ),
    "technical": (
        "систем", "устройств", "программ", "алгоритм", "функци", "интерфейс",
        "сервер", "процессор", "напряжен", "давлен", "температур", "реакци",
        "молекул", "частиц", "двигател", "механизм", "конструкц", "монтаж",
        "датчик", "кабел", "микросхем", "энерги", "мощност", "частот",
        "формул", "уравнен", "компилят", "сет", "протокол", "шифров",
    ),
    "business": (
        "компани", "клиент", "договор", "сделк", "продаж", "рынк", "рынок",
        "прибыл", "убытк", "бюджет", "поставк", "менеджер", "инвест",
        "акци", "капитал", "контракт", "заказчик", "тендер", "маркетинг",
        "выручк", "оборот", "предприят", "фирм", "бизнес", "стартап",
    ),
    "school": (
        "ученик", "учител", "урок", "школ", "экзамен", "класс", "тетрад",
        "учебник", "домашн задан", "сочинен", "диктант", "препода",
        "студент", "лекци", "семинар", "зачёт", "зачет", "задачник",
        "правил русского", "университет", "факультет", "аттестат",
    ),
    "docs": (
        "инструкци", "руководств", "нажмит", "выберит", "установит",
        "настрой", "раздел", "пункт меню", "по умолчанию", "версия",
        "параметр", "шаг ", "приложени", "справк", "документаци",
        "введите", "укажите", "сохраните", "перезагрузите",
    ),
    "formal": (
        "необходим", "следует", "в соответствии", "настоящ", "согласно",
        "порядк", "закон", "стать", "положени", "утвержд", "постановлен",
        "обязан", "вправе", "надлежащ", "уполномочен", "регламент",
        "предусмотрен", "осуществля", "нормативн", "ходатайств",
    ),
    "news": (
        "сообщил", "заявил", "по данным", "президент", "министр", "област",
        "губернатор", "депутат", "агентств", "корреспондент", "происшеств",
        "пресс-служб", "правительств", "парламент", "выбор", "митинг",
        "заседани", "конференци", "соглашени", "делегаци",
    ),
    "social": (
        "подписывайт", "лайк", "репост", "пост", "блог", "сторис",
        "друзь", "подписчик", "коммент", "хэштег", "инстаграм", "телеграм",
        "видео", "канал", "рекоменд", "делит", "выклад",
    ),
    "chat": (
        "привет", "здоров", "как дела", "давай", "слушай", "спасиб",
        "пожалуйста", "ладно", "конечно", "прикин", "чувак", "ага",
        "ок ", "норм", "пока ", "созвон", "напиш", "перезвон",
    ),
}

#: Applied after the lexical vote; breaks ties toward the register that the
#: sentence *shape* supports.
_INFORMAL = re.compile(r"\b(ты|тебя|тебе|твой|твоя|вы|вас|ваш|я|мне|меня|мы|нас)\b", re.IGNORECASE)
_QUOTED_SPEECH = re.compile(r"[\"']|\s—\s")
_YEAR = re.compile(r"\b(1[6-9]\d\d|20[0-2]\d)\b")


def classify_domain(text: str, origin: str) -> tuple[str, str]:
    """``(domain, how)`` where ``how`` is ``"lexis"`` or ``"fallback"``.

    ``how`` is carried into ``meta.json`` so a reader can tell how much of the
    labelling is topical evidence and how much is a register guess.
    """
    low = text.lower().replace("ё", "е")
    scores: Counter[str] = Counter()
    for domain, keys in _DOMAIN_LEXIS.items():
        for k in keys:
            if k.replace("ё", "е") in low:
                scores[domain] += 1
    if origin == "chat":
        scores["chat"] += 1
    if scores:
        best = max(sorted(scores), key=lambda d: (scores[d], -DOMAINS.index(d)))
        return best, "lexis"

    # Structural fallback -- register, not topic.
    ntok = len(text.split())
    has_person = bool(_INFORMAL.search(low))
    stripped = text.rstrip()
    if ntok <= 9 and (stripped.endswith(("?", "!")) or has_person):
        return "chat", "fallback"
    if _YEAR.search(text):
        return "news", "fallback"
    if ntok >= 20:
        return "formal", "fallback"
    if has_person:
        return "social", "fallback"
    if _QUOTED_SPEECH.search(text):
        return "news", "fallback"
    return ("formal" if ntok >= 14 else "news"), "fallback"


# ---------------------------------------------------------------------------
# RAW readers -- streaming, never load a source file whole
# ---------------------------------------------------------------------------


def iter_wiktionary(path: Path, limit: int | None = None) -> Iterator[tuple[str, str]]:
    """Yield ``(sentence, "wiktionary")`` from ``definitions[].examples[]``."""
    if not path.exists():
        return
    n = 0
    with path.open(encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                rec = json.loads(line)
            except json.JSONDecodeError:
                continue
            if rec.get("language") not in (None, "ru"):
                continue
            for d in rec.get("definitions") or ():
                for ex in d.get("examples") or ():
                    if not isinstance(ex, str):
                        continue
                    yield ex, "wiktionary"
                    n += 1
                    if limit is not None and n >= limit:
                        return


def iter_chat(chat_dir: Path, limit: int | None = None) -> Iterator[tuple[str, str]]:
    """Yield ``(sentence, "chat")`` from the ``target`` side of the chat data."""
    if not chat_dir.exists():
        return
    n = 0
    # all.jsonl is the union of the split files; reading it alone avoids
    # counting every sentence four times.
    files = [chat_dir / "all.jsonl"]
    if not files[0].exists():
        files = sorted(chat_dir.glob("*.jsonl"))
    for p in files:
        with p.open(encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                try:
                    rec = json.loads(line)
                except json.JSONDecodeError:
                    continue
                tgt = rec.get("target")
                if isinstance(tgt, str) and tgt:
                    yield tgt, "chat"
                    n += 1
                    if limit is not None and n >= limit:
                        return


# ---------------------------------------------------------------------------
# Contamination check
# ---------------------------------------------------------------------------


def load_benchmark_keys(path: Path) -> set[str]:
    """Every :func:`dedup_key` reachable from the frozen benchmark, if present.

    The benchmark is owned by another pipeline and its exact schema is not
    fixed here, so every plausible text-bearing field is harvested.  Missing
    file -> empty set (the check still runs and is reported).
    """
    keys: set[str] = set()
    if not path.exists():
        return keys
    fields = (
        "source", "target", "text", "sentence", "input", "output",
        "original", "corrected", "correct", "incorrect", "reference",
        "prompt", "expected", "wrong", "right",
    )
    with path.open(encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                rec = json.loads(line)
            except json.JSONDecodeError:
                continue
            if isinstance(rec, str):
                keys.add(dedup_key(normalise(rec)))
                continue
            if not isinstance(rec, dict):
                continue
            for f in fields:
                v = rec.get(f)
                if isinstance(v, str) and v.strip():
                    keys.add(dedup_key(normalise(v)))
                elif isinstance(v, list):
                    for item in v:
                        if isinstance(item, str) and item.strip():
                            keys.add(dedup_key(normalise(item)))
    keys.discard(dedup_key(""))
    return keys


# ---------------------------------------------------------------------------
# Harvest
# ---------------------------------------------------------------------------


@dataclass
class Candidate:
    key: str
    text: str
    origin: str


def harvest(
    args: argparse.Namespace,
    stats: FilterStats,
) -> list[Candidate]:
    """Stream every RAW source, normalise, cheap-filter and dedup."""
    seen: set[str] = set()
    out: list[Candidate] = []
    streams: list[Iterable[tuple[str, str]]] = [
        iter_chat(Path(args.chat_dir), args.limit_raw),
        iter_wiktionary(Path(args.wiktionary), args.limit_raw),
    ]
    for stream in streams:
        for raw, origin in stream:
            stats.seen += 1
            text = normalise(raw)
            if not cheap_filter(
                text, stats, min_tokens=args.min_tokens, max_tokens=args.max_tokens
            ):
                continue
            key = dedup_key(text)
            if key in seen:
                stats.duplicate += 1
                continue
            seen.add(key)
            stats.passed_cheap += 1
            out.append(Candidate(key, text, origin))
    return out


# ---------------------------------------------------------------------------
# Synthesis
# ---------------------------------------------------------------------------


def record_id(split: str, key: str, variant: str) -> str:
    return f"ru-{split[:2]}-{key[:14]}-{variant}"


def is_single_word_substitution(err: dict) -> bool:
    """True when the error swapped exactly one word for exactly one word.

    That is the only shape that makes a clean reranker pair: two sentences of
    the same shape differing in a single token.
    """
    a, b = err["original"], err["replacement"]
    if not a or not b:
        return False
    if " " in a or " " in b:
        return False
    return a.replace("-", "").isalpha() and b.replace("-", "").isalpha()


def build(args: argparse.Namespace) -> dict:
    seed = args.seed
    out_dir = Path(args.out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    stats = FilterStats()

    print("[1/6] harvesting RAW sources ...", flush=True)
    candidates = harvest(args, stats)
    print(f"      unique sentences after cheap filter + dedup: {len(candidates)}", flush=True)

    print("[2/6] deterministic shuffle ...", flush=True)
    candidates.sort(key=lambda c: (stable_seed(seed, "order", c.key), c.key))

    bench_path = Path(args.benchmark)
    bench_keys = load_benchmark_keys(bench_path)
    print(
        f"[3/6] contamination check against {bench_path.name}: "
        f"{'present' if bench_path.exists() else 'ABSENT'}, "
        f"{len(bench_keys)} normalised keys",
        flush=True,
    )

    # Applied to the *whole* candidate pool, not just the prefix we end up
    # using, so the reported number is the true contamination of the source
    # material rather than an artefact of where the quota happened to stop.
    if bench_keys:
        kept = [c for c in candidates if c.key not in bench_keys]
        stats.contaminated = len(candidates) - len(kept)
        candidates = kept
    print(f"      contaminated sentences removed: {stats.contaminated}", flush=True)

    print("[4/6] morphology filter + source selection ...", flush=True)
    accepted: list[Candidate] = []
    want_sources = args.max_sources
    for c in candidates:
        if len(accepted) >= want_sources:
            break
        if not morph_filter(
            c.text,
            stats,
            max_unknown_ratio=args.max_unknown_ratio,
            max_unknown=args.max_unknown,
        ):
            continue
        stats.accepted += 1
        accepted.append(c)
    print(f"      selected source sentences: {len(accepted)}", flush=True)

    # -- split assignment: by position in the deterministic order, so a source
    #    sentence lives in exactly one split and splits never share sources.
    n = len(accepted)
    n_train = int(n * args.split_ratios[0])
    n_val = int(n * args.split_ratios[1])
    split_of: list[str] = (
        ["train"] * n_train
        + ["validation"] * n_val
        + ["test"] * (n - n_train - n_val)
    )

    print("[5/6] synthesising errors ...", flush=True)
    gen = RussianErrorGenerator()
    gen_conf = RussianErrorGenerator(
        ErrorConfig().with_only("confusion_pair"),
        confusion_sets=gen.confusion_sets,
    )

    records: dict[str, list[dict]] = {s: [] for s in SPLITS}
    rerank: dict[str, list[dict]] = {s: [] for s in SPLITS}
    error_types: Counter[str] = Counter()
    error_types_by_split: dict[str, Counter[str]] = {s: Counter() for s in SPLITS}
    categories: Counter[str] = Counter()
    domains: Counter[str] = Counter()
    domains_by_split: dict[str, Counter[str]] = {s: Counter() for s in SPLITS}
    origins: Counter[str] = Counter()
    domain_evidence: Counter[str] = Counter()
    clean_emitted: set[int] = set()
    clean_count = 0
    hard_negative_pairs = 0
    forced_confusions = 0
    failed_corruptions = 0
    verify_failures = 0

    for idx, cand in enumerate(accepted):
        split = split_of[idx]
        domain, domain_how = classify_domain(cand.text, cand.origin)
        rng = random.Random(stable_seed(seed, "role", cand.key))

        def emit_clean(variant: str = "clean") -> dict:
            nonlocal clean_count
            rec = {
                "id": record_id(split, cand.key, variant),
                "source": cand.text,
                "target": cand.text,
                "errors": [],
                "domain": domain,
                "synthetic": False,
            }
            records[split].append(rec)
            clean_emitted.add(idx)
            clean_count += 1
            domains[domain] += 1
            domains_by_split[split][domain] += 1
            domain_evidence[domain_how] += 1
            origins[cand.origin] += 1
            return rec

        if rng.random() < args.clean_only_p:
            emit_clean("clean")
            continue

        # Force a real-word confusion when the sentence actually contains a
        # confusable word -- this is the signal the reranker exists for.
        result = None
        forced = False
        if gen_conf.has_confusable(cand.text) and rng.random() < args.force_confusion_p:
            result = gen_conf.corrupt_n(cand.text, seed=stable_seed(seed, cand.key), n_errors=1)
            forced = bool(result)
        if not result:
            forced = False
            for attempt in range(3):
                result = gen.corrupt(cand.text, seed=stable_seed(seed, cand.key, attempt))
                if result:
                    break
        if not result or not result.changed:
            failed_corruptions += 1
            emit_clean("clean")
            continue
        if forced:
            forced_confusions += 1

        errs = [c.as_record() for c in result]
        # Verify the contract the whole corpus rests on.
        ok = all(result.text[e["start"] : e["end"]] == e["replacement"] for e in errs)
        ok = ok and apply_corrections(result.text, result) == cand.text
        if not ok:
            verify_failures += 1
            emit_clean("clean")
            continue

        rec = {
            "id": record_id(split, cand.key, "err"),
            "source": result.text,
            "target": cand.text,
            "errors": errs,
            "domain": domain,
            "synthetic": True,
        }
        records[split].append(rec)
        domains[domain] += 1
        domains_by_split[split][domain] += 1
        domain_evidence[domain_how] += 1
        origins[cand.origin] += 1
        for c in result:
            error_types[c.error_type] += 1
            error_types_by_split[split][c.error_type] += 1
            categories[c.category] += 1

        has_confusion = any(c.error_type == "confusion_pair" for c in result)

        # -- rerank view: only single-word substitutions give a clean pair.
        if len(errs) == 1 and is_single_word_substitution(errs[0]):
            etype = errs[0]["type"]
            cat = result[0].category
            base = rec["id"]
            rerank[split].append(
                {
                    "id": base + "-pos",
                    "sentence": cand.text,
                    "label": 1,
                    "pair_id": base,
                    "error_type": etype,
                    "category": cat,
                    "domain": domain,
                    "split": split,
                }
            )
            rerank[split].append(
                {
                    "id": base + "-neg",
                    "sentence": result.text,
                    "label": 0,
                    "pair_id": base,
                    "error_type": etype,
                    "category": cat,
                    "domain": domain,
                    "split": split,
                }
            )

        # -- hard negatives: every real-word confusion must also appear as the
        #    correct sentence, so only context can tell them apart.
        if has_confusion:
            emit_clean("hn")
            hard_negative_pairs += 1
        elif rng.random() < args.clean_twin_p:
            emit_clean("twin")

    # -- top up clean records if the 35% floor was not reached -----------------
    #    Only sentences that do not already contribute a clean record are used,
    #    so no sentence is ever duplicated verbatim within a split.
    total = sum(len(v) for v in records.values())
    floor = args.min_clean_fraction
    topped_up = 0
    if total and floor < 1.0 and clean_count / total < floor:
        # solve (clean + k) / (total + k) >= floor
        needed = math.ceil((floor * total - clean_count) / (1.0 - floor))
        for i, cand in enumerate(accepted):
            if topped_up >= needed:
                break
            if i in clean_emitted:
                continue
            split = split_of[i]
            domain, domain_how = classify_domain(cand.text, cand.origin)
            records[split].append(
                {
                    "id": record_id(split, cand.key, "top"),
                    "source": cand.text,
                    "target": cand.text,
                    "errors": [],
                    "domain": domain,
                    "synthetic": False,
                }
            )
            clean_emitted.add(i)
            clean_count += 1
            topped_up += 1
            domains[domain] += 1
            domains_by_split[split][domain] += 1
            domain_evidence[domain_how] += 1
            origins[cand.origin] += 1

    print("[6/6] writing outputs ...", flush=True)
    sha = {}
    for split in SPLITS:
        p = out_dir / f"{split}.jsonl"
        sha[p.name] = write_jsonl(p, records[split])
    for split in SPLITS:
        p = out_dir / f"rerank_{split}.jsonl"
        sha[p.name] = write_jsonl(p, rerank[split])

    total = sum(len(v) for v in records.values())
    rerank_total = sum(len(v) for v in rerank.values())
    rerank_labels = Counter(r["label"] for v in rerank.values() for r in v)
    rerank_types = Counter(r["error_type"] for v in rerank.values() for r in v if r["label"] == 0)
    rerank_cats = Counter(r["category"] for v in rerank.values() for r in v if r["label"] == 0)

    meta = {
        "name": "writelite-ru-error-corpus",
        "builder_version": BUILDER_VERSION,
        "generator_version": GENERATOR_VERSION,
        "seed": seed,
        "sources": {
            "wiktionary": str(Path(args.wiktionary).relative_to(_REPO)) if Path(args.wiktionary).exists() else None,
            "chat": str(Path(args.chat_dir).relative_to(_REPO)),
            "confusion_sets": len(gen.confusion_sets),
            "records_by_origin": dict(sorted(origins.items())),
        },
        "counts": {
            "total": total,
            "by_split": {s: len(records[s]) for s in SPLITS},
            "source_sentences": len(accepted),
            "source_sentences_by_split": {
                s: sum(1 for x in split_of if x == s) for s in SPLITS
            },
        },
        "clean": {
            "records": clean_count,
            "fraction": round(clean_count / total, 6) if total else 0.0,
            "min_required": floor,
        },
        "errors": {
            "total": sum(error_types.values()),
            "by_type": dict(sorted(error_types.items())),
            "by_type_by_split": {
                s: dict(sorted(error_types_by_split[s].items())) for s in SPLITS
            },
            "by_category": dict(sorted(categories.items())),
            "real_word": categories.get("real_word", 0),
            "non_word": categories.get("non_word", 0),
            "not_lexical": categories.get("not_lexical", 0),
        },
        "domains": {
            "labels": list(DOMAINS),
            "total": dict(sorted(domains.items())),
            "by_split": {s: dict(sorted(domains_by_split[s].items())) for s in SPLITS},
            # How the label was reached: "lexis" = topical keywords matched,
            # "fallback" = register heuristic on sentence shape.
            "evidence": dict(sorted(domain_evidence.items())),
        },
        "hard_negatives": {
            "confusion_hard_negative_pairs": hard_negative_pairs,
            "forced_confusion_corruptions": forced_confusions,
        },
        "rerank": {
            "total": rerank_total,
            "by_split": {s: len(rerank[s]) for s in SPLITS},
            "label_counts": {str(k): v for k, v in sorted(rerank_labels.items())},
            "negative_by_error_type": dict(sorted(rerank_types.items())),
            "negative_by_category": dict(sorted(rerank_cats.items())),
        },
        "contamination": {
            "benchmark_path": str(bench_path.relative_to(_REPO)),
            "benchmark_present": bench_path.exists(),
            "benchmark_keys": len(bench_keys),
            "removed": stats.contaminated,
        },
        "filter_stats": stats.as_dict(),
        "quality_thresholds": {
            "min_tokens": args.min_tokens,
            "max_tokens": args.max_tokens,
            "min_cyrillic_letter_ratio": 0.90,
            "max_unknown_ratio": args.max_unknown_ratio,
            "max_unknown_tokens": args.max_unknown,
        },
        "synthesis": {
            "clean_only_p": args.clean_only_p,
            "clean_twin_p": args.clean_twin_p,
            "force_confusion_p": args.force_confusion_p,
            "failed_corruptions": failed_corruptions,
            "verify_failures": verify_failures,
            "clean_records_topped_up": topped_up,
        },
        "split_ratios": list(args.split_ratios),
        "sha256": sha,
        "record_schema": {
            "id": "str",
            "source": "str -- model input (may contain errors)",
            "target": "str -- correct sentence",
            "errors": "[{start,end,original,replacement,type}] -- spans index `source`",
            "domain": f"one of {list(DOMAINS)}",
            "synthetic": "bool -- True when errors were synthesised",
        },
        "rerank_schema": {
            "id": "str",
            "sentence": "str",
            "label": "1 = correct Russian sentence, 0 = one wrong word substituted",
            "pair_id": "str -- links the two members of a minimal pair",
            "error_type": "str",
            "category": "real_word | non_word | not_lexical",
            "domain": "str",
            "split": "str",
        },
    }
    meta_path = out_dir / "meta.json"
    meta_path.write_text(
        json.dumps(meta, ensure_ascii=False, indent=2, sort_keys=False) + "\n",
        encoding="utf-8",
    )
    return meta


def write_jsonl(path: Path, rows: Sequence[dict]) -> str:
    """Write ``rows`` and return the sha256 of the resulting bytes."""
    h = hashlib.sha256()
    with path.open("w", encoding="utf-8", newline="\n") as fh:
        for r in rows:
            line = json.dumps(r, ensure_ascii=False) + "\n"
            fh.write(line)
            h.update(line.encode("utf-8"))
    return h.hexdigest()


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--seed", type=int, default=20260810)
    p.add_argument("--wiktionary", default=str(DEFAULT_WIKTIONARY))
    p.add_argument("--chat-dir", default=str(DEFAULT_CHAT_DIR))
    p.add_argument("--benchmark", default=str(DEFAULT_BENCHMARK))
    p.add_argument("--out-dir", default=str(DEFAULT_OUT_DIR))
    p.add_argument("--max-sources", type=int, default=22000,
                   help="unique clean sentences to draw on (records come out higher)")
    p.add_argument("--limit-raw", type=int, default=None,
                   help="stop after N raw items per source (smoke tests)")
    p.add_argument("--min-tokens", type=int, default=4)
    p.add_argument("--max-tokens", type=int, default=30)
    p.add_argument("--max-unknown-ratio", type=float, default=0.15)
    p.add_argument("--max-unknown", type=int, default=2)
    p.add_argument("--clean-only-p", type=float, default=0.30,
                   help="P(a source sentence is kept clean and never corrupted)")
    p.add_argument("--clean-twin-p", type=float, default=0.22,
                   help="P(a non-confusion corruption also gets a clean twin)")
    p.add_argument("--force-confusion-p", type=float, default=0.55,
                   help="P(force a real-word confusion when the sentence allows one)")
    p.add_argument("--min-clean-fraction", type=float, default=0.35)
    p.add_argument("--split-ratios", type=float, nargs=3, default=list(DEFAULT_SPLIT_RATIOS))
    return p.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_args(argv)
    meta = build(args)
    c = meta["counts"]
    print("\n=== ru error corpus ===")
    print(f"records              {c['total']}  {c['by_split']}")
    print(f"source sentences     {c['source_sentences']}")
    print(f"clean fraction       {meta['clean']['fraction']:.4f}  ({meta['clean']['records']} records)")
    print(f"errors               {meta['errors']['total']}  "
          f"real_word={meta['errors']['real_word']} non_word={meta['errors']['non_word']} "
          f"not_lexical={meta['errors']['not_lexical']}")
    print(f"rerank               {meta['rerank']['total']}  {meta['rerank']['by_split']}  "
          f"labels={meta['rerank']['label_counts']}")
    print(f"contamination        removed={meta['contamination']['removed']} "
          f"(benchmark_present={meta['contamination']['benchmark_present']})")
    print(f"domains              {meta['domains']['total']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
