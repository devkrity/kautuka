#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 || $EUID -eq 0 ]]; then
    echo 'Usage: test-appliance-service-images.sh <built service-images directory> (Linux rootless Podman)' >&2
    exit 2
fi

images="$(realpath -e -- "$1")"
work="$(mktemp -d)"
trap 'podman unshare rm -rf -- "$work"' EXIT
podman_fixture=(podman --storage-driver vfs --root "$work/storage" --runroot "$work/run")

for role in credentialshost channelhost gateway webhost updateservice; do
    archive="$images/$role.tar"
    reference="$(python3 -c 'import json, sys; print(json.load(open(sys.argv[1]))["services"][sys.argv[2]]["image"])' \
        "$images/appliance-deployment.json" "$role")"
    digest="sha256:$(skopeo inspect --raw "oci-archive:$archive" | sha256sum | cut -d ' ' -f 1)"
    [[ "$reference" == *@$digest ]]
    "${podman_fixture[@]}" load --input "$archive"
    [[ "$("${podman_fixture[@]}" image inspect --format '{{.Digest}}' "$reference")" == "$digest" ]]
    "${podman_fixture[@]}" run --rm --pull=never --network none --read-only --cap-drop=ALL \
        --entrypoint /bin/sh "$reference" -ec '
        role=$1
        test "$(id -u)" -eq 65532
        test "$(readlink /opt/kautuka/service)" = "kautuka-$role"
        test -x "/opt/kautuka/kautuka-$role"
        case "$role" in
            gateway)
                test -s /opt/kautuka/built-in-mcpb/kautuka-google-mail.mcpb
                test -s /opt/kautuka/built-in-mcpb/kautuka-m365-mail.mcpb
                test -s /opt/kautuka/built-in-mcpb/kautuka-web-tools.mcpb
                ;;
            webhost) test -s /opt/kautuka/wwwroot/index.html ;;
        esac
    ' appliance-image-check "$role"
    printf 'Verified %s by its archived digest.\n' "$role"
done
