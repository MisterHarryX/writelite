"""Exports a trained punctuation classifier to the single int8 ONNX artefact the app loads.

Deliberately a separate script from training. An experiment can be run, judged and thrown
away without ever producing a shippable artefact, and the one that wins can be exported
without being retrained. Keeping them apart is also what makes it possible to re-export an
existing checkpoint when the runtime contract changes.

Three details here are not obvious and each one cost a debugging session in an earlier phase,
so they are preserved rather than rediscovered:

* The TorchScript exporter is used rather than dynamo. Dynamo emits shape annotations that
  defeat onnxruntime's dynamic quantiser ("Inferred shape and existing shape differ"), and
  int8 is the difference between a ~117 MB artefact and a ~30 MB one.
* torch spills weights into a sidecar ``.onnx.data`` once the graph is large enough. The C#
  runtime ships one file, and a copied ``.onnx`` whose sidecar was left behind loads as an
  empty model rather than failing loudly. The graph is re-saved with tensors inlined first.
* rubert-tiny2 is a **cased** model. Getting ``lowercase`` wrong in the C# tokenizer would not
  throw — it would quietly segment every capitalised word differently from training — so the
  flag travels with the artefact in ``punctuation.json``.

    python ai/scripts/export_punctuation_onnx.py --experiment PUNC-B --threshold 0.62
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import shutil

DEFAULT_ROOT = os.path.join("models", "writelite-punctuation")
DEPLOY_DIR = os.path.join("models", "writelite-punctuation")


def sha256_of(path: str) -> str:
    digest = hashlib.sha256()
    with io.open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def export(experiment: str, max_length: int, encoding: str, threshold: float) -> dict:
    import torch
    from transformers import AutoModelForSequenceClassification, AutoTokenizer

    source = os.path.join(DEFAULT_ROOT, experiment, "hf")
    if not os.path.isdir(source):
        raise SystemExit(f"no trained checkpoint at {source}")

    tokenizer = AutoTokenizer.from_pretrained(source)
    model = AutoModelForSequenceClassification.from_pretrained(source).eval()

    os.makedirs(DEPLOY_DIR, exist_ok=True)
    fp32_path = os.path.join(DEPLOY_DIR, "model.fp32.onnx")

    sample = tokenizer(
        ["пример предложения"],
        ["и его продолжение"] if encoding == "segment" else None,
        return_tensors="pt",
        padding="max_length",
        truncation=True,
        max_length=max_length,
    )
    names = ["input_ids", "attention_mask"]
    inputs = [sample["input_ids"], sample["attention_mask"]]
    if "token_type_ids" in sample:
        names.append("token_type_ids")
        inputs.append(sample["token_type_ids"])

    export_kwargs = dict(
        input_names=names,
        output_names=["logits"],
        dynamic_axes={name: {0: "batch"} for name in names + ["logits"]},
        opset_version=17,
        do_constant_folding=True,
    )
    try:
        torch.onnx.export(model, tuple(inputs), fp32_path, dynamo=False, **export_kwargs)
    except TypeError:
        torch.onnx.export(model, tuple(inputs), fp32_path, **export_kwargs)

    import onnx

    graph = onnx.load(fp32_path, load_external_data=True)
    inlined_path = os.path.join(DEPLOY_DIR, "model.inlined.onnx")
    onnx.save(graph, inlined_path, save_as_external_data=False)

    final_path = os.path.join(DEPLOY_DIR, "model.onnx")
    quantised = False
    quantisation_error = None
    try:
        from onnxruntime.quantization import QuantType, quantize_dynamic

        quantize_dynamic(inlined_path, final_path, weight_type=QuantType.QInt8)
        quantised = True
    except Exception as error:  # noqa: BLE001 - reported, not hidden
        shutil.copyfile(inlined_path, final_path)
        quantisation_error = f"{type(error).__name__}: {error}"

    vocab_path = os.path.join(DEPLOY_DIR, "vocab.txt")
    source_vocab = os.path.join(source, "vocab.txt")
    if os.path.exists(source_vocab):
        shutil.copyfile(source_vocab, vocab_path)
    else:
        with io.open(vocab_path, "w", encoding="utf-8", newline="\n") as handle:
            for token, _id in sorted(tokenizer.get_vocab().items(), key=lambda kv: kv[1]):
                handle.write(token + "\n")

    lowercase = False
    tokenizer_config = os.path.join(source, "tokenizer_config.json")
    if os.path.exists(tokenizer_config):
        with io.open(tokenizer_config, encoding="utf-8") as handle:
            lowercase = bool(json.load(handle).get("do_lower_case", False))

    manifest = {
        "version": experiment,
        "task": "punctuation_decision",
        "baseModel": "cointegrated/rubert-tiny2",
        "baseModelLicense": "MIT",
        "maxLength": max_length,
        "encoding": encoding,
        "lowercase": lowercase,
        "labels": {"0": "NO_CHANGE", "1": "COMMA"},
        "commaLabelIndex": 1,
        # Chosen on validation and frozen before the golden set is evaluated. The runtime
        # reads it from here rather than hardcoding, so re-tuning does not need a rebuild.
        "acceptanceThreshold": threshold,
    }
    with io.open(os.path.join(DEPLOY_DIR, "punctuation.json"), "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, ensure_ascii=False, indent=2)

    for temporary in (fp32_path, inlined_path, fp32_path + ".data"):
        if os.path.exists(temporary):
            os.remove(temporary)

    return {
        "onnx": final_path,
        "sha256": sha256_of(final_path),
        "quantisedInt8": quantised,
        "quantisationError": quantisation_error,
        "bytes": os.path.getsize(final_path),
        "vocabTokens": sum(1 for _ in io.open(vocab_path, encoding="utf-8")),
        "manifest": manifest,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--experiment", required=True)
    parser.add_argument("--max-length", type=int, default=64)
    parser.add_argument("--encoding", default="segment", choices=["segment", "marker"])
    parser.add_argument("--threshold", type=float, default=0.5)
    args = parser.parse_args()

    result = export(args.experiment, args.max_length, args.encoding, args.threshold)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    print(f"\n{result['bytes'] / 1e6:.1f} MB  int8={result['quantisedInt8']}  -> {result['onnx']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
