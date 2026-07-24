#!/usr/bin/env bash
set -euo pipefail

repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_root="$(brew --prefix dotnet)/libexec"

export DOTNET_ROOT="$dotnet_root"
exec "$repo_dir/src/Eva.App/bin/Debug/net10.0/Eva.App" "$@"
