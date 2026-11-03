#!/usr/bin/env bash
set -euo pipefail

# Run in a disposable installation with a root-owned trusted key. Arguments are
# a valid signed manifest, its signature, and its release tag, which must not
# be the currently active release. The archive is deliberately unsigned.
[[ ${EUID} -eq 0 && $# -eq 3 ]] || {
    echo 'usage: sudo bash test-apply-release-trust.sh <manifest> <signature> <target-tag>' >&2
    exit 2
}
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
stage_parent=/var/lib/kautuka/updateService/updates/staging
[[ -d "$stage_parent" ]] || { echo 'error: no disposable Kautuka staging directory' >&2; exit 1; }
test_dir="$(mktemp -d "$stage_parent/trust-test-XXXXXX")"
trap 'rm -rf "$test_dir"' EXIT
cp -- "$1" "$test_dir/kautuka-platform-release.json"
cp -- "$2" "$test_dir/kautuka-platform-release.sig"
printf 'attacker-controlled archive\n' > "$test_dir/unsigned.tar.gz"
before="$(readlink -f /opt/kautuka/manager/current)"

set +e
KAUTUKA_APPLY_DETACHED=1 \
KAUTUKA_APPLY_LOCK="$test_dir/lock" \
    bash "$script_dir/kautuka-apply-release.sh" --artifact "$test_dir/unsigned.tar.gz" --version "$3" >"$test_dir/output" 2>&1
result=$?
set -e
if [[ $result -eq 0 ]] || ! grep -Eq 'error: copied artifact (size|hash) does not match signed manifest' "$test_dir/output"; then
    echo 'error: helper did not reject the unsigned artifact at the signed-hash check' >&2
    cat "$test_dir/output" >&2
    exit 1
fi
[[ "$(readlink -f /opt/kautuka/manager/current)" == "$before" ]] || {
    echo 'error: active release changed during rejected update' >&2
    exit 1
}

printf ' ' >> "$test_dir/kautuka-platform-release.json"
set +e
KAUTUKA_APPLY_DETACHED=1 \
KAUTUKA_APPLY_LOCK="$test_dir/lock" \
    bash "$script_dir/kautuka-apply-release.sh" --artifact "$test_dir/unsigned.tar.gz" --version "$3" >"$test_dir/output" 2>&1
result=$?
set -e
if [[ $result -eq 0 ]] || ! grep -Fq 'release manifest signature verification failed in privileged helper' "$test_dir/output"; then
    echo 'error: helper did not reject the tampered manifest signature' >&2
    cat "$test_dir/output" >&2
    exit 1
fi
[[ "$(readlink -f /opt/kautuka/manager/current)" == "$before" ]] || {
    echo 'error: active release changed during tampered-manifest update' >&2
    exit 1
}
echo 'privileged helper rejected unsigned artifact and tampered manifest before activation'
