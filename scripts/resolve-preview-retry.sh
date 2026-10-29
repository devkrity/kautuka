#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 4 ]]; then
  echo 'usage: resolve-preview-retry.sh <repository> <tag> <tag-sha> <main-sha>' >&2
  exit 2
fi

repository="$1"
tag="$2"
tag_sha="$3"
main_sha="$4"
gh_bin="${GH_BIN:-gh}"

if [[ "$tag_sha" != "$main_sha" ]]; then
  echo 'error: only an exact Preview tag on current main can be retried.' >&2
  exit 1
fi

if release="$("$gh_bin" api "repos/$repository/releases/tags/$tag" 2>&1)"; then
  :
else
  if grep -Eq 'HTTP 404|Not Found \(HTTP 404\)' <<< "$release"; then
    # The immutable tag exists, but release creation did not complete. Reuse
    # this exact tag; never create a replacement tag for the same main commit.
    echo 'reuse'
    exit 0
  fi
  echo 'error: GitHub release lookup failed; refusing Preview tag recovery.' >&2
  exit 1
fi

if ! jq -e --arg tag "$tag" --arg sha "$tag_sha" \
  '.tag_name == $tag and .target_commitish == $sha and .prerelease == true' \
  <<< "$release" >/dev/null; then
  echo 'error: Preview release identity does not match its existing tag and source commit.' >&2
  exit 1
fi

draft="$(jq -r '.draft' <<< "$release")"
asset_count="$(jq -er 'if (.assets | type) == "array" then (.assets | length) else error("assets is not an array") end' <<< "$release")" || {
  echo 'error: Preview release asset state is invalid; refusing recovery.' >&2
  exit 1
}

if [[ "$draft" == 'true' ]]; then
  if [[ "$asset_count" == '0' ]]; then
    echo 'reuse'
    exit 0
  fi
  echo 'error: Preview draft has assets from an interrupted upload; maintainer review is required before retry.' >&2
  exit 1
fi

if [[ "$draft" == 'false' ]]; then
  if [[ "$(jq -r '.immutable' <<< "$release")" != 'true' ]]; then
    echo 'error: published Preview release is not immutable; refusing to skip or reuse its tag.' >&2
    exit 1
  fi
  echo 'skip'
  exit 0
fi

echo 'error: Preview release draft state is invalid; refusing recovery.' >&2
exit 1
