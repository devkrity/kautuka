#!/usr/bin/env bash
set -euo pipefail

repository="${1:-${GITHUB_REPOSITORY:-}}"
if [[ ! "$repository" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]]; then
  echo 'error: a valid owner/repository is required to verify release immutability.' >&2
  exit 2
fi

if ! enabled="$(gh api \
  -H 'X-GitHub-Api-Version: 2026-03-10' \
  "repos/${repository}/immutable-releases" \
  --jq '.enabled' 2>/dev/null)"; then
  echo 'error: could not read GitHub release-immutability settings; keep release publication disabled.' >&2
  exit 1
fi

if [[ "$enabled" != 'true' ]]; then
  echo 'error: GitHub release immutability is not enabled; keep release publication disabled.' >&2
  exit 1
fi

printf 'GitHub release immutability is enabled for %s.\n' "$repository"
