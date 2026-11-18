#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf -- "$tmp"' EXIT
mkdir -p "$tmp/docker" "$tmp/oci" "$tmp/hybrid" "$tmp/unknown"
touch "$tmp/docker/manifest.json" "$tmp/oci/oci-layout" "$tmp/hybrid/manifest.json" "$tmp/hybrid/oci-layout"
for name in docker oci hybrid unknown; do
  tar -cf "$tmp/$name.tar" -C "$tmp/$name" .
done
[[ "$(bash "$root/scripts/detect-image-archive-format.sh" "$tmp/docker.tar")" == docker-archive ]]
[[ "$(bash "$root/scripts/detect-image-archive-format.sh" "$tmp/oci.tar")" == oci-archive ]]
[[ "$(bash "$root/scripts/detect-image-archive-format.sh" "$tmp/hybrid.tar")" == docker-archive ]]
if bash "$root/scripts/detect-image-archive-format.sh" "$tmp/unknown.tar" >/dev/null 2>&1; then
  echo 'unknown image archive format was accepted' >&2
  exit 1
fi
echo 'Image archive format checks passed.'
