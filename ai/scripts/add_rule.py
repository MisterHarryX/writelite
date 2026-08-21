"""Adds or replaces a rule in a WriteLite Russian rule-pack fragment.

The pack is validated at load time — schema version, one pack version across all
fragments, unique rule ids, positive and negative tests on every rule — so editing it
by hand is how a fragment ends up rejected at startup. This script does the edit and
leaves the invariants intact.

    python ai/scripts/add_rule.py grammar rule.json
    python ai/scripts/add_rule.py --bump ru-1.6.0
"""

from __future__ import annotations

import argparse
import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
RULES = ROOT / "resources" / "rules" / "ru"


def load(fragment: str) -> tuple[pathlib.Path, dict]:
    path = RULES / f"{fragment}.json"
    return path, json.loads(path.read_text(encoding="utf-8"))


def save(path: pathlib.Path, document: dict) -> None:
    path.write_text(
        json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )


def bump(version: str) -> None:
    for path in sorted(RULES.glob("*.json")):
        document = json.loads(path.read_text(encoding="utf-8"))
        document["packVersion"] = version
        save(path, document)
        print(f"{path.name}: packVersion -> {version}")


def add(fragment: str, rule_file: str) -> int:
    path, document = load(fragment)
    incoming = json.loads(pathlib.Path(rule_file).read_text(encoding="utf-8"))
    rules = incoming if isinstance(incoming, list) else [incoming]

    by_id = {rule["ruleId"]: index for index, rule in enumerate(document["rules"])}
    for rule in rules:
        missing = [
            field
            for field in (
                "ruleId",
                "language",
                "category",
                "issueCategory",
                "title",
                "shortMessage",
                "detailedExplanation",
                "badExamples",
                "goodExamples",
                "severity",
                "suggestions",
                "source",
                "safeToApply",
                "implementation",
                "tests",
            )
            if field not in rule
        ]
        if missing:
            print(f"{rule.get('ruleId')}: missing {', '.join(missing)}", file=sys.stderr)
            return 1

        tests = rule["tests"]
        if not any(t["shouldMatch"] for t in tests):
            print(f"{rule['ruleId']}: needs a positive test", file=sys.stderr)
            return 1
        if not any(not t["shouldMatch"] for t in tests):
            print(f"{rule['ruleId']}: needs a negative test", file=sys.stderr)
            return 1

        if rule["ruleId"] in by_id:
            document["rules"][by_id[rule["ruleId"]]] = rule
            print(f"{path.name}: replaced {rule['ruleId']}")
        else:
            document["rules"].append(rule)
            print(f"{path.name}: added {rule['ruleId']}")

    save(path, document)
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("fragment", nargs="?", help="grammar | punctuation | spelling")
    parser.add_argument("rule_file", nargs="?", help="JSON file with one rule or a list")
    parser.add_argument("--bump", help="set packVersion across every fragment")
    args = parser.parse_args()

    if args.bump:
        bump(args.bump)
        return 0

    if not args.fragment or not args.rule_file:
        parser.error("fragment and rule_file are required unless --bump is used")

    return add(args.fragment, args.rule_file)


if __name__ == "__main__":
    raise SystemExit(main())
