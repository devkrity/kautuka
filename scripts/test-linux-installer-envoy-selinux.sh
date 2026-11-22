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
command() {
  if [[ "$1" == -v && "$2" == dnf && "${TEST_APT:-no}" == yes ]]; then return 1; fi
  if [[ "$1" == -v && ( "$2" == dnf || "$2" == apt-get ) && "${TEST_ZYPPER:-no}" == yes ]]; then return 1; fi
  builtin command "$@"
}
zypper() {
  [[ "$*" == '--non-interactive install udica' ]] || return 1
  touch "$tmp/templates/base_container.cil" "$tmp/templates/net_container.cil"
  printf 'zypper\n' > "$tmp/package-manager"
}
apt-get() {
  [[ "$*" == 'install -y udica' ]] || return 1
  touch "$tmp/templates/base_container.cil" "$tmp/templates/net_container.cil"
  printf 'apt-get\n' > "$tmp/package-manager"
}
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
if install_envoy_selinux_policy "$tmp/kautuka_envoy.cil" "$tmp/templates" "$tmp/kautuka_manager_peer.cil" >/dev/null 2>&1; then
  echo 'missing appliance Manager peer policy was accepted' >&2
  exit 1
fi
touch "$tmp/kautuka_manager_peer.cil"
install_envoy_selinux_policy "$tmp/kautuka_envoy.cil" "$tmp/templates" "$tmp/kautuka_manager_peer.cil"
grep -Fxq -- "-i $tmp/kautuka_envoy.cil $tmp/kautuka_manager_peer.cil" "$tmp/semodule-call"
if grep -Eq 'base_container.cil|net_container.cil' "$tmp/semodule-call"; then echo 'existing shared policy templates were replaced' >&2; exit 1; fi
MODULES_PRESENT=no install_envoy_selinux_policy "$tmp/kautuka_envoy.cil" "$tmp/templates"
grep -Fq "$tmp/templates/base_container.cil" "$tmp/semodule-call"
grep -Fq "$tmp/templates/net_container.cil" "$tmp/semodule-call"
if FAIL_INSTALL=yes install_envoy_selinux_policy "$tmp/kautuka_envoy.cil" "$tmp/templates" >/dev/null 2>&1; then echo 'failed policy installation was accepted' >&2; exit 1; fi
rm "$tmp/templates/base_container.cil" "$tmp/templates/net_container.cil"
TEST_APT=yes install_envoy_selinux_policy "$tmp/kautuka_envoy.cil" "$tmp/templates"
grep -Fxq 'apt-get' "$tmp/package-manager"
rm "$tmp/templates/base_container.cil" "$tmp/templates/net_container.cil"
TEST_ZYPPER=yes install_envoy_selinux_policy "$tmp/kautuka_envoy.cil" "$tmp/templates"
grep -Fxq 'zypper' "$tmp/package-manager"
rm "$tmp/kautuka_envoy.cil"
if install_envoy_selinux_policy "$tmp/kautuka_envoy.cil" "$tmp/templates" >/dev/null 2>&1; then echo 'missing SELinux policy was accepted' >&2; exit 1; fi
getenforce() { printf '%s\n' Disabled; }
install_envoy_selinux_policy "$tmp/kautuka_envoy.cil" "$tmp/templates"
write_manager_user_unit "$tmp/manager.service" /opt/kautuka/manager /var/lib/kautuka true
grep -Fxq 'Environment=KAUTUKA_ENVOY_SELINUX=1' "$tmp/manager.service"
write_manager_user_unit "$tmp/manager.service" /opt/kautuka/manager /var/lib/kautuka false
if grep -Fq 'KAUTUKA_ENVOY_SELINUX' "$tmp/manager.service"; then echo 'SELinux domain enabled on an unsupported host' >&2; exit 1; fi
# A minimal Fedora host can enforce SELinux without the executable labeling tools.
source <(sed -n '/^ensure_selinux_runtime_exec_policy() {/,/^}/p' "$repo_root/scripts/kautuka-install-linux.sh.in")
fail() { echo "$*" >&2; exit 1; }
getenforce() { printf '%s\n' Enforcing; }
command() {
  if [[ "$1" == -v && ( "$2" == semanage || "$2" == restorecon ) && ! -f "$tmp/label-tools-ready" ]]; then return 1; fi
  builtin command "$@"
}
dnf() {
  [[ "$*" == 'install -y policycoreutils-python-utils policycoreutils' && "${LABEL_TOOLS_INSTALL_FAIL:-no}" != yes ]] || return 1
  printf '%s\n' installed >> "$tmp/label-tools-installs"
  touch "$tmp/label-tools-ready"
}
semanage() { printf '%s\n' "$*" >> "$tmp/label-rules"; }
restorecon() { return 0; }
if (LABEL_TOOLS_INSTALL_FAIL=yes ensure_selinux_runtime_exec_policy true) >"$tmp/label-tools-failure" 2>&1; then
  echo 'failed SELinux tool installation was accepted' >&2
  exit 1
fi
grep -Fq 'could not install SELinux labeling tools' "$tmp/label-tools-failure"
test ! -e "$tmp/label-rules"
ensure_selinux_runtime_exec_policy true
ensure_selinux_runtime_exec_policy true
test "$(wc -l < "$tmp/label-tools-installs")" -eq 1
grep -Fq 'fcontext -a -t bin_t' "$tmp/label-rules"
grep -Fq 'fcontext -a -t container_file_t' "$tmp/label-rules"

echo 'Envoy SELinux installation checks passed.'
