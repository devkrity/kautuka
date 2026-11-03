#!/usr/bin/env bash
set -euo pipefail
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
temp_dir="$(mktemp -d)"
trap 'rm -rf "$temp_dir"' EXIT
source <(sed -n '/^assert_no_unresolved_update() {/,/^}/p' "$script_dir/kautuka-install-linux.sh.in")
fail() { echo "$*" >&2; exit 1; }

assert_no_unresolved_update "$temp_dir"
for phase in capturing verified verifying starting restoring recovered; do
    printf '{"phase":"%s"}\n' "$phase" > "$temp_dir/operation.json"
    if (assert_no_unresolved_update "$temp_dir") 2>/dev/null; then
        echo "installer accepted unresolved phase $phase" >&2; exit 1
    fi
done
for phase in committed completed aborted; do
    printf '{"phase":"%s"}\n' "$phase" > "$temp_dir/operation.json"
    assert_no_unresolved_update "$temp_dir"
    touch "$temp_dir/maintenance.json"
    if (assert_no_unresolved_update "$temp_dir") 2>/dev/null; then
        echo "installer accepted fenced phase $phase" >&2; exit 1
    fi
    rm "$temp_dir/maintenance.json"
done
printf '{invalid' > "$temp_dir/operation.json"
if (assert_no_unresolved_update "$temp_dir") 2>/dev/null; then
    echo 'installer accepted malformed journal' >&2; exit 1
fi
echo 'installer rejects unresolved update transactions'
