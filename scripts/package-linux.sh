#!/usr/bin/env bash
set -euo pipefail

forge_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
output="$forge_root/artifacts/horizon-forge-linux-x64"
stage=$(mktemp -d "/tmp/horizon-forge-package.XXXXXX")
trap 'rm -rf -- "$stage"' EXIT

npm --prefix "$forge_root" run build:desktop
dotnet publish "$forge_root/src/Forge.Host/Forge.Host.csproj" \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained true \
  --output "$stage/resources/host"

cp -a "$forge_root/node_modules/electron/dist/." "$stage/"
mv "$stage/electron" "$stage/horizon-forge"
mkdir -p "$stage/resources/app"
cp -a "$forge_root/dist" "$forge_root/dist-electron" "$forge_root/package.json" "$stage/resources/app/"

rm -rf -- "$output"
mkdir -p "$(dirname "$output")"
mv "$stage" "$output"
trap - EXIT
printf '%s\n' "$output"
