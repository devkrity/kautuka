#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 || ! -d "$1" ]]; then
  echo "Usage: $0 <extracted-sbom-input-directory>" >&2
  exit 2
fi

root="$1"

# Appliance archives preserve upstream read-only modes. The release runner owns
# the extracted temporary tree, but cannot remove entries from mode 0555 dirs.
# Add owner permissions only to temporary regular files and directories so
# scanners can read them and the runner can clean them up afterward.
find "$root" -type d -exec chmod u+rwx {} +
find "$root" -type f -exec chmod u+rw {} +
