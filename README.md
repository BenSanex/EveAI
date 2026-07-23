# Eva

Eva is a Linux-first, local-voice EVE Online assistant:

`voice → local transcription → Codex → eve-esi → Codex answer → local speech`

- `Eva.App`: Avalonia desktop client and local audio/Codex process bridges.
- `EveEsi.Cli`: bounded, read-only EVE ESI command-line client.
- `EveEsi.Core`: ESI HTTP, OAuth/PKCE, caching, and Secret Service support.
- `tests`: contract and safety tests.

## Build

The .NET SDK is pinned by `global.json`.

```sh
dotnet restore
dotnet build --no-restore
dotnet test --no-build
```

## CLI examples

```sh
dotnet run --project src/EveEsi.Cli -- help
dotnet run --project src/EveEsi.Cli -- universe system --id 30000142 --json
dotnet run --project src/EveEsi.Cli -- market prices --type 34 --json
```

Character commands read refresh tokens from Linux Secret Service. Secrets never
appear in command arguments or output. Configure the EVE SSO client ID in Eva
Settings or with `EVA_EVE_CLIENT_ID`.

See [docs/architecture.md](docs/architecture.md) and
[docs/security.md](docs/security.md).
