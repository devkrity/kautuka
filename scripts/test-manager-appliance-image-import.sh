#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 || $EUID -eq 0 ]]; then
    echo 'Usage: test-manager-appliance-image-import.sh <native Manager executable> <service-image fixtures> (Linux rootless Podman)' >&2
    exit 2
fi

manager="$(realpath -e -- "$1")"
images="$(realpath -e -- "$2")"
[[ -x "$manager" && -f "$images/appliance-deployment.json" ]]
work="$(mktemp -d)"
trap 'podman unshare rm -rf -- "$work"' EXIT
mkdir -m0700 "$work"/{version,trust,xdg,bin}
mkdir "$work/version/service-images"
version=1.2.3-preview.1
printf 'v%s\n' "$version" > "$work/version/release-id.txt"
manifest="$work/version/kautuka-platform-release.json"
signature="$work/version/kautuka-platform-release.sig"
python3 - "$images/appliance-deployment.json" "$manifest" "$version" <<'PY'
import json
import sys

descriptor_path, output_path, version = sys.argv[1:]
with open(descriptor_path, encoding="utf-8") as descriptor:
    deployment = json.load(descriptor)
with open(output_path, "w", encoding="utf-8") as output:
    json.dump({
        "schemaVersion": 2,
        "version": version,
        "releaseId": f"v{version}",
        "releaseTag": f"v{version}",
        "sourceSha": "0123456789abcdef0123456789abcdef01234567",
        "signature": {"algorithm": "ecdsa-p256-sha256", "format": "base64"},
        "applianceDeployment": deployment,
    }, output)
PY
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out "$work/signing-key.pem"
openssl pkey -in "$work/signing-key.pem" -pubout -out "$work/trust/manifest-public-key.pem"
openssl dgst -sha256 -sign "$work/signing-key.pem" "$manifest" | base64 > "$signature"
candidate=1.2.3-preview.2
mkdir "$work/candidate"
python3 - "$manifest" "$work/candidate/kautuka-platform-release.json" "$candidate" <<'PY'
import json
import sys

source_path, target_path, version = sys.argv[1:]
with open(source_path, encoding="utf-8") as source:
    release = json.load(source)
release.update(version=version, releaseId=f"v{version}", releaseTag=f"v{version}")
with open(target_path, "w", encoding="utf-8") as target:
    json.dump(release, target)
PY
printf 'v%s\n' "$candidate" > "$work/candidate/release-id.txt"
openssl dgst -sha256 -sign "$work/signing-key.pem" "$work/candidate/kautuka-platform-release.json" | base64 > "$work/candidate/kautuka-platform-release.sig"
rm -- "$work/signing-key.pem"

cat > "$work/bin/podman" <<'PODMAN'
#!/bin/bash
set -euo pipefail
printf '%s\n' "$*" >> /work/podman-commands
exec /usr/bin/podman --storage-driver vfs --root /work/storage --runroot /work/run "$@"
PODMAN
chmod 0755 "$work/bin/podman"
installed="/opt/kautuka/manager/versions/$version"
podman unshare bwrap --unshare-net \
    --ro-bind /usr /usr --symlink usr/lib64 /lib64 --symlink usr/bin /bin \
    --proc /proc --dev /dev --tmpfs /tmp --dir /var --tmpfs /var/tmp \
    --ro-bind /sys/fs/cgroup /sys/fs/cgroup --ro-bind /etc/containers /etc/containers \
    --bind "$work" /work \
    --ro-bind "$manager" /work/manager \
    --ro-bind "$work/version" "$installed" \
    --ro-bind "$work/candidate" "/opt/kautuka/manager/versions/$candidate" \
    --ro-bind "$images" "$installed/service-images" \
    --ro-bind "$work/trust" /etc/kautuka \
    --symlink "versions/$version" /opt/kautuka/manager/current \
    --setenv HOME /work --setenv XDG_RUNTIME_DIR /work/xdg \
    --setenv PATH /work/bin:/usr/bin \
    -- /bin/bash -ec '
        /work/manager --verify-appliance-release --candidate-version 1.2.3-preview.2
        if /work/manager --prepare-appliance-images --candidate-version 1.2.3-preview.2; then
            echo "Cold candidate without images unexpectedly prepared." >&2
            exit 1
        fi
        test "$(grep -cx load /work/podman-commands || true)" -eq 0
        /work/manager --prepare-appliance-images
        test "$(grep -xc load /work/podman-commands)" -eq 5
        : > /work/podman-commands
        /work/manager --prepare-appliance-images
        /work/manager --prepare-appliance-images --candidate-version 1.2.3-preview.2
        test "$(wc -l < /work/podman-commands)" -eq 2
        test "$(grep -Ec "^image inspect( [^ ]+){5}$" /work/podman-commands)" -eq 2
        test "$(readlink /opt/kautuka/manager/current)" = versions/1.2.3-preview.1
    '
echo 'Native Manager imported all five signed images offline and prepared each warm release with one image inspection.'
