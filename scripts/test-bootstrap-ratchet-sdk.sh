#!/usr/bin/env bash
set -euo pipefail

[[ $# == 1 ]] || {
  echo "Usage: $0 /path/to/pinned/ratchet-ps2-cli" >&2
  exit 2
}

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
sdk_source=$(git -C "$1" rev-parse --show-toplevel)
bash "$script_dir/bootstrap-ratchet-sdk.sh" --source "$sdk_source"

fixture_root=$(mktemp -d)
trap 'rm -rf "$fixture_root"' EXIT
fixture_forge="$fixture_root/Forge checkout"
mkdir -p "$fixture_forge/scripts" "$fixture_root/bin"
cp "$script_dir/bootstrap-ratchet-sdk.sh" "$fixture_forge/scripts/"
cp "$script_dir/../ratchet-sdk.version" "$fixture_forge/"
# The source bootstrap above builds for real; isolate checkout tests from repeated .NET builds.
printf '#!/usr/bin/env bash\nexit 0\n' > "$fixture_root/bin/dotnet"
chmod +x "$fixture_root/bin/dotnet"
export PATH="$fixture_root/bin:$PATH"
export RATCHET_SDK_REPOSITORY="$sdk_source"
bootstrap="$fixture_forge/scripts/bootstrap-ratchet-sdk.sh"
sdk_root="$fixture_forge/.dependencies/ratchet-ps2-cli"
selector=$(tr -d '[:space:]' < "$fixture_forge/ratchet-sdk.version")
expected_revision=$(git -C "$sdk_source" rev-parse "$selector^{commit}")

# A fresh clone of the released SDK and a repeat run must both stay clean.
bash "$bootstrap"
bash "$bootstrap"
[[ $(git -C "$sdk_root" rev-parse HEAD) == "$expected_revision" ]]
[[ -z $(git -C "$sdk_root" status --porcelain) ]]
marker=$(tr -d '\r\n' < "$fixture_forge/.dependencies/ratchet-sdk-root")
expected_marker=$sdk_root
if command -v cygpath >/dev/null 2>&1; then expected_marker=$(cygpath -w "$sdk_root"); fi
[[ $marker == "$expected_marker" ]]

printf '\n// user edit\n' >> "$sdk_root/src/RatchetPs2.Games.GC/GCGameModule.cs"
if bash "$bootstrap" 2>/dev/null; then
  echo "Bootstrap accepted local SDK edits" >&2
  exit 1
fi
[[ $(tail -1 "$sdk_root/src/RatchetPs2.Games.GC/GCGameModule.cs") == '// user edit' ]]
git -C "$sdk_root" restore src/RatchetPs2.Games.GC/GCGameModule.cs
printf 'user data\n' > "$sdk_root/unrelated-file"
if bash "$bootstrap" --source "$sdk_root" 2>/dev/null; then
  echo "Bootstrap accepted untracked SDK files" >&2
  exit 1
fi
[[ $(cat "$sdk_root/unrelated-file") == 'user data' ]]
rm "$sdk_root/unrelated-file"

# Keep the pinned tree: older blobs may be absent from CI's filtered SDK clone.
git -C "$sdk_root" -c user.name=Forge -c user.email=forge.invalid -c commit.gpgsign=false \
  commit --allow-empty --quiet -m "Bootstrap revision mismatch fixture"
if bash "$bootstrap" --source "$sdk_root" 2>/dev/null; then
  echo "Bootstrap accepted an unpinned SDK revision" >&2
  exit 1
fi
bash "$bootstrap"
[[ $(git -C "$sdk_root" rev-parse HEAD) == "$expected_revision" ]]
echo "Bootstrap source, fresh clone, repeat, revision, and local-change checks passed"
