"""Deterministic synthetic error generator with fixed seed."""

from __future__ import annotations

import random
import re
from dataclasses import dataclass, field
from typing import Callable


@dataclass
class AppliedError:
    error_id: str
    error_type: str
    original_span: str
    corrupted_span: str


@dataclass
class ErrorTransform:
    error_id: str
    language: str
    error_type: str
    probability: float
    enabled: bool = True
    apply: Callable[[str, random.Random], tuple[str, AppliedError | None]] | None = None


def _swap_adjacent(text: str, rng: random.Random) -> tuple[str, AppliedError | None]:
    letters = [i for i, ch in enumerate(text) if ch.isalpha()]
    if len(letters) < 2:
        return text, None
    i = rng.choice(letters[:-1])
    j = i + 1
    if not text[j].isalpha():
        return text, None
    chars = list(text)
    chars[i], chars[j] = chars[j], chars[i]
    new = "".join(chars)
    return new, AppliedError("swap_adjacent", "typo", text[i : j + 1], new[i : j + 1])


def _drop_char(text: str, rng: random.Random) -> tuple[str, AppliedError | None]:
    letters = [i for i, ch in enumerate(text) if ch.isalpha()]
    if len(letters) < 4:
        return text, None
    i = rng.choice(letters[1:-1])
    new = text[:i] + text[i + 1 :]
    return new, AppliedError("drop_char", "typo", text[i], "")


def _duplicate_char(text: str, rng: random.Random) -> tuple[str, AppliedError | None]:
    letters = [i for i, ch in enumerate(text) if ch.isalpha()]
    if not letters:
        return text, None
    i = rng.choice(letters)
    new = text[: i + 1] + text[i] + text[i + 1 :]
    return new, AppliedError("dup_char", "typo", text[i], text[i] * 2)


def _strip_punctuation(text: str, rng: random.Random) -> tuple[str, AppliedError | None]:
    if not any(ch in text for ch in ",.!?;:"):
        return text, None
    new = re.sub(r"[,.!?;:]+", "", text)
    new = re.sub(r"\s{2,}", " ", new).strip()
    if new == text:
        return text, None
    return new, AppliedError("strip_punct", "punctuation", text, new)


def _lower_first(text: str, rng: random.Random) -> tuple[str, AppliedError | None]:
    if not text or not text[0].isalpha() or not text[0].isupper():
        return text, None
    new = text[0].lower() + text[1:]
    return new, AppliedError("lower_first", "capitalization", text[0], new[0])


def _glue_ne(text: str, rng: random.Random) -> tuple[str, AppliedError | None]:
    m = re.search(r"\bне\s+(\w+)", text, flags=re.IGNORECASE)
    if not m:
        return text, None
    glued = m.group(0).replace(" ", "")
    new = text[: m.start()] + glued + text[m.end() :]
    return new, AppliedError("glue_ne", "spelling", m.group(0), glued)


def _ru_common_misspell(text: str, rng: random.Random) -> tuple[str, AppliedError | None]:
    pairs = [
        ("здравствуйте", "здраствуйте"),
        ("извините", "извените"),
        ("в общем", "вообщем"),
        ("потому что", "потомучто"),
        ("участвовал", "учавствовал"),
    ]
    rng.shuffle(pairs)
    for good, bad in pairs:
        if good in text.lower():
            # preserve case roughly
            idx = text.lower().find(good)
            new = text[:idx] + bad + text[idx + len(good) :]
            return new, AppliedError("ru_misspell", "spelling", good, bad)
    return text, None


def _en_common_misspell(text: str, rng: random.Random) -> tuple[str, AppliedError | None]:
    pairs = [
        ("receive", "recieve"),
        ("separate", "seperate"),
        ("definitely", "definately"),
        ("because", "becuase"),
        ("which", "wich"),
        ("tomorrow", "tommorow"),
        ("environment", "enviroment"),
        ("success", "sucess"),
        ("believe", "beleive"),
    ]
    rng.shuffle(pairs)
    lower = text.lower()
    for good, bad in pairs:
        if good in lower:
            idx = lower.find(good)
            new = text[:idx] + bad + text[idx + len(good) :]
            return new, AppliedError("en_misspell", "spelling", good, bad)
    return text, None


def _en_agreement(text: str, rng: random.Random) -> tuple[str, AppliedError | None]:
    pairs = [
        (r"\bI have\b", "I has"),
        (r"\bhe has\b", "he have"),
        (r"\bshe has\b", "she have"),
        (r"\bit works\b", "it work"),
        (r"\bworks well\b", "work good"),
    ]
    rng.shuffle(pairs)
    for pat, bad in pairs:
        m = re.search(pat, text, flags=re.IGNORECASE)
        if m:
            new = text[: m.start()] + bad + text[m.end() :]
            return new, AppliedError("en_agreement", "agreement", m.group(0), bad)
    return text, None


def _duplicate_word(text: str, rng: random.Random) -> tuple[str, AppliedError | None]:
    words = text.split()
    if len(words) < 2:
        return text, None
    i = rng.randrange(len(words))
    words.insert(i, words[i])
    new = " ".join(words)
    return new, AppliedError("dup_word", "repetition", words[i], words[i] + " " + words[i])


def _space_before_punct(text: str, rng: random.Random) -> tuple[str, AppliedError | None]:
    new = re.sub(r"([,.!?])", r" \1", text)
    new = re.sub(r"\s{2,}", " ", new)
    if new == text:
        return text, None
    return new, AppliedError("space_punct", "spacing", text, new)


def default_transforms() -> list[ErrorTransform]:
    return [
        ErrorTransform("swap_adjacent", "any", "typo", 0.35, True, _swap_adjacent),
        ErrorTransform("drop_char", "any", "typo", 0.25, True, _drop_char),
        ErrorTransform("dup_char", "any", "typo", 0.2, True, _duplicate_char),
        ErrorTransform("strip_punct", "any", "punctuation", 0.55, True, _strip_punctuation),
        ErrorTransform("lower_first", "any", "capitalization", 0.5, True, _lower_first),
        ErrorTransform("glue_ne", "ru", "spelling", 0.6, True, _glue_ne),
        ErrorTransform("ru_misspell", "ru", "spelling", 0.55, True, _ru_common_misspell),
        ErrorTransform("en_misspell", "en", "spelling", 0.55, True, _en_common_misspell),
        ErrorTransform("en_agreement", "en", "agreement", 0.45, True, _en_agreement),
        ErrorTransform("dup_word", "any", "repetition", 0.2, True, _duplicate_word),
        ErrorTransform("space_punct", "any", "spacing", 0.25, True, _space_before_punct),
    ]


def corrupt_text(
    text: str,
    language: str,
    rng: random.Random,
    transforms: list[ErrorTransform] | None = None,
    max_errors: int = 3,
) -> tuple[str, list[AppliedError]]:
    transforms = transforms or default_transforms()
    applied: list[AppliedError] = []
    current = text
    # Do not corrupt protected-looking tokens aggressively: skip if mostly URL/path.
    if re.search(r"https?://|@|\\", text):
        max_errors = min(max_errors, 1)

    candidates = [
        t
        for t in transforms
        if t.enabled and t.apply is not None and t.language in ("any", language)
    ]
    rng.shuffle(candidates)
    for t in candidates:
        if len(applied) >= max_errors:
            break
        if rng.random() > t.probability:
            continue
        new, info = t.apply(current, rng)
        if info is not None and new != current:
            current = new
            applied.append(info)
    return current, applied
