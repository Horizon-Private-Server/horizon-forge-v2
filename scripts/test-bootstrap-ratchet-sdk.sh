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

echo "Bootstrap local-source and revision-mismatch checks passed"
