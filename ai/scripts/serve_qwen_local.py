#!/usr/bin/env python3
"""Fast, loopback-only development server for WriteLite-Qwen.

Python/Transformers is intentionally a development backend.  It loads the model and
LoRA once, serializes generation (one small model cannot usefully serve concurrent
interactive requests), warms up before accepting traffic, and never logs request text.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import threading
import time
from collections import OrderedDict
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

# Training used this prompt.  The serving prompt is deliberately short: it reduces
# prefill work and prevents Qwen 2.5 from spending tokens explaining its answer.
SYSTEM_PROMPT = (
    "You are WriteLite. Correct only spelling, grammar and punctuation. "
    "Preserve URLs, emails, paths and code. Return one compact JSON object: "
    '{"schemaVersion":1,"language":"ru|en","correctedText":"...","issues":[],"modelVersion":"WriteLite-Qwen-0.6B-GEC-1.0.0-dev","uncertain":false}.'
)


def resolve_base_model(value: str) -> str:
    """Use the checked-in model first; do not contact the Hub when it is present."""
    local = ROOT / "models" / "Qwen2.5-0.5B-Instruct"
    return str(local) if value == "Qwen/Qwen2.5-0.5B-Instruct" and local.exists() else value


def select_device(torch):
    if torch.cuda.is_available():
        return "cuda", torch.float16
    # DirectML is optional and deliberately best-effort; CPU stays deterministic.
    try:
        import torch_directml  # type: ignore
        return torch_directml.device(), torch.float32
    except Exception:
        return "cpu", torch.float32


def load_model(base: str, adapter: Path | None, cpu_threads: int):
    import torch
    from transformers import AutoModelForCausalLM, AutoTokenizer

    device, dtype = select_device(torch)
    if device == "cpu":
        torch.set_num_threads(max(1, cpu_threads))
        torch.set_num_interop_threads(1)
    base = resolve_base_model(base)
    tok_source = str(adapter) if adapter and adapter.exists() else base
    tokenizer = AutoTokenizer.from_pretrained(tok_source, trust_remote_code=True, local_files_only=Path(base).exists())
    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token
    model = AutoModelForCausalLM.from_pretrained(
        base, trust_remote_code=True, dtype=dtype, local_files_only=Path(base).exists(),
    )
    if adapter and adapter.exists():
        from peft import PeftModel
        model = PeftModel.from_pretrained(model, str(adapter))
    model.to(device)
    model.eval()
    return tokenizer, model, torch, device


class Runtime:
    def __init__(self, tokenizer, model, torch, device, max_new_tokens: int, max_input_chars: int, cache_size: int):
        self.tokenizer, self.model, self.torch, self.device = tokenizer, model, torch, device
        self.max_new_tokens, self.max_input_chars = max_new_tokens, max_input_chars
        self.generation_lock = threading.BoundedSemaphore(1)
        self.cache: OrderedDict[str, tuple[float, dict]] = OrderedDict()
        self.cache_size = cache_size
        self.cache_lock = threading.Lock()

    def _cache_key(self, messages, max_new):
        # Store only a digest as the key; request text never reaches diagnostics/disk.
        normalized = json.dumps(messages, ensure_ascii=False, separators=(",", ":"))
        return hashlib.sha256((normalized + "|" + str(max_new)).encode("utf-8")).hexdigest()

    def infer(self, messages, requested_max: int):
        max_new = max(16, min(requested_max or self.max_new_tokens, self.max_new_tokens))
        key = self._cache_key(messages, max_new)
        with self.cache_lock:
            hit = self.cache.get(key)
            if hit and time.monotonic() - hit[0] < 600:
                self.cache.move_to_end(key)
                return hit[1] | {"cacheHit": True}
        if not self.generation_lock.acquire(blocking=False):
            return None
        try:
            started = time.perf_counter()
            chat = [{"role": "system", "content": SYSTEM_PROMPT}]
            for message in messages:
                if message.get("role") != "system":
                    content = str(message.get("content", ""))[: self.max_input_chars]
                    chat.append({"role": message.get("role", "user"), "content": content})
            prompt = self.tokenizer.apply_chat_template(chat, tokenize=False, add_generation_prompt=True)
            t0 = time.perf_counter()
            inputs = self.tokenizer(prompt, return_tensors="pt", truncation=True, max_length=768)
            inputs = {k: v.to(self.device) for k, v in inputs.items()}
            t1 = time.perf_counter()
            with self.torch.inference_mode():
                output = self.model.generate(
                    **inputs, max_new_tokens=max_new, do_sample=False, use_cache=True,
                    pad_token_id=self.tokenizer.pad_token_id, eos_token_id=self.tokenizer.eos_token_id,
                )
            t2 = time.perf_counter()
            generated = output[0][inputs["input_ids"].shape[-1]:]
            content = self.tokenizer.decode(generated, skip_special_tokens=True)
            t3 = time.perf_counter()
            result = {"content": content, "promptTokens": int(inputs["input_ids"].shape[-1]),
                      "outputTokens": int(generated.shape[-1]), "durationMs": round((t3-started)*1000),
                      "tokenizeMs": round((t1-t0)*1000), "generateMs": round((t2-t1)*1000),
                      "decodeMs": round((t3-t2)*1000), "cacheHit": False}
            with self.cache_lock:
                self.cache[key] = (time.monotonic(), result)
                self.cache.move_to_end(key)
                while len(self.cache) > self.cache_size:
                    self.cache.popitem(last=False)
            return result
        finally:
            self.generation_lock.release()


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--base-model", default="Qwen/Qwen2.5-0.5B-Instruct")
    p.add_argument("--adapter", type=Path, default=ROOT / "outputs" / "qwen_smoke" / "adapter")
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--port", type=int, default=8742)
    p.add_argument("--max-new-tokens", type=int, default=96)
    p.add_argument("--max-input-chars", type=int, default=480)
    p.add_argument("--cpu-threads", type=int, default=max(1, (os.cpu_count() or 4) // 2))
    p.add_argument("--cache-size", type=int, default=128)
    args = p.parse_args()
    if args.host not in ("127.0.0.1", "localhost", "::1"):
        print("Refusing non-loopback host", file=sys.stderr); return 2
    load_started = time.perf_counter()
    print("event=qwen-model-loading", flush=True)
    tokenizer, model, torch, device = load_model(args.base_model, args.adapter if args.adapter.exists() else None, args.cpu_threads)
    runtime = Runtime(tokenizer, model, torch, device, args.max_new_tokens, args.max_input_chars, args.cache_size)
    print(f"event=qwen-model-ready device={device} loadMs={round((time.perf_counter()-load_started)*1000)}", flush=True)
    # One very small warmup moves lazy allocations out of the first interactive request.
    print("event=qwen-warmup-started", flush=True)
    runtime.infer([{"role": "user", "content": '{"language":"en","text":"Hello."}'}], 32)
    print("event=qwen-warmup-completed", flush=True)

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, fmt, *a):
            # HTTP status only; BaseHTTPRequestHandler's request line contains no body.
            sys.stderr.write("serve %s\n" % (fmt % a))
        def _json(self, status, payload):
            data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
            self.send_response(status); self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data))); self.end_headers(); self.wfile.write(data)
        def do_GET(self):
            if self.path.startswith("/health"):
                self._json(200, {"ok": True, "backend": "writelight-qwen-transformers", "device": str(device)})
            else: self.send_error(404)
        def do_POST(self):
            if self.path.rstrip("/") not in ("/v1/chat/completions", "/chat/completions"):
                self.send_error(404); return
            try:
                n = min(int(self.headers.get("Content-Length", "0")), 128 * 1024)
                body = json.loads(self.rfile.read(n).decode("utf-8"))
                result = runtime.infer(body.get("messages") or [], int(body.get("max_tokens") or args.max_new_tokens))
            except Exception as ex:
                print(f"event=qwen-response-rejected reason={type(ex).__name__}", file=sys.stderr, flush=True)
                self._json(400, {"error": "invalid request"}); return
            if result is None:
                print("event=qwen-request-cancelled reason=queue-full", file=sys.stderr, flush=True)
                self._json(429, {"error": "busy"}); return
            print("event=qwen-response-received durationMs=%s outputTokens=%s cacheHit=%s tokenizeMs=%s generateMs=%s decodeMs=%s" %
                  (result["durationMs"], result["outputTokens"], int(result["cacheHit"]), result["tokenizeMs"], result["generateMs"], result["decodeMs"]), flush=True)
            self._json(200, {"id":"writelight-local","object":"chat.completion","model":"writelight-qwen",
                "choices":[{"index":0,"message":{"role":"assistant","content":result["content"]},"finish_reason":"stop"}],
                "usage":{"prompt_tokens":result["promptTokens"],"completion_tokens":result["outputTokens"],"total_tokens":result["promptTokens"]+result["outputTokens"]}})
    print(f"WriteLite-Qwen local server on http://{args.host}:{args.port} (loopback only)", flush=True)
    ThreadingHTTPServer((args.host, args.port), Handler).serve_forever()
    return 0

if __name__ == "__main__": raise SystemExit(main())
