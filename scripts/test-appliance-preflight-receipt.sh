#!/usr/bin/env bash
set -euo pipefail
# Runs the real host space checks and receipt writer with isolated temp paths.
[[ $(id -u) == 0 ]] || { echo 'Run as root in a disposable Linux environment.' >&2; exit 2; }
script_dir="$(cd "$(dirname "$0")" && pwd)"
test_root="$(mktemp -d /tmp/kautuka-preflight-test.XXXXXXXX)"
trap 'rm -rf -- "$test_root"' EXIT
mkdir -p "$test_root/recovery/backups" "$test_root/data" "$test_root/opt" "$test_root/input"
printf 'fixture\n' > "$test_root/input/file"
tar -czf "$test_root/archive.tar.gz" -C "$test_root/input" .
printf 'assert __name__ != "__main__"\ndef inventory(*args):\n    return []\n' > "$test_root/kautuka-update-recovery"
recovery_root="$test_root/recovery"
backups_root="$recovery_root/backups"
database_root="$test_root/data"
recovery_tool="$test_root/kautuka-update-recovery"
consume_appliance_request=true
appliance_request_id=0123456789abcdef0123456789abcdef
version=1.2.4
declare -A source_pointers=([manager]=1.2.3)
export KAUTUKA_TEST_FREE_BYTES=1
is_disposable_test() { return 0; }
source <(sed -n '/^record_appliance_preflight_space_failure() {/,/^}/p; /^check_verified_archive_space() {/,/^}/p; /^check_staging_space() {/,/^}/p' "$script_dir/kautuka-apply-release.sh" | sed "s@'/opt/kautuka'@'$test_root/opt'@")
for check in check_verified_archive_space check_staging_space; do
    rm -f "$recovery_root/activation-preflight.json"
    if "$check" "$test_root/archive.tar.gz" appliance; then
        echo "Expected $check to reject low disk space" >&2; exit 1
    else
        [[ $? == 73 ]]
    fi
    python3 - "$recovery_root/activation-preflight.json" <<'PY'
import json, os, stat, sys
path = sys.argv[1]
assert json.load(open(path, encoding='utf-8')) == {
    'requestId': '0123456789abcdef0123456789abcdef', 'sourceVersion': '1.2.3',
    'targetVersion': '1.2.4', 'code': 'insufficient_space'}
assert stat.S_IMODE(os.stat(path).st_mode) == 0o644
assert os.stat(path).st_uid == os.geteuid()
PY
done
rm -f "$recovery_root/activation-preflight.json"
KAUTUKA_TEST_FREE_BYTES=1000000000 check_staging_space "$test_root/archive.tar.gz" appliance
[[ ! -e "$recovery_root/activation-preflight.json" && ! -e "$test_root/__pycache__" ]]
# Exercise the exact request parser against strict identity and newline rules.
mkdir -p "$test_root/kautuka/updateService/updates"
request="$test_root/kautuka/updateService/updates/activate-appliance"
awk '/^import os, re, stat$/ { capture=1 } capture && /^PY$/ { exit } capture { print }' \
    "$script_dir/kautuka-apply-release.sh" | sed "s@'/var/lib'@'$test_root'@" > "$test_root/parser.py"
printf '1.2.4\n0123456789abcdef0123456789abcdef\n' > "$request"
[[ "$(python3 "$test_root/parser.py")" == $'1.2.4\n0123456789abcdef0123456789abcdef' ]]
for payload in $'1.2.4\n0123456789ABCDEF0123456789abcdef\n' $'1.2.4\n0123456789abcdef0123456789abcdef' $'1.2.4\n'; do
    printf '%s' "$payload" > "$request"
    if python3 "$test_root/parser.py" >/dev/null 2>&1; then
        echo 'Malformed activation request was accepted.' >&2; exit 1
    fi
done
echo 'Both low-space checks wrote the typed receipt, and request parsing was strict.'
