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
mkdir -p "$source_root/src/backend/Kautuka.Manager" "$tmp/bin" "$tmp/rootfs"
allowlist="$source_root/src/backend/Kautuka.Manager/runtime-images.json"
export TEST_ARCHIVE_CALLS="$tmp/calls"
cat > "$tmp/bin/sudo" <<'EOF'
#!/usr/bin/env bash
"$@"
EOF
cat > "$tmp/bin/test-runtime" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$TEST_ARCHIVE_CALLS"
if [[ "$1" == save ]]; then printf 'archive' > "$3"; fi
EOF
chmod +x "$tmp/bin/sudo" "$tmp/bin/test-runtime"
export PATH="$tmp/bin:$PATH"
container_runtime=test-runtime
log_phase() { :; }
# Load the actual staging function without executing the release builder.
source <(sed -n '/^stage_oci_image_archives() {/,/^}$/p' "$repo_root/scripts/build-release-assets.sh")

cat > "$allowlist" <<'EOF'
{"images":[{"id":"base","image":"base-ref","archive":null},{"id":"one","image":"one-ref","archive":"one.tar"},{"id":"two","image":"two-ref","archive":"two.tar"}]}
EOF
stage_oci_image_archives "$tmp/rootfs"
[[ "$(cat "$tmp/rootfs/var/lib/kautuka/oci-images/one.tar")" == archive ]]
[[ "$(cat "$tmp/rootfs/var/lib/kautuka/oci-images/two.tar")" == archive ]]
[[ "$(wc -l < "$TEST_ARCHIVE_CALLS")" == 4 ]]

for invalid in '{"id":"bad","image":"ref","archive":"../../escape.tar"}' \
  '{"id":"bad","image":"ref","archive":"one.tar"}' \
  '{"id":"bad","image":"ref","archive":42}'; do
  printf '{"images":[{"id":"one","image":"one-ref","archive":"one.tar"},%s]}\n' "$invalid" > "$allowlist"
  : > "$TEST_ARCHIVE_CALLS"
  if stage_oci_image_archives "$tmp/rootfs" > "$tmp/log" 2>&1; then
    echo 'invalid archive inventory was accepted' >&2; exit 1
  fi
  [[ ! -s "$TEST_ARCHIVE_CALLS" ]] || { echo 'invalid inventory performed a pull or save' >&2; exit 1; }
done
printf '{not json' > "$allowlist"
if stage_oci_image_archives "$tmp/rootfs" > "$tmp/log" 2>&1; then
  echo 'malformed inventory was accepted' >&2; exit 1
fi
[[ ! -s "$TEST_ARCHIVE_CALLS" ]] || { echo 'malformed inventory performed a pull or save' >&2; exit 1; }
echo 'Runtime archive staging skips registry-only images and fails closed on invalid inventories.'
