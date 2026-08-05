# Privacy

## Defaults

- Analysis is local-only. Lite runs in-process; Qwen and the language engine use loopback HTTP.
- Non-loopback AI endpoints, API keys and external AI providers are not supported.
- User text is not written to compatibility logs.  
- Telemetry events may include: event name, duration, model version, input **length**, issue **count**, backend id, error code.

## Prompt injection

User text is treated as data. The Lite engine has no tool/use/network side effects. Injection phrases do not unlock secrets or change settings.

## Model loading

Only local filesystem model packs under the app directory / configured model path. No remote model download at runtime in MVP.
