#!/usr/bin/env python3
"""Reproducible loopback benchmark; it records metadata only, never prompt text."""
from __future__ import annotations
import argparse, json, os, statistics, time, urllib.request

SAMPLES = ["I has a new computer and it work good", "привет как у тебя дела я сегодня небыл в школе", "я думаю что это хороший вариант но нужно проверить"]
def percentile(v, p): return v[min(len(v)-1, max(0, round((len(v)-1)*p)))]
def post(endpoint, text):
    body=json.dumps({"model":"writelight-qwen","max_tokens":96,"messages":[{"role":"user","content":json.dumps({"text":text},ensure_ascii=False)}]}).encode()
    t=time.perf_counter();
    with urllib.request.urlopen(urllib.request.Request(endpoint+"/v1/chat/completions",body,{"Content-Type":"application/json"}),timeout=120) as r: result=json.load(r)
    ms=(time.perf_counter()-t)*1000; usage=result.get("usage",{}); return {"durationMs":round(ms),"outputTokens":usage.get("completion_tokens",0),"promptTokens":usage.get("prompt_tokens",0),"inputChars":len(text)}
def main():
    p=argparse.ArgumentParser();p.add_argument("--endpoint",default="http://127.0.0.1:8742");p.add_argument("--iterations",type=int,default=3);a=p.parse_args()
    rows=[]
    for text in SAMPLES:
        for _ in range(a.iterations): rows.append(post(a.endpoint,text))
    times=sorted(x["durationMs"] for x in rows); tokens=sum(x["outputTokens"] for x in rows); seconds=sum(x["durationMs"] for x in rows)/1000
    report={"samples":len(SAMPLES),"iterations":a.iterations,"p50Ms":percentile(times,.5),"p95Ms":percentile(times,.95),"tokensPerSecond":round(tokens/seconds,2) if seconds else 0,"rows":rows}
    print(json.dumps(report,ensure_ascii=False,indent=2))
if __name__=="__main__": main()
