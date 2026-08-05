# WriteLite stable release report

Built on 2026-07-26:

- Folder: `artifacts/release-2026-07-26/WriteLite-win-x64`
- Archive: `artifacts/release-2026-07-26/WriteLite-win-x64.zip`
- Runtime: .NET 10.0.9, Windows x64, self-contained
- Archive size: 729,062,832 bytes
- SHA-256: `A8FCAB5464D127B2CC9806FC4CC8F5F7F38E9EF46EED297EDCA565A5662076A5`

Validation:

- 391 automated tests passed
- 4 optional live/integration tests skipped
- 0 tests failed
- 2,335 files in the release directory
- Russian and English Hunspell dictionaries are bundled
- OpenRussian lexical pack contains 58,613 entries
- LanguageTool 6.4 offline runtime is bundled
- WriteLite Qwen GGUF model and llama.cpp runtime are bundled

The executable was not launched as a second interactive instance during this
validation because another user-owned WriteLite instance was already running.
The isolated Release build, full automated suite, publish output, runtime
manifest, and required offline assets were verified.
