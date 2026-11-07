#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_root="$(mktemp -d)"
trap 'rm -rf -- "$test_root"' EXIT
mkdir -p "$test_root/payload/bin" "$test_root/payload/oci-images"
python3 - "$test_root/payload" <<'PY'
import hashlib, json, pathlib, sys
root = pathlib.Path(sys.argv[1]); images = []
for role in ('kautuka-base', 'openobserve', 'git-pkgs-proxy', 'core-credentialshost',
             'core-channelhost', 'core-gateway', 'core-webhost', 'egress-setup',
             'egress-envoy', 'egress-dns', 'image-acquisition'):
    content = ('fixture archive ' + role).encode()
    digest = hashlib.sha256(content).hexdigest()
    name = f'{role}-{digest}.oci'
    (root / 'oci-images' / name).write_bytes(content)
    images.append(dict(id=role, image='example.invalid/offline@sha256:'+'b'*64,
                       archive=name, archiveSha256=digest, localImageId='sha256:'+digest, platform='linux/amd64'))
(root / 'bin/runtime-images.json').write_text(json.dumps(dict(schemaVersion=1, images=images)))
PY
cp "$test_root/payload/bin/runtime-images.json" "$test_root/original.json"
run_prepare() {
    local log="$1" exists="${2:-false}" wrong="${3:-false}"
    : > "$log"
    MOCK_LOG="$log" MOCK_EXISTS="$exists" MOCK_WRONG="$wrong" KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1 bash -c '
        source "$1/scripts/kautuka-install-linux.sh.in"
        run_as_service_user() {
            shift 3
            printf "%s\n" "$*" >> "$MOCK_LOG"
            case "$*" in
                "podman image exists"*)
                    [[ "$MOCK_EXISTS" != error ]] || return 125
                    [[ "$MOCK_EXISTS" == true ]]; return ;;
                "podman load") cat >/dev/null ;;
                "podman image inspect"*)
                    local identity="${@: -1}"
                    [[ "$MOCK_WRONG" == false ]] || identity="sha256:$(printf %064d 0)"
                    printf "[{\"Id\":\"%s\",\"Os\":\"linux\",\"Architecture\":\"amd64\"}]\n" "$identity" ;;
                *) echo "Unexpected registry/build operation: $*" >&2; return 99 ;;
            esac
        }
        prepare_install_images "$2/bin" kautuka /home/kautuka /run/user/1000
    ' _ "$repo_root" "$test_root/payload"
}
run_prepare "$test_root/load.log"
[[ "$(grep -c '^podman load$' "$test_root/load.log")" == 11 ]]
[[ "$(grep -c '^podman image inspect ' "$test_root/load.log")" == 11 ]]
run_prepare "$test_root/cached.log" true
! grep -q '^podman load$' "$test_root/cached.log"
[[ "$(grep -c '^podman image inspect ' "$test_root/cached.log")" == 11 ]]
if run_prepare "$test_root/engine-error.log" error; then
    echo 'Runtime store failure was treated as an image cache miss' >&2; exit 1
fi
! grep -q '^podman load$' "$test_root/engine-error.log"
if run_prepare "$test_root/wrong.log" false true; then
    echo 'Wrong loaded image identity was accepted' >&2; exit 1
fi
# Corrupt the last archive: every input must be validated before any image IO.
last_archive="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["images"][-1]["archive"])' "$test_root/original.json")"
printf changed >> "$test_root/payload/oci-images/$last_archive"
if run_prepare "$test_root/tampered.log"; then
    echo 'Tampered offline archive was accepted' >&2; exit 1
fi
[[ ! -s "$test_root/tampered.log" ]]
python3 - "$test_root/payload/bin/runtime-images.json" <<'PY'
import json, sys
path = sys.argv[1]; inventory = json.load(open(path)); inventory['images'].pop()
with open(path, 'w') as stream: json.dump(inventory, stream)
PY
if run_prepare "$test_root/missing.log"; then
    echo 'Incomplete offline inventory was accepted or fell back to registry pulls' >&2; exit 1
fi
[[ ! -s "$test_root/missing.log" ]]
echo 'PASS offline installer cold/cache preparation, exact image verification, tamper and incomplete-inventory denial'
