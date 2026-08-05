"""Evaluation metrics for GEC-style correction."""

from __future__ import annotations

import math
import time
from collections import Counter
from dataclasses import dataclass, field
from typing import Iterable


def levenshtein(a: str, b: str) -> int:
    if a == b:
        return 0
    if not a:
        return len(b)
    if not b:
        return len(a)
    prev = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        cur = [i]
        for j, cb in enumerate(b, 1):
            cost = 0 if ca == cb else 1
            cur.append(min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + cost))
        prev = cur
    return prev[-1]


def cer(ref: str, hyp: str) -> float:
    if not ref:
        return 0.0 if not hyp else 1.0
    return levenshtein(ref, hyp) / max(1, len(ref))


def wer(ref: str, hyp: str) -> float:
    r = ref.split()
    h = hyp.split()
    if not r:
        return 0.0 if not h else 1.0
    # word-level levenshtein
    dp = [[0] * (len(h) + 1) for _ in range(len(r) + 1)]
    for i in range(len(r) + 1):
        dp[i][0] = i
    for j in range(len(h) + 1):
        dp[0][j] = j
    for i in range(1, len(r) + 1):
        for j in range(1, len(h) + 1):
            cost = 0 if r[i - 1] == h[j - 1] else 1
            dp[i][j] = min(dp[i - 1][j] + 1, dp[i][j - 1] + 1, dp[i - 1][j - 1] + cost)
    return dp[-1][-1] / max(1, len(r))


def exact_match(ref: str, hyp: str) -> float:
    return 1.0 if ref == hyp else 0.0


def token_f1(ref: str, hyp: str) -> float:
    r = Counter(ref.split())
    h = Counter(hyp.split())
    if not r and not h:
        return 1.0
    common = sum((r & h).values())
    if common == 0:
        return 0.0
    precision = common / max(1, sum(h.values()))
    recall = common / max(1, sum(r.values()))
    if precision + recall == 0:
        return 0.0
    return 2 * precision * recall / (precision + recall)


@dataclass
class EvalBucket:
    name: str
    n: int = 0
    exact: float = 0.0
    cer_sum: float = 0.0
    wer_sum: float = 0.0
    f1_sum: float = 0.0
    false_positive_clean: int = 0
    clean_n: int = 0
    latencies_ms: list[float] = field(default_factory=list)

    def add(self, ref: str, hyp: str, source: str, latency_ms: float) -> None:
        self.n += 1
        self.exact += exact_match(ref, hyp)
        self.cer_sum += cer(ref, hyp)
        self.wer_sum += wer(ref, hyp)
        self.f1_sum += token_f1(ref, hyp)
        self.latencies_ms.append(latency_ms)
        if source == ref:
            self.clean_n += 1
            if hyp != ref:
                self.false_positive_clean += 1

    def summary(self) -> dict:
        n = max(1, self.n)
        lats = sorted(self.latencies_ms)
        def pct(p: float) -> float:
            if not lats:
                return 0.0
            idx = min(len(lats) - 1, max(0, int(math.ceil(p * len(lats)) - 1)))
            return lats[idx]
        return {
            "name": self.name,
            "n": self.n,
            "exact_match": self.exact / n,
            "cer": self.cer_sum / n,
            "wer": self.wer_sum / n,
            "token_f1": self.f1_sum / n,
            "clean_false_positive_rate": (self.false_positive_clean / self.clean_n) if self.clean_n else 0.0,
            "latency_ms_p50": pct(0.50),
            "latency_ms_p95": pct(0.95),
            "latency_ms_mean": (sum(lats) / len(lats)) if lats else 0.0,
        }


def evaluate_pairs(
    pairs: Iterable[dict],
    predict_fn,
    bucket_fn=None,
) -> dict:
    overall = EvalBucket("overall")
    buckets: dict[str, EvalBucket] = {}
    for row in pairs:
        source = row["source"]
        target = row["target"]
        t0 = time.perf_counter()
        hyp = predict_fn(source, row)
        dt = (time.perf_counter() - t0) * 1000
        overall.add(target, hyp, source, dt)
        names = []
        if bucket_fn:
            names = bucket_fn(row)
        else:
            names = [row.get("language", "und")]
            if source == target:
                names.append("clean")
            if "http" in source or "@" in source or "\\" in source:
                names.append("protected")
        for name in names:
            b = buckets.setdefault(name, EvalBucket(name))
            b.add(target, hyp, source, dt)

    return {
        "overall": overall.summary(),
        "buckets": {k: v.summary() for k, v in buckets.items()},
    }
