#!/usr/bin/env bash
set -euo pipefail

[[ $# == 1 ]] || {
  echo "Usage: $0 /path/to/pinned/ratchet-ps2-cli" >&2
  exit 2
}

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
"$script_dir/bootstrap-ratchet-sdk.sh" --source "$1"

mismatch_root=$(mktemp -d)
trap 'rm -rf "$mismatch_root"' EXIT
git -C "$mismatch_root" init --quiet
git -C "$mismatch_root" -c user.name=Forge -c user.email=forge.invalid commit --allow-empty --quiet -m mismatch

if "$script_dir/bootstrap-ratchet-sdk.sh" --source "$mismatch_root" 2>/dev/null; then
  echo "Bootstrap accepted an unpinned SDK revision" >&2
  exit 1
fi

# Check patch repeatability and refusal of unrelated work without changing the real SDK index.
sdk_root="$mismatch_root/pinned"
git clone --quiet --shared --no-checkout "$1" "$sdk_root"
selector=$(tr -d '[:space:]' < "$script_dir/../ratchet-sdk.version")
git -C "$sdk_root" checkout --quiet --detach "$selector"
sdk_patch="$script_dir/../build/ratchet-sdk-gc-assets.patch"
source "$script_dir/ratchet-sdk-patch.sh"
sdk_prepare_forge_patch
sdk_matches_forge_patch
sdk_matches_forge_patch
printf '\n// unrelated local edit\n' >> "$sdk_root/src/RatchetPs2.Games.GC/GCGameModule.cs"
if sdk_prepare_forge_patch 2>/dev/null; then
  echo "Bootstrap patch check accepted unrelated tracked changes" >&2
  exit 1
fi
git -C "$sdk_root" restore src/RatchetPs2.Games.GC/GCGameModule.cs
printf 'unrelated\n' > "$sdk_root/unrelated-file"
if sdk_prepare_forge_patch 2>/dev/null; then
  echo "Bootstrap patch check accepted unrelated untracked files" >&2
  exit 1
fi
rm "$sdk_root/unrelated-file"
sdk_matches_forge_patch
# Upgrade a recorded patch, including a change to an existing SDK file.
previous_patch=$sdk_patch
next_patch="$mismatch_root/next.patch"
cp "$sdk_patch" "$next_patch"
printf '\n// next Forge patch\n' >> "$sdk_root/src/RatchetPs2.Games.GC/GCGameModule.cs"
git -C "$sdk_root" diff -- src/RatchetPs2.Games.GC/GCGameModule.cs >> "$next_patch"
git -C "$sdk_root" restore src/RatchetPs2.Games.GC/GCGameModule.cs
sdk_patch=$next_patch
printf '\n// user edit during upgrade\n' >> "$sdk_root/src/RatchetPs2.Games.GC/GCGameModule.cs"
if sdk_prepare_forge_patch 2>/dev/null; then
  echo "Bootstrap upgrade replaced a local edit" >&2
  exit 1
fi
[[ $(tail -1 "$sdk_root/src/RatchetPs2.Games.GC/GCGameModule.cs") == '// user edit during upgrade' ]]
git -C "$sdk_root" restore src/RatchetPs2.Games.GC/GCGameModule.cs
printf 'user data\n' > "$sdk_root/unrelated-file"
if sdk_prepare_forge_patch 2>/dev/null; then
  echo "Bootstrap upgrade accepted an unrelated untracked file" >&2
  exit 1
fi
[[ $(cat "$sdk_root/unrelated-file") == 'user data' ]]
rm "$sdk_root/unrelated-file"
sdk_prepare_forge_patch
sdk_matches_forge_patch
sdk_prepare_forge_patch
# An invalid replacement must leave the recorded patch in place.
sdk_patch="$mismatch_root/invalid.patch"
printf 'invalid patch\n' > "$sdk_patch"
if sdk_prepare_forge_patch 2>/dev/null; then
  echo "Bootstrap accepted an invalid replacement patch" >&2
  exit 1
fi
sdk_patch=$next_patch
sdk_matches_forge_patch
sdk_patch=$previous_patch
sdk_prepare_forge_patch
sdk_matches_forge_patch
# An ignored user file can block a new addition after validation; restore the previous patch.
collision_patch="$mismatch_root/collision.patch"
cp "$previous_patch" "$collision_patch"
printf 'new SDK file\n' > "$sdk_root/forge-upgrade-probe.cs"
(cd "$sdk_root" && git diff --no-index -- /dev/null forge-upgrade-probe.cs >> "$collision_patch") || [[ $? == 1 ]]
printf '/forge-upgrade-probe.cs\n' >> "$sdk_root/.git/info/exclude"
printf 'user data\n' > "$sdk_root/forge-upgrade-probe.cs"
sdk_patch=$collision_patch
if sdk_prepare_forge_patch 2>/dev/null; then
  echo "Bootstrap upgrade replaced an ignored user file" >&2
  exit 1
fi
[[ $(cat "$sdk_root/forge-upgrade-probe.cs") == 'user data' ]]
sdk_matches_forge_patch "$previous_patch"
rm "$sdk_root/forge-upgrade-probe.cs"
sdk_patch=$previous_patch
sdk_remove_forge_patch
[[ -z $(git -C "$sdk_root" status --porcelain) ]]
echo "Bootstrap local-source, revision-mismatch, and GC patch preservation checks passed"
