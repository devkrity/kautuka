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

gateway_state="$test_root/gateway-state"
mkdir -p "$gateway_state/agents/agent-test"
touch "$gateway_state/kautuka.db" "$gateway_state/kautuka.db-wal" \
  "$gateway_state/gateway-auth.key" "$gateway_state/setup-token" \
  "$gateway_state/agents/agent-test/agent.db" \
  "$gateway_state/agents/agent-test/agent.db-wal" \
  "$gateway_state/agents/agent-test/agent.db-shm"
MOCK_STATE_PERMISSIONS_LOG="$test_root/gateway-state-permissions.log" \
  KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1 bash -c '
    source "$1/scripts/kautuka-install-linux.sh.in"
    chown() { printf "chown %s %s\n" "$1" "$2" >> "$MOCK_STATE_PERMISSIONS_LOG"; }
    chmod() { printf "chmod %s %s\n" "$1" "$2" >> "$MOCK_STATE_PERMISSIONS_LOG"; }
    prepare_gateway_database_owner "$2"
  ' _ "$repo_root" "$gateway_state"

for gateway_secret in gateway-auth.key setup-token; do
  grep -F "chown kautuka-gateway:kautuka $gateway_state/$gateway_secret" \
    "$test_root/gateway-state-permissions.log" >/dev/null || {
    echo "Existing Gateway secret was not transferred to its service owner: $gateway_secret" >&2
    exit 1
  }
  grep -F "chmod 0600 $gateway_state/$gateway_secret" \
    "$test_root/gateway-state-permissions.log" >/dev/null || {
    echo "Existing Gateway secret was not restricted to owner-only access: $gateway_secret" >&2
    exit 1
  }
done

for agent_database in agent.db agent.db-wal agent.db-shm; do
  grep -F "chown kautuka-gateway:kautuka $gateway_state/agents/agent-test/$agent_database" \
    "$test_root/gateway-state-permissions.log" >/dev/null || {
    echo "Existing per-agent Gateway database was not transferred to its service owner: $agent_database" >&2
    exit 1
  }
  grep -F "chmod 0600 $gateway_state/agents/agent-test/$agent_database" \
    "$test_root/gateway-state-permissions.log" >/dev/null || {
    echo "Existing per-agent Gateway database was not restricted to owner-only access: $agent_database" >&2
    exit 1
  }
done

policy_state="$test_root/preview-policy-state"
MOCK_POLICY_PERMISSIONS_LOG="$test_root/preview-policy-permissions.log" \
  KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1 bash -c '
    source "$1/scripts/kautuka-install-linux.sh.in"
    id() { return 0; }
    getent() { return 0; }
    chown() { printf "chown %s %s\n" "$1" "$2" >> "$MOCK_POLICY_PERMISSIONS_LOG"; }
    write_initial_update_runtime_policy "$2" preview kautuka kautuka
  ' _ "$repo_root" "$policy_state"
grep -F '"releaseChannel": "preview"' "$policy_state/updateService/update-runtime-policy.json" >/dev/null || {
  echo 'A fresh Preview install must seed UpdateService with the Preview channel.' >&2
  exit 1
}
grep -F "chown kautuka:kautuka $policy_state/updateService/update-runtime-policy.json" \
  "$test_root/preview-policy-permissions.log" >/dev/null || {
  echo 'The initial update policy must be readable by UpdateService.' >&2
  exit 1
}

printf '{\n  "schemaVersion": 1,\n  "releaseChannel": "stable"\n}\n' \
  > "$policy_state/updateService/update-runtime-policy.json"
KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1 bash -c '
  source "$1/scripts/kautuka-install-linux.sh.in"
  write_initial_update_runtime_policy "$2" preview kautuka kautuka
' _ "$repo_root" "$policy_state"
grep -F '"releaseChannel": "stable"' "$policy_state/updateService/update-runtime-policy.json" >/dev/null || {
  echo 'Reinstalling must preserve the user-selected update channel.' >&2
  exit 1
}

echo 'Linux installer image preparation tests passed.'
