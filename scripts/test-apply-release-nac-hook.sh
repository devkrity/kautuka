#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf -- "$tmp"' EXIT
source <(sed -n '/^same_file_contents() {/,/^}/p' "$repo_root/scripts/kautuka-apply-release.sh")
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
# Fresh native registrations use the stable version pointer, not the legacy
# interpreter or copies of the nft templates in the service home.
manager="$tmp/manager"
mkdir -p "$manager/versions/v1/scripts"
cp /bin/true "$manager/versions/v1/scripts/kautuka-oci-hook"
cp /bin/true "$tmp/candidate/scripts/kautuka-oci-hook"
ln -s "$manager/versions/v1" "$manager/current"
native_registration() {
  printf '{"version":"1.0.0","hook":{"path":"%s/current/scripts/kautuka-oci-hook","timeout":45},"when":{"always":true},"stages":["createRuntime"]}\n' "$manager" > "$hooks/kautuka_hook.json"
}
native_registration
verify_nac_hook_compatibility "$tmp/candidate" "$tmp/home" "$manager/current"
printf 'not-elf\n' > "$tmp/candidate/scripts/kautuka-oci-hook"
if verify_nac_hook_compatibility "$tmp/candidate" "$tmp/home" "$manager/current" >/dev/null 2>&1; then
  echo 'invalid candidate native hook was accepted' >&2; exit 1
fi
cp /bin/true "$tmp/candidate/scripts/kautuka-oci-hook"
chmod 0644 "$tmp/candidate/scripts/kautuka-oci-hook"
if verify_nac_hook_compatibility "$tmp/candidate" "$tmp/home" "$manager/current" >/dev/null 2>&1; then
  echo 'non-executable candidate native hook was accepted' >&2; exit 1
fi
chmod 0755 "$tmp/candidate/scripts/kautuka-oci-hook"
printf '{"version":"1.0.0","hook":{"path":"%s/current/scripts/kautuka-oci-hook","timeout":45,"args":["bad"]},"when":{"always":true},"stages":["createRuntime"]}\n' "$manager" > "$hooks/kautuka_hook.json"
if verify_nac_hook_compatibility "$tmp/candidate" "$tmp/home" "$manager/current" >/dev/null 2>&1; then
  echo 'native registration with extra arguments was accepted' >&2; exit 1
fi
native_registration
chmod 0777 "$manager/versions/v1/scripts"
if verify_nac_hook_compatibility "$tmp/candidate" "$tmp/home" "$manager/current" >/dev/null 2>&1; then
  echo 'writable native hook directory was accepted' >&2; exit 1
fi
chmod 0755 "$manager/versions/v1/scripts"
rm "$manager/current"
ln -s "$tmp/current" "$manager/current"
if verify_nac_hook_compatibility "$tmp/candidate" "$tmp/home" "$manager/current" >/dev/null 2>&1; then
  echo 'untrusted Manager pointer was accepted' >&2; exit 1
fi
mkdir -p "$tmp/current/scripts"
printf 'policy-v1\n' > "$tmp/current/scripts/kautuka_envoy.cil"
cp "$tmp/current/scripts/kautuka_envoy.cil" "$tmp/candidate/scripts/kautuka_envoy.cil"
loaded_checksum="sha256:$(sha256sum "$tmp/current/scripts/kautuka_envoy.cil" | cut -d' ' -f1)"
semodule() { printf '400 kautuka_envoy cil %s\n' "$loaded_checksum"; }
verify_envoy_selinux_policy_compatibility "$tmp/candidate" "$tmp/current" Enforcing
semodule() { printf '400 kautuka_envoy cil %s\n500 kautuka_envoy cil sha256:wrong\n' "$loaded_checksum"; }
if verify_envoy_selinux_policy_compatibility "$tmp/candidate" "$tmp/current" Enforcing >/dev/null 2>&1; then
  echo 'higher-priority changed Envoy module was accepted' >&2; exit 1
fi
semodule() { printf '400 kautuka_envoy cil %s\n' "$loaded_checksum"; }
loaded_checksum='sha256:0000000000000000000000000000000000000000000000000000000000000000'
if verify_envoy_selinux_policy_compatibility "$tmp/candidate" "$tmp/current" Enforcing >/dev/null 2>&1; then
  echo 'changed loaded Envoy SELinux module was accepted' >&2; exit 1
fi
loaded_checksum="sha256:$(sha256sum "$tmp/current/scripts/kautuka_envoy.cil" | cut -d' ' -f1)"
semodule() { :; }
if verify_envoy_selinux_policy_compatibility "$tmp/candidate" "$tmp/current" Enforcing >/dev/null 2>&1; then
  echo 'missing loaded Envoy SELinux module was accepted' >&2; exit 1
fi
semodule() { printf '400 kautuka_envoy cil %s\n' "$loaded_checksum"; }
printf 'policy-v2\n' > "$tmp/candidate/scripts/kautuka_envoy.cil"
if verify_envoy_selinux_policy_compatibility "$tmp/candidate" "$tmp/current" Enforcing >/dev/null 2>&1; then
  echo 'changed Envoy SELinux policy was accepted' >&2; exit 1
fi
verify_envoy_selinux_policy_compatibility "$tmp/candidate" "$tmp/current" Disabled
printf 'manager-peer-policy\n' > "$tmp/current/scripts/kautuka_manager_peer.cil"
cp "$tmp/current/scripts/kautuka_manager_peer.cil" "$tmp/candidate/scripts/kautuka_manager_peer.cil"
loaded_checksum="sha256:$(sha256sum "$tmp/current/scripts/kautuka_manager_peer.cil" | cut -d' ' -f1)"
if verify_envoy_selinux_policy_compatibility "$tmp/candidate" "$tmp/current" Enforcing kautuka_manager_peer >/dev/null 2>&1; then
    echo 'an Envoy module checksum was accepted for the Manager peer policy' >&2; exit 1
fi
semodule() { printf '400 kautuka_manager_peer cil %s\n' "$loaded_checksum"; }
verify_envoy_selinux_policy_compatibility "$tmp/candidate" "$tmp/current" Enforcing kautuka_manager_peer
rm "$tmp/current/scripts/kautuka_envoy.cil"
if verify_envoy_selinux_policy_compatibility "$tmp/candidate" "$tmp/current" Enforcing >/dev/null 2>&1; then
  echo 'missing current Envoy SELinux policy was accepted' >&2; exit 1
fi
echo 'NAC updater compatibility checks passed.'
