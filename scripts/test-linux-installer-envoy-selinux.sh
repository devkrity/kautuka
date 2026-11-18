#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf -- "$tmp"' EXIT
source <(sed -n '/^install_envoy_selinux_policy() {/,/^}/p' "$repo_root/scripts/kautuka-install-linux.sh.in")
source <(sed -n '/^write_manager_user_unit() {/,/^}/p' "$repo_root/scripts/kautuka-install-linux.sh.in")
mkdir -p "$tmp/templates"
touch "$tmp/templates/base_container.cil" "$tmp/templates/net_container.cil" "$tmp/kautuka_envoy.cil"
getenforce() { printf '%s\n' Enforcing; }
semodule() {
  if [[ "$1" == -l ]]; then
    if [[ "${MODULES_PRESENT:-yes}" == yes ]]; then printf 'base_container\nnet_container\n'; fi
    return
  fi
  [[ "${FAIL_INSTALL:-no}" != yes ]] || return 1
  printf '%s\n' "$*" > "$tmp/semodule-call"
}
install_envoy_selinux_policy "$tmp/kautuka_envoy.cil" "$tmp/templates"
grep -Fq "$tmp/kautuka_envoy.cil" "$tmp/semodule-call"
if grep -Eq 'base_container.cil|net_container.cil' "$tmp/semodule-call"; then echo 'existing shared policy templates were replaced' >&2; exit 1; fi
MODULES_PRESENT=no install_envoy_selinux_policy "$tmp/kautuka_envoy.cil" "$tmp/templates"
grep -Fq "$tmp/templates/base_container.cil" "$tmp/semodule-call"
grep -Fq "$tmp/templates/net_container.cil" "$tmp/semodule-call"
if FAIL_INSTALL=yes install_envoy_selinux_policy "$tmp/kautuka_envoy.cil" "$tmp/templates" >/dev/null 2>&1; then echo 'failed policy installation was accepted' >&2; exit 1; fi
rm "$tmp/kautuka_envoy.cil"
if install_envoy_selinux_policy "$tmp/kautuka_envoy.cil" "$tmp/templates" >/dev/null 2>&1; then echo 'missing SELinux policy was accepted' >&2; exit 1; fi
getenforce() { printf '%s\n' Disabled; }
install_envoy_selinux_policy "$tmp/kautuka_envoy.cil" "$tmp/templates"
write_manager_user_unit "$tmp/manager.service" /opt/kautuka/manager /var/lib/kautuka true
grep -Fxq 'Environment=KAUTUKA_ENVOY_SELINUX=1' "$tmp/manager.service"
write_manager_user_unit "$tmp/manager.service" /opt/kautuka/manager /var/lib/kautuka false
if grep -Fq 'KAUTUKA_ENVOY_SELINUX' "$tmp/manager.service"; then echo 'SELinux domain enabled on an unsupported host' >&2; exit 1; fi
echo 'Envoy SELinux installation checks passed.'
