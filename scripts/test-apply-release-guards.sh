#!/usr/bin/env bash
set -euo pipefail

# Run as root in a disposable Linux environment. The helper must reject the old
# release-revert CLI explicitly without invoking any update transaction.
[[ ${EUID} -eq 0 ]] || { echo 'error: run this test as root' >&2; exit 1; }
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
temp_dir="$(mktemp -d)"
trap 'rm -rf "$temp_dir"' EXIT

set +e
KAUTUKA_APPLY_DETACHED=1 KAUTUKA_APPLY_LOCK="$temp_dir/lock" \
    bash "$script_dir/kautuka-apply-release.sh" --revert v0.1.0 >"$temp_dir/output" 2>&1
result=$?
set -e
if [[ $result -ne 2 ]] || ! grep -Fq 'error: unknown argument: --revert' "$temp_dir/output"; then
    echo "error: helper did not explicitly reject the retired revert command (exit $result)" >&2
    cat "$temp_dir/output" >&2
    exit 1
fi

echo 'apply-release version guards passed'
