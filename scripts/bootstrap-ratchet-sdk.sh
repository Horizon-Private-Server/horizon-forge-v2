#!/usr/bin/env bash
set -euo pipefail

forge_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
version_file="$forge_root/ratchet-sdk.version"
managed_root="$forge_root/.dependencies/ratchet-ps2-cli"
repository=${RATCHET_SDK_REPOSITORY:-https://github.com/badger41/ratchet-ps2-cli.git}

usage() {
  echo "Usage: $0 [--source /path/to/ratchet-ps2-cli]" >&2
  exit 2
}

source_root=
case $# in
  0) ;;
  2)
    [[ $1 == --source ]] || usage
    source_root=$2
    ;;
  *) usage ;;
esac

revision=$(tr -d '[:space:]' < "$version_file")
[[ $revision =~ ^[0-9a-f]{40}$ ]] || {
  echo "Invalid Ratchet SDK revision in $version_file" >&2
  exit 1
}

if [[ -n $source_root ]]; then
  [[ -d $source_root ]] || {
    echo "Ratchet SDK source directory does not exist: $source_root" >&2
    exit 1
  }
  sdk_root=$(git -C "$source_root" rev-parse --show-toplevel 2>/dev/null) || {
    echo "Ratchet SDK source is not a Git checkout: $source_root" >&2
    exit 1
  }
else
  sdk_root=$managed_root
  if [[ ! -d $sdk_root/.git ]]; then
    [[ ! -e $sdk_root ]] || {
      echo "Managed Ratchet SDK path exists but is not a Git checkout: $sdk_root" >&2
      exit 1
    }
    mkdir -p "$(dirname "$sdk_root")"
    git clone --filter=blob:none "$repository" "$sdk_root"
  fi

  if ! git -C "$sdk_root" cat-file -e "$revision^{commit}" 2>/dev/null; then
    git -C "$sdk_root" fetch --depth 1 origin "$revision"
  fi

  if [[ ! -e $sdk_root/.git/index ]]; then
    git -C "$sdk_root" checkout --detach "$revision"
  fi

  current_revision=$(git -C "$sdk_root" rev-parse HEAD 2>/dev/null || true)
  if [[ $current_revision != "$revision" ]]; then
    [[ -z $(git -C "$sdk_root" status --porcelain) ]] || {
      echo "Managed Ratchet SDK has local changes; refusing to replace them: $sdk_root" >&2
      exit 1
    }
    git -C "$sdk_root" checkout --detach "$revision"
  fi
fi

actual_revision=$(git -C "$sdk_root" rev-parse HEAD)
[[ $actual_revision == "$revision" ]] || {
  echo "Ratchet SDK revision mismatch: expected $revision, found $actual_revision at $sdk_root" >&2
  exit 1
}

[[ -z $(git -C "$sdk_root" status --porcelain) ]] || {
  echo "Ratchet SDK checkout has local changes and cannot represent pinned revision $revision: $sdk_root" >&2
  exit 1
}

sdk_project="$sdk_root/src/RatchetPs2.Sdk/RatchetPs2.Sdk.csproj"
core_project="$sdk_root/src/RatchetPs2.Core/RatchetPs2.Core.csproj"
uya_project="$sdk_root/src/RatchetPs2.Games.UYA/RatchetPs2.Games.UYA.csproj"
for project in "$sdk_project" "$core_project" "$uya_project"; do
  [[ -f $project ]] || {
    echo "Required Ratchet SDK project is missing: $project" >&2
    exit 1
  }
done

dotnet restore "$sdk_project"
dotnet build "$sdk_project" --configuration Release --no-restore

marker="$forge_root/.dependencies/ratchet-sdk-root"
mkdir -p "$(dirname "$marker")"
msbuild_sdk_root=$sdk_root
if command -v cygpath >/dev/null 2>&1; then
  msbuild_sdk_root=$(cygpath -w "$sdk_root")
fi
printf '%s\n' "$msbuild_sdk_root" > "$marker.tmp"
mv -f "$marker.tmp" "$marker"

dotnet build "$forge_root/src/Forge.Host/Forge.Host.csproj" --configuration Release
echo "Ratchet SDK $revision ready at $sdk_root"
