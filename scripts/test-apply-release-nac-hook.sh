#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf -- "$tmp"' EXIT
source <(sed -n '/^verify_nac_hook_compatibility() {/,/^}/p' "$repo_root/scripts/kautuka-apply-release.sh")
source <(sed -n '/^verify_envoy_selinux_policy_compatibility() {/,/^}/p' "$repo_root/scripts/kautuka-apply-release.sh")
mkdir -p "$tmp/candidate/scripts" "$tmp/home/.config/containers/hooks.d" "$tmp/home/.config/containers/containers.conf.d"
hooks="$tmp/home/.config/containers/hooks.d"
for file in kautuka_hook.py kautuka_workload.nft.json.tmpl kautuka_package_proxy.nft.json.tmpl; do
  printf 'verified-%s\n' "$file" > "$tmp/candidate/scripts/$file"
  cp "$tmp/candidate/scripts/$file" "$hooks/$file"
done
printf '[engine]\nhooks_dir = ["%s", { append = true }]\n' "$hooks" > "$tmp/home/.config/containers/containers.conf.d/99-kautuka-hooks.conf"
printf '{"version":"1.0.0","hook":{"path":"%s/kautuka_hook.py","timeout":45},"when":{"always":true},"stages":["createRuntime"]}\n' "$hooks" > "$hooks/kautuka_hook.json"
verify_nac_hook_compatibility "$tmp/candidate" "$tmp/home"
printf 'modified\n' > "$hooks/kautuka_workload.nft.json.tmpl"
if verify_nac_hook_compatibility "$tmp/candidate" "$tmp/home" >/dev/null 2>&1; then
  echo 'modified ruleset was accepted' >&2; exit 1
fi
cp "$tmp/candidate/scripts/kautuka_workload.nft.json.tmpl" "$hooks/kautuka_workload.nft.json.tmpl"
rm -f "$hooks/kautuka_hook.json"
if verify_nac_hook_compatibility "$tmp/candidate" "$tmp/home" >/dev/null 2>&1; then
  echo 'missing hook configuration was accepted' >&2; exit 1
fi
mkdir -p "$tmp/current/scripts"
printf 'policy-v1\n' > "$tmp/current/scripts/kautuka_envoy.cil"
cp "$tmp/current/scripts/kautuka_envoy.cil" "$tmp/candidate/scripts/kautuka_envoy.cil"
verify_envoy_selinux_policy_compatibility "$tmp/candidate" "$tmp/current" Enforcing
printf 'policy-v2\n' > "$tmp/candidate/scripts/kautuka_envoy.cil"
if verify_envoy_selinux_policy_compatibility "$tmp/candidate" "$tmp/current" Enforcing >/dev/null 2>&1; then
  echo 'changed Envoy SELinux policy was accepted' >&2; exit 1
fi
verify_envoy_selinux_policy_compatibility "$tmp/candidate" "$tmp/current" Disabled
rm "$tmp/current/scripts/kautuka_envoy.cil"
if verify_envoy_selinux_policy_compatibility "$tmp/candidate" "$tmp/current" Enforcing >/dev/null 2>&1; then
  echo 'missing current Envoy SELinux policy was accepted' >&2; exit 1
fi
echo 'NAC updater compatibility checks passed.'
