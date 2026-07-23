# Architecture

Eva owns the microphone, local STT/TTS processes, Avalonia UI, and an
authenticated `codex app-server` subprocess. Codex receives a dedicated runtime
workspace copied from `runtime/codex-workspace`; its `PATH` includes the packaged
`eve-esi` directory.

`eve-esi` accepts only a fixed command catalogue and maps each operation to
reviewed ESI `GET` routes. `EveEsi.Core` rejects non-HTTPS ESI origins and every
HTTP method other than GET.

Authentication is Authorization Code + PKCE on a loopback callback. Refresh
tokens are keyed by character ID in Linux Secret Service. Large collections
require limits and return cursor metadata.

Responses use a stable envelope:

```json
{"ok":true,"data":{},"meta":{"retrievedAt":"...","sourceUrls":[]},"errors":[]}
```
