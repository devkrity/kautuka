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
changed=1.2.3-preview.3
mkdir "$work/candidate" "$work/changed"
python3 - "$manifest" "$work" "$candidate" "$changed" <<'PY'
import copy
import json
from pathlib import Path
import sys

source_path, work, candidate, changed = sys.argv[1:]
with open(source_path, encoding="utf-8") as source:
    baseline = json.load(source)
for name, version in (("candidate", candidate), ("changed", changed)):
    release = copy.deepcopy(baseline)
    release.update(version=version, releaseId=f"v{version}", releaseTag=f"v{version}", sourceSha="1" * 40)
    if name == "changed":
        gateway = release["applianceDeployment"]["services"]["gateway"]
        repository, old_digest = gateway["image"].rsplit("@", 1)
        digest = "sha256:" + ("0" if old_digest != "sha256:" + "0" * 64 else "1") * 64
        gateway["image"] = f"{repository}@{digest}"
    directory = Path(work) / name
    (directory / "release-id.txt").write_text(f"v{version}\n", encoding="utf-8")
    (directory / "kautuka-platform-release.json").write_text(json.dumps(release), encoding="utf-8")
PY
for directory in "$work/candidate" "$work/changed"; do
    openssl dgst -sha256 -sign "$work/signing-key.pem" "$directory/kautuka-platform-release.json" \
        | base64 > "$directory/kautuka-platform-release.sig"
done
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
    --ro-bind "$work/changed" "/opt/kautuka/manager/versions/$changed" \
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
        : > /work/podman-commands
        /work/manager --verify-appliance-release --candidate-version 1.2.3-preview.3
        if /work/manager --prepare-appliance-images --candidate-version 1.2.3-preview.3 2>/work/changed-error; then
            echo "Candidate with a different digest and no archive unexpectedly prepared." >&2
            exit 1
        fi
        grep -F "The signed gateway image is missing from Podman" /work/changed-error
        grep -Eq "^image exists [^ ]+@sha256:(0{64}|1{64})$" /work/podman-commands
        test "$(grep -Ec "^(load|pull|run|stop|rm)( |$)" /work/podman-commands || true)" -eq 0
        test "$(readlink /opt/kautuka/manager/current)" = versions/1.2.3-preview.1
    '
echo 'Native Manager imported the signed images, reused unchanged digests, and rejected the missing changed image without switching releases.'
