"""Export Helsinki-NLP opus-mt Marian models to ONNX for WriteLite's offline translator.

Produces, per direction, in models/writelite-translate/<pair>/:
  encoder.onnx, decoder.onnx   (int8 dynamic-quantized, no KV cache)
  source.tsv, target.tsv       (SentencePiece unigram pieces + scores)
  vocab.tsv                    (piece -> Marian id, line number = id)
  config.json                  (pad/eos/unk ids, decoder start, max length)

Usage: python -I export_marian_onnx.py <out_root> [pair ...]   (pairs: ru-en en-ru)
"""
import json, os, sys, shutil
import torch
from transformers import MarianMTModel, MarianTokenizer
import sentencepiece as spm
from onnxruntime.quantization import quantize_dynamic, QuantType


class Encoder(torch.nn.Module):
    def __init__(self, model):
        super().__init__()
        self.encoder = model.get_encoder()

    def forward(self, input_ids, attention_mask):
        return self.encoder(input_ids=input_ids, attention_mask=attention_mask).last_hidden_state


class Decoder(torch.nn.Module):
    def __init__(self, model):
        super().__init__()
        self.decoder = model.get_decoder()
        self.lm_head = model.lm_head
        self.register_buffer("bias", model.final_logits_bias.clone())

    def forward(self, input_ids, encoder_hidden_states, encoder_attention_mask):
        out = self.decoder(input_ids=input_ids,
                           encoder_hidden_states=encoder_hidden_states,
                           encoder_attention_mask=encoder_attention_mask,
                           use_cache=False).last_hidden_state
        logits = self.lm_head(out) + self.bias
        return logits[:, -1, :]


def dump_spm(path, out):
    sp = spm.SentencePieceProcessor(model_file=path)
    with open(out, "w", encoding="utf-8") as f:
        for i in range(sp.get_piece_size()):
            f.write(f"{sp.id_to_piece(i)}\t{sp.get_score(i)}\t{1 if sp.is_unknown(i) or sp.is_control(i) else 0}\n")


def export(pair, out_root):
    name = f"Helsinki-NLP/opus-mt-{pair}"
    out = os.path.join(out_root, pair)
    os.makedirs(out, exist_ok=True)
    tok = MarianTokenizer.from_pretrained(name)
    model = MarianMTModel.from_pretrained(name).eval()
    ids = tok(["Привет, как дела?" if pair.startswith("ru") else "Hello, how are you?"], return_tensors="pt")
    enc = Encoder(model).eval()
    dec = Decoder(model).eval()
    tmp = os.path.join(out, "tmp")
    os.makedirs(tmp, exist_ok=True)
    with torch.no_grad():
        hidden = enc(ids["input_ids"], ids["attention_mask"])
        torch.onnx.export(enc, (ids["input_ids"], ids["attention_mask"]), os.path.join(tmp, "encoder.onnx"),
                          input_names=["input_ids", "attention_mask"], output_names=["hidden"],
                          dynamic_axes={"input_ids": {0: "b", 1: "s"}, "attention_mask": {0: "b", 1: "s"}, "hidden": {0: "b", 1: "s"}},
                          opset_version=17, dynamo=False)
        start = torch.tensor([[model.config.decoder_start_token_id, 100]], dtype=torch.long)
        torch.onnx.export(dec, (start, hidden, ids["attention_mask"]), os.path.join(tmp, "decoder.onnx"),
                          input_names=["input_ids", "encoder_hidden_states", "encoder_attention_mask"], output_names=["logits"],
                          dynamic_axes={"input_ids": {0: "b", 1: "t"}, "encoder_hidden_states": {0: "b", 1: "s"},
                                        "encoder_attention_mask": {0: "b", 1: "s"}, "logits": {0: "b"}},
                          opset_version=17, dynamo=False)
    for part in ("encoder", "decoder"):
        quantize_dynamic(os.path.join(tmp, f"{part}.onnx"), os.path.join(out, f"{part}.onnx"), weight_type=QuantType.QInt8)
    shutil.rmtree(tmp, ignore_errors=True)
    files = tok.save_pretrained(tmp)
    dump_spm(os.path.join(tmp, "source.spm"), os.path.join(out, "source.tsv"))
    dump_spm(os.path.join(tmp, "target.spm"), os.path.join(out, "target.tsv"))
    vocab = json.load(open(os.path.join(tmp, "vocab.json"), encoding="utf-8"))
    by_id = sorted(vocab.items(), key=lambda kv: kv[1])
    assert all(i == v for i, (_, v) in enumerate(by_id)), "vocab ids not dense"
    with open(os.path.join(out, "vocab.tsv"), "w", encoding="utf-8") as f:
        for piece, _ in by_id:
            f.write(piece.replace("\t", " ").replace("\n", " ") + "\n")
    shutil.rmtree(tmp, ignore_errors=True)
    cfg = {"source": name, "license": "CC-BY-4.0", "pad_id": model.config.pad_token_id,
           "eos_id": model.config.eos_token_id, "unk_id": tok.unk_token_id,
           "decoder_start_id": model.config.decoder_start_token_id, "vocab_size": len(by_id), "max_length": 256}
    json.dump(cfg, open(os.path.join(out, "config.json"), "w"), indent=2)
    # reference translation for the C# parity test
    with torch.no_grad():
        ref = model.generate(**ids, num_beams=1, max_new_tokens=64, do_sample=False)
    cfg["probe"] = {"input": tok.batch_decode(ids["input_ids"], skip_special_tokens=True)[0],
                    "greedy": tok.batch_decode(ref, skip_special_tokens=True)[0]}
    json.dump(cfg, open(os.path.join(out, "config.json"), "w", encoding="utf-8"), indent=2, ensure_ascii=False)
    print(pair, "done", cfg["probe"])


if __name__ == "__main__":
    root = sys.argv[1]
    for p in (sys.argv[2:] or ["ru-en", "en-ru"]):
        export(p, root)
