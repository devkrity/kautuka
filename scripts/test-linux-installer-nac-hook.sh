#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
temp_root="$(mktemp -d)"
trap 'rm -rf "$temp_root"' EXIT

KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1 source "$repo_root/scripts/kautuka-install-linux.sh.in"

mkdir -p "$temp_root/payload/scripts" "$temp_root/hooks"
printf '%s\n' '#!/usr/bin/env python3' 'print("hook")' > "$temp_root/payload/scripts/kautuka_hook.py"
printf '%s\n' '{"nftables":[]}' > "$temp_root/payload/scripts/kautuka_workload.nft.json.tmpl"
printf '%s\n' '{"nftables":["package-proxy"]}' > "$temp_root/payload/scripts/kautuka_package_proxy.nft.json.tmpl"
install_nac_hook_payload \
  "$temp_root/payload/scripts/kautuka_hook.py" \
  "$temp_root/hooks/kautuka_hook.py"
cmp -s "$temp_root/payload/scripts/kautuka_hook.py" "$temp_root/hooks/kautuka_hook.py"
cmp -s "$temp_root/payload/scripts/kautuka_workload.nft.json.tmpl" "$temp_root/hooks/kautuka_workload.nft.json.tmpl"
cmp -s "$temp_root/payload/scripts/kautuka_package_proxy.nft.json.tmpl" "$temp_root/hooks/kautuka_package_proxy.nft.json.tmpl"
[[ -x "$temp_root/hooks/kautuka_hook.py" ]]
[[ ! -x "$temp_root/hooks/kautuka_workload.nft.json.tmpl" ]]

missing_output="$temp_root/missing-hook.stderr"
if ( install_nac_hook_payload \
  "$temp_root/missing/scripts/kautuka_hook.py" \
  "$temp_root/hooks/missing-hook.py" ) 2>"$missing_output"; then
  echo 'installer accepted a payload with no NAC hook' >&2
  exit 1
fi
grep -Fq 'required network access hook payload is missing' "$missing_output"

mkdir -p "$temp_root/missing-template/scripts"
printf '%s\n' '#!/usr/bin/env python3' 'print("hook")' > "$temp_root/missing-template/scripts/kautuka_hook.py"
template_missing_output="$temp_root/missing-template.stderr"
if ( install_nac_hook_payload \
  "$temp_root/missing-template/scripts/kautuka_hook.py" \
  "$temp_root/hooks/missing-template-hook.py" ) 2>"$template_missing_output"; then
  echo 'installer accepted a NAC hook without its rendered ruleset template' >&2
  exit 1
fi
grep -Fq 'required network access ruleset template is missing' "$template_missing_output"

mkdir -p "$temp_root/missing-proxy/scripts"
cp "$temp_root/payload/scripts/kautuka_hook.py" "$temp_root/missing-proxy/scripts/"
cp "$temp_root/payload/scripts/kautuka_workload.nft.json.tmpl" "$temp_root/missing-proxy/scripts/"
if ( install_nac_hook_payload "$temp_root/missing-proxy/scripts/kautuka_hook.py" "$temp_root/hooks/kautuka_hook.py" ) 2>"$temp_root/missing-proxy.stderr"; then
  echo 'installer accepted a NAC hook without its package proxy template' >&2
  exit 1
fi
grep -Fq 'required package proxy ruleset template is missing' "$temp_root/missing-proxy.stderr"

# Exercise the installer with isolated paths and no host service mutations.
ensure_subid_entry() { :; }
ensure_rootless_idmap_helpers() { :; }
ensure_nac_prerequisites() { :; }
chown() { :; }
loginctl() { :; }
validate_linger_enabled() { :; }
install_user_manager_restart_dropin() { :; }
systemctl() { :; }
run_as_service_user() { :; }
mkdir -p "$temp_root/runtime"
ensure_rootless_podman_ready "$(id -un)" "$(id -gn)" "$temp_root/home" "$temp_root/runtime"
python3 - "$temp_root/home/.config/containers/containers.conf.d/99-kautuka-hooks.conf" "$temp_root/home/.config/containers/hooks.d" <<'PY'
import sys, tomllib
with open(sys.argv[1], 'rb') as config:
    hooks = tomllib.load(config)['engine']['hooks_dir']
assert hooks == [sys.argv[2], {'append': True}], hooks
PY

echo 'Linux installer NAC hook payload checks passed.'
