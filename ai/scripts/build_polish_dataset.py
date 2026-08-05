#!/usr/bin/env python3
"""Build a small, deterministic hard-case dataset for release polishing."""

from __future__ import annotations

import json
import random
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from writelight_ai.prompts import make_chat_example  # noqa: E402


PAIRS = [
    ("ru", "привет как у тебя дела я сегодня небыл в школе", "Привет, как у тебя дела? Я сегодня не был в школе."),
    ("ru", "Когда я пришел домой мама уже приготовила ужин", "Когда я пришёл домой, мама уже приготовила ужин."),
    ("ru", "Я небыл дома потому что работал", "Я не был дома, потому что работал."),
    ("ru", "Она небыла уверена что письмо отправлено", "Она не была уверена, что письмо отправлено."),
    ("ru", "Это корректный текст без ошибок.", "Это корректный текст без ошибок."),
    ("ru", "Напиши на test@example.com или открой https://example.com.", "Напиши на test@example.com или открой https://example.com."),
    ("en", "I has a new computer and it work good", "I have a new computer, and it works well."),
    ("en", "She have a book and he have a pen", "She has a book, and he has a pen."),
    ("en", "It does not works correctly", "It does not work correctly."),
    ("en", "where are you going i dont know", "Where are you going? I don't know."),
    ("en", "This sentence is already correct.", "This sentence is already correct."),
    ("en", "Email test@example.com and URL https://example.com must stay unchanged.", "Email test@example.com and URL https://example.com must stay unchanged."),
]


def main() -> int:
    output = ROOT / "data" / "polish"
    output.mkdir(parents=True, exist_ok=True)
    rng = random.Random(20260725)
    rows = [
        make_chat_example(source=source, target=target, language=language)
        for _ in range(16)
        for language, source, target in PAIRS
    ]
    rng.shuffle(rows)
    split = int(len(rows) * 0.85)
    for name, data in (("train.jsonl", rows[:split]), ("validation.jsonl", rows[split:])):
        with (output / name).open("w", encoding="utf-8", newline="\n") as stream:
            for row in data:
                stream.write(json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n")
    print(f"polish dataset: train={split} validation={len(rows) - split} output={output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
