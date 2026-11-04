#!/usr/bin/env bash
set -euo pipefail
forge_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
marker="$forge_root/.dependencies/ratchet-sdk-root"
[[ -f $marker ]] || { echo 'Run npm run bootstrap:sdk before SDK tests.' >&2; exit 1; }
sdk_root=$(tr -d '\r\n' < "$marker")
if command -v cygpath >/dev/null 2>&1; then sdk_root=$(cygpath -u "$sdk_root"); fi
dotnet run --project "$sdk_root/tests/RatchetPs2.LevelTests/RatchetPs2.LevelTests.csproj" \
  --configuration Release -p:BuildInParallel=false -- --gc-to-uya-ties
