#!/usr/bin/env bash
set -euo pipefail

if [[ "$(id -u)" != 0 ]]; then
    echo 'Run the appliance image retention test as root in a disposable Linux environment.' >&2
    exit 2
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
mkdir -p "$work/manager/versions"
source <(sed -n '/^assert_forward_version() {/,/^}/p; /^retire_superseded_appliance_images() {/,/^}/p' \
    "$script_dir/kautuka-apply-release.sh" |
    sed 's|manager=/opt/kautuka/manager|manager="$work/manager"|; s|in /opt /opt/kautuka "$manager"|in "$work" "$manager"|')

previous=1.0.0-preview.10
current=1.0.0-preview.11
staged=1.0.0-preview.12
obsolete=1.0.0-preview.9
versions="$work/manager/versions"
for version in "$previous" "$current" "$staged" "$obsolete"; do
    mkdir -p "$versions/$version/service-images"
    for role in credentialshost channelhost gateway webhost updateservice; do
        printf 'fixture\n' > "$versions/$version/service-images/$role.tar"
    done
done
ln -s "$versions/$current" "$work/manager/current"

allow_retirement=false
appliance_user() {
    [[ "$*" == "$versions/$current/kautuka-manager --retire-appliance-images $obsolete" ]] || return 1
    [[ "$allow_retirement" == true ]]
}

if retire_superseded_appliance_images "$previous" "$current"; then
    echo 'Retirement should stop when protected image verification fails.' >&2
    exit 1
fi
test -f "$versions/$obsolete/service-images/gateway.tar"

allow_retirement=true
retire_superseded_appliance_images "$previous" "$current"
retire_superseded_appliance_images "$previous" "$current"
test -z "$(find "$versions/$obsolete/service-images" -type f -print -quit)"
for version in "$previous" "$current" "$staged"; do
    test "$(find "$versions/$version/service-images" -type f | wc -l)" -eq 5
done
echo 'Only superseded offline archives were removed. Repeating retirement preserved the current, previous, and staged releases.'
