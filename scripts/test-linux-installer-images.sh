#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_root="$(mktemp -d)"
trap 'rm -rf "$test_root"' EXIT
payload="$test_root/payload"
mkdir -p "$payload/sandbox" "$payload/sidecar-assets/pkgs-proxy"
cat > "$payload/runtime-images.json" <<'JSON'
{
  "schemaVersion": 1,
  "images": [
    {"id": "kautuka-base", "image": "ghcr.io/devkrity/kautuka-base@sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", "archive": "kautuka-base.tar"},
    {"id": "openobserve", "image": "ghcr.io/devkrity/kautuka-observability@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "archive": "openobserve.tar"},
    {"id": "git-pkgs-proxy", "image": "ghcr.io/devkrity/kautuka-package-proxy@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "archive": "git-pkgs-proxy.tar"}
  ]
}
JSON
cat > "$payload/sidecars.json" <<'JSON'
{
  "sidecars": [
    {"name": "kautuka-pkgs-proxy", "image": "localhost/kautuka-pkgs-proxy:local", "buildContext": "sidecar-assets/pkgs-proxy", "dockerfile": null}
  ]
}
JSON
cat > "$payload/sandbox/Dockerfile" <<'DOCKERFILE'
ARG FEDORA_IMAGE=quay.io/fedora/fedora-minimal:44
FROM ${FEDORA_IMAGE}
DOCKERFILE
cat > "$payload/sidecar-assets/pkgs-proxy/Dockerfile" <<'DOCKERFILE'
FROM ghcr.io/devkrity/kautuka-package-proxy@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
DOCKERFILE

MOCK_PODMAN_LOG="$test_root/podman.log" KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1 bash -c '
  source "$1/scripts/kautuka-install-linux.sh.in"
  run_as_service_user() {
    shift 3
    printf "%s\n" "$*" >> "$MOCK_PODMAN_LOG"
    case "$*" in
      "podman image exists"*) return 1 ;;
    esac
    return 0
  }
  prepare_install_images "$2" kautuka /home/kautuka /run/user/1000
' _ "$repo_root" "$payload" > "$test_root/progress.log"

for expected in \
  'podman pull ghcr.io/devkrity/kautuka-base@sha256:' \
  'podman pull ghcr.io/devkrity/kautuka-observability@sha256:' \
  'podman pull ghcr.io/devkrity/kautuka-package-proxy@sha256:'; do
  grep -F "$expected" "$test_root/podman.log" >/dev/null || {
    echo "Expected rootless Podman preparation step was not run: $expected" >&2
    exit 1
  }
done

grep -F 'Downloading pinned runtime image: openobserve' "$test_root/progress.log" >/dev/null
grep -F 'Downloading pinned runtime image: kautuka-base' "$test_root/progress.log" >/dev/null
if grep -F 'podman pull quay.io/fedora/fedora-minimal:44' "$test_root/podman.log" >/dev/null; then
  echo 'A release with a pinned Kautuka base image must not pull the fallback Fedora base.' >&2
  exit 1
fi
grep -F 'Package proxy runtime image ready:' "$test_root/progress.log" >/dev/null
grep -F 'Manager will validate or build the local health-check wrapper: localhost/kautuka-pkgs-proxy:local' "$test_root/progress.log" >/dev/null
if grep -F 'podman build' "$test_root/podman.log" >/dev/null; then
  echo 'The installer must leave package proxy wrapper validation/building to Manager.' >&2
  exit 1
fi

python3 - "$payload/runtime-images.json" <<'PY'
import json
import sys

path = sys.argv[1]
with open(path, encoding="utf-8") as stream:
    document = json.load(stream)
document["images"] = [image for image in document["images"] if image["id"] != "kautuka-base"]
with open(path, "w", encoding="utf-8") as stream:
    json.dump(document, stream)
PY

MOCK_PODMAN_LOG="$test_root/fallback-podman.log" KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1 bash -c '
  source "$1/scripts/kautuka-install-linux.sh.in"
  run_as_service_user() {
    shift 3
    printf "%s\n" "$*" >> "$MOCK_PODMAN_LOG"
    case "$*" in
      "podman image exists"*) return 1 ;;
    esac
    return 0
  }
  prepare_install_images "$2" kautuka /home/kautuka /run/user/1000
' _ "$repo_root" "$payload" > "$test_root/fallback-progress.log"

grep -F 'podman pull quay.io/fedora/fedora-minimal:44' "$test_root/fallback-podman.log" >/dev/null || {
  echo 'A local build without a pinned Kautuka base must prefetch its Fedora Dockerfile base.' >&2
  exit 1
}

echo 'Linux installer image preparation tests passed.'
