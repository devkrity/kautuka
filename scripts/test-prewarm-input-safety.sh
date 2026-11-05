#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tmp_root="$(realpath -e -- "${TMPDIR:-/tmp}")"
tmp="$(mktemp -d "$tmp_root/kautuka-prewarm-test.XXXXXX")"
cleanup() {
  local actual
  actual="$(realpath -e -- "$tmp")" || return 1
  case "$actual" in
    "$tmp_root"/kautuka-prewarm-test.*) rm -rf -- "$actual" ;;
    *) echo 'refusing to remove an unexpected test directory' >&2; return 1 ;;
  esac
}
trap cleanup EXIT
mkdir -p "$tmp/bin" "$tmp/output/0.1.99-preview.1"
printf '#!/usr/bin/env bash\nexit 42\n' > "$tmp/bin/node"
chmod +x "$tmp/bin/node"
input="$tmp/output/0.1.99-preview.1/input.tar.gz"
printf 'input sentinel' > "$input"

run_prewarm() {
  PATH="$tmp/bin:$PATH" KAUTUKA_RELEASE_DOTNET_SDK_VERSION=10.0.403 KAUTUKA_RELEASE_NODE_VERSION=26.3.0 \
    bash "$repo_root/scripts/build-release-assets.sh" --prewarm-only --output-root "$tmp/output" \
      --platform-tarball "$input" --version 0.1.99-preview.1 --product-version 0.1.99 \
      --source-sha 0000000000000000000000000000000000000000 --channel preview > "$tmp/log" 2>&1
}

# Stop at the mocked compliance step, after output-directory preparation.
if run_prewarm; then echo 'expected the mocked compliance step to fail' >&2; exit 1; else status=$?; fi
[[ "$status" == 42 ]] || { cat "$tmp/log" >&2; echo "unexpected exit: $status" >&2; exit 1; }
[[ "$(cat "$input")" == 'input sentinel' ]] || { echo 'prewarm removed its input' >&2; exit 1; }

for name in kautuka-platform-release.json kautuka-platform-release.sig kautuka-release-assets.sha256; do
  metadata="$tmp/output/0.1.99-preview.1/$name"
  printf 'metadata sentinel' > "$metadata"
  if run_prewarm; then echo "expected $name to be rejected" >&2; exit 1; fi
  grep -q 'prewarm-only cannot replace an appliance beside existing release metadata' "$tmp/log"
  [[ "$(cat "$input")" == 'input sentinel' && "$(cat "$metadata")" == 'metadata sentinel' ]]
  rm -f "$metadata"
done

output="$tmp/output/0.1.99-preview.1/kautuka-wsl-appliance.tar.gz"
printf 'appliance sentinel' > "$output"
check_alias() {
  input="$1"
  if run_prewarm; then echo 'expected aliased input to be rejected' >&2; exit 1; fi
  grep -q 'prewarm input must be a different file' "$tmp/log"
  [[ "$(cat "$output")" == 'appliance sentinel' ]]
}
check_alias "$output"
if ln "$output" "$tmp/hardlink.tar.gz" 2>/dev/null; then
  check_alias "$tmp/hardlink.tar.gz"
fi
if ln -s "$output" "$tmp/symlink.tar.gz" 2>/dev/null && [[ "$tmp/symlink.tar.gz" -ef "$output" ]]; then
  check_alias "$tmp/symlink.tar.gz"
fi
echo 'Prewarm input and existing release metadata remain intact.'
