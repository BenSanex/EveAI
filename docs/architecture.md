# Architecture

Eva owns the microphone, local STT/TTS processes, Avalonia UI, and an
authenticated `codex app-server` subprocess. Codex receives a dedicated runtime
workspace copied from `runtime/codex-workspace`; its `PATH` includes the packaged
`eve-esi` directory.

PipeWire captures mono 16 kHz WAV recordings. A persistent Python worker keeps
the CTranslate2 Distil-Whisper model resident, prefers CUDA float16, and retries
with CPU int8 if CUDA fails. A separate persistent Piper worker keeps the voice
model warm, writes a temporary WAV, and plays it through PipeWire. Recordings
and synthesized WAV files are deleted after use.

`eve-esi` accepts only a fixed command catalogue and maps each operation to
reviewed ESI `GET` routes. `EveEsi.Core` rejects non-HTTPS ESI origins and every
HTTP method other than GET.

Authentication is Authorization Code + PKCE on a loopback callback. Refresh
tokens are keyed by character ID in Linux Secret Service. The app and CLI share
the non-secret client ID and callback through `eve-sso.json` in Eva's local data
directory; existing `settings.json` installations migrate automatically.

Eva checks CCP's official `latest.jsonl` static-data metadata in the background.
It downloads the versioned JSONL archive, records its SHA-256 checksum, streams
a query-focused set of entities, blueprints, and dogma into SQLite, then
atomically replaces the old database. Failed or interrupted updates leave the
current index usable.

CLI responses project ESI payloads into small typed summaries. Names resolve
through the local index, large collections require limits, and a market
availability query aggregates order books before Codex sees them. Raw command
output is routed to a bounded, collapsed diagnostics panel; only exact
`item/agentMessage/delta` events enter the transcript.

Responses use a stable envelope:

```json
{"ok":true,"data":{},"meta":{"retrievedAt":"...","sourceUrls":[]},"errors":[]}
```
