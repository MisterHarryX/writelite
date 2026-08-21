#!/usr/bin/env python3
"""Reproducibly rebuild resources/spelling/hunspell/en_US.{aff,dic}.

Why this exists
---------------
The English Hunspell dictionary was previously present only inside
artifacts/release-product/, untracked and unbuildable from a clean checkout.
The .dic is byte-identical to LibreOffice upstream; the .aff is upstream plus
one documented downstream patch (apostrophe handling).  This script pins the
upstream commit, verifies its hashes, re-applies that single patch, and writes
the result, so the shipped bytes can always be re-derived and audited.

Licence
-------
SCOWL / Kevin Atkinson, Copyright 2000-2018.  Permissive: use, copy, modify,
distribute and sell are granted without fee provided the copyright notice is
retained.  The notice is written to NOTICE.en_US.txt next to the dictionary.

Usage:  python tools/build-spelling/build_en_us_dictionary.py [--check]
"""
from __future__ import annotations

import argparse
import hashlib
import sys
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT_DIR = ROOT / "resources" / "spelling" / "hunspell"

# LibreOffice/dictionaries, latest commit touching en/en_US.dic (2021-05-12).
UPSTREAM_COMMIT = "4fa94195b8136364dd40bf2b0366a0fe32058899"
UPSTREAM_REPO = "https://github.com/LibreOffice/dictionaries"
UPSTREAM_RAW = (f"https://raw.githubusercontent.com/LibreOffice/dictionaries/"
                f"{UPSTREAM_COMMIT}/en/")

# Verified SHA-256 of the pristine upstream files at UPSTREAM_COMMIT.
UPSTREAM_SHA256 = {
    "en_US.aff": "c7a8c4d08c29d237880844b1623099f59092602f189be38ce3912e457ff38bc1",
    "en_US.dic": "f0b1a234bd178bdd01875b2a392a9647f888b8fe879f79c52aae62c2759b3647",
}

# The single downstream patch, as shipped since 2024-01-29 (Marco A.G. Pinto):
# treat the typographic apostrophe as a word character so "don’t" is not split.
PATCH_HEADER = (
    "# 2024-01-29 (Marco A.G.Pinto)\n"
    "# - Fix: apostrophe handling, by adding: WORDCHARS 0123456789’ to the .aff.\n"
    "#\n"
    "\n"
)
PATCH_FROM = "WORDCHARS 0123456789\n"
PATCH_TO = "WORDCHARS 0123456789’\n"

# Expected result after patching -- these are the bytes WriteLite ships today.
EXPECTED_OUTPUT_SHA256 = {
    "en_US.aff": "e746c882dd6f303c2c46e7452804b9201115a6942cfeb15f18f8edf774d2e24e",
    "en_US.dic": "f0b1a234bd178bdd01875b2a392a9647f888b8fe879f79c52aae62c2759b3647",
}

NOTICE = """\
English Hunspell dictionary (en_US) -- provenance and licence
=============================================================

Source     : LibreOffice/dictionaries, path en/, commit
             {commit}
             {repo}
Upstream   : SCOWL (http://wordlist.sourceforge.net), en_US Hunspell dictionary
Licence    : SCOWL collective work, Copyright 2000-2018 Kevin Atkinson.

  Permission to use, copy, modify, distribute and sell these word lists, the
  associated scripts, the output created from the scripts, and its
  documentation for any purpose is hereby granted without fee, provided that
  the above copyright notice appears in all copies and that both that
  copyright notice and this permission notice appear in supporting
  documentation. Kevin Atkinson makes no representations about the
  suitability of this array for any purpose. It is provided "as is" without
  express or implied warranty.

The affix file additionally derives from Geoff Kuenning's Ispell english.aff
and is covered by his BSD licence, as stated in the upstream README_en_US.txt.

Local modification
------------------
en_US.dic is byte-identical to upstream.
en_US.aff is upstream plus one patch: WORDCHARS gains the typographic
apostrophe (U+2019) so contractions are not split at the apostrophe.
Rebuild with:  python tools/build-spelling/build_en_us_dictionary.py

Commercial use : allowed
Modification   : allowed
Redistribution : allowed with the copyright notice above
"""


def fetch(name: str) -> bytes:
    request = urllib.request.Request(
        UPSTREAM_RAW + name,
        headers={"User-Agent": "WriteLite verified spelling builder/1.0"},
    )
    with urllib.request.urlopen(request, timeout=90) as response:
        return response.read()


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def build(check_only: bool) -> int:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    results: dict[str, str] = {}

    aff_upstream = fetch("en_US.aff")
    dic_upstream = fetch("en_US.dic")

    for name, data in (("en_US.aff", aff_upstream), ("en_US.dic", dic_upstream)):
        digest = sha256(data)
        if digest != UPSTREAM_SHA256[name]:
            raise RuntimeError(
                f"{name}: upstream bytes changed at pinned commit "
                f"(got {digest}, expected {UPSTREAM_SHA256[name]})")

    if PATCH_FROM not in aff_upstream.decode("utf-8"):
        raise RuntimeError("upstream en_US.aff no longer contains the WORDCHARS line to patch")

    aff_text = aff_upstream.decode("utf-8")
    aff_patched = (PATCH_HEADER + aff_text.replace(PATCH_FROM, PATCH_TO, 1)).encode("utf-8")

    outputs = {"en_US.aff": aff_patched, "en_US.dic": dic_upstream}
    for name, data in outputs.items():
        digest = sha256(data)
        results[name] = digest
        expected = EXPECTED_OUTPUT_SHA256[name]
        if digest != expected:
            raise RuntimeError(
                f"{name}: rebuilt bytes do not match the shipped dictionary "
                f"(got {digest}, expected {expected})")
        target = OUT_DIR / name
        if check_only:
            if not target.exists():
                raise RuntimeError(f"{name}: missing from {OUT_DIR}")
            if sha256(target.read_bytes()) != digest:
                raise RuntimeError(f"{name}: on-disk copy differs from the rebuilt bytes")
        else:
            target.write_bytes(data)

    if not check_only:
        (OUT_DIR / "NOTICE.en_US.txt").write_text(
            NOTICE.format(commit=UPSTREAM_COMMIT, repo=UPSTREAM_REPO), encoding="utf-8")

    for name, digest in sorted(results.items()):
        print(f"{name} bytes={len(outputs[name])} sha256={digest}")
    print("mode=" + ("check" if check_only else "write"))
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true",
                        help="verify the checked-in files instead of rewriting them")
    args = parser.parse_args()
    try:
        return build(args.check)
    except Exception as error:
        print(f"error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
