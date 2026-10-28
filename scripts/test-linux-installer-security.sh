#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root_dir="$(cd "$script_dir/.." && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

openssl ecparam -name prime256v1 -genkey -noout -out "$tmp/private.pem"
openssl pkey -in "$tmp/private.pem" -pubout -out "$tmp/public.pem" >/dev/null 2>&1
bash "$script_dir/generate-linux-installer.sh" \
  --output "$tmp/generated-kautuka-install-linux.sh" \
  --version '0.1.20-preview.1' \
  --release-channel preview \
  --artifact-url 'https://github.com/devkrity/kautuka/releases/download/v0.1.20-preview.1/kautuka-linux-amd64.tar.gz' \
  --manifest-url 'https://github.com/devkrity/kautuka/releases/download/v0.1.20-preview.1/kautuka-platform-release.json' \
  --manifest-public-key "$tmp/public.pem"
bash -n "$tmp/generated-kautuka-install-linux.sh"
grep -q '^verify_linux_release() {' "$tmp/generated-kautuka-install-linux.sh"
verification_line="$(grep -n 'verify_linux_release \\' "$tmp/generated-kautuka-install-linux.sh" | head -n 1 | cut -d: -f1)"
mutation_line="$(grep -n 'ensure_service_account "$service_user"' "$tmp/generated-kautuka-install-linux.sh" | tail -n 1 | cut -d: -f1)"
[[ "$verification_line" =~ ^[0-9]+$ && "$mutation_line" =~ ^[0-9]+$ && "$verification_line" -lt "$mutation_line" ]] || {
  echo 'error: generated installer does not verify the signed release before account creation.' >&2
  exit 1
}

mock_bin="$tmp/mock-bin"
mkdir -p "$mock_bin"
cat > "$mock_bin/gh" <<'MOCK_GH'
#!/usr/bin/env bash
set -euo pipefail
[[ "$1 $2" == 'attestation verify' && "$4 $5" == '--repo devkrity/kautuka' && "$6 $7" == '--signer-workflow devkrity/kautuka/.github/workflows/release-build-platform-assets.yml' ]]
actual="$(sha256sum "$3" | awk '{print $1}')"
[[ "$actual" == "$EXPECTED_ATTESTED_INSTALLER_SHA256" ]]
MOCK_GH
cat > "$mock_bin/sudo" <<'MOCK_SUDO'
#!/usr/bin/env bash
set -euo pipefail
[[ "$1" == 'bash' ]]
shift
exec bash "$@"
MOCK_SUDO
chmod +x "$mock_bin/gh" "$mock_bin/sudo"
cat > "$tmp/test-installer.sh" <<'INSTALLER'
#!/usr/bin/env bash
printf 'executed\n' > "$EXECUTED_INSTALLER_MARKER"
INSTALLER
attested_installer_hash="$(sha256sum "$tmp/test-installer.sh" | awk '{print $1}')"
PATH="$mock_bin:$PATH" EXPECTED_ATTESTED_INSTALLER_SHA256="$attested_installer_hash" EXECUTED_INSTALLER_MARKER="$tmp/installer-executed" \
  bash "$script_dir/install-verified-linux.sh" "$tmp/test-installer.sh" >/dev/null
[[ -f "$tmp/installer-executed" ]]
printf '# substituted\n' >> "$tmp/test-installer.sh"
rm -f "$tmp/installer-executed"
if PATH="$mock_bin:$PATH" EXPECTED_ATTESTED_INSTALLER_SHA256="$attested_installer_hash" EXECUTED_INSTALLER_MARKER="$tmp/installer-executed" \
  bash "$script_dir/install-verified-linux.sh" "$tmp/test-installer.sh" >/dev/null 2>&1; then
  echo 'error: substituted Linux installer unexpectedly passed provenance verification.' >&2
  exit 1
fi
[[ ! -e "$tmp/installer-executed" ]] || { echo 'error: substituted installer executed before provenance rejection.' >&2; exit 1; }

printf 'known release archive bytes\n' > "$tmp/kautuka-linux-amd64.tar.gz"
artifact_hash="$(sha256sum "$tmp/kautuka-linux-amd64.tar.gz" | awk '{print $1}')"
artifact_size="$(stat -c '%s' "$tmp/kautuka-linux-amd64.tar.gz")"
tag='v0.1.20-preview.1'
version='0.1.20-preview.1'
channel='preview'
artifact_url="https://github.com/devkrity/kautuka/releases/download/${tag}/kautuka-linux-amd64.tar.gz"
signature_url="https://github.com/devkrity/kautuka/releases/download/${tag}/kautuka-platform-release.sig"

write_manifest() {
  local output="$1" manifest_version="${2:-$version}" manifest_channel="${3:-$channel}" manifest_tag="${4:-$tag}" manifest_url="$artifact_url"
  if [[ -n "${5:-}" ]]; then manifest_url="$5"; fi
  python3 - "$output" "$manifest_version" "$manifest_channel" "$manifest_tag" "$manifest_url" "$signature_url" "$artifact_hash" "$artifact_size" <<'PY'
import json
import sys

output, version, channel, tag, artifact_url, signature_url, sha256, size = sys.argv[1:]
manifest = {
    'schemaVersion': 2,
    'version': version,
    'channel': channel,
    'releaseTag': tag,
    'signature': {'algorithm': 'ecdsa-p256-sha256', 'format': 'base64', 'url': signature_url},
    'artifacts': [{'name': 'kautuka-linux-amd64.tar.gz', 'platform': 'linux-x64', 'url': artifact_url, 'sha256': sha256, 'size': int(size)}],
}
with open(output, 'w', encoding='utf-8') as stream:
    json.dump(manifest, stream, indent=2)
    stream.write('\n')
PY
}

write_manifest "$tmp/manifest.json"
openssl dgst -sha256 -sign "$tmp/private.pem" -out "$tmp/signature.der" "$tmp/manifest.json"
base64 -w0 "$tmp/signature.der" > "$tmp/signature.sig"

run_preflight() {
  local manifest="$1" signature="$2" archive="$3" expected_version="${4:-$version}" expected_channel="${5:-$channel}" expected_tag="${6:-$tag}" expected_url="${7:-$artifact_url}" marker="$8"
  env VERIFIER="$script_dir/verify-linux-release.sh" MANIFEST="$manifest" SIGNATURE="$signature" PUBLIC_KEY="$tmp/public.pem" ARCHIVE="$archive" \
    VERSION="$expected_version" CHANNEL="$expected_channel" TAG="$expected_tag" ARTIFACT_URL="$expected_url" SIGNATURE_URL="$signature_url" MARKER="$marker" \
    bash -c 'source "$VERIFIER"; verify_linux_release --manifest "$MANIFEST" --signature "$SIGNATURE" --public-key "$PUBLIC_KEY" --archive "$ARCHIVE" --version "$VERSION" --channel "$CHANNEL" --tag "$TAG" --artifact-name kautuka-linux-amd64.tar.gz --artifact-url "$ARTIFACT_URL" --signature-url "$SIGNATURE_URL" && touch "$MARKER"' 2>/dev/null
}

expect_failure_before_mutation() {
  local label="$1" manifest="$2" signature="$3" archive="$4" expected_version="${5:-$version}" expected_channel="${6:-$channel}" expected_tag="${7:-$tag}" expected_url="${8:-$artifact_url}"
  local marker="$tmp/${label}.persistent-mutation"
  rm -f "$marker"
  if run_preflight "$manifest" "$signature" "$archive" "$expected_version" "$expected_channel" "$expected_tag" "$expected_url" "$marker"; then
    echo "error: expected $label to fail before persistent mutation." >&2
    exit 1
  fi
  [[ ! -e "$marker" ]] || { echo "error: $label ran past verification." >&2; exit 1; }
}

run_preflight "$tmp/manifest.json" "$tmp/signature.sig" "$tmp/kautuka-linux-amd64.tar.gz" "$version" "$channel" "$tag" "$artifact_url" "$tmp/valid.marker"
[[ -f "$tmp/valid.marker" ]]

printf 'tampered' >> "$tmp/manifest.json"
expect_failure_before_mutation tampered-manifest "$tmp/manifest.json" "$tmp/signature.sig" "$tmp/kautuka-linux-amd64.tar.gz"
write_manifest "$tmp/manifest.json"
printf 'bad-signature\n' > "$tmp/signature.sig"
expect_failure_before_mutation unsigned-manifest "$tmp/manifest.json" "$tmp/signature.sig" "$tmp/kautuka-linux-amd64.tar.gz"
openssl dgst -sha256 -sign "$tmp/private.pem" -out "$tmp/signature.der" "$tmp/manifest.json"
base64 -w0 "$tmp/signature.der" > "$tmp/signature.sig"
printf 'substituted archive\n' > "$tmp/substituted.tar.gz"
expect_failure_before_mutation wrong-digest "$tmp/manifest.json" "$tmp/signature.sig" "$tmp/substituted.tar.gz"

for identity in version channel tag artifact-url; do
  write_manifest "$tmp/manifest.json"
  openssl dgst -sha256 -sign "$tmp/private.pem" -out "$tmp/signature.der" "$tmp/manifest.json"
  base64 -w0 "$tmp/signature.der" > "$tmp/signature.sig"
  case "$identity" in
    version) expect_failure_before_mutation mismatched-version "$tmp/manifest.json" "$tmp/signature.sig" "$tmp/kautuka-linux-amd64.tar.gz" '0.1.20-preview.2' ;;
    channel) expect_failure_before_mutation mismatched-channel "$tmp/manifest.json" "$tmp/signature.sig" "$tmp/kautuka-linux-amd64.tar.gz" "$version" stable ;;
    tag) expect_failure_before_mutation mismatched-tag "$tmp/manifest.json" "$tmp/signature.sig" "$tmp/kautuka-linux-amd64.tar.gz" "$version" "$channel" 'v0.1.20-preview.2' ;;
    artifact-url) expect_failure_before_mutation mismatched-artifact-url "$tmp/manifest.json" "$tmp/signature.sig" "$tmp/kautuka-linux-amd64.tar.gz" "$version" "$channel" "$tag" 'https://github.com/devkrity/kautuka/releases/download/v0.1.20-preview.2/kautuka-linux-amd64.tar.gz' ;;
  esac
done

# Exercise the generated installer's real main path. All persistent system
# commands are replaced with hermetic stubs; groupadd records and stops at the
# first account-mutation boundary. Only the signed valid case may reach it.
main_manifest_url="https://github.com/devkrity/kautuka/releases/download/${tag}/kautuka-platform-release.json"
bash "$script_dir/generate-linux-installer.sh" \
  --output "$tmp/generated-main-installer.sh" \
  --version "$version" \
  --release-channel "$channel" \
  --artifact-url "$artifact_url" \
  --manifest-url "$main_manifest_url" \
  --manifest-public-key "$tmp/public.pem"
bash -n "$tmp/generated-main-installer.sh"

main_mock_bin="$tmp/main-mock-bin"
mkdir -p "$main_mock_bin"
cat > "$main_mock_bin/curl" <<'MOCK_CURL'
#!/usr/bin/env bash
set -euo pipefail
output=''
url=''
while [[ $# -gt 0 ]]; do
  case "$1" in
    --output) output="$2"; shift 2 ;;
    --*) shift ;;
    *) url="$1"; shift ;;
  esac
done
[[ -n "$output" && -n "$url" ]]
printf '%s\n' "$(dirname "$output")" >> "$MAIN_TMP_ROOT_LOG"
case "$url" in
  *.json) cat "$MAIN_MANIFEST_SOURCE" > "$output" ;;
  *.sig) cat "$MAIN_SIGNATURE_SOURCE" > "$output" ;;
  *.tar.gz) cat "$MAIN_ARCHIVE_SOURCE" > "$output" ;;
  *) echo "unexpected test download URL" >&2; exit 90 ;;
esac
MOCK_CURL
cat > "$main_mock_bin/getent" <<'MOCK_GETENT'
#!/usr/bin/env bash
exit 2
MOCK_GETENT
cat > "$main_mock_bin/groupadd" <<'MOCK_GROUPADD'
#!/usr/bin/env bash
set -euo pipefail
printf 'groupadd %s\n' "$*" >> "$MAIN_MUTATION_LOG"
exit 97
MOCK_GROUPADD
for command_name in systemctl loginctl install useradd usermod chown chmod mkdir mv ln; do
  cat > "$main_mock_bin/$command_name" <<'MOCK_MUTATION'
#!/usr/bin/env bash
set -euo pipefail
printf '%s %s\n' "${0##*/}" "$*" >> "$MAIN_MUTATION_LOG"
exit 98
MOCK_MUTATION
done
real_rm_path="$(command -v rm)"
cat > "$main_mock_bin/rm" <<'MOCK_RM'
#!/usr/bin/env bash
set -euo pipefail
for path in "$@"; do
  case "$path" in
    -*) ;;
    "$MAIN_ALLOWED_TMP_ROOT"/*) ;;
    *) printf 'rm %s\n' "$*" >> "$MAIN_MUTATION_LOG"; exit 98 ;;
  esac
done
exec "$REAL_RM_PATH" "$@"
MOCK_RM
for command_name in tar runuser podman sudo; do
  cat > "$main_mock_bin/$command_name" <<'MOCK_FORBIDDEN'
#!/usr/bin/env bash
printf 'unexpected %s invocation\n' "${0##*/}" >&2
exit 99
MOCK_FORBIDDEN
done
chmod +x "$main_mock_bin"/*
mkdir -p "$tmp/main-installer-tmp"

write_manifest "$tmp/main-valid-manifest.json"
openssl dgst -sha256 -sign "$tmp/private.pem" -out "$tmp/main-valid-signature.der" "$tmp/main-valid-manifest.json"
base64 -w0 "$tmp/main-valid-signature.der" > "$tmp/main-valid-signature.sig"

run_generated_main() {
  local manifest="$1" signature="$2" archive="$3"
  local -a runner_command=(env \
    "PATH=$main_mock_bin:$PATH" \
    "GENERATED_INSTALLER=$tmp/generated-main-installer.sh" \
    "MAIN_MANIFEST_SOURCE=$manifest" \
    "MAIN_SIGNATURE_SOURCE=$signature" \
    "MAIN_ARCHIVE_SOURCE=$archive" \
    "MAIN_TMP_ROOT_LOG=$tmp/main-temp-roots.log" \
    "MAIN_MUTATION_LOG=$tmp/main-mutations.log" \
    "MAIN_ALLOWED_TMP_ROOT=$tmp/main-installer-tmp" \
    "TMPDIR=$tmp/main-installer-tmp" \
    "REAL_RM_PATH=$real_rm_path" \
    bash -c 'set -euo pipefail; source "$GENERATED_INSTALLER"; require_root() { return 0; }; main "$@"' \
    kautuka-installer-test-run \
    --service-user "kautuka-installer-test-$$")
  "${runner_command[@]}"
}

expect_main_rejected_before_account_mutation() {
  local label="$1" manifest="$2" signature="$3" archive="$4"
  : > "$tmp/main-mutations.log"
  : > "$tmp/main-temp-roots.log"
  if run_generated_main "$manifest" "$signature" "$archive" >/dev/null 2>&1; then
    echo "error: generated installer main accepted $label." >&2
    exit 1
  fi
  [[ ! -s "$tmp/main-mutations.log" ]] || { echo "error: generated installer reached a system mutation for $label." >&2; exit 1; }
  local installer_tmp_root
  installer_tmp_root="$(tail -n 1 "$tmp/main-temp-roots.log")"
  [[ -n "$installer_tmp_root" && ! -e "$installer_tmp_root" ]] || {
    echo "error: generated installer did not clean its temporary directory after rejecting $label." >&2
    exit 1
  }
}

cp "$tmp/main-valid-manifest.json" "$tmp/main-tampered-manifest.json"
printf 'tampered' >> "$tmp/main-tampered-manifest.json"
expect_main_rejected_before_account_mutation \
  tampered-main-manifest "$tmp/main-tampered-manifest.json" "$tmp/main-valid-signature.sig" "$tmp/kautuka-linux-amd64.tar.gz"
printf 'invalid signature\n' > "$tmp/main-invalid-signature.sig"
expect_main_rejected_before_account_mutation \
  unsigned-main-manifest "$tmp/main-valid-manifest.json" "$tmp/main-invalid-signature.sig" "$tmp/kautuka-linux-amd64.tar.gz"
expect_main_rejected_before_account_mutation \
  main-wrong-digest "$tmp/main-valid-manifest.json" "$tmp/main-valid-signature.sig" "$tmp/substituted.tar.gz"

: > "$tmp/main-mutations.log"
: > "$tmp/main-temp-roots.log"
set +e
run_generated_main "$tmp/main-valid-manifest.json" "$tmp/main-valid-signature.sig" "$tmp/kautuka-linux-amd64.tar.gz" >/dev/null 2>&1
valid_main_status=$?
set -e
[[ "$valid_main_status" -eq 97 && "$(wc -l < "$tmp/main-mutations.log")" -eq 1 && "$(head -n 1 "$tmp/main-mutations.log")" == groupadd* ]] || {
  echo 'error: valid signed installer did not reach the mocked first account-mutation boundary.' >&2
  exit 1
}
installer_tmp_root="$(tail -n 1 "$tmp/main-temp-roots.log")"
[[ -n "$installer_tmp_root" && ! -e "$installer_tmp_root" ]] || {
  echo 'error: generated installer did not clean its temporary directory after the mocked mutation stop.' >&2
  exit 1
}

echo 'Linux installer trust checks rejected tampering, unsigned manifests, digest mismatch, and candidate identity mismatches; generated main reached the mocked mutation boundary only for the valid release.'
