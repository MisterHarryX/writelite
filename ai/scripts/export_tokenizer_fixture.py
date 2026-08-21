"""Freeze HuggingFace tokenizer output so the C# port can be checked against it.

The reranker's scores are only meaningful if the runtime tokenizes exactly the
way training did. This writes the reference segmentation for a spread of
Russian text — slang, obscenity, brand names, mixed script, punctuation,
numbers — and `WordPieceTokenizerTests` asserts the C# implementation
reproduces it token for token.
"""

from __future__ import annotations

import argparse
import io
import json
import os
import sys

SAMPLES = [
    "Привет, как дела?",
    "Я хочу купить новый ноутбук.",
    "Компания проводит исследование рынка.",
    "Кампания проводит исследование рынка.",
    "Это вообще имба, го катку.",
    "Скинь ссылку в Телеграм или Дискорд.",
    "Разверни бэкенд и запушь коммит в репозиторий на GitHub.",
    "Нейросеть ChatGPT ответила за две секунды.",
    "Стоимость составила 12 500 рублей (без НДС).",
    "Пиши на support@example.com или загляни на https://example.com/docs",
    "Файл лежит в C:\\Users\\test\\проект.txt",
    "Ему было пофиг на всё это, если честно.",
    "надо разобраться почему оно не работает",
    "ПОЧЕМУ ВСЁ СЛОМАЛОСЬ?!",
    "Ёлки-палки, ещё один баг.",
    "Мы обсудим это завтра, — сказал он.",
]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", default=os.path.join("ai", "models", "rubert-tiny2"))
    parser.add_argument(
        "--out",
        default=os.path.join("tests", "WriteLite.Tests", "Fixtures", "tokenizer-reference.json"),
    )
    parser.add_argument("--max-length", type=int, default=64)
    args = parser.parse_args()

    from transformers import AutoTokenizer

    tokenizer = AutoTokenizer.from_pretrained(args.model)
    cases = []
    for text in SAMPLES:
        encoded = tokenizer(
            text, truncation=True, max_length=args.max_length,
            padding="max_length", add_special_tokens=True,
        )
        cases.append({
            "text": text,
            "inputIds": encoded["input_ids"],
            "attentionMask": encoded["attention_mask"],
        })

    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with io.open(args.out, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(
            {
                "model": "cointegrated/rubert-tiny2",
                "maxLength": args.max_length,
                "lowercase": bool(getattr(tokenizer, "do_lower_case", True)),
                "specialTokens": {
                    "cls": tokenizer.cls_token_id,
                    "sep": tokenizer.sep_token_id,
                    "pad": tokenizer.pad_token_id,
                    "unk": tokenizer.unk_token_id,
                },
                "cases": cases,
            },
            handle, ensure_ascii=False, indent=1,
        )

    print(json.dumps({"cases": len(cases), "out": args.out,
                      "vocab": tokenizer.vocab_size,
                      "lowercase": bool(getattr(tokenizer, "do_lower_case", True))}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
