#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
test_root="$(mktemp -d)"
trap 'rm -rf "$test_root"' EXIT

export KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1
source "$script_dir/kautuka-install-linux.sh.in"

# An untouched machine has nothing to validate. A partial existing inventory
# must fail without deleting or changing any of the surviving identity files.
validate_existing_channel_security_inventory "$test_root"
mkdir -p "$test_root/security/channel/gateway-private"
printf 'preserve me\n' > "$test_root/security/channel/gateway-private/gateway-channel-client.pfx"
if (validate_existing_channel_security_inventory "$test_root") 2>/dev/null; then
    echo 'Incomplete channel identity was accepted.' >&2
    exit 1
fi
[[ "$(cat "$test_root/security/channel/gateway-private/gateway-channel-client.pfx")" == 'preserve me' ]]

for path in gateway-private/gateway-channel-delegation.pfx gateway-trust/channel-ca.cer \
    channel-private/channel-server.pfx channel-trust/channel-ca.cer \
    channel-trust/gateway-channel-client.cer channel-trust/gateway-channel-delegation.cer \
    offline-authority/channel-ca.key offline-authority/channel-ca.cer; do
    mkdir -p "$(dirname "$test_root/security/channel/$path")"
    touch "$test_root/security/channel/$path"
done
validate_existing_channel_security_inventory "$test_root"
echo 'Linux installer upgrade inventory checks passed.'
