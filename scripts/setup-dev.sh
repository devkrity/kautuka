#!/usr/bin/env bash
set -eo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
args=()
for arg in "$@"; do [[ "$arg" == '--check-only' ]] && arg='--check'; args+=("$arg"); done
exec bash "$ROOT/setup_the_world.sh" "${args[@]}"
