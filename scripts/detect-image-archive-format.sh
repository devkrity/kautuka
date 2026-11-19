#!/usr/bin/env bash
set -euo pipefail
entries="$(tar -tf "$1")"
if printf '%s\n' "$entries" | grep -Ex '(\./)?manifest\.json' > /dev/null; then
  echo docker-archive
elif printf '%s\n' "$entries" | grep -Ex '(\./)?oci-layout' > /dev/null; then
  echo oci-archive
else
  echo 'error: unrecognized image archive format' >&2
  exit 1
fi
