#!/usr/bin/env bash
set -euo pipefail

[[ ${EUID} -eq 0 ]] || { echo 'error: run this test as root' >&2; exit 1; }
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
temp_dir="$(mktemp -d)"
trap 'rm -rf -- "$temp_dir"' EXIT

set +e
KAUTUKA_APPLY_DETACHED=1 KAUTUKA_APPLY_LOCK="$temp_dir/lock" \
    bash "$script_dir/kautuka-apply-release.sh" --revert v0.1.0 >"$temp_dir/output" 2>&1
result=$?
set -e
[[ $result -eq 2 ]] && grep -Fq 'error: unknown argument: --revert' "$temp_dir/output" || {
    echo 'error: legacy release revert command did not fail explicitly' >&2
    cat "$temp_dir/output" >&2
    exit 1
}

echo 'legacy external revert request fails explicitly without running recovery'
