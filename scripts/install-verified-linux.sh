#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo 'Usage: scripts/install-verified-linux.sh <downloaded-installer> [installer arguments...]' >&2
  exit 2
fi

installer_path="$(realpath "$1")"
shift
[[ -f "$installer_path" ]] || { echo "error: installer not found: $installer_path" >&2; exit 1; }
command -v gh >/dev/null 2>&1 || { echo 'error: GitHub CLI (gh) is required for provenance verification.' >&2; exit 1; }

gh attestation verify "$installer_path" \
  --repo devkrity/kautuka \
  --signer-workflow devkrity/kautuka/.github/workflows/release-build-platform-assets.yml

exec sudo bash "$installer_path" "$@"
