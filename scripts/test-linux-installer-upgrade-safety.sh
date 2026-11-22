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
# The first-run checkpoint can survive a Manager startup and a native reboot.
# Resume must preserve the modes that Manager's CONNECT inventory validates.
manager_state="$test_root/manager"
connect="$manager_state/networkAccess/gateway-connect"
mkdir -p "$connect/gateway" "$connect/envoy" "$test_root/workspaces"
chmod 0700 "$manager_state" "$manager_state/networkAccess" "$connect" "$connect/gateway" "$connect/envoy"
touch "$connect/gateway/client.pfx" "$connect/envoy/server.key" "$test_root/workspaces/state"
chmod 0600 "$connect/gateway/client.pfx" "$test_root/workspaces/state"
chmod 0400 "$connect/envoy/server.key"
group="$(id -gn)"
chown_tree_excluding "$test_root" "$(id -un):$group" "$test_root/security"
grant_shared_group_write_excluding "$test_root" "$group" "$test_root/security"
[[ "$(stat -c '%a' "$connect")" == 700 && "$(stat -c '%a' "$connect/gateway")" == 700 ]]
[[ "$(stat -c '%a' "$connect/gateway/client.pfx")" == 600 && "$(stat -c '%a' "$connect/envoy/server.key")" == 400 ]]
[[ "$(stat -c '%a' "$test_root/workspaces/state")" == 660 ]]

echo 'Linux installer upgrade inventory checks passed.'
