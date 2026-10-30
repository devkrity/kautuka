#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo 'usage: find-github-release-by-tag.sh <owner/repository> <vX.Y.Z[-preview.N]>' >&2
  exit 2
fi

repository="$1"
tag="$2"
gh_bin="${GH_BIN:-gh}"
if [[ ! "$repository" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]]; then
  echo 'error: repository must be owner/name.' >&2
  exit 2
fi
if [[ ! "$tag" =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-preview\.(0|[1-9][0-9]*))?$ ]]; then
  echo "error: unsupported release tag '$tag'." >&2
  exit 2
fi

# The REST by-tag endpoint does not return draft releases. Search the paginated
# collection instead; drafts are visible there and are what the publisher needs.
pages="$("$gh_bin" api --paginate --slurp "repos/$repository/releases?per_page=100")"
matches="$(jq -c --arg tag "$tag" '[.[][] | select(.tag_name == $tag)]' <<< "$pages")"
match_count="$(jq -r 'length' <<< "$matches")"
if [[ "$match_count" == '0' ]]; then
  printf 'null\n'
elif [[ "$match_count" == '1' ]]; then
  jq -c '.[0]' <<< "$matches"
else
  echo "error: GitHub returned multiple releases for tag '$tag'." >&2
  exit 1
fi
