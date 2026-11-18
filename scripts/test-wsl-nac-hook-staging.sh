#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf -- "$tmp"' EXIT
source <(sed -n '/^stage_nac_hook_for_rootfs() {/,/^}/p' "$repo_root/scripts/build-release-assets.sh")
sudo() { "$@"; }
mkdir -p "$tmp/payload/scripts" "$tmp/rootfs"
for f in kautuka_hook.py kautuka_workload.nft.json.tmpl kautuka_package_proxy.nft.json.tmpl; do printf '%s\n' "$f" > "$tmp/payload/scripts/$f"; done
stage_nac_hook_for_rootfs "$tmp/rootfs" "$tmp/payload" "$(id -u)" "$(id -g)"
hooks="$tmp/rootfs/var/lib/kautuka/.config/containers/hooks.d"
for f in kautuka_hook.py kautuka_workload.nft.json.tmpl kautuka_package_proxy.nft.json.tmpl; do cmp "$tmp/payload/scripts/$f" "$hooks/$f"; done
python3 - "$hooks/kautuka_hook.json" "$tmp/rootfs/var/lib/kautuka/.config/containers/containers.conf.d/99-kautuka-hooks.conf" <<'PY'
import json,sys,tomllib
with open(sys.argv[1]) as f: hook=json.load(f)
assert hook['hook']=={'path':'/var/lib/kautuka/.config/containers/hooks.d/kautuka_hook.py','timeout':45}
assert hook['when']=={'always':True} and hook['stages']==['createRuntime']
with open(sys.argv[2], 'rb') as f: config=tomllib.load(f)
assert config['engine']['hooks_dir']==['/var/lib/kautuka/.config/containers/hooks.d', {'append':True}]
PY
rm "$tmp/payload/scripts/kautuka_hook.py"
if stage_nac_hook_for_rootfs "$tmp/rootfs" "$tmp/payload" "$(id -u)" "$(id -g)" >/dev/null 2>&1; then echo 'missing hook was accepted' >&2; exit 1; fi
echo 'WSL NAC hook staging checks passed.'
