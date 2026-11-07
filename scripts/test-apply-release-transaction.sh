#!/usr/bin/env bash
set -euo pipefail

# This fixture uses real /opt, /etc, and /var/lib paths. It is intentionally
# restricted to a disposable test distro to exercise the production helper.
[[ ${EUID} -eq 0 && "${KAUTUKA_DISPOSABLE_TEST:-}" == 1 ]] || {
    echo 'error: requires root and KAUTUKA_DISPOSABLE_TEST=1 in a disposable system' >&2
    exit 1
}
for path in /opt/kautuka /etc/kautuka /var/lib/kautuka /var/lib/kautuka-recovery /usr/local/lib/kautuka /run/user/992 /run/kautuka-apply.rollback /run/kautuka-db-writer.lock; do
    [[ ! -e "$path" && ! -L "$path" ]] || { echo "error: fixture path already exists: $path" >&2; exit 1; }
done

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
temp_dir="$(mktemp -d)"
cleanup() {
    rm -rf -- "$temp_dir" /opt/kautuka /etc/kautuka /var/lib/kautuka /var/lib/kautuka-recovery /usr/local/lib/kautuka /run/user/992
    rm -f /run/kautuka-db-writer.lock
}
trap cleanup EXIT

mkdir -p /etc/kautuka /usr/local/lib/kautuka /var/lib/kautuka/updateService/updates/staging/test
install -m 0644 /dev/null /run/kautuka-db-writer.lock
touch /etc/kautuka/.allow-recovery-test
chmod 600 /etc/kautuka/.allow-recovery-test
install -m 0755 "$script_dir/kautuka-apply-release.sh" /usr/local/lib/kautuka/apply-release
install -m 0755 "$script_dir/kautuka-update-recovery.py" /usr/local/lib/kautuka/update-recovery.py
install -m 0755 "$script_dir/kautuka-runtime-images.py" /usr/local/lib/kautuka/runtime-images.py
openssl ecparam -name prime256v1 -genkey -noout -out "$temp_dir/private.pem"
openssl ec -in "$temp_dir/private.pem" -pubout -out /etc/kautuka/manifest-public-key.pem 2>/dev/null
chmod 644 /etc/kautuka/manifest-public-key.pem

services=(manager updateservice webhost gateway channelhost credentialshost)
for service in "${services[@]}"; do
    mkdir -p "/opt/kautuka/$service/versions/v1.0.0"
    printf '#!/bin/sh\nexit 0\n' > "/opt/kautuka/$service/versions/v1.0.0/kautuka-$service"
    chmod 755 "/opt/kautuka/$service/versions/v1.0.0/kautuka-$service"
    ln -s "versions/v1.0.0" "/opt/kautuka/$service/current"
done
printf '%s\n' 'kautuka-maintenance-page-v1' > /opt/kautuka/webhost/current/.maintenance-page-v1
mkdir -p "/var/lib/kautuka/credentialsHost/vault/vault-keys"
python3 - <<'PY'
import sqlite3
for path, table, value in (
    ('/var/lib/kautuka/kautuka.db', 'registry', 'source-state'),
    ('/var/lib/kautuka/channel-outbox.db', 'outbox', 'pending-message'),
    ('/var/lib/kautuka/credentialsHost/vault/kautuka.credentials.db', 'user_credentials', 'KAUTUKA-CREDENTIAL-V2:A1B2C3D4:aa:bb:cc'),
):
    db = sqlite3.connect(path)
    db.execute('PRAGMA journal_mode=WAL')
    if table == 'user_credentials':
        db.execute('CREATE TABLE user_credentials(encrypted_value TEXT)')
        db.execute('CREATE TABLE system_credentials(encrypted_value TEXT)')
        db.execute(f'INSERT INTO {table} VALUES (?)', (value,))
    else:
        db.execute(f'CREATE TABLE {table}(value TEXT)')
        db.execute(f'INSERT INTO {table} VALUES (?)', (value,))
    db.commit(); db.close()
open('/var/lib/kautuka/kautuka.key', 'wb').write(b'm' * 32)
open('/var/lib/kautuka/gateway-auth.key', 'wb').write(b'g' * 32)
open('/var/lib/kautuka/credentialsHost/vault/vault-keys/active', 'w').write('A1B2C3D4\n')
open('/var/lib/kautuka/credentialsHost/vault/vault-keys/A1B2C3D4.key', 'wb').write(b'k' * 32)
PY
chmod 600 /var/lib/kautuka/kautuka.key /var/lib/kautuka/gateway-auth.key /var/lib/kautuka/credentialsHost/vault/vault-keys/*

mkdir -p "$temp_dir/mock" /run/user/992
cat > "$temp_dir/mock/systemctl" <<'SH'
#!/bin/sh
if [ "$1" = "show" ] && [ "$2" = "--property=MainPID" ]; then echo 0; fi
exit 0
SH
cat > "$temp_dir/mock/runuser" <<'SH'
#!/bin/sh
exit 0
SH
cat > "$temp_dir/mock/getent" <<'SH'
#!/bin/sh
printf 'kautuka:x:992:992::/var/lib/kautuka:/usr/sbin/nologin\n'
SH
cat > "$temp_dir/mock/id" <<'SH'
#!/bin/sh
printf '992\n'
SH
chmod 755 "$temp_dir/mock/"*
python3 -c 'import socket; s = socket.socket(socket.AF_UNIX); s.bind("/run/user/992/bus"); s.close()'

make_signed_release() {
    local release="$1" protocol="${2:-current}" payload="$temp_dir/payload-$1" stage='/var/lib/kautuka/updateService/updates/staging/test'
    rm -rf -- "$payload"
    mkdir -p "$payload/bin"
    for service in "${services[@]}"; do
        local destination
        if [[ "$service" == manager ]]; then destination="$payload/bin"; else destination="$payload/services/$service"; mkdir -p "$destination"; fi
        printf '#!/bin/sh\nexit 0\n' > "$destination/kautuka-$service"
        chmod 755 "$destination/kautuka-$service"
    done
    printf '%s\n' 'kautuka-maintenance-page-v1' > "$payload/services/webhost/.maintenance-page-v1"
    if [[ -f "$temp_dir/core-manager" ]]; then cp "$temp_dir/core-manager" "$payload/bin/kautuka-manager"; fi
    if [[ "$protocol" == current ]]; then
        cp "$script_dir/kautuka-apply-release.sh" "$payload/bin/kautuka-apply-release"
        cp "$script_dir/kautuka-runtime-images.py" "$payload/bin/kautuka-runtime-images"
    else
        printf '#!/usr/bin/env bash\n# Kautuka-Apply-Protocol: 4\nprintf "%%s\\n" safe-state-recovery-v2\n' > "$payload/bin/kautuka-apply-release"
        chmod 755 "$payload/bin/kautuka-apply-release"
    fi
    if [[ -f "$temp_dir/core-manager" ]]; then
        python3 - "$payload" "$release" <<'PY'
import hashlib, json, pathlib, sys
payload, release = pathlib.Path(sys.argv[1]), sys.argv[2]
archives = payload / 'oci-images'; archives.mkdir()
roles = ('kautuka-base', 'openobserve', 'git-pkgs-proxy', 'core-credentialshost',
         'core-channelhost', 'core-gateway', 'core-webhost', 'egress-setup',
         'egress-envoy', 'egress-dns', 'image-acquisition')
entries = []
for role in roles:
    data = ('fixture archive ' + role + ' ' + release).encode()
    digest = hashlib.sha256(data).hexdigest()
    name = f'{role}-{digest}.oci'
    (archives / name).write_bytes(data)
    entries.append(dict(id=role, archive=name, archiveSha256=digest, platform='linux/amd64'))
(payload / 'bin/runtime-images.json').write_text(json.dumps(dict(schemaVersion=1, images=entries)))
PY
    fi
    tar --owner=1234 --group=1234 -czf "$stage/kautuka-linux-amd64.tar.gz" -C "$payload" .
    python3 - "$stage" "$release" <<'PY'
import hashlib, json, os, sys
stage, release = sys.argv[1:]
artifact = os.path.join(stage, 'kautuka-linux-amd64.tar.gz')
with open(artifact, 'rb') as stream: digest = hashlib.file_digest(stream, 'sha256').hexdigest()
manifest = {'version': release, 'releaseTag': 'v' + release,
            'signature': {'algorithm': 'ecdsa-p256-sha256', 'format': 'base64'},
            'artifacts': [{'name': 'kautuka-linux-amd64.tar.gz', 'sha256': digest,
                           'size': os.path.getsize(artifact)}]}
with open(os.path.join(stage, 'kautuka-platform-release.json'), 'w') as stream: json.dump(manifest, stream)
PY
    openssl dgst -sha256 -sign "$temp_dir/private.pem" -out "$temp_dir/signature.der" "$stage/kautuka-platform-release.json"
    base64 "$temp_dir/signature.der" > "$stage/kautuka-platform-release.sig"
}

run_helper() {
    PATH="$temp_dir/mock:$PATH" KAUTUKA_DISPOSABLE_TEST=1 KAUTUKA_TEST_HEALTH_OK=1 \
      KAUTUKA_APPLY_DETACHED=1 KAUTUKA_APPLY_LOCK="$temp_dir/lock" \
      /usr/local/lib/kautuka/apply-release "$@"
}

[[ "$(run_helper --capabilities)" == 'safe-state-recovery-v5' ]]
exec 8>"$temp_dir/lock"
flock -n 8
run_helper --boot-recover
exec 8>&-
[[ ! -e /var/lib/kautuka-recovery/operation.json && ! -e /var/lib/kautuka-recovery/maintenance.json ]]
touch /run/kautuka-apply.rollback
set +e
run_helper --capabilities >"$temp_dir/legacy-marker.log" 2>&1
legacy_marker_status=$?
set -e
[[ $legacy_marker_status -ne 0 && -f /run/kautuka-apply.rollback ]]
grep -Fq 'legacy recovery marker requires administrator review' "$temp_dir/legacy-marker.log"
rm -f /run/kautuka-apply.rollback
touch /var/lib/kautuka-recovery/kautuka-apply.rollback
set +e
run_helper --capabilities >"$temp_dir/legacy-state-marker.log" 2>&1
legacy_state_marker_status=$?
set -e
[[ $legacy_state_marker_status -ne 0 && -f /var/lib/kautuka-recovery/kautuka-apply.rollback ]]
grep -Fq 'legacy recovery marker requires administrator review' "$temp_dir/legacy-state-marker.log"
rm -f /var/lib/kautuka-recovery/kautuka-apply.rollback
make_signed_release 1.0.1 missing
for rejected_version in 0.9.9 1.0.0 v1.0.0 1.0 01.0.0; do
    set +e
    run_helper --artifact /var/lib/kautuka/updateService/updates/staging/test/kautuka-linux-amd64.tar.gz --version "$rejected_version" >"$temp_dir/version-reject.log" 2>&1
    version_status=$?
    set -e
    [[ $version_status -ne 0 ]] || { echo "error: accepted non-forward release identity $rejected_version" >&2; exit 1; }
done
set +e
run_helper --artifact /var/lib/kautuka/updateService/updates/staging/test/kautuka-linux-amd64.tar.gz --version 1.0.1 >"$temp_dir/missing-protocol.log" 2>&1
protocol_status=$?
set -e
[[ $protocol_status -ne 0 ]]
grep -Fq 'required safe-state-recovery-v5' "$temp_dir/missing-protocol.log"

make_signed_release 1.0.1
set +e
PATH="$temp_dir/mock:$PATH" KAUTUKA_DISPOSABLE_TEST=1 KAUTUKA_TEST_FREE_BYTES=1 \
  KAUTUKA_APPLY_DETACHED=1 KAUTUKA_APPLY_LOCK="$temp_dir/lock" \
  /usr/local/lib/kautuka/apply-release --artifact /var/lib/kautuka/updateService/updates/staging/test/kautuka-linux-amd64.tar.gz --version 1.0.1 >"$temp_dir/low-space.log" 2>&1
low_space_status=$?
set -e
[[ $low_space_status -ne 0 ]]
grep -Fq 'insufficient free space for safe update staging' "$temp_dir/low-space.log"
[[ ! -e /opt/kautuka/manager/versions/1.0.1 && ! -e /var/lib/kautuka-recovery/maintenance.json ]]
set +e
PATH="$temp_dir/mock:$PATH" KAUTUKA_DISPOSABLE_TEST=1 KAUTUKA_TEST_STAGE_FAIL_AFTER_MANAGER=1 \
  KAUTUKA_APPLY_DETACHED=1 KAUTUKA_APPLY_LOCK="$temp_dir/lock" \
  /usr/local/lib/kautuka/apply-release --artifact /var/lib/kautuka/updateService/updates/staging/test/kautuka-linux-amd64.tar.gz --version 1.0.1 >"$temp_dir/stage-failure.log" 2>&1
stage_failure_status=$?
set -e
[[ $stage_failure_status -ne 0 ]]
grep -Fq 'injected failure after staging Manager' "$temp_dir/stage-failure.log"
[[ ! -e /opt/kautuka/manager/versions/1.0.1 && ! -e /var/lib/kautuka-recovery/maintenance.json ]]
run_helper --artifact /var/lib/kautuka/updateService/updates/staging/test/kautuka-linux-amd64.tar.gz --version 1.0.1
[[ -f /var/lib/kautuka-recovery/maintenance.json ]]
[[ "$(python3 -c 'import json;print(json.load(open("/var/lib/kautuka-recovery/operation.json"))["phase"])')" == verifying ]]
[[ "$(python3 -c 'import json;print(len(json.load(open("/var/lib/kautuka-recovery/operation.json"))["snapshotManifestSha256"]))')" == 64 ]]
[[ "$(readlink -f /opt/kautuka/manager/current)" == /opt/kautuka/manager/versions/1.0.1 ]]
run_helper --commit --version 1.0.1
[[ ! -e /var/lib/kautuka-recovery/maintenance.json ]]
[[ -d /var/lib/kautuka-recovery/backups/last-known-good/state ]]
python3 - <<'PY'
import sqlite3
assert sqlite3.connect('/var/lib/kautuka-recovery/backups/last-known-good/state/kautuka.db').execute('select value from registry').fetchone()[0] == 'source-state'
assert sqlite3.connect('/var/lib/kautuka-recovery/backups/last-known-good/state/channel-outbox.db').execute('select value from outbox').fetchone()[0] == 'pending-message'
PY

# Crash after switching pointers, before activation: early boot recovery must
# restore data without waiting for the service-user bus or starting app units.
make_signed_release 1.0.2
set +e
PATH="$temp_dir/mock:$PATH" KAUTUKA_DISPOSABLE_TEST=1 KAUTUKA_TEST_HEALTH_OK=1 KAUTUKA_TEST_CRASH_AT=switched \
  KAUTUKA_APPLY_DETACHED=1 KAUTUKA_APPLY_LOCK="$temp_dir/lock" \
  /usr/local/lib/kautuka/apply-release --artifact /var/lib/kautuka/updateService/updates/staging/test/kautuka-linux-amd64.tar.gz --version 1.0.2 >/dev/null 2>&1
crash_status=$?
set -e
[[ $crash_status -ne 0 && "$(readlink -f /opt/kautuka/manager/current)" == /opt/kautuka/manager/versions/1.0.2 ]]
cp /var/lib/kautuka-recovery/operation.json "$temp_dir/operation.json"
cp /var/lib/kautuka-recovery/backups/$(python3 -c 'import json;print(json.load(open("/var/lib/kautuka-recovery/operation.json"))["operationId"])')/manifest.json "$temp_dir/manifest.json"
python3 - <<'PY'
import json
path = '/var/lib/kautuka-recovery/backups/' + json.load(open('/var/lib/kautuka-recovery/operation.json'))['operationId'] + '/manifest.json'
manifest = json.load(open(path))
manifest['entries'].pop()
json.dump(manifest, open(path, 'w'))
PY
set +e
run_helper --recover >"$temp_dir/tampered-manifest.log" 2>&1
manifest_status=$?
set -e
[[ $manifest_status -ne 0 && -f /var/lib/kautuka-recovery/maintenance.json ]]
grep -Fq 'snapshot manifest does not match its durable journal binding' "$temp_dir/tampered-manifest.log"
cp "$temp_dir/manifest.json" /var/lib/kautuka-recovery/backups/$(python3 -c 'import json;print(json.load(open("/var/lib/kautuka-recovery/operation.json"))["operationId"])')/manifest.json
rm -f /var/lib/kautuka-recovery/maintenance.json
printf 'not-json\n' > /var/lib/kautuka-recovery/operation.json
set +e
run_helper --recover >"$temp_dir/unreadable-recovery.log" 2>&1
unreadable_status=$?
set -e
[[ $unreadable_status -ne 0 && -f /var/lib/kautuka-recovery/maintenance.json ]]
mv "$temp_dir/operation.json" /var/lib/kautuka-recovery/operation.json
cp /var/lib/kautuka-recovery/operation.json "$temp_dir/operation.json"
python3 - <<'PY'
import json
path = '/var/lib/kautuka-recovery/operation.json'
with open(path) as stream: journal = json.load(stream)
journal['sourcePointers']['manager'] = '0.9.9'
with open(path, 'w') as stream: json.dump(journal, stream)
PY
set +e
run_helper --recover >"$temp_dir/tampered-recovery.log" 2>&1
tampered_status=$?
set -e
[[ $tampered_status -ne 0 && -f /var/lib/kautuka-recovery/maintenance.json ]]
mv "$temp_dir/operation.json" /var/lib/kautuka-recovery/operation.json
mkdir -p /opt/kautuka/gateway/versions/0.9.9
ln -sfn versions/0.9.9 /opt/kautuka/gateway/current
set +e
run_helper --recover >"$temp_dir/third-pointer-recovery.log" 2>&1
pointer_status=$?
set -e
[[ $pointer_status -ne 0 && -f /var/lib/kautuka-recovery/maintenance.json ]]
ln -sfn versions/1.0.2 /opt/kautuka/gateway/current
rm -f /run/user/992/bus
printf '#!/bin/sh\nexit 99\n' > "$temp_dir/mock/runuser"
chmod 755 "$temp_dir/mock/runuser"
run_helper --boot-recover
[[ -f /var/lib/kautuka-recovery/maintenance.json ]]
[[ "$(readlink -f /opt/kautuka/manager/current)" == /opt/kautuka/manager/versions/1.0.1 ]]
[[ "$(python3 -c 'import json;print(json.load(open("/var/lib/kautuka-recovery/operation.json"))["phase"])')" == recovered ]]
python3 - <<'PY'
import sqlite3
db = sqlite3.connect('/var/lib/kautuka/kautuka.db')
db.execute("insert into registry values ('after-boot-recovery-write')")
db.commit(); db.close()
PY
printf '#!/bin/sh\nexit 0\n' > "$temp_dir/mock/runuser"
chmod 755 "$temp_dir/mock/runuser"
python3 -c 'import socket; s = socket.socket(socket.AF_UNIX); s.bind("/run/user/992/bus"); s.close()'
# A crash after clearing the fence but before the completing restart must put
# the device back into maintenance before any writer is drained or restored.
rm -f /var/lib/kautuka-recovery/maintenance.json
set +e
PATH="$temp_dir/mock:$PATH" KAUTUKA_DISPOSABLE_TEST=1 KAUTUKA_TEST_CRASH_AT=recovery-fenced \
  KAUTUKA_APPLY_DETACHED=1 KAUTUKA_APPLY_LOCK="$temp_dir/lock" \
  /usr/local/lib/kautuka/apply-release --recover >/dev/null 2>&1
crash_status=$?
set -e
[[ $crash_status -ne 0 && -f /var/lib/kautuka-recovery/maintenance.json ]]
run_helper --recover
[[ -f /var/lib/kautuka-recovery/maintenance.json ]]
python3 - <<'PY'
import sqlite3
assert sqlite3.connect('/var/lib/kautuka/kautuka.db').execute('select value from registry').fetchone()[0] == 'source-state'
assert sqlite3.connect('/var/lib/kautuka/channel-outbox.db').execute('select value from outbox').fetchone()[0] == 'pending-message'
PY
set +e
PATH="$temp_dir/mock:$PATH" KAUTUKA_DISPOSABLE_TEST=1 KAUTUKA_TEST_RECOVERY_START_FAIL=1 \
  KAUTUKA_APPLY_DETACHED=1 KAUTUKA_APPLY_LOCK="$temp_dir/lock" \
  /usr/local/lib/kautuka/apply-release --complete-recovery --version 1.0.1 >"$temp_dir/recovery-start-failure.log" 2>&1
recovery_start_status=$?
set -e
[[ $recovery_start_status -ne 0 && -f /var/lib/kautuka-recovery/maintenance.json ]]
[[ "$(python3 -c 'import json;print(json.load(open("/var/lib/kautuka-recovery/operation.json"))["phase"])')" == completed ]]
run_helper --recover
[[ ! -e /var/lib/kautuka-recovery/maintenance.json ]]
[[ -d /var/lib/kautuka-recovery/backups/last-known-good/state ]]
[[ "$(find /var/lib/kautuka-recovery/backups -mindepth 1 -maxdepth 1 -type d | wc -l)" == 1 ]]
python3 - <<'PY'
import sqlite3
db = sqlite3.connect('/var/lib/kautuka/kautuka.db')
db.execute("insert into registry values ('after-recovery-write')")
db.commit(); db.close()
PY
# Repeated boot recovery and runtime recovery must honor the durable terminal
# decision. Replaying the candidate snapshot here would erase this new row.
run_helper --boot-recover
run_helper --recover
run_helper --boot-recover
python3 - <<'PY'
import sqlite3
assert sqlite3.connect('/var/lib/kautuka/kautuka.db').execute("select count(*) from registry where value='after-recovery-write'").fetchone()[0] == 1
PY

# A crash after the durable committed marker is roll-forward only: recovery must
# not restore the pre-update database, and commit is idempotently resumable.
make_signed_release 1.0.3
run_helper --artifact /var/lib/kautuka/updateService/updates/staging/test/kautuka-linux-amd64.tar.gz --version 1.0.3
set +e
PATH="$temp_dir/mock:$PATH" KAUTUKA_DISPOSABLE_TEST=1 KAUTUKA_TEST_HEALTH_OK=1 KAUTUKA_TEST_CRASH_AT=committed \
  KAUTUKA_APPLY_DETACHED=1 KAUTUKA_APPLY_LOCK="$temp_dir/lock" \
  /usr/local/lib/kautuka/apply-release --commit --version 1.0.3 >/dev/null 2>&1
crash_status=$?
set -e
[[ $crash_status -ne 0 && "$(python3 -c 'import json;print(json.load(open("/var/lib/kautuka-recovery/operation.json"))["phase"])')" == committed ]]
make_signed_release 1.0.4
cp /var/lib/kautuka-recovery/operation.json "$temp_dir/committed-operation.json"
set +e
run_helper --artifact /var/lib/kautuka/updateService/updates/staging/test/kautuka-linux-amd64.tar.gz --version 1.0.4 >"$temp_dir/second-apply.log" 2>&1
second_apply_status=$?
set -e
[[ $second_apply_status -ne 0 ]]
grep -Eq 'update operation.*unresolved|terminal update operation.*maintenance fence' "$temp_dir/second-apply.log"
cmp -s "$temp_dir/committed-operation.json" /var/lib/kautuka-recovery/operation.json
python3 - <<'PY'
import sqlite3
db = sqlite3.connect('/var/lib/kautuka/kautuka.db')
db.execute("insert into registry values ('committed-target-state')")
db.commit(); db.close()
PY
run_helper --recover
[[ "$(python3 -c 'import json;print(json.load(open("/var/lib/kautuka-recovery/operation.json"))["phase"])')" == committed ]]
python3 - <<'PY'
import sqlite3
assert sqlite3.connect('/var/lib/kautuka/kautuka.db').execute("select count(*) from registry where value='committed-target-state'").fetchone()[0] == 1
PY
run_helper --commit --version 1.0.3
[[ ! -e /var/lib/kautuka-recovery/maintenance.json ]]
[[ "$(find /var/lib/kautuka-recovery/backups -mindepth 1 -maxdepth 1 -type d | wc -l)" == 1 ]]

# A failure in the final normal-worker startup is still precommit. The source
# release and its database must be restored, with admission left fenced until
# the source updater completes recovery.
make_signed_release 1.0.4
run_helper --artifact /var/lib/kautuka/updateService/updates/staging/test/kautuka-linux-amd64.tar.gz --version 1.0.4
python3 - <<'PY'
import sqlite3
db = sqlite3.connect('/var/lib/kautuka/kautuka.db')
db.execute("insert into registry values ('failed-target-write')")
db.commit(); db.close()
PY
set +e
PATH="$temp_dir/mock:$PATH" KAUTUKA_DISPOSABLE_TEST=1 KAUTUKA_TEST_FINAL_START_FAIL=1 \
  KAUTUKA_APPLY_DETACHED=1 KAUTUKA_APPLY_LOCK="$temp_dir/lock" \
  /usr/local/lib/kautuka/apply-release --commit --version 1.0.4 >"$temp_dir/final-start-failure.log" 2>&1
final_start_status=$?
set -e
[[ $final_start_status -ne 0 && -f /var/lib/kautuka-recovery/maintenance.json ]]
[[ "$(python3 -c 'import json;print(json.load(open("/var/lib/kautuka-recovery/operation.json"))["phase"])')" == recovered ]]
[[ "$(readlink -f /opt/kautuka/manager/current)" == /opt/kautuka/manager/versions/1.0.3 ]]
python3 - <<'PY'
import sqlite3
db = sqlite3.connect('/var/lib/kautuka/kautuka.db')
assert db.execute("select count(*) from registry where value='failed-target-write'").fetchone()[0] == 0
assert db.execute("select count(*) from registry where value='committed-target-state'").fetchone()[0] == 1
PY
run_helper --complete-recovery --version 1.0.3
make_signed_release 1.0.5
run_helper --artifact /var/lib/kautuka/updateService/updates/staging/test/kautuka-linux-amd64.tar.gz --version 1.0.5
set +e
PATH="$temp_dir/mock:$PATH" KAUTUKA_DISPOSABLE_TEST=1 KAUTUKA_TEST_CRASH_AT=starting \
  KAUTUKA_APPLY_DETACHED=1 KAUTUKA_APPLY_LOCK="$temp_dir/lock" \
  /usr/local/lib/kautuka/apply-release --commit --version 1.0.5 >/dev/null 2>&1
starting_crash_status=$?
set -e
[[ $starting_crash_status -ne 0 ]]
[[ "$(python3 -c 'import json;print(json.load(open("/var/lib/kautuka-recovery/operation.json"))["phase"])')" == starting ]]
run_helper --boot-recover
[[ "$(readlink -f /opt/kautuka/manager/current)" == /opt/kautuka/manager/versions/1.0.3 ]]
[[ "$(python3 -c 'import json;print(json.load(open("/var/lib/kautuka-recovery/operation.json"))["phase"])')" == recovered ]]
echo 'verified snapshot, maintenance fencing, precommit recovery, unresolved-operation rejection, and committed roll-forward passed'

# Exercise the container transaction seam with a fixed Manager CLI stand-in.
# Lifecycle is mocked here; the separate rootless proof uses actual containers.
run_helper --complete-recovery --version 1.0.3
printf '%s' "$temp_dir/lock" > /var/lib/kautuka-recovery/test-apply-lock-path
cat > "$temp_dir/core-manager" <<'PY'
#!/usr/bin/python3
import errno, fcntl, json, os, pathlib, sys
# The parent still owns its apply lease, but no maintenance child (or a
# subsequently spawned conmon) may inherit either helper descriptor.
for fd in (8, 9):
    try:
        os.fstat(fd)
    except OSError as error:
        assert error.errno == errno.EBADF
    else:
        raise AssertionError(f'helper lease descriptor {fd} leaked to Manager')
with open(pathlib.Path('/var/lib/kautuka-recovery/test-apply-lock-path').read_text()) as apply_lease:
    try:
        fcntl.flock(apply_lease, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        pass
    else:
        raise AssertionError('parent released its apply lease during Manager execution')
if sys.argv[1:] == ['--update-core-capabilities']:
    print('core-service-update-v1'); raise SystemExit(0)
action, operation = sys.argv[1:]
action = action.removeprefix('--update-core-')
release = pathlib.Path(__file__).parent.name
record = json.loads(pathlib.Path('/var/lib/kautuka-recovery/operation.json').read_text())
assert record['operationId'] == operation
with open('/run/kautuka-db-writer.lock') as lease:
    fcntl.flock(lease, fcntl.LOCK_SH | fcntl.LOCK_NB)
if action == 'stop' and pathlib.Path('/etc/kautuka/.core-deny-stop').exists():
    raise SystemExit(1)
with open('/var/lib/kautuka-recovery/test-core-events.jsonl', 'a') as stream:
    stream.write(json.dumps({'action':action, 'release':release, 'phase':record['phase']}) + '\n')
result = {'action':action, 'operationId':operation, 'release':release,
          'stopped' if action == 'stop' else 'ready':True}
if action == 'verify':
    pathlib.Path('/var/lib/kautuka-recovery/test-core-running').write_text(release)
    result['cohort'] = [{'service':name, 'containerId':chr(97+i)*64, 'imageId':'e'*64}
                        for i,name in enumerate(('credentialshost','channelhost','gateway','webhost'))]
    if release == '1.0.7': result['cohort'][0]['containerId'] = 'invalid'
else:
    pathlib.Path('/var/lib/kautuka-recovery/test-core-running').unlink(missing_ok=True)
print(json.dumps(result))
PY
chmod 755 "$temp_dir/core-manager"
cp "$temp_dir/core-manager" /opt/kautuka/manager/versions/1.0.3/kautuka-manager
cat > "$temp_dir/mock/runuser" <<'SH'
#!/bin/sh
while [ "$1" != -- ]; do shift; done
shift
exec "$@"
SH
chmod 755 "$temp_dir/mock/runuser"
chown 992:992 /run/user/992
printf '%s\n' '{"schemaVersion":2,"username":"kautuka","target":"kautuka.target","userUnits":["kautuka-manager.service","kautuka-updateservice.service"],"systemUnits":[],"applicationServices":"containers"}' > /etc/kautuka/service-scope.json
run_helper --capabilities | grep -Fxq container-core-update-v1
make_signed_release 1.0.6
mv /usr/local/lib/kautuka/runtime-images.py /usr/local/lib/kautuka/runtime-images.saved
set +e
run_helper --artifact /var/lib/kautuka/updateService/updates/staging/test/kautuka-linux-amd64.tar.gz --version 1.0.6 >"$temp_dir/missing-offline-utility.log" 2>&1
missing_utility_status=$?
set -e
mv /usr/local/lib/kautuka/runtime-images.saved /usr/local/lib/kautuka/runtime-images.py
[[ $missing_utility_status -ne 0 && ! -e /var/lib/kautuka-recovery/maintenance.json ]]
[[ "$(readlink -f /opt/kautuka/manager/current)" == /opt/kautuka/manager/versions/1.0.3 ]]
touch /etc/kautuka/.core-deny-stop
set +e
run_helper --artifact /var/lib/kautuka/updateService/updates/staging/test/kautuka-linux-amd64.tar.gz --version 1.0.6 >"$temp_dir/core-stop-failure.log" 2>&1
core_stop_status=$?
set -e
[[ $core_stop_status -ne 0 && -f /var/lib/kautuka-recovery/maintenance.json ]]
[[ "$(readlink -f /opt/kautuka/manager/current)" == /opt/kautuka/manager/versions/1.0.3 ]]
rm /etc/kautuka/.core-deny-stop
run_helper --recover
run_helper --artifact /var/lib/kautuka/updateService/updates/staging/test/kautuka-linux-amd64.tar.gz --version 1.0.6
run_helper --commit --version 1.0.6
[[ ! -e /var/lib/kautuka-recovery/maintenance.json ]]
make_signed_release 1.0.7
set +e
run_helper --artifact /var/lib/kautuka/updateService/updates/staging/test/kautuka-linux-amd64.tar.gz --version 1.0.7 >"$temp_dir/core-cohort-failure.log" 2>&1
core_cohort_status=$?
set -e
[[ $core_cohort_status -ne 0 && -f /var/lib/kautuka-recovery/maintenance.json ]]
[[ "$(readlink -f /opt/kautuka/manager/current)" == /opt/kautuka/manager/versions/1.0.6 ]]
run_helper --complete-recovery --version 1.0.6
make_signed_release 1.0.8
set +e
PATH="$temp_dir/mock:$PATH" KAUTUKA_DISPOSABLE_TEST=1 KAUTUKA_TEST_CRASH_AT=switched \
  KAUTUKA_APPLY_DETACHED=1 KAUTUKA_APPLY_LOCK="$temp_dir/lock" \
  /usr/local/lib/kautuka/apply-release --artifact /var/lib/kautuka/updateService/updates/staging/test/kautuka-linux-amd64.tar.gz --version 1.0.8 >/dev/null 2>&1
core_boot_crash=$?
set -e
[[ $core_boot_crash -ne 0 && ! -e /var/lib/kautuka-recovery/test-core-running ]]
run_helper --boot-recover
[[ ! -e /var/lib/kautuka-recovery/test-core-running ]]
python3 - <<'PY'
import json, sqlite3
assert json.load(open('/var/lib/kautuka-recovery/operation.json'))['phase'] == 'recovered'
with sqlite3.connect('/var/lib/kautuka/kautuka.db') as db:
    db.execute("insert into registry values ('post-restore-write')")
PY
run_helper --resume-runtime
[[ "$(cat /var/lib/kautuka-recovery/test-core-running)" == 1.0.6 ]]
[[ -f /var/lib/kautuka-recovery/maintenance.json ]]
python3 - <<'PY'
import json, sqlite3
assert json.load(open('/var/lib/kautuka-recovery/operation.json'))['phase'] == 'recovered'
with sqlite3.connect('/var/lib/kautuka/kautuka.db') as db:
    assert db.execute("select count(*) from registry where value='post-restore-write'").fetchone()[0] == 1
PY
run_helper --complete-recovery --version 1.0.6
python3 - <<'PY'
import json
events = [json.loads(line) for line in open('/var/lib/kautuka-recovery/test-core-events.jsonl')]
assert any(e == {'action':'verify','release':'1.0.6','phase':'verified'} for e in events)
assert any(e == {'action':'verify','release':'1.0.6','phase':'committed'} for e in events)
assert any(e == {'action':'verify','release':'1.0.6','phase':'recovered'} for e in events)
assert events[0]['action'] == 'stop' and events[0]['phase'] == 'capturing'
assert any(e['action'] == 'verify' and e['phase'] == 'aborted' for e in events)
PY
echo 'container CLI retirement, fenced stop failure, phase-selected verification, commit and wrong-cohort recovery passed'

# Target staging never deletes the archive set needed by source recovery.
python3 - <<'PY'
import hashlib, pathlib
cache = pathlib.Path('/var/lib/kautuka/oci-images')
for release in ('1.0.6', '1.0.7', '1.0.8'):
    data = ('fixture archive core-gateway ' + release).encode()
    name = 'core-gateway-' + hashlib.sha256(data).hexdigest() + '.oci'
    assert (cache / name).read_bytes() == data
print('verified retained source and target offline images')
PY
