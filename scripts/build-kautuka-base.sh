#!/usr/bin/env bash
# Build kautuka-base from Fedora 44, prove the sandbox contract, and optionally push it.
# The pushed name is ghcr.io/devkrity/kautuka-base:<release-tag>. The digest is printed last.
set -euo pipefail

source_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
runtime="${KAUTUKA_RELEASE_CONTAINER_RUNTIME:-podman}"
fedora_image="${FEDORA_IMAGE:-quay.io/fedora/fedora-minimal:44}"
local_tag="${KAUTUKA_BASE_LOCAL_TAG:-kautuka-base:test}"
dockerfile="$source_root/src/backend/Kautuka.Manager/SandboxAssets/Dockerfile"
context="$source_root/src/backend/Kautuka.Manager/SandboxAssets"

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

if [[ -z "${KAUTUKA_BASE_PUSH:-}" ]]; then
  printf '%s\n' "$local_tag"
  exit 0
fi

"$runtime" tag "$local_tag" "$KAUTUKA_BASE_PUSH"
"$runtime" push "$KAUTUKA_BASE_PUSH"
"$runtime" image inspect --format '{{range .RepoDigests}}{{println .}}{{end}}' "$local_tag" \
  | grep "^ghcr.io/devkrity/kautuka-base@sha256:" \
  | head -n 1
