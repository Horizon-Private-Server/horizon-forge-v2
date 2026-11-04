#!/usr/bin/env bash
# Shared by bootstrap and its tests; sdk_root and sdk_patch are supplied by the caller.
sdk_matches_forge_patch() (
  local candidate=${1:-$sdk_patch}
  local patch_index
  patch_index=$(mktemp)
  trap 'rm -f "$patch_index" "$patch_index.lock"' EXIT
  export GIT_INDEX_FILE=$patch_index
  git -C "$sdk_root" read-tree HEAD &&
    git -C "$sdk_root" apply --cached "$candidate" &&
    git -C "$sdk_root" diff --quiet &&
    [[ -z $(git -C "$sdk_root" ls-files --others --exclude-standard) ]]
)

sdk_record_forge_patch() {
  local record temporary
  record=$(git -C "$sdk_root" rev-parse --path-format=absolute --git-path forge-gc-assets.patch)
  temporary=$(mktemp "$record.XXXXXX")
  if ! cp "$sdk_patch" "$temporary" || ! mv -f "$temporary" "$record"; then
    rm -f "$temporary"
    return 1
  fi
}

sdk_remove_forge_patch() {
  local candidate record
  candidate=$sdk_patch
  record=$(git -C "$sdk_root" rev-parse --path-format=absolute --git-path forge-gc-assets.patch)
  if ! sdk_matches_forge_patch "$candidate"; then
    [[ -f $record ]] && sdk_matches_forge_patch "$record" || return 1
    candidate=$record
  fi
  git -C "$sdk_root" apply --reverse "$candidate"
}

sdk_prepare_forge_patch() {
  local rollback_patch=
  if [[ -n $(git -C "$sdk_root" status --porcelain) ]]; then
    if sdk_matches_forge_patch; then
      sdk_record_forge_patch
      return
    fi
    # Validate the new patch against HEAD before removing the recorded, known-good patch.
    (
      local patch_index
      patch_index=$(mktemp)
      trap 'rm -f "$patch_index" "$patch_index.lock"' EXIT
      export GIT_INDEX_FILE=$patch_index
      git -C "$sdk_root" read-tree HEAD && git -C "$sdk_root" apply --cached "$sdk_patch"
    ) || return 1
    rollback_patch=$(git -C "$sdk_root" rev-parse --path-format=absolute --git-path forge-gc-assets.patch)
    sdk_remove_forge_patch || return 1
  fi
  if ! git -C "$sdk_root" apply --check "$sdk_patch" || ! git -C "$sdk_root" apply "$sdk_patch"; then
    # Ignored local files can block additions even after the index-only validation succeeds.
    if [[ -n $rollback_patch ]]; then git -C "$sdk_root" apply "$rollback_patch"; fi
    return 1
  fi
  sdk_record_forge_patch
}
