#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf -- "$tmp"' EXIT
source <(sed -n '/^stage_nac_hook_for_rootfs() {/,/^}/p' "$repo_root/scripts/build-release-assets.sh")
source <(sed -n '/^assert_packed_appliance_network_seal() {/,/^}/p' "$repo_root/scripts/build-release-assets.sh")
sudo() {
    if [[ $1 == chown && $2 == root:root ]]; then return 0; fi
    "$@"
}
version=0.0.1
manager="$tmp/rootfs/opt/kautuka/manager"
version_root="$manager/versions/$version"
hooks="$tmp/rootfs/var/lib/kautuka/.config/containers/hooks.d"
mkdir -p "$version_root/scripts" "$hooks" "$tmp/rootfs/usr/lib/tmpfiles.d"
ln -s "versions/$version" "$manager/current"
cp -L "$(type -P true)" "$version_root/scripts/kautuka-oci-hook"
chmod 0755 "$version_root/scripts/kautuka-oci-hook"
touch "$hooks/kautuka_hook.py" "$hooks/kautuka_workload.nft.json.tmpl" "$hooks/kautuka_package_proxy.nft.json.tmpl"
stage_nac_hook_for_rootfs "$tmp/rootfs" "$version_root" "$(id -u)" "$(id -g)"
[[ ! -e "$hooks/kautuka_hook.py" && ! -e "$hooks/kautuka_workload.nft.json.tmpl" && ! -e "$hooks/kautuka_package_proxy.nft.json.tmpl" ]]
[[ -x "$manager/current/scripts/kautuka-oci-hook" ]]
python3 - "$hooks/kautuka_hook.json" "$tmp/rootfs/var/lib/kautuka/.config/containers/containers.conf.d/99-kautuka-hooks.conf" <<'PY'
import json,sys,tomllib
with open(sys.argv[1]) as f: hook=json.load(f)
assert hook['hook']=={'path':'/opt/kautuka/manager/current/scripts/kautuka-oci-hook','timeout':45}
assert hook['when']=={'always':True} and hook['stages']==['createRuntime']
with open(sys.argv[2], 'rb') as f: config=tomllib.load(f)
assert config['engine']['hooks_dir']==['/var/lib/kautuka/.config/containers/hooks.d', {'append':True}]
PY
{
    for service in manager updateservice gateway credentialshost channelhost; do
        printf 'd /run/kautuka/%s 0710 kautuka kautuka -\n' "$service"
    done
    printf '%s\n' 'f /run/kautuka-db-writer.lock 0644 root root -' 'd /run/kautuka/networkaccess-envoy 0710 kautuka kautuka -'
} > "$tmp/rootfs/usr/lib/tmpfiles.d/kautuka-sockets.conf"
tar --owner=0 --group=0 -czf "$tmp/rootfs.tar.gz" -C "$tmp/rootfs" .
assert_packed_appliance_network_seal "$tmp/rootfs.tar.gz" "" "$version"
ln -sfn versions/invalid "$manager/current"
tar --owner=0 --group=0 -czf "$tmp/rootfs-invalid.tar.gz" -C "$tmp/rootfs" .
if (assert_packed_appliance_network_seal "$tmp/rootfs-invalid.tar.gz" "" "$version") >/dev/null 2>&1; then
    echo 'wrong Manager version pointer was accepted' >&2
    exit 1
fi
rm "$version_root/scripts/kautuka-oci-hook"
if stage_nac_hook_for_rootfs "$tmp/rootfs" "$version_root" "$(id -u)" "$(id -g)" >/dev/null 2>&1; then
    echo 'missing hook was accepted' >&2
    exit 1
fi
printf '#!/bin/sh\nexit 0\n' > "$version_root/scripts/kautuka-oci-hook"
chmod 0755 "$version_root/scripts/kautuka-oci-hook"
if stage_nac_hook_for_rootfs "$tmp/rootfs" "$version_root" "$(id -u)" "$(id -g)" >/dev/null 2>&1; then
    echo 'non-ELF hook was accepted' >&2
    exit 1
fi
echo 'WSL NAC hook staging checks passed.'
