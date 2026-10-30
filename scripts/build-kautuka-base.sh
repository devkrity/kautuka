#!/usr/bin/env bash
# Build and test the separately versioned kautuka-base runtime image.
set -euo pipefail

source_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
runtime="${KAUTUKA_RELEASE_CONTAINER_RUNTIME:-podman}"
fedora_image="${FEDORA_IMAGE:-quay.io/fedora/fedora-minimal:44}"
local_tag="${KAUTUKA_BASE_LOCAL_TAG:-kautuka-base:test}"
dockerfile="$source_root/src/images/kautuka-base/Dockerfile"
context="$source_root/src/images/kautuka-base"

# podman accepts --pull=always. docker build treats --pull as a boolean and
# rejects the podman value. Both forms refresh the Fedora 44 tag.
if [[ "$runtime" == "docker" ]]; then
  pull_args=(--pull)
else
  pull_args=(--pull=always)
fi

"$runtime" build "${pull_args[@]}" \
  --build-arg "FEDORA_IMAGE=${fedora_image}" \
  -f "$dockerfile" \
  -t "$local_tag" \
  "$context"

bash "$source_root/scripts/test-kautuka-base.sh" "$local_tag"

printf '%s\n' "$local_tag"
