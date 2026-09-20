#!/usr/bin/env bash
set -euo pipefail

forge_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
binary="$forge_root/artifacts/horizon-forge-linux-x64/horizon-forge"
log=${1:-"$forge_root/artifacts/package-smoke.log"}

[[ -x $binary ]] || { echo "Packaged app is missing: $binary" >&2; exit 1; }
mkdir -p "$(dirname "$log")"

set +e
xvfb-run -a timeout --signal=INT 15s env -u ELECTRON_RUN_AS_NODE "$binary" >"$log" 2>&1
status=$?
set -e

[[ $status == 124 ]] || { cat "$log" >&2; exit 1; }
grep -Fq '[Forge.Host] Horizon Forge host is ready' "$log" || { cat "$log" >&2; exit 1; }
echo "Packaged Linux smoke passed"
