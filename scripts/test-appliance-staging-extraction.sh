#!/usr/bin/env bash
set -euo pipefail

# Exercises the actual staging function with temporary paths and a fake Manager.
# It never uses the installed appliance or invokes Podman.
[[ $(id -u) == 0 ]] || { echo 'Run as root in a disposable Linux environment.' >&2; exit 2; }
script_dir="$(cd "$(dirname "$0")" && pwd)"
test_root="$(mktemp -d /tmp/kautuka-stage-images-test.XXXXXXXX)"
trap 'rm -rf -- "$test_root"' EXIT
chmod 0755 "$test_root"
mkdir -p "$test_root/kautuka/manager/versions" "$test_root/source/bin/scripts" \
    "$test_root/source/service-images" "$test_root/verified" "$test_root/backups"
printf '\177ELFfake' > "$test_root/source/bin/kautuka-manager"
printf '\177ELFfake' > "$test_root/source/bin/scripts/kautuka-oci-hook"
chmod 0755 "$test_root/source/bin/kautuka-manager" "$test_root/source/bin/scripts/kautuka-oci-hook"
printf 'role image bytes\n' > "$test_root/source/service-images/gateway.tar"
printf 'manifest\n' > "$test_root/verified/manifest.json"
printf 'signature\n' > "$test_root/verified/signature.b64"
tar -czf "$test_root/verified/artifact.tar.gz" -C "$test_root/source" .
verified_root="$test_root/verified"
backups_root="$test_root/backups"
appliance_service_home="$test_root"
stage_extract_root=''
appliance_stage_root=''
stage_version_in_progress=''
host_services=(manager)
check_staging_space() { :; }
verify_nac_hook_compatibility() { :; }
verify_envoy_selinux_policy_compatibility() { :; }
getenforce() { echo Disabled; }
appliance_user() {
    [[ "$*" == *'--prepare-appliance-images --candidate-version 1.2.3'* ]]
    [[ -z "$stage_extract_root" ]]
    [[ -z "$(find "$test_root" -maxdepth 1 -name 'extract.*' -print -quit)" ]]
    local version="$test_root/kautuka/manager/versions/1.2.3"
    cmp "$test_root/source/service-images/gateway.tar" "$version/service-images/gateway.tar"
    cmp "$verified_root/manifest.json" "$version/kautuka-platform-release.json"
    cmp "$verified_root/signature.b64" "$version/kautuka-platform-release.sig"
    [[ ! -e "$verified_root/artifact.tar.gz" ]]
    [[ "$(stat -c '%a' "$version/service-images/gateway.tar")" == 644 ]]
    printf 'observed\n' > "$test_root/prepared"
}
# Substitute only the test fixture paths; all staging and cleanup logic is real.
source <(sed -n '/^cleanup_stage_extract() {/,/^}/p; /^same_file_contents() {/,/^}/p; /^stage_appliance_version() {/,/^}/p' "$script_dir/kautuka-apply-release.sh" | \
    sed -e 's@manager_root=/opt/kautuka/manager@manager_root="$test_root/kautuka/manager"@' \
        -e 's@for directory in /opt /opt/kautuka@for directory in "$test_root" "$test_root/kautuka"@' \
        -e 's@/var/tmp/kautuka-appliance-stage\.XXXXXXXX@'"$test_root"'/extract.XXXXXXXX@')
stage_appliance_version 1.2.3
[[ -f "$test_root/prepared" && -z "$stage_version_in_progress" ]]
echo 'Image preparation saw the promoted archives after the private extraction was removed.'
