"""Choose the real-word detection margin from evidence, not intuition.

Flagging a word that is spelled correctly is the riskiest thing WriteLite does:
the token is in the dictionary, the user probably meant it, and being wrong here
edits text that was already fine. The margin that gates it should therefore be
read off the frozen benchmark — real_word items measure what the margin buys,
and the clean items measure what it costs — rather than picked by feel.

Runs the shipped int8 graph, so the number applies to what users get.
"""

from __future__ import annotations

import argparse
import io
import json
import os
import re
import sys

import numpy as np

WORD = re.compile(r"[^\W\d_]+", re.UNICODE)


def load_confusions(path: str) -> dict[str, list[str]]:
    with io.open(path, encoding="utf-8") as handle:
        payload = json.load(handle)
    alternatives: dict[str, list[str]] = {}
    for entry in payload["sets"]:
        members = [m.strip() for m in entry["members"] if m and m.strip()]
        for word in members:
            key = word.lower()
            bucket = alternatives.setdefault(key, [])
            for other in members:
                if other.lower() != key and other not in bucket:
                    bucket.append(other)
    return alternatives


class Scorer:
    def __init__(self, model_dir: str):
        import onnxruntime as ort
        from transformers import AutoTokenizer

        self.tokenizer = AutoTokenizer.from_pretrained(os.path.join(model_dir, "hf"))
        self.session = ort.InferenceSession(
            os.path.join(model_dir, "model.onnx"), providers=["CPUExecutionProvider"]
        )
        self.names = {i.name for i in self.session.get_inputs()}

    def score(self, sentences: list[str]) -> np.ndarray:
        encoded = self.tokenizer(
            sentences, padding="max_length", truncation=True, max_length=64, return_tensors="np"
        )
        feed = {k: v.astype(np.int64) for k, v in encoded.items() if k in self.names}
        logits = self.session.run(None, feed)[0]
        shifted = logits - logits.max(axis=-1, keepdims=True)
        exponentiated = np.exp(shifted)
        return (exponentiated / exponentiated.sum(axis=-1, keepdims=True))[:, 1]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", default=os.path.join("models", "writelight-reranker"))
    parser.add_argument("--confusions", default=os.path.join("resources", "lexical", "ru-confusion-sets.json"))
    parser.add_argument("--benchmark", default=os.path.join("ai", "data", "benchmark", "ru_frozen_v1.jsonl"))
    parser.add_argument("--out", default=os.path.join("ai", "outputs", "reranker", "margin-calibration.json"))
    parser.add_argument(
        "--use-index",
        action="store_true",
        help="also propose real-word alternatives one edit away in the form index",
    )
    parser.add_argument("--index-dir", default=os.path.join("resources", "lexical"))
    parser.add_argument("--index-max-rank", type=int, default=50000)
    args = parser.parse_args()

    alternatives = load_confusions(args.confusions)
    scorer = Scorer(args.model)

    index = None
    if args.use_index:
        # The hand-authored sets are precise but narrow. The form index can
        # propose any real word one edit away, which is what a real-word typo
        # actually is; the frequency ceiling keeps the list to words a writer
        # might plausibly have meant.
        sys.setrecursionlimit(10000)
        sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
        from verify_ru_form_index import FormIndex

        index = FormIndex(args.index_dir)

    def candidates_for(token: str) -> list[str]:
        options = list(alternatives.get(token.lower(), []))
        if index is not None and len(token) >= 4:
            for word, word_id, distance in index.find_within(token.lower(), 1, 64):
                if distance != 1 or word == token.lower() or word in options:
                    continue
                rank = index.info(word_id)["rank"]
                if rank and rank <= args.index_max_rank:
                    options.append(word)
        return options

    items = [json.loads(line) for line in io.open(args.benchmark, encoding="utf-8") if line.strip()]
    real_word = [i for i in items if i["category"] == "real_word"]
    clean = [i for i in items if i["category"] == "clean"]

    # For every candidate token, record the gain the model sees from swapping it,
    # plus whether that swap is the correction the benchmark expects.
    observations: list[tuple[float, bool, bool]] = []   # (gain, is_expected_fix, is_clean_item)
    for group, is_clean in ((real_word, False), (clean, True)):
        for item in group:
            source = item["source"]
            expected = {
                (e["original"], e["replacement"]) for e in item.get("errors", [])
            }
            base = float(scorer.score([source])[0])
            for match in WORD.finditer(source):
                options = candidates_for(match.group(0))
                if not options:
                    continue
                variants = [
                    source[:match.start()] + option + source[match.end():] for option in options
                ]
                scores = scorer.score(variants)
                best = int(np.argmax(scores))
                gain = float(scores[best]) - base
                fixes = (match.group(0), options[best]) in expected
                observations.append((gain, fixes, is_clean))

    total_real_word_errors = sum(len(i.get("errors", [])) for i in real_word)
    rows = []
    for margin in [round(m, 2) for m in np.arange(0.05, 0.96, 0.05)]:
        fired = [o for o in observations if o[0] >= margin]
        correct = sum(1 for o in fired if o[1])
        clean_false = sum(1 for o in fired if o[2])
        wrong_on_errors = sum(1 for o in fired if not o[1] and not o[2])
        rows.append({
            "margin": margin,
            "flagged": len(fired),
            "correctFixes": correct,
            "recall": round(correct / max(total_real_word_errors, 1), 4),
            "precision": round(correct / max(len(fired), 1), 4),
            "falsePositivesOnCleanItems": clean_false,
            "wrongSuggestionsOnErrorItems": wrong_on_errors,
        })

    # The operating point: no false positive on any clean sentence, then as much
    # recall as that allows. Clean text is the constraint, not a tie-breaker.
    safe = [r for r in rows if r["falsePositivesOnCleanItems"] == 0]
    recommended = max(safe, key=lambda r: r["recall"]) if safe else None

    report = {
        "model": args.model,
        "realWordItems": len(real_word),
        "realWordErrors": total_real_word_errors,
        "cleanItems": len(clean),
        "candidateTokensConsidered": len(observations),
        "candidateSource": "confusion-sets + form-index" if args.use_index else "confusion-sets",
        "sweep": rows,
        "recommended": recommended,
    }
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with io.open(args.out, "w", encoding="utf-8") as handle:
        json.dump(report, handle, ensure_ascii=False, indent=2)

    print(json.dumps({k: v for k, v in report.items() if k != "sweep"}, ensure_ascii=False))
    for row in rows:
        print(row)
    return 0


if __name__ == "__main__":
    sys.exit(main())
