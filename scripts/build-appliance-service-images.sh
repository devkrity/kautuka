#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 5 ]]; then
    echo 'Usage: build-appliance-service-images.sh <staged-services> <output-dir> <pinned-fedora-base> <repository-prefix> <version-tag>' >&2
    exit 2
fi

payloads="$(realpath -e -- "$1")"
output="$(realpath -m -- "$2")"
base="$3"
repository_prefix="$4"
version="$5"
script_dir="$(cd "$(dirname "$0")" && pwd)"
image_dir="$script_dir/../src/images/appliance-service"
roles=(credentialshost channelhost gateway webhost updateservice)
repository_pattern='(localhost|[a-z0-9-]+(\.[a-z0-9-]+)+)(:[0-9]{1,5})?(/[a-z0-9]+([._-]+[a-z0-9]+)*)+'

[[ "$base" =~ ^$repository_pattern@sha256:[0-9a-f]{64}$ ]] || { echo 'Runtime base must be digest-pinned.' >&2; exit 1; }
[[ "$repository_prefix" =~ ^$repository_pattern$ ]] || { echo 'Invalid appliance image repository prefix.' >&2; exit 1; }
[[ "$version" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$ ]] || { echo 'Invalid appliance image version tag.' >&2; exit 1; }
[[ ! -e "$output" && ! -L "$2" ]] || { echo 'Appliance image output already exists.' >&2; exit 1; }

for role in "${roles[@]}"; do
    executable="$payloads/$role/kautuka-$role"
    if [[ -L "$payloads/$role" || ! -f "$executable" || -L "$executable" || ! -x "$executable" ]] ||
        [[ "$(od -An -tx1 -N4 -- "$executable" | tr -d '[:space:]')" != 7f454c46 ]]; then
        echo "Missing executable Linux payload for $role." >&2
        exit 1
    fi
done
for asset in gateway/built-in-mcpb/kautuka-{google-mail,m365-mail,web-tools}.mcpb webhost/wwwroot/index.html; do
    [[ -s "$payloads/$asset" && -f "$payloads/$asset" ]] || { echo "Missing release asset: $asset." >&2; exit 1; }
done

parent="$(dirname "$output")"
mkdir -p -- "$parent"
work="$(mktemp -d "$parent/.kautuka-service-images.XXXXXXXX")"
temporary_repository="localhost/kautuka-service-build-${work##*.}"
temporary_repository="${temporary_repository,,}"
tags=()
cleanup() {
    for tag in "${tags[@]}"; do podman image rm "$tag" >/dev/null 2>&1 || true; done
    if [[ -d "$work" ]]; then rm -rf -- "$work"; fi
}
trap cleanup EXIT

for role in "${roles[@]}"; do
    tag="$temporary_repository/$role:build"
    repository="$repository_prefix-$role"
    tags+=("$tag")
    podman build --layers --format oci --pull=missing \
        --file "$image_dir/Containerfile" --ignorefile "$image_dir/ContainerIgnore" \
        --build-arg "RUNTIME_BASE=$base" --build-arg "SERVICE_ROLE=$role" \
        --build-arg "SERVICE_VERSION=$version" -t "$tag" "$payloads/$role" >&2

    archive="$work/$role.tar"
    skopeo copy --preserve-digests "containers-storage:$tag" "oci-archive:$archive:$repository:$version" >&2
    digest="sha256:$(skopeo inspect --raw "oci-archive:$archive" | sha256sum | cut -d ' ' -f 1)"
    [[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]] || { echo "Invalid OCI manifest digest for $role." >&2; exit 1; }
    printf '%s\t%s@%s\n' "$role" "$repository" "$digest" >> "$work/service-images.tsv"
done

python3 - "$work/service-images.tsv" > "$work/appliance-deployment.json" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    images = dict(line.rstrip("\n").split("\t") for line in source)
json.dump({"schemaVersion": 1, "hookAbi": 1, "services": {role: {"image": image} for role, image in images.items()}},
          sys.stdout, indent=2)
print()
PY
rm -- "$work/service-images.tsv"
mv -T --no-clobber -- "$work" "$output"
[[ ! -d "$work" ]] || { echo 'Appliance image output appeared during the build.' >&2; exit 1; }
printf '%s\n' "$output/appliance-deployment.json"
