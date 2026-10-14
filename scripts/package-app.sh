#!/usr/bin/env bash
set -euo pipefail

[[ $# == 4 ]] || { echo "usage: package-app.sh <runtime> <platform> <electron-binary> <app-binary>" >&2; exit 2; }
runtime=$1
platform=$2
electron_binary=$3
app_binary=$4
forge_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
output="$forge_root/artifacts/horizon-forge-$platform"
stage=$(mktemp -d)
trap 'rm -rf -- "$stage"' EXIT

version=${FORGE_VERSION:-$(node -p "require(process.argv[1]).version" "$forge_root/package.json")}
channel=${FORGE_CHANNEL:-development}
commit=${FORGE_COMMIT:-$(git -C "$forge_root" rev-parse HEAD)}
sdk_revision=$(tr -d '[:space:]' < "$forge_root/ratchet-sdk.version")

npm --prefix "$forge_root" run build:desktop
dotnet publish "$forge_root/src/Forge.Host/Forge.Host.csproj" \
  --configuration Release \
  --runtime "$runtime" \
  --self-contained true \
  --output "$stage/resources/host" \
  -p:Version="$version" \
  -p:InformationalVersion="$version+$commit" \
  -p:IncludeSourceRevisionInInformationalVersion=false
dotnet publish "$forge_root/src/Forge.Updater/Forge.Updater.csproj" \
  --configuration Release \
  --runtime "$runtime" \
  --self-contained true \
  --output "$stage/resources/updater" \
  -p:Version="$version" \
  -p:InformationalVersion="$version+$commit" \
  -p:IncludeSourceRevisionInInformationalVersion=false

cp -a "$forge_root/node_modules/electron/dist/." "$stage/"
mv "$stage/$electron_binary" "$stage/$app_binary"
mkdir -p "$stage/resources/app"
cp -a "$forge_root/dist" "$forge_root/dist-electron" "$forge_root/package.json" "$stage/resources/app/"
npm --prefix "$stage/resources/app" pkg set \
  "version=$version" \
  "forge.channel=$channel" \
  "forge.commit=$commit" \
  "forge.sdkRevision=$sdk_revision" \
  "forge.bridgeProtocol=1" \
  "forge.platform=$platform"

update_key_id=${FORGE_UPDATE_KEY_ID:-}
update_public_key=${FORGE_UPDATE_PUBLIC_KEY_SPKI:-}
if [[ -n "$update_key_id" || -n "$update_public_key" ]]; then
  [[ -n "$update_key_id" && -n "$update_public_key" ]] \
    || { echo "update key ID and public key must be configured together" >&2; exit 1; }
  npm --prefix "$stage/resources/app" pkg set \
    "forge.updateKeys[0].id=$update_key_id" \
    "forge.updateKeys[0].publicKeySpkiBase64=$update_public_key"
fi

node "$forge_root/scripts/package-manifest.mjs" \
  "$stage" "$version" "$channel" "$commit" "$sdk_revision" "$platform" "$app_binary"

rm -rf -- "$output"
mkdir -p "$(dirname "$output")"
mv "$stage" "$output"
trap - EXIT
printf '%s\n' "$output"
