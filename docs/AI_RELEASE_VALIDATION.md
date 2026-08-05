# WriteLite-Qwen release validation

## Accepted runtime

- Pack version: `WriteLite-Qwen-0.6B-GEC-1.0.1`
- Runtime weights: `writelight-qwen-q4_k_m.gguf`
- SHA-256: `a25b3ab9c1b19fe01facd11978383d8035d1e22728dad000bba70b7a437aa390`
- Inference remains fully local through the bundled `llama-server`.
- Prompt revision 2 explicitly forbids deleting, inserting, replacing, or reordering
  semantic words and requires preservation of URLs, email addresses, paths, code,
  names, and product names.
- High-precision Lite rules always polish an accepted neural correction. Model
  offsets are ignored and the final correction is re-diffed and validated.

## July 2026 polish experiment

A real LoRA continuation was trained on 163 hard-case RU/EN examples with 29
validation examples. Eight CPU training steps reduced step loss from `2.060` to
`1.421`; final validation loss was `1.408`.

The merged Q4_K_M candidate was then tested through a live llama.cpp server. It
preserved protected URL/email tokens and corrected several punctuation cases,
but introduced a semantic deletion in the Russian control sentence
`как у тебя дела`. The candidate was therefore rejected for the product release.

Artifacts are retained under `ai/outputs/qwen_polish` and
`models/writelight-qwen-polish-staging` for research. They are not copied into
the application output. A lower validation loss alone is not an acceptance
criterion; semantic preservation and fixed live probes are mandatory.
