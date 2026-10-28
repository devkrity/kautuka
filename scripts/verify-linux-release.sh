verify_linux_release() {
(
  set -euo pipefail
  local manifest='' signature='' public_key='' archive='' version='' channel='' tag='' artifact_name='' artifact_url='' expected_signature_url=''
  local manifest_version manifest_channel manifest_tag signature_algorithm signature_format signature_url artifact_count expected_hash expected_size recorded_url metadata
  usage() {
  echo 'Usage: verify-linux-release.sh --manifest FILE --signature FILE --public-key FILE --archive FILE --version VERSION --channel stable|preview --tag TAG --artifact-name NAME --artifact-url URL --signature-url URL' >&2
    exit 2
  }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --manifest) manifest="$2"; shift 2 ;;
    --signature) signature="$2"; shift 2 ;;
    --public-key) public_key="$2"; shift 2 ;;
    --archive) archive="$2"; shift 2 ;;
    --version) version="$2"; shift 2 ;;
    --channel) channel="$2"; shift 2 ;;
    --tag) tag="$2"; shift 2 ;;
    --artifact-name) artifact_name="$2"; shift 2 ;;
    --artifact-url) artifact_url="$2"; shift 2 ;;
    --signature-url) expected_signature_url="$2"; shift 2 ;;
    *) usage ;;
  esac
done

for variable in manifest signature public_key archive version channel tag artifact_name artifact_url expected_signature_url; do
  [[ -n "${!variable:-}" ]] || usage
done

for command_name in python3 openssl sha256sum stat awk; do
  command -v "$command_name" >/dev/null 2>&1 || {
    echo "error: required command not found: $command_name" >&2
    exit 1
  }
done

[[ -s "$manifest" ]] || { echo 'error: signed release manifest is missing or empty.' >&2; exit 1; }
[[ -s "$signature" ]] || { echo 'error: signed release signature is missing or empty.' >&2; exit 1; }
[[ -s "$archive" ]] || { echo 'error: platform archive is missing or empty.' >&2; exit 1; }
[[ -s "$public_key" ]] || { echo 'error: trusted release public key is missing or empty.' >&2; exit 1; }

metadata="$(python3 - "$manifest" "$artifact_name" <<'PY'
import json
import sys

with open(sys.argv[1], encoding='utf-8') as stream:
    document = json.load(stream)
artifacts = [item for item in document.get('artifacts', []) if isinstance(item, dict) and item.get('name') == sys.argv[2]]
if len(artifacts) != 1:
    raise SystemExit('manifest must contain exactly one requested platform artifact')
signature = document.get('signature') or {}
artifact = artifacts[0]
values = [document.get('version'), document.get('channel'), document.get('releaseTag'), signature.get('algorithm'), signature.get('format'), signature.get('url'), artifact.get('sha256'), artifact.get('size'), artifact.get('url')]
if any(value is None or isinstance(value, (dict, list, bool)) for value in values):
    raise SystemExit('manifest is missing a required identity, signature, or artifact field')
if not isinstance(values[7], int) or values[7] <= 0:
    raise SystemExit('manifest artifact size must be a positive integer')
print('\t'.join(str(value) for value in values))
PY
)" || { echo 'error: release manifest identity or artifact metadata is incomplete.' >&2; exit 1; }
IFS=$'\t' read -r manifest_version manifest_channel manifest_tag signature_algorithm signature_format signature_url expected_hash expected_size recorded_url <<< "$metadata"
[[ "$manifest_version" == "$version" ]] || { echo "error: release manifest version '$manifest_version' does not match '$version'." >&2; exit 1; }
[[ "$manifest_channel" == "$channel" ]] || { echo "error: release manifest channel '$manifest_channel' does not match '$channel'." >&2; exit 1; }
[[ "$manifest_tag" == "$tag" ]] || { echo "error: release manifest tag '$manifest_tag' does not match '$tag'." >&2; exit 1; }

[[ "$signature_algorithm" == 'ecdsa-p256-sha256' && "$signature_format" == 'base64' ]] || {
  echo 'error: release manifest signature format is unsupported.' >&2
  exit 1
}

[[ "$signature_url" == "$expected_signature_url" ]] || { echo 'error: release signature URL does not match the exact-tag release asset.' >&2; exit 1; }

[[ "$expected_hash" =~ ^[0-9a-fA-F]{64}$ ]] || { echo 'error: release artifact SHA-256 is invalid.' >&2; exit 1; }
[[ "$recorded_url" == "$artifact_url" ]] || { echo 'error: release artifact URL does not match the exact-tag release asset.' >&2; exit 1; }

signature_der="${signature}.der"
trap 'rm -f "$signature_der"' EXIT
base64 --decode "$signature" > "$signature_der" 2>/dev/null || { echo 'error: release signature is not valid base64.' >&2; exit 1; }
openssl dgst -sha256 -verify "$public_key" -signature "$signature_der" "$manifest" >/dev/null || {
  echo 'error: release manifest signature verification failed.' >&2
  exit 1
}

actual_hash="$(sha256sum "$archive" | awk '{print $1}')"
actual_size="$(stat -c '%s' "$archive")"
[[ "${actual_hash,,}" == "${expected_hash,,}" ]] || {
  echo 'error: platform archive SHA-256 does not match the signed release manifest.' >&2
  exit 1
}
[[ "$actual_size" == "$expected_size" ]] || {
  echo 'error: platform archive size does not match the signed release manifest.' >&2
  exit 1
}

echo "verified signed release $tag ($channel): $artifact_name"
)
}
