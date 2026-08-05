"""Build ≥10k WriteLite-Qwen chat instruction examples (RU/EN + protect)."""

from __future__ import annotations

import hashlib
import json
import random
import re
from pathlib import Path
from typing import Any, Iterable

from .errors import corrupt_text, default_transforms
from .prompts import MODEL_VERSION_PLACEHOLDER, make_chat_example


# Expanded clean templates — realistic short messages / sentences.
RU_TEMPLATES = [
    "Привет, как у тебя дела?",
    "Я сегодня не был в школе, потому что заболел.",
    "Когда я пришёл домой, мама уже приготовила ужин.",
    "Я думаю, что это хороший вариант, но нужно проверить.",
    "Вы не нашли школу в реестре, потому что он неправильный.",
    "Конечно, мы можем обсудить детали завтра.",
    "Если будет время, зайди в магазин.",
    "Здравствуйте, подскажите, когда будет готов заказ?",
    "В общем, это очень длинное предложение, которое нужно проверить.",
    "Спасибо большое за быстрый ответ.",
    "Потому что погода испортилась, мы остались дома.",
    "Он участвовал в конференции в Москве.",
    "Сегодня хорошая погода.",
    "Напиши мне, пожалуйста, как только освободишься.",
    "Мне кажется, что решение уже найдено.",
    "К сожалению, я не смогу прийти вовремя.",
    "Однако результаты оказались лучше ожиданий.",
    "Чтобы успеть, нужно выйти раньше.",
    "Несмотря на дождь, мы продолжили работу.",
    "Скажи, пожалуйста, где находится ближайшая станция?",
    "Я хотел узнать, когда вы сможете отправить заказ.",
    "Это интересный проект, и я готов помочь.",
    "Мы встретимся в понедельник в три часа.",
    "Давай обсудим это после обеда.",
    "Он сказал, что придёт позже.",
    "Я не знаю, что делать дальше.",
    "Пожалуйста, проверь файл перед отправкой.",
    "Вчера мы долго ждали автобус.",
    "Завтра будет важная встреча с клиентом.",
    "Можешь перезвонить через пять минут?",
    "Я уже сделал домашнее задание.",
    "Она работает в большой компании.",
    "Нам нужно больше времени на подготовку.",
    "Это было очень приятное утро.",
    "Почему ты не ответил на сообщение?",
    "Где ты купил этот телефон?",
    "Сколько это будет стоить примерно?",
    "Я забыл ключи дома.",
    "Они уехали в другую страну.",
    "Мы должны закончить отчёт сегодня.",
    "Подождите, я сейчас подойду.",
    "Извините, я опоздал на пять минут.",
    "Здравствуйте! Как ваши дела?",
    "Добрый день, я хотел бы уточнить детали.",
    "Вечером будет холодно, возьми куртку.",
    "Если хочешь, можем встретиться у метро.",
    "Я прочитал книгу, которую ты рекомендовал.",
    "Он всегда приходит вовремя.",
    "Мы уже почти закончили работу.",
    "Это не то, что я ожидал увидеть.",
]

EN_TEMPLATES = [
    "Hello, how are you today?",
    "I have a new computer and it works well.",
    "She has already finished the report.",
    "Where are you going? I don't know.",
    "They have completed the assignment on time.",
    "I believe we should separate the concerns carefully.",
    "Definitely schedule the meeting for tomorrow.",
    "An apple a day keeps the doctor away.",
    "Because of the rain, the event was postponed.",
    "Which option do you prefer?",
    "The environment is important for everyone.",
    "Success requires consistent practice.",
    "Please send me the document when you can.",
    "He doesn't work on weekends.",
    "We are going to the store later.",
    "I think this is a good idea, but we need to check.",
    "Could you open the window, please?",
    "There is a problem with the configuration.",
    "She was reading a book when I arrived.",
    "They were happy to see their friends.",
    "I received your email yesterday.",
    "Do you know where the nearest station is?",
    "We should leave earlier to avoid traffic.",
    "This project needs more careful review.",
    "I don't know what to do next.",
    "Please call me in five minutes.",
    "He always arrives on time.",
    "We almost finished the work already.",
    "That is not what I expected to see.",
    "Why didn't you answer the message?",
    "How much will it cost approximately?",
    "I forgot my keys at home.",
    "They moved to another country.",
    "We must finish the report today.",
    "Wait a moment, I will be right there.",
    "Sorry, I am five minutes late.",
    "Good afternoon, I would like to clarify the details.",
    "It will be cold in the evening; take a jacket.",
    "If you want, we can meet near the subway.",
    "I read the book you recommended.",
    "There are three options available.",
    "She works for a large company.",
    "We need more time for preparation.",
    "It was a very pleasant morning.",
    "Please check the file before sending.",
    "Yesterday we waited a long time for the bus.",
    "Tomorrow there will be an important client meeting.",
    "Can you call me back later?",
    "I already completed the homework.",
    "This is an interesting project, and I am ready to help.",
]

RU_NAMES = ["Аня", "Иван", "Мария", "Сергей", "Оля", "Дмитрий", "Катя", "Павел"]
EN_NAMES = ["Anna", "John", "Maria", "Serge", "Olga", "Alex", "Kate", "Paul"]
PRODUCTS = ["WriteLite", "WriteLite 2.0", "Telegram", "Notepad", "Excel", "Discord"]
SLANG_RU = [
    "короче норм идея",
    "щас приду",
    "лол это жесть",
    "имхо так лучше",
    "ок давай завтра",
]
SLANG_EN = [
    "lol that was wild",
    "gonna finish later",
    "tbh not sure",
    "idk maybe tomorrow",
    "ok cool thanks",
]


def _fp(*parts: str) -> str:
    h = hashlib.sha256()
    for p in parts:
        h.update(p.encode("utf-8"))
        h.update(b"\0")
    return h.hexdigest()[:16]


def _load_seed_clean(seed_dir: Path) -> list[dict[str, str]]:
    rows: list[dict[str, str]] = []
    for name in ("ru_clean.jsonl", "en_clean.jsonl"):
        path = seed_dir / name
        if not path.exists():
            continue
        for line in path.read_text(encoding="utf-8").splitlines():
            if not line.strip():
                continue
            obj = json.loads(line)
            rows.append({"language": obj.get("language", "und"), "text": obj["text"]})
    return rows


def _generate_clean_pool(rng: random.Random) -> list[dict[str, str]]:
    pool: list[dict[str, str]] = []
    for t in RU_TEMPLATES:
        pool.append({"language": "ru", "text": t})
    for t in EN_TEMPLATES:
        pool.append({"language": "en", "text": t})

    # Combinatorial expansions for volume + variety.
    for i in range(400):
        name = rng.choice(RU_NAMES)
        product = rng.choice(PRODUCTS)
        a = rng.choice(RU_TEMPLATES)
        b = rng.choice(RU_TEMPLATES)
        if a == b:
            b = rng.choice(RU_TEMPLATES)
        pool.append({"language": "ru", "text": f"{name}, {a[0].lower() + a[1:]}"})
        pool.append({"language": "ru", "text": f"{a} {product} помогает проверить текст."})
        pool.append({"language": "ru", "text": f"{a[:-1] if a.endswith('.') else a}. {b}"})

    for i in range(400):
        name = rng.choice(EN_NAMES)
        product = rng.choice(PRODUCTS)
        a = rng.choice(EN_TEMPLATES)
        b = rng.choice(EN_TEMPLATES)
        pool.append({"language": "en", "text": f"{name}, {a[0].lower() + a[1:]}"})
        pool.append({"language": "en", "text": f"{a} {product} helps check writing."})
        pool.append({"language": "en", "text": f"{a.rstrip('.')} Then {b[0].lower() + b[1:]}"})

    for s in SLANG_RU:
        pool.append({"language": "ru", "text": s})
    for s in SLANG_EN:
        pool.append({"language": "en", "text": s})

    return pool


def _protected_examples(rng: random.Random) -> list[dict[str, str]]:
    examples = [
        {"language": "ru", "text": "Открой E:\\Programming\\DProjects\\WriteLite-Starter"},
        {"language": "ru", "text": "Напиши на example@example.com и открой https://example.com?id=123"},
        {"language": "ru", "text": "Это WriteLite 2.0, не меняй название продукта."},
        {"language": "en", "text": "Visit https://example.com/test?id=123 for details."},
        {"language": "en", "text": "Please email me at example@example.com."},
        {"language": "en", "text": "Open the file C:\\Users\\Test\\Documents\\report.txt"},
        {"language": "en", "text": "Server IP is 192.168.1.42 and port 8080."},
        {"language": "ru", "text": "Дата встречи: 2026-07-11, версия 1.2.3."},
        {"language": "en", "text": "Run command: git status && dotnet build"},
        {"language": "ru", "text": 'JSON: {"ok": true, "count": 12}'},
        {"language": "en", "text": "Nickname @cool_gamer42 joined the lobby."},
        {"language": "ru", "text": "Ник soft_dev_99 уже занят."},
        {"language": "en", "text": "Use `LocalAiTextAnalyzer` in the app."},
        {"language": "ru", "text": "Путь: /home/user/projects/writelight/README.md"},
        {"language": "mixed", "text": "WriteLite API key is not needed for local mode."},
    ]
    # Variants with surrounding correct prose (model must not touch protected tokens).
    for i in range(80):
        url = f"https://example.com/item/{rng.randint(1, 9999)}"
        email = f"user{rng.randint(1, 999)}@example.com"
        path = rf"C:\Users\Test\Docs\file{rng.randint(1, 200)}.txt"
        if rng.random() < 0.5:
            examples.append(
                {
                    "language": "ru",
                    "text": f"Пожалуйста, открой {path} и напиши на {email}.",
                }
            )
            examples.append(
                {
                    "language": "ru",
                    "text": f"Ссылка {url} ведёт на документацию WriteLite.",
                }
            )
        else:
            examples.append(
                {
                    "language": "en",
                    "text": f"Please open {path} and email {email}.",
                }
            )
            examples.append(
                {
                    "language": "en",
                    "text": f"See {url} for WriteLite docs.",
                }
            )
    return examples


def _deterministic_corruptions(text: str, language: str, rng: random.Random) -> list[tuple[str, str, list[dict[str, Any]]]]:
    """Return list of (source, target, issue_meta) including multi-error variants."""
    out: list[tuple[str, str, list[dict[str, Any]]]] = []
    target = text

    # Identity (must not change)
    out.append((text, text, []))

    for k in range(3):
        src, applied = corrupt_text(
            text,
            "ru" if language.startswith("ru") else ("en" if language.startswith("en") else "en"),
            random.Random(rng.randint(0, 10_000_000) + k * 17),
            max_errors=1 + k,
        )
        if src == text:
            continue
        issues = [
            {
                "type": a.error_type,
                "original": a.corrupted_span,
                "replacement": a.original_span,
                "error_id": a.error_id,
                "confidence": 0.9,
                "explanation": f"Fix {a.error_type}.",
            }
            for a in applied
        ]
        out.append((src, target, issues))

    # Explicit high-value patterns
    if language.startswith("ru"):
        pairs = [
            (text.replace("не был", "небыл").replace("не была", "небыла"), "spelling"),
            (text[0].lower() + text[1:] if text and text[0].isupper() else text, "capitalization"),
            (re.sub(r"[,.!?]", "", text), "punctuation"),
            (text.replace("потому что", "потомучто").replace("в общем", "вообщем"), "spelling"),
        ]
    else:
        pairs = [
            (text.replace("I have", "I has").replace("works well", "work good"), "agreement"),
            (text.replace("don't", "dont").replace("I don't", "i dont"), "punctuation"),
            (text[0].lower() + text[1:] if text and text[0].isupper() else text, "capitalization"),
            (re.sub(r"[,.!?]", "", text), "punctuation"),
        ]
    for src, typ in pairs:
        if src != text and src.strip():
            out.append((src, target, [{"type": typ, "confidence": 0.85, "explanation": f"Fix {typ}."}]))

    return out


def build_chat_dataset(
    *,
    seed_dir: Path,
    output_dir: Path,
    seed: int = 42,
    target_size: int = 12_000,
    model_version: str = MODEL_VERSION_PLACEHOLDER,
) -> dict[str, int]:
    rng = random.Random(seed)
    clean: list[dict[str, str]] = []
    clean.extend(_load_seed_clean(seed_dir))
    clean.extend(_generate_clean_pool(rng))
    clean.extend(_protected_examples(rng))

    # Dedupe clean by text
    seen_clean: set[str] = set()
    unique_clean: list[dict[str, str]] = []
    for row in clean:
        key = row["text"].strip()
        if not key or key in seen_clean:
            continue
        seen_clean.add(key)
        unique_clean.append(row)

    examples: list[dict[str, Any]] = []
    seen_pair: set[str] = set()

    def add(source: str, target: str, language: str, issues: list[dict[str, Any]], source_type: str) -> None:
        fp = _fp(source, target, language)
        if fp in seen_pair:
            return
        seen_pair.add(fp)
        chat = make_chat_example(
            source=source,
            target=target,
            language=language if language != "mixed" else "en",
            issues=issues,
            model_version=model_version,
        )
        chat["id"] = fp
        chat["sourceType"] = source_type
        chat["license"] = "Apache-2.0"
        examples.append(chat)

    # Protect: identity only (critical)
    for row in _protected_examples(rng):
        add(row["text"], row["text"], row["language"], [], "protected-identity")

    # Generate corruptions until target_size
    idx = 0
    while len(examples) < target_size and idx < target_size * 20:
        row = unique_clean[idx % len(unique_clean)]
        idx += 1
        lang = row["language"]
        text = row["text"]
        # Skip aggressive corruption of protected-looking rows
        is_protect = bool(re.search(r"https?://|@|[A-Za-z]:\\|/home/|WriteLite|`|\{", text))
        if is_protect:
            add(text, text, lang, [], "protected-identity")
            continue
        for source, target, issues in _deterministic_corruptions(text, lang, rng):
            add(source, target, lang, issues, "synthetic" if source != target else "clean-identity")
            if len(examples) >= target_size:
                break

    # Assign splits by target fingerprint (reduce leakage)
    by_target: dict[str, list[dict[str, Any]]] = {}
    for ex in examples:
        key = _fp(ex["target"])
        by_target.setdefault(key, []).append(ex)
    keys = list(by_target.keys())
    rng.shuffle(keys)
    n = len(keys)
    n_train = int(n * 0.8)
    n_val = int(n * 0.1)
    train_keys = set(keys[:n_train])
    val_keys = set(keys[n_train : n_train + n_val])

    for key, group in by_target.items():
        split = "train" if key in train_keys else ("validation" if key in val_keys else "test")
        for ex in group:
            ex["split"] = split

    output_dir.mkdir(parents=True, exist_ok=True)
    counts = {"train": 0, "validation": 0, "test": 0, "all": len(examples)}

    def write_split(name: str, rows: Iterable[dict[str, Any]]) -> int:
        path = output_dir / f"{name}.jsonl"
        n_written = 0
        with path.open("w", encoding="utf-8") as f:
            for r in rows:
                f.write(json.dumps(r, ensure_ascii=False) + "\n")
                n_written += 1
        return n_written

    for split in ("train", "validation", "test"):
        subset = [e for e in examples if e["split"] == split]
        counts[split] = write_split(split, subset)
    write_split("all", examples)

    meta = {
        "seed": seed,
        "target_size": target_size,
        "counts": counts,
        "model_version": model_version,
        "format": "chat-messages-writelight-qwen",
        "license": "Apache-2.0 (synthetic WriteLite seed)",
        "note": "Offsets in issues are optional; production re-diffs correctedText in C#.",
    }
    (output_dir / "dataset_meta.json").write_text(json.dumps(meta, indent=2, ensure_ascii=False), encoding="utf-8")
    return counts
