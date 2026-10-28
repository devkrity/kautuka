#!/usr/bin/env bash
# Contract for the finished sandbox image. Fedora latest is only the build input.
# These checks are the tools and identity the appliance actually requires.
set -euo pipefail

image="${1:?image name or id}"
runtime="${KAUTUKA_RELEASE_CONTAINER_RUNTIME:-podman}"

"$runtime" run --rm "$image" bash -lc '
set -euo pipefail
test -x /usr/bin/bash
command -v python3 >/dev/null
command -v pip3 >/dev/null || command -v pip >/dev/null
command -v git >/dev/null
command -v curl >/dev/null
command -v tar >/dev/null
test "$(id -u)" = 1000
test "$(id -un)" = node
test "$(id -u node)" = 1000
test "$(node -v)" = "v26.3.0"
command -v uv >/dev/null
command -v uvx >/dev/null
uv --version | grep -q "0.11.19"
'
