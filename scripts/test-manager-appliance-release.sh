#!/usr/bin/env bash
set -euo pipefail

# The verification command uses the real absolute appliance paths. Run this
# fixture as root only in a disposable container or distro.
if [[ ${EUID} -ne 0 || ${KAUTUKA_DISPOSABLE_TEST:-} != 1 || $# -ne 1 ]]; then
    echo 'Usage: KAUTUKA_DISPOSABLE_TEST=1 bash test-manager-appliance-release.sh <native Manager executable> (disposable root only)' >&2
    exit 2
fi
[[ -x "$1" ]]
for path in /opt/kautuka /etc/kautuka; do
    [[ ! -e "$path" && ! -L "$path" ]] || { echo "Fixture path already exists: $path" >&2; exit 1; }
done

temp="$(mktemp -d)"
trap 'rm -rf -- "$temp" /opt/kautuka /etc/kautuka' EXIT
chmod 0755 "$temp"
manager=/opt/kautuka/manager
version="$manager/versions/1.2.3-preview.1"
trust=/etc/kautuka
install -d -m 0755 "$version" "$trust"
install -m 0755 -- "$1" "$temp/manager"
printf '%s\n' 'v1.2.3-preview.1' > "$version/release-id.txt"
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out "$temp/test-signing.pem"
chmod 0600 "$temp/test-signing.pem"
openssl pkey -in "$temp/test-signing.pem" -pubout -out "$trust/manifest-public-key.pem"
chmod 0644 "$trust/manifest-public-key.pem"
python3 - "$temp/manifest.json" <<'PY'
import json, sys

roles = ('credentialshost', 'channelhost', 'gateway', 'webhost', 'updateservice')
manifest = {
    'schemaVersion': 2, 'version': '1.2.3-preview.1',
    'releaseId': 'v1.2.3-preview.1', 'releaseTag': 'v1.2.3-preview.1',
    'sourceSha': '0123456789abcdef0123456789abcdef01234567',
    'signature': {'algorithm': 'ecdsa-p256-sha256', 'format': 'base64'},
    'applianceDeployment': {
        'schemaVersion': 1, 'hookAbi': 1,
        'services': {role: {'image': f"ghcr.io/devkrity/kautuka-{role}@sha256:{'a' * 64}"} for role in roles},
    },
}
with open(sys.argv[1], 'w', encoding='utf-8') as output:
    json.dump(manifest, output)
PY
openssl dgst -sha256 -sign "$temp/test-signing.pem" "$temp/manifest.json" | base64 > "$temp/manifest.sig"
script_dir="$(cd "$(dirname "$0")" && pwd)"
source <(sed -n '/^install_verified_release_metadata() {/,/^}/p' "$script_dir/kautuka-install-linux.sh.in")
install_verified_release_metadata "$version" "$temp/manifest.json" "$temp/manifest.sig"
manifest="$version/kautuka-platform-release.json"
signature="$version/kautuka-platform-release.sig"
cmp "$temp/manifest.json" "$manifest"
cmp "$temp/manifest.sig" "$signature"
[[ "$(stat -c '%u:%g:%a' "$manifest" "$signature")" == $'0:0:644\n0:0:644' ]]
chmod 0644 "$version/release-id.txt"
ln -s 'versions/1.2.3-preview.1' "$manager/current"

verify() {
    timeout 10 setpriv --no-new-privs --reuid 65534 --regid 65534 --clear-groups \
        "$temp/manager" --verify-appliance-release
}

expect_refused() {
    local scenario="$1" status
    if verify > "$temp/result" 2>&1; then
        echo "Manager accepted $scenario" >&2
        exit 1
    else
        status=$?
    fi
    if [[ $status -ne 1 ]] || ! grep -q '^Appliance release rejected:' "$temp/result"; then
        echo "Manager did not cleanly refuse $scenario (exit $status)" >&2
        cat "$temp/result" >&2
        exit 1
    fi
}

verify | grep -Fx 'Verified appliance release v1.2.3-preview.1.'
chmod 0664 "$manifest"
expect_refused 'a group-writable manifest'
chmod 0644 "$manifest"
mv -- "$manifest" "$version/saved-manifest.json"
ln -s saved-manifest.json "$manifest"
expect_refused 'a symbolic manifest'
rm -- "$manifest"
mkfifo -m 0644 "$manifest"
expect_refused 'a FIFO in place of the manifest'
rm -- "$manifest"
cp -- "$version/saved-manifest.json" "$manifest"
printf ' ' >> "$manifest"
expect_refused 'tampered signed bytes'
cp -- "$version/saved-manifest.json" "$manifest"
mv -- "$signature" "$version/saved-signature"
expect_refused 'a missing signature'
mv -- "$version/saved-signature" "$signature"
ln -sfn 'versions/../versions/1.2.3-preview.1' "$manager/current"
expect_refused 'a noncanonical version pointer'
ln -sfn "$version" "$manager/current"
verify | grep -Fx 'Verified appliance release v1.2.3-preview.1.'
echo 'Native Manager verified the trusted appliance release and refused unsafe input before starting services.'
