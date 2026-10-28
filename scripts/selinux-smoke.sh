#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: scripts/selinux-smoke.sh [options]

Runs a focused Fedora sandbox smoke check for bind-mount relabeling and non-root write access.

Options:
  --require-enforcing   Fail instead of skipping when SELinux is unavailable or not enforcing/permissive.
  -h, --help            Show this help.
EOF
}

require_enforcing='false'

while [[ $# -gt 0 ]]; do
  case "$1" in
    --require-enforcing)
      require_enforcing='true'
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "error: unknown argument: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

if ! command -v podman >/dev/null 2>&1; then
  echo 'SELinux smoke skipped: podman is not available.'
  [[ "$require_enforcing" == 'true' ]] && exit 1 || exit 0
fi

if ! command -v getenforce >/dev/null 2>&1; then
  echo 'SELinux smoke skipped: getenforce is not available.'
  [[ "$require_enforcing" == 'true' ]] && exit 1 || exit 0
fi

mode="$(getenforce 2>/dev/null || true)"
case "$mode" in
  Enforcing|Permissive)
    ;;
  *)
    echo "SELinux smoke skipped: SELinux mode is '${mode:-unknown}'."
    [[ "$require_enforcing" == 'true' ]] && exit 1 || exit 0
    ;;
esac

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
image_ref='localhost/kautuka-selinux-smoke:latest'
work_dir="$(mktemp -d "$repo_root/.selinux-smoke.XXXXXX")"
host_workspace="$work_dir/workspace"

cleanup() {
  podman image rm -f "$image_ref" >/dev/null 2>&1 || true
  rm -rf "$work_dir"
}
trap cleanup EXIT

mkdir -p "$host_workspace"
chmod 0777 "$host_workspace"

podman build \
  -t "$image_ref" \
  -f "$repo_root/src/backend/Kautuka.Manager/SandboxAssets/Dockerfile" \
  "$repo_root/src/backend/Kautuka.Manager/SandboxAssets" \
  >/dev/null

podman run --rm \
  -v "$host_workspace:/workspace:Z" \
  "$image_ref" \
  sh -lc 'node --version >/dev/null && python3 --version >/dev/null && printf "selinux-smoke\n" > /workspace/probe.txt && test -s /workspace/probe.txt'

if [[ ! -s "$host_workspace/probe.txt" ]]; then
  echo 'error: SELinux smoke failed: probe file was not created on the host bind mount.' >&2
  exit 1
fi

echo "SELinux smoke passed (${mode})."