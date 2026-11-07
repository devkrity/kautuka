#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tmp_root="$(realpath -e -- "${TMPDIR:-/tmp}")"
tmp="$(mktemp -d "$tmp_root/kautuka-archive-test.XXXXXX")"
cleanup() {
  local actual
  actual="$(realpath -e -- "$tmp")" || return 1
  case "$actual" in
    "$tmp_root"/kautuka-archive-test.*) rm -rf -- "$actual" ;;
    *) echo 'refusing to remove an unexpected test directory' >&2; return 1 ;;
  esac
}
trap cleanup EXIT
source_root="$tmp/source"
mkdir -p "$source_root/src/backend/Kautuka.Manager" "$source_root/scripts" "$tmp/bin" "$tmp/payload/bin" "$tmp/payload/oci-images"
cp "$repo_root/scripts/bind-runtime-image-archives.py" "$source_root/scripts/"
allowlist="$source_root/src/backend/Kautuka.Manager/runtime-images.json"
export TEST_ARCHIVE_CALLS="$tmp/calls"
cat > "$tmp/bin/sudo" <<'EOF'
#!/usr/bin/env bash
"$@"
EOF
cat > "$tmp/bin/test-runtime" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$TEST_ARCHIVE_CALLS"
if [[ "$1" == save ]]; then
python3 - "$3" <<'PY'
import io, json, hashlib, sys, tarfile
config=json.dumps({'os':'linux','architecture':'amd64','rootfs':{'type':'layers','diff_ids':[]}}).encode()
name=hashlib.sha256(config).hexdigest()+'.json'
with tarfile.open(sys.argv[1],'w') as archive:
    for path,data in [(name,config),('manifest.json',json.dumps([{'Config':name,'Layers':[]}]).encode())]:
        member=tarfile.TarInfo(path);member.size=len(data);archive.addfile(member,io.BytesIO(data))
PY
fi
EOF
chmod +x "$tmp/bin/sudo" "$tmp/bin/test-runtime"
export PATH="$tmp/bin:$PATH"
container_runtime=test-runtime
log_phase() { :; }
# Load the actual staging function without executing the release builder.
source <(sed -n '/^stage_oci_image_archives() {/,/^}$/p' "$repo_root/scripts/build-release-assets.sh")
source <(sed -n '/^copy_staged_oci_image_archives() {/,/^}$/p' "$repo_root/scripts/build-release-assets.sh")

cat > "$allowlist" <<'EOF'
{"images":[{"id":"base","image":"base-ref","archive":null},{"id":"one","image":"ghcr.io/example/one@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","archive":"one.tar"},{"id":"two","image":"ghcr.io/example/two@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","archive":"two.tar"}]}
EOF
stage_oci_image_archives "$tmp/payload/oci-images" "$tmp/payload/bin/runtime-images.json"
python3 - "$tmp/payload/bin/runtime-images.json" "$tmp/payload/oci-images" <<'PY'
import json, pathlib, hashlib, sys
bound=json.loads(pathlib.Path(sys.argv[1]).read_text())
for image in bound['images']:
    if image['archive'] is None: continue
    archive=pathlib.Path(sys.argv[2])/image['archive']
    assert image['archiveSha256']==hashlib.sha256(archive.read_bytes()).hexdigest()
    assert image['platform']=='linux/amd64' and image['localImageId'].startswith('sha256:')
PY
[[ "$(wc -l < "$TEST_ARCHIVE_CALLS")" == 4 ]]

: > "$TEST_ARCHIVE_CALLS"
copy_staged_oci_image_archives "$tmp/payload" "$tmp/wsl-images" "$tmp/wsl-inventory.json"
cmp "$tmp/payload/bin/runtime-images.json" "$tmp/wsl-inventory.json"
cmp "$tmp/payload/oci-images/one.tar" "$tmp/wsl-images/one.tar"
cmp "$tmp/payload/oci-images/two.tar" "$tmp/wsl-images/two.tar"
[[ ! -s "$TEST_ARCHIVE_CALLS" ]] || { echo 'copying the common stage performed registry IO' >&2; exit 1; }
printf 'tampered' >> "$tmp/payload/oci-images/one.tar"
if copy_staged_oci_image_archives "$tmp/payload" "$tmp/rejected-wsl-images" "$tmp/rejected.json" > "$tmp/log" 2>&1; then
  echo 'tampered common archive was accepted' >&2; exit 1
fi
[[ ! -e "$tmp/rejected.json" && ! -e "$tmp/rejected-wsl-images" ]]
if copy_staged_oci_image_archives "$tmp/absent-payload" "$tmp/rejected-wsl-images" "$tmp/rejected.json" > "$tmp/log" 2>&1; then
  echo 'missing common inventory was accepted' >&2; exit 1
fi

for invalid in '{"id":"bad","image":"ref","archive":"../../escape.tar"}' \
  '{"id":"bad","image":"ref","archive":"one.tar"}' \
  '{"id":"bad","image":"ref","archive":42}'; do
  printf '{"images":[{"id":"one","image":"ghcr.io/example/one@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","archive":"one.tar"},%s]}\n' "$invalid" > "$allowlist"
  : > "$TEST_ARCHIVE_CALLS"
  if stage_oci_image_archives "$tmp/payload/oci-images" "$tmp/payload/bin/runtime-images.json" > "$tmp/log" 2>&1; then
    echo 'invalid archive inventory was accepted' >&2; exit 1
  fi
  [[ ! -s "$TEST_ARCHIVE_CALLS" ]] || { echo 'invalid inventory performed a pull or save' >&2; exit 1; }
done
printf '{not json' > "$allowlist"
if stage_oci_image_archives "$tmp/payload/oci-images" "$tmp/payload/bin/runtime-images.json" > "$tmp/log" 2>&1; then
  echo 'malformed inventory was accepted' >&2; exit 1
fi
[[ ! -s "$TEST_ARCHIVE_CALLS" ]] || { echo 'malformed inventory performed a pull or save' >&2; exit 1; }
echo 'Common archive staging and byte-identical offline WSL copy reject invalid inventories and changed archives.'
