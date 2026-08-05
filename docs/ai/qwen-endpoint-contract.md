# WriteLite-Qwen HTTP contract (loopback)

## Endpoint

| | |
|--|--|
| Base | `http://127.0.0.1:8742` (loopback only) |
| Health | `GET /health` → `{"ok":true}` |
| Inference | `POST /v1/chat/completions` |

Configured via:

- `WriteLiteAppSettings.QwenEndpoint` (default `http://127.0.0.1:8742`)
- env `WRITELITE_QWEN_ENDPOINT`
- pack file `models/writelight-qwen/local_endpoint.txt`

## Request (OpenAI-compatible)

```http
POST /v1/chat/completions
Content-Type: application/json
```

```json
{
  "model": "writelight-qwen",
  "temperature": 0.0,
  "top_p": 1.0,
  "max_tokens": 256,
  "stream": false,
  "messages": [
    {
      "role": "system",
      "content": "<fixed WriteLite system prompt>"
    },
    {
      "role": "user",
      "content": "{\"language\":\"en\",\"text\":\"I has a new computer and it work good\"}"
    }
  ]
}
```

User content is **JSON data**, not free-form chat instructions.

## Response

```json
{
  "id": "writelight-local",
  "object": "chat.completion",
  "choices": [
    {
      "index": 0,
      "message": {
        "role": "assistant",
        "content": "{\"schemaVersion\":1,\"language\":\"en\",\"correctedText\":\"I have a new computer and it works well.\",\"issues\":[],\"modelVersion\":\"WriteLite-Qwen-0.6B-GEC-1.0.0-dev\"}"
      },
      "finish_reason": "stop"
    }
  ],
  "model": "writelight-qwen"
}
```

Assistant `content` must be a JSON object. Supported fields:

- `correctedText` (required) — or alias `text`
- `language`
- `schemaVersion`
- `modelVersion`
- `issues[]` (optional; C# re-diffs from `correctedText`)
- `uncertain`

## C# logs (compatibility.log)

| Event | Meaning |
|-------|---------|
| `qwen-availability` | health/pack probe |
| `qwen-request-started` | length, endpoint, path |
| `qwen-response-received` | status, durationMs, correctedLength |
| `qwen-response-rejected` | reason, status, duration |
| `qwen-fallback-used` | Lite/rules path |
| `local-ai-provider-result` | backend=writelight-qwen\|lite\|lite-fallback |

Never includes user text.
