#!/usr/bin/env python3
"""Wikitext helpers for the Russian Wiktionary lexical extractor.

Kept separate from the extractor so the cleaning rules can be unit-tested
without touching a 334 MB dump.  Nothing here performs I/O.

Rules were derived by inspecting real ru.wiktionary pages (дом, быстрый,
бежать) rather than assumed; see tests/test_wikitext_ru.py for the fixtures.
"""
from __future__ import annotations

import re

# Register / usage labels ruwiktionary places as a leading template on a
# definition line, mapped to the short label shown in the WriteLite card.
REGISTER_LABELS: dict[str, str] = {
    "п.": "перен.",
    "перен.": "перен.",
    "разг.": "разг.",
    "прост.": "прост.",
    "книжн.": "книжн.",
    "устар.": "устар.",
    "истор.": "истор.",
    "поэт.": "поэт.",
    "офиц.": "офиц.",
    "спец.": "спец.",
    "науч.": "науч.",
    "техн.": "техн.",
    "ирон.": "ирон.",
    "шутл.": "шутл.",
    "неодобр.": "неодобр.",
    "пренебр.": "пренебр.",
    "груб.": "груб.",
    "бран.": "бран.",
    "фам.": "фам.",
    "диал.": "диал.",
    "рег.": "рег.",
    "жарг.": "жарг.",
    "сленг": "сленг",
    "детск.": "детск.",
    "редк.": "редк.",
    "высок.": "высок.",
    "сниж.": "сниж.",
    "ласк.": "ласк.",
    "уменьш.": "уменьш.",
    "трад.-нар.": "трад.-нар.",
    "церк.": "церк.",
    # Topic labels.
    "спорт.": "спорт.",
    "мед.": "мед.",
    "биол.": "биол.",
    "бот.": "бот.",
    "зоол.": "зоол.",
    "хим.": "хим.",
    "физ.": "физ.",
    "матем.": "матем.",
    "лингв.": "лингв.",
    "юр.": "юр.",
    "комп.": "комп.",
    "воен.": "воен.",
    "муз.": "муз.",
    "рел.": "рел.",
    "геол.": "геол.",
    "геогр.": "геогр.",
    "экон.": "экон.",
    "фин.": "фин.",
    "полит.": "полит.",
    "анат.": "анат.",
    "астрон.": "астрон.",
    "кулин.": "кулин.",
    "мор.": "мор.",
    "авиац.": "авиац.",
    "техн.-строит.": "строит.",
    "строит.": "строит.",
    "с.-х.": "с.-х.",
    "филос.": "филос.",
    "психол.": "психол.",
    "лит.": "лит.",
    "театр.": "театр.",
    "карт.": "карт.",
    "охотн.": "охотн.",
    "рыбол.": "рыбол.",
    "текст.": "текст.",
    "эл.-техн.": "эл.-техн.",
    "физиол.": "физиол.",
}

# Templates whose visible content must survive stripping.
KEEP_CONTENT_TEMPLATES = {
    "выдел", "курсив", "разрядка", "подчёркивание", "подчеркивание",
    "напр.", "итал", "прямо",
}

# Part-of-speech templates in "Морфологические и синтаксические свойства",
# mapped onto the POS vocabulary LexicalPackLoader.ParsePos accepts.
# Order matters: longer/more specific prefixes first.
POS_TEMPLATE_PREFIXES: tuple[tuple[str, str], ...] = (
    ("сущ", "noun"),
    ("гл", "verb"),
    ("прич", "adj"),
    ("деепр", "adv"),
    ("прил", "adj"),
    ("нареч", "adv"),
    ("предик", "adv"),
    ("мест", "pron"),
    ("числ", "num"),
    ("предл", "prep"),
    ("союз", "conj"),
    ("част", "particle"),
    ("межд", "interj"),
    ("вводн", "adv"),
    ("падежи", "noun"),
)

# Lines that mean "no data" rather than a real sense.
EMPTY_MARKERS = {"", "-", "—", "–", "?", "??", "…", "...", "&mdash;"}

_RE_COMMENT = re.compile(r"<!--.*?-->", re.DOTALL)
_RE_REF = re.compile(r"<ref[^>]*>.*?</ref>|<ref[^>]*/>", re.DOTALL | re.IGNORECASE)
_RE_TAG = re.compile(r"</?[a-zA-Z][^>]*>")
_RE_LINK = re.compile(r"\[\[([^\[\]|]+)(?:\|([^\[\]]*))?\]\]")
_RE_EXT_LINK = re.compile(r"\[(?:https?|ftp)://[^\s\]]+\s*([^\]]*)\]")
_RE_QUOTES = re.compile(r"'{2,5}")
_RE_WS = re.compile(r"\s+")
_RE_HEADING = re.compile(r"^(={1,6})\s*(.*?)\s*\1\s*$", re.MULTILINE)
_RE_LANG_SECTION = re.compile(r"^=\s*\{\{-([a-z-]+)-\}\}\s*=\s*$", re.MULTILINE)


def find_templates(text: str, start: int = 0) -> list[tuple[int, int, str]]:
    """Return (start, end, inner) for every balanced top-level ``{{...}}``.

    Nested templates stay inside ``inner`` rather than being returned separately.
    """
    out: list[tuple[int, int, str]] = []
    i, n = start, len(text)
    while i < n - 1:
        if text[i] == "{" and text[i + 1] == "{":
            depth, j = 1, i + 2
            while j < n - 1 and depth:
                if text[j] == "{" and text[j + 1] == "{":
                    depth += 1
                    j += 2
                elif text[j] == "}" and text[j + 1] == "}":
                    depth -= 1
                    j += 2
                else:
                    j += 1
            if depth == 0:
                out.append((i, j, text[i + 2 : j - 2]))
                i = j
                continue
        i += 1
    return out


def split_params(inner: str) -> list[str]:
    """Split template contents on top-level ``|`` only."""
    parts: list[str] = []
    depth_t = depth_l = 0
    buf: list[str] = []
    i, n = 0, len(inner)
    while i < n:
        two = inner[i : i + 2]
        if two in ("{{", "[["):
            (depth_t, depth_l) = (depth_t + 1, depth_l) if two == "{{" else (depth_t, depth_l + 1)
            buf.append(two)
            i += 2
            continue
        if two in ("}}", "]]"):
            (depth_t, depth_l) = (depth_t - 1, depth_l) if two == "}}" else (depth_t, depth_l - 1)
            buf.append(two)
            i += 2
            continue
        ch = inner[i]
        if ch == "|" and depth_t <= 0 and depth_l <= 0:
            parts.append("".join(buf))
            buf = []
        else:
            buf.append(ch)
        i += 1
    parts.append("".join(buf))
    return parts


def template_name(inner: str) -> str:
    return split_params(inner)[0].strip()


def positional_params(inner: str) -> list[str]:
    """Positional (non ``key=value``) parameters after the template name."""
    out: list[str] = []
    for param in split_params(inner)[1:]:
        head = param.split("[[")[0].split("{{")[0]
        if re.match(r"^\s*[A-Za-zА-Яа-я0-9_ .-]{1,20}\s*=", head):
            continue
        out.append(param)
    return out


def _expand_templates(text: str, depth: int = 0) -> str:
    """Replace templates: keep the content of highlight templates, drop the rest."""
    if depth > 6:
        return text
    spans = find_templates(text)
    if not spans:
        return text
    out: list[str] = []
    prev = 0
    for start, end, inner in spans:
        out.append(text[prev:start])
        name = template_name(inner).lower()
        if name in ("-", "—"):
            out.append(" — ")
        elif name == "=":
            out.append("=")
        elif name in KEEP_CONTENT_TEMPLATES:
            params = positional_params(inner)
            if params:
                out.append(_expand_templates(params[0], depth + 1))
        prev = end
    out.append(text[prev:])
    return "".join(out)


def strip_markup(text: str) -> str:
    """Reduce wikitext to plain readable Russian text."""
    text = _RE_COMMENT.sub("", text)
    text = _RE_REF.sub("", text)
    text = _expand_templates(text)
    text = _RE_EXT_LINK.sub(r"\1", text)
    text = _RE_LINK.sub(lambda m: (m.group(2) or m.group(1)), text)
    text = _RE_TAG.sub("", text)
    text = _RE_QUOTES.sub("", text)
    text = (text.replace("&nbsp;", " ").replace("&amp;", "&")
                .replace("&quot;", '"').replace("&mdash;", "—"))
    text = _RE_WS.sub(" ", text)
    return text.strip(" \t.,;:·—–-")


def is_empty_sense(text: str) -> bool:
    return strip_markup(text).strip().lower() in EMPTY_MARKERS


def extract_examples(line: str, limit: int = 2, min_length: int = 12) -> list[str]:
    """Pull the first positional argument out of each ``{{пример|...}}``."""
    found: list[str] = []
    for _, _, inner in find_templates(line):
        if template_name(inner).lower() != "пример":
            continue
        params = positional_params(inner)
        if not params:
            continue
        text = strip_markup(params[0])
        if len(text) >= min_length and text not in found:
            found.append(text)
        if len(found) >= limit:
            break
    return found


def extract_label(line: str) -> str | None:
    """Leading register/topic templates on a definition line."""
    rest = line.lstrip("# ").lstrip()
    labels: list[str] = []
    while rest.startswith("{{"):
        spans = find_templates(rest)
        if not spans or spans[0][0] != 0:
            break
        _, end, inner = spans[0]
        name = template_name(inner).lower()
        if name in REGISTER_LABELS:
            labels.append(REGISTER_LABELS[name])
        elif name == "помета":
            params = positional_params(inner)
            text = strip_markup(params[0]) if params else ""
            if text and len(text) <= 40:
                labels.append(text)
        else:
            break
        rest = rest[end:].lstrip()
    return ", ".join(dict.fromkeys(labels)) if labels else None


def parse_pos(section_text: str) -> str | None:
    """Map the morphology template of a Russian entry onto a POS token."""
    for _, _, inner in find_templates(section_text):
        name = template_name(inner).lower()
        base = name.split("|")[0].strip()
        for prefix, pos in POS_TEMPLATE_PREFIXES:
            if base == prefix or base.startswith(prefix + " ") or base.startswith(prefix + "-"):
                return pos
    return None


def split_sense_links(line: str, limit: int = 8) -> list[str]:
    """Split a synonym/antonym list line into individual lexemes."""
    text = strip_markup(line.lstrip("# "))
    if text.lower() in EMPTY_MARKERS:
        return []
    out: list[str] = []
    for part in re.split(r"[,;]", text):
        candidate = part.strip(" \t.·—–-")
        if not candidate or candidate.lower() in EMPTY_MARKERS:
            continue
        if len(candidate) > 48 or len(candidate.split()) > 3:
            continue
        if candidate not in out:
            out.append(candidate)
        if len(out) >= limit:
            break
    return out


def language_section(text: str, code: str = "ru") -> str | None:
    """Return only the ``= {{-ru-}} =`` part of a multi-language page."""
    matches = list(_RE_LANG_SECTION.finditer(text))
    for index, match in enumerate(matches):
        if match.group(1) == code:
            end = matches[index + 1].start() if index + 1 < len(matches) else len(text)
            return text[match.end() : end]
    return None


def iter_sections(text: str) -> list[tuple[int, str, str]]:
    """Return (level, title, body) for every heading in the text."""
    matches = list(_RE_HEADING.finditer(text))
    sections: list[tuple[int, str, str]] = []
    for index, match in enumerate(matches):
        end = matches[index + 1].start() if index + 1 < len(matches) else len(text)
        sections.append((len(match.group(1)), match.group(2).strip(), text[match.end() : end]))
    return sections


def numbered_lines(body: str) -> list[str]:
    """The ``#`` list items of a section, excluding ``#:`` / ``#*`` sub-items."""
    out: list[str] = []
    for raw in body.splitlines():
        line = raw.strip()
        if not line.startswith("#") or line.startswith(("#:", "#*", "##")):
            continue
        out.append(line)
    return out
