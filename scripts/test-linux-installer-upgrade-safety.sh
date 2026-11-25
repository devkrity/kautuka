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
# Provision only the named appliance directories, then preserve populated state on resume.
appliance_root="$test_root/appliance"
mkdir "$appliance_root"
service_user="$(id -un)"
group="$(id -gn)"
prepare_appliance_state_directories "$appliance_root" "$service_user" "$group"
for directory in manager updateService logs/manager workspaces; do
    [[ "$(stat -c '%u:%g:%a' "$appliance_root/$directory")" == "$(id -u):$(id -g):700" ]]
done

connect="$appliance_root/manager/networkAccess/gateway-connect"
registry="$appliance_root/.local/state/kautuka/nac"
mkdir -p "$connect/gateway" "$connect/envoy" "$registry"
chmod 0700 "$connect" "$connect/gateway" "$connect/envoy" "$registry"
touch "$connect/gateway/client.pfx" "$connect/envoy/server.key" "$registry/netns-owners.json" "$appliance_root/updateService/client.pfx"
chmod 0600 "$connect/gateway/client.pfx" "$registry/netns-owners.json" "$appliance_root/updateService/client.pfx"
chmod 0400 "$connect/envoy/server.key"
prepare_appliance_state_directories "$appliance_root" "$service_user" "$group"
# This unit-level fixture does not require root to normalize the two shared ancestors.
(
    prepare_isolated_service_ancestors() { [[ "$1" == "$appliance_root" && "$2" == "$appliance_root" ]]; }
    prepare_install_state_permissions "$appliance_root" "$appliance_root" "$service_user" "$group" true
)
[[ "$(stat -c '%a' "$connect")" == 700 && "$(stat -c '%a' "$connect/gateway")" == 700 ]]
[[ "$(stat -c '%a' "$connect/gateway/client.pfx")" == 600 && "$(stat -c '%a' "$connect/envoy/server.key")" == 400 ]]
[[ "$(stat -c '%a' "$registry/netns-owners.json")" == 600 && "$(stat -c '%a' "$appliance_root/updateService/client.pfx")" == 600 ]]

echo 'Linux installer upgrade inventory checks passed.'
