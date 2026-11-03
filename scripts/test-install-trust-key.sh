#!/usr/bin/env bash
set -euo pipefail

[[ ${EUID} -eq 0 ]] || { echo 'error: run this test as root' >&2; exit 1; }
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
temp_dir="$(mktemp -d)"
trap 'rm -rf "$temp_dir"' EXIT
chmod 755 "$temp_dir"
key="$temp_dir/manifest-public-key.pem"
printf 'old user-controlled key\n' > "$key"
chown nobody "$key"

source <(sed -n '/^write_trusted_manifest_public_key() {/,/^}/p' "$script_dir/kautuka-install-linux.sh.in")
write_trusted_manifest_public_key 'test public key' "$key"
[[ "$(stat -c '%U:%G %a' "$key")" == 'root:root 644' ]]
runuser -u nobody -- test -r "$key"
if runuser -u nobody -- sh -c 'printf tampered > "$1"' sh "$key" 2>/dev/null; then
    echo 'error: an unprivileged user replaced the trusted key' >&2
    exit 1
fi
[[ "$(cat "$key")" == 'test public key' ]]
echo 'trusted manifest key ownership test passed'
