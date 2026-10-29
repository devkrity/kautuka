#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 6 ]]; then
  echo 'usage: prepare-release-source-snapshot.sh <repo-root> <source-root> <stage-dir> <private-key> <cache-root> <compiler-context>' >&2
  exit 2
fi

repo_root="$(realpath -e -- "$1")"
source_root="$2"
stage_dir="$3"
private_key="$4"
cache_root="$5"
compiler_context="$6"

mkdir -p "$source_root" "$stage_dir" "$compiler_context"
if [[ -n "$(find "$source_root" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
  echo 'error: source snapshot destination must be an empty dedicated directory.' >&2
  exit 1
fi
if [[ -n "$(find "$compiler_context" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
  echo 'error: compiler image context must be an empty dedicated directory.' >&2
  exit 1
fi

git -C "$repo_root" rev-parse --show-toplevel >/dev/null 2>&1 || {
  echo 'error: release source snapshot requires a Git working tree.' >&2
  exit 1
}

snapshot_excludes=(
  --exclude='.git'
  --exclude='artifacts'
  --exclude='publish'
  --exclude='src/frontend/node_modules'
  --exclude='src/frontend/web/node_modules'
  --exclude='src/frontend/web/dist'
)

cache_root_resolved="$(realpath -m -- "$cache_root")"
if [[ "$cache_root_resolved" == "$repo_root"/* ]]; then
  snapshot_excludes+=(--exclude="${cache_root_resolved#"$repo_root"/}")
fi

if [[ -n "$private_key" ]]; then
  if [[ "$private_key" == /* ]]; then
    private_key_resolved="$(realpath -m -- "$private_key")"
  else
    private_key_resolved="$(realpath -m -- "$repo_root/$private_key")"
  fi

  if [[ "$private_key_resolved" == "$repo_root"/* ]]; then
    private_key_relative="${private_key_resolved#"$repo_root"/}"
    snapshot_excludes+=(
      --exclude="$private_key_relative"
      --exclude="./$private_key_relative"
    )
  fi
fi

(
  cd "$repo_root"
  # Preserve the current contents of tracked files (including unstaged edits),
  # plus non-ignored untracked source files, while leaving ignored local data,
  # credentials, caches, and other workspace-only files out of build contexts.
  git ls-files --cached --others --exclude-standard -z \
    | while IFS= read -r -d '' relative_path; do
        [[ -e "$relative_path" || -L "$relative_path" ]] || continue
        printf './%s\0' "$relative_path"
      done \
    | tar "${snapshot_excludes[@]}" --null --no-recursion -cf - --files-from -
) | tar -xf - -C "$source_root"

cp "$source_root/LICENSE" "$stage_dir/LICENSE"
cp "$source_root/NOTICE" "$stage_dir/NOTICE"
