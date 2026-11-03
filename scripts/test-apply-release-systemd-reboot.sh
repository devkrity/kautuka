#!/usr/bin/env bash
set -euo pipefail

# Real-systemd test for a disposable WSL distro. This has no recovery bypass:
# the production helper, unit files, user manager, Unix-socket health probe and
# reboot ordering all run with their normal configuration. The distro is left
# populated for the caller to unregister after verify2.
[[ ${EUID} -eq 0 && "${KAUTUKA_DISPOSABLE_SYSTEMD_TEST:-}" == 1 ]] || {
    echo 'error: run as root in a disposable WSL distro with KAUTUKA_DISPOSABLE_SYSTEMD_TEST=1' >&2
    exit 1
}
grep -qi microsoft /proc/version || { echo 'error: this reboot test requires WSL' >&2; exit 1; }
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
phase="${1:-}"
source_version='v0.1.20-preview.17'
target_version='v0.1.20-preview.18'

install_health_server() {
    command -v cc >/dev/null 2>&1 || { echo 'error: C compiler is required for process-identity test binaries' >&2; exit 1; }
    cat > /usr/local/lib/kautuka/systemd-test-service.c <<'C'
#include <signal.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/file.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <unistd.h>

int main(int argc, char **argv) {
    (void)argc;
    const char *name = strrchr(argv[0], '/');
    name = name ? name + 1 : argv[0];
    if (strstr(name, "kautuka-manager") || strstr(name, "kautuka-gateway")
        || strstr(name, "kautuka-channelhost") || strstr(name, "kautuka-credentialshost")) {
        int lease = open("/run/kautuka-db-writer.lock", O_RDONLY);
        if (lease < 0 || flock(lease, LOCK_SH) < 0) return 5;
    }
    if (strcmp(name, "kautuka-gateway") == 0) {
        const char *path = "/run/kautuka/gateway/gateway.sock";
        int server = socket(AF_UNIX, SOCK_STREAM, 0);
        if (server < 0) return 2;
        struct sockaddr_un address = {0};
        address.sun_family = AF_UNIX;
        snprintf(address.sun_path, sizeof(address.sun_path), "%s", path);
        unlink(path);
        if (bind(server, (struct sockaddr *)&address, sizeof(address)) < 0) return 3;
        chmod(path, 0666);
        if (listen(server, 16) < 0) return 4;
        const char body[] = "{\"lifecycle\":{\"coreReady\":true},\"workspace\":{\"status\":\"healthy\"}}";
        char response[512];
        int response_length = snprintf(response, sizeof(response), "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: %zu\r\nConnection: close\r\n\r\n%s", sizeof(body) - 1, body);
        for (;;) {
            int client = accept(server, NULL, NULL);
            if (client < 0) continue;
            char request[4096];
            ssize_t received = read(client, request, sizeof(request));
            ssize_t sent = write(client, response, (size_t)response_length);
            if (received < 0 || sent < 0) { close(client); continue; }
            close(client);
        }
    }
    for (;;) pause();
}
C
    cc -O2 -Wall -Wextra -Werror /usr/local/lib/kautuka/systemd-test-service.c -o /usr/local/lib/kautuka/systemd-test-service
    chmod 0755 /usr/local/lib/kautuka/systemd-test-service
    cat > /usr/local/lib/kautuka/systemd-test-no-userbus.sh <<'SH'
#!/bin/sh
set -eu
test ! -S "/run/user/$1/bus"
date +%s.%N >> /var/lib/kautuka-recovery/test-no-userbus.log
SH
    chmod 0755 /usr/local/lib/kautuka/systemd-test-no-userbus.sh
}

install_fake_release_tree() {
    local service="$1" version="$2" executable="/opt/kautuka/$1/versions/$2/kautuka-$1"
    install -d -m 0755 "/opt/kautuka/$service/versions/$version"
    install -m 0755 /usr/local/lib/kautuka/systemd-test-service "$executable"
    chmod 0755 "$executable"
    ln -sfn "versions/$version" "/opt/kautuka/$service/current"
}

if [[ "$phase" == prepare || "$phase" == prepare-committed ]]; then
    grep -q '^systemd=true$' /etc/wsl.conf || { echo 'error: systemd is not enabled in /etc/wsl.conf' >&2; exit 1; }
    grep -A1 '^\[interop\]$' /etc/wsl.conf | grep -qx 'enabled=false' \
        && grep -A1 '^\[automount\]$' /etc/wsl.conf | grep -qx 'enabled=false' \
        && grep -A2 '^\[interop\]$' /etc/wsl.conf | grep -qx 'appendWindowsPath=false' \
        || { echo 'error: interop and automount must be disabled in this test distro' >&2; exit 1; }
    for path in /opt/kautuka /etc/kautuka /var/lib/kautuka /var/lib/kautuka-recovery /usr/local/lib/kautuka /run/kautuka-apply.rollback /run/kautuka-db-writer.lock; do
        [[ ! -e "$path" && ! -L "$path" ]] || { echo "error: refusing to overwrite existing test path: $path" >&2; exit 1; }
    done
    [[ "$(ps -p 1 -o comm= | tr -d '[:space:]')" == systemd ]] || { echo 'error: systemd is not the active PID 1 manager' >&2; exit 1; }
    systemd_state="$(systemctl is-system-running 2>/dev/null || true)"
    [[ "$systemd_state" == running || "$systemd_state" == degraded ]] || { echo "error: systemd manager is unavailable ($systemd_state)" >&2; exit 1; }
    useradd --system --user-group --create-home --home-dir /var/lib/kautuka --shell /usr/sbin/nologin kautuka
    install -d -o root -g root -m 0755 /etc/kautuka /usr/local/lib/kautuka /var/lib/kautuka-recovery/backups
    install -d -m 0755 /usr/lib/tmpfiles.d
    printf '%s\n' 'd /run/kautuka 0755 root root -' 'f /run/kautuka-db-writer.lock 0644 root root -' > /usr/lib/tmpfiles.d/kautuka-sockets.conf
    systemd-tmpfiles --create /usr/lib/tmpfiles.d/kautuka-sockets.conf
    install -d -o kautuka -g kautuka -m 0750 /var/lib/kautuka
    install -m 0755 "$script_dir/kautuka-apply-release.sh" /usr/local/lib/kautuka/apply-release
    install -m 0755 "$script_dir/kautuka-update-recovery.py" /usr/local/lib/kautuka/update-recovery.py
    install -m 0644 "$script_dir/kautuka-update-recovery.service" /etc/systemd/system/kautuka-update-recovery.service
    install_health_server

    services=(manager updateservice webhost gateway channelhost credentialshost)
    for service in "${services[@]}"; do
        install_fake_release_tree "$service" "$source_version"
        install_fake_release_tree "$service" "$target_version"
    done
    for release in "$source_version" "$target_version"; do
        printf '%s\n' 'kautuka-maintenance-page-v1' > "/opt/kautuka/webhost/versions/$release/.maintenance-page-v1"
    done
    # The real updater reads stdout from the helper; no detached override is used.
    [[ "$(/usr/local/lib/kautuka/apply-release --capabilities)" == 'safe-state-recovery-v5' ]]
    mkdir -p /var/lib/kautuka/agents/agent-123 /var/lib/kautuka/agents/agent-123/files \
        /var/lib/kautuka/credentialsHost/vault/vault-keys \
        /var/lib/kautuka/security/channel/channel-private \
        /var/lib/kautuka/security/channel/gateway-private \
        /var/lib/kautuka/security/channel/gateway-trust \
        /var/lib/kautuka/security/channel/channel-trust \
        /var/lib/kautuka/security/channel/offline-authority
    python3 - <<'PY'
import sqlite3
for path, table, value in (
    ('/var/lib/kautuka/kautuka.db', 'registry', 'source-row'),
    ('/var/lib/kautuka/channel-outbox.db', 'outbox', 'pending-outbox'),
    ('/var/lib/kautuka/agents/agent-123/agent.db', 'runs', 'source-agent'),
):
    db = sqlite3.connect(path)
    db.execute('PRAGMA journal_mode=WAL')
    db.execute(f'CREATE TABLE {table}(value TEXT)')
    db.execute(f'INSERT INTO {table} VALUES (?)', (value,))
    db.commit(); db.close()
open('/var/lib/kautuka/kautuka.key', 'wb').write(b'm' * 32)
open('/var/lib/kautuka/gateway-auth.key', 'wb').write(b'g' * 32)
open('/var/lib/kautuka/credentialsHost/vault/vault-keys/active', 'w').write('A1B2C3D4\n')
open('/var/lib/kautuka/credentialsHost/vault/vault-keys/A1B2C3D4.key', 'wb').write(b'k' * 32)
open('/var/lib/kautuka/agents/agent-123/files/user.txt', 'w').write('source user file')
PY
    chmod 0600 /var/lib/kautuka/kautuka.key /var/lib/kautuka/gateway-auth.key \
        /var/lib/kautuka/credentialsHost/vault/vault-keys/*

    for required in \
        security/channel/channel-private/channel-server.pfx \
        security/channel/gateway-private/gateway-channel-client.pfx \
        security/channel/gateway-private/gateway-channel-delegation.pfx \
        security/channel/gateway-trust/channel-ca.cer \
        security/channel/channel-trust/channel-ca.cer \
        security/channel/channel-trust/gateway-channel-client.cer \
        security/channel/channel-trust/gateway-channel-delegation.cer \
        security/channel/offline-authority/channel-ca.key \
        security/channel/offline-authority/channel-ca.cer; do
        install -m 0600 /dev/null "/var/lib/kautuka/$required"
    done

    for unit in credentialshost channelhost gateway; do
        unit_path="/etc/systemd/system/kautuka-$unit.service"
        extra_environment=''
        extra_start=''
        case "$unit" in
            gateway)
                extra_environment=$'Environment=Kautuka__ChannelGateway__ProductionMaterial__GatewayPrivateRoot=/var/lib/kautuka/security/channel/gateway-private\nEnvironment=Kautuka__ChannelGateway__ProductionMaterial__GatewayTrustRoot=/var/lib/kautuka/security/channel/gateway-trust'
                extra_start=$'ExecStartPre=+/usr/bin/install -d -o root -g root -m 0755 /run/kautuka\nExecStartPre=+/usr/bin/install -d -o kautuka -g kautuka -m 0775 /run/kautuka/gateway'
                ;;
            channelhost)
                extra_environment=$'Environment=Kautuka__ChannelGateway__ProductionMaterial__ChannelPrivateRoot=/var/lib/kautuka/security/channel/channel-private\nEnvironment=Kautuka__ChannelGateway__ProductionMaterial__ChannelTrustRoot=/var/lib/kautuka/security/channel/channel-trust'
                ;;
        esac
        exec_command="/opt/kautuka/$unit/current/kautuka-$unit"
        cat > "$unit_path" <<UNIT
[Unit]
Description=Kautuka test $unit
Requires=kautuka-update-recovery.service
After=kautuka-update-recovery.service network.target
$( [[ "$unit" == gateway ]] && printf 'Requires=kautuka-channelhost.service\nAfter=kautuka-channelhost.service\n' )

[Service]
Type=exec
User=kautuka
Group=kautuka
UMask=0077
WorkingDirectory=/opt/kautuka/$unit/current
ExecStart=$exec_command
$( [[ -n "$extra_start" ]] && printf '%s\n' "$extra_start" )
Environment=Kautuka__Mode=prod
Environment=Kautuka__ContainerStateRoot=/var/lib/kautuka
$extra_environment
Restart=always
RestartSec=1
TimeoutStopSec=15

[Install]
WantedBy=multi-user.target
UNIT
        systemctl enable "kautuka-$unit.service" >/dev/null
    done

    install -d -o kautuka -g kautuka -m 0750 /var/lib/kautuka/.config/systemd/user/default.target.wants
    for unit in manager updateservice webhost; do
        executable="/opt/kautuka/$unit/current/kautuka-$unit"
        cat > "/var/lib/kautuka/.config/systemd/user/kautuka-$unit.service" <<UNIT
[Unit]
Description=Kautuka test $unit
After=network.target
PartOf=kautuka.target

[Service]
Type=exec
WorkingDirectory=/opt/kautuka/$unit/current
        ExecStart=$executable
Environment=Kautuka__Mode=prod
Environment=Kautuka__ContainerStateRoot=/var/lib/kautuka
Restart=always
RestartSec=1
TimeoutStopSec=15

[Install]
WantedBy=kautuka.target
UNIT
        chown kautuka:kautuka "/var/lib/kautuka/.config/systemd/user/kautuka-$unit.service"
    done
    cat > /var/lib/kautuka/.config/systemd/user/kautuka.target <<'UNIT'
[Unit]
Description=Kautuka test user services
Wants=kautuka-manager.service kautuka-updateservice.service kautuka-webhost.service
UNIT
    chown kautuka:kautuka /var/lib/kautuka/.config/systemd/user/kautuka.target
    ln -sfn ../kautuka.target /var/lib/kautuka/.config/systemd/user/default.target.wants/kautuka.target

    uid="$(id -u kautuka)"
    install -d -m 0755 "/etc/systemd/system/user@${uid}.service.d" \
        /etc/systemd/system/kautuka-update-recovery.service.d \
        /var/lib/systemd/linger "/etc/systemd/system/multi-user.target.wants"
    cat > "/etc/systemd/system/user@${uid}.service.d/10-kautuka-recovery.conf" <<'UNIT'
[Unit]
Requires=kautuka-update-recovery.service
After=kautuka-update-recovery.service
UNIT
    cat > /etc/systemd/system/kautuka-update-recovery.service.d/99-test-no-userbus.conf <<UNIT
[Service]
ExecStartPre=/usr/local/lib/kautuka/systemd-test-no-userbus.sh $uid
UNIT
    user_template="$(systemctl show --property=FragmentPath --value "user@${uid}.service")"
    [[ -f "$user_template" ]] || { echo 'error: systemd user@ template unit was not found' >&2; exit 1; }
    ln -sfn "$user_template" "/etc/systemd/system/multi-user.target.wants/user@${uid}.service"
    touch "/var/lib/systemd/linger/kautuka"
    systemctl enable kautuka-update-recovery.service >/dev/null
    systemctl daemon-reload

    operation='11111111111111111111111111111111'
    snapshot="/var/lib/kautuka-recovery/backups/$operation"
    python3 /usr/local/lib/kautuka/update-recovery.py capture --data-root /var/lib/kautuka \
        --destination "$snapshot" --operation "$operation" --source "$source_version" --target "$target_version"
    manifest_sha="$(sha256sum "$snapshot/manifest.json" | cut -d' ' -f1)"
    journal_phase='verifying'
    [[ "$phase" == prepare-committed ]] && journal_phase='committed'
    python3 - "$operation" "$snapshot" "$manifest_sha" "$source_version" "$target_version" "$journal_phase" <<'PY'
import json, sys
operation, snapshot, manifest_sha, source, target, phase = sys.argv[1:]
document = {
    'format': 1, 'operationId': operation, 'phase': phase,
    'sourceRelease': source, 'targetRelease': target,
    'snapshot': snapshot, 'dataRoot': '/var/lib/kautuka',
    'sourcePointers': {name: source for name in ('manager', 'updateservice', 'webhost', 'gateway', 'channelhost', 'credentialshost')},
    'snapshotManifestSha256': manifest_sha,
}
for path in ('/var/lib/kautuka-recovery/operation.json', '/var/lib/kautuka-recovery/maintenance.json'):
    with open(path, 'w', encoding='utf-8') as stream:
        json.dump(document, stream, sort_keys=True)
        stream.write('\n')
PY
    for service in "${services[@]}"; do ln -sfn "versions/$target_version" "/opt/kautuka/$service/current"; done
    python3 - <<'PY'
import sqlite3
db = sqlite3.connect('/var/lib/kautuka/kautuka.db')
db.execute("insert into registry values ('target-migration')")
db.commit(); db.close()
PY
    if [[ "$phase" == prepare-committed ]]; then
        rm -f /run/kautuka-db-writer.lock
        echo 'Prepared a committed v-prefixed release transaction. Shut down and restart this disposable WSL distro, then run verify-committed.'
        exit 0
    fi
    rm -f /var/lib/kautuka/security/channel/offline-authority/channel-ca.key
    rm /var/lib/kautuka/agents/agent-123/agent.db \
       /var/lib/kautuka/credentialsHost/vault/vault-keys/A1B2C3D4.key
    printf '%s' 'target file store remains outside database recovery' > /var/lib/kautuka/agents/agent-123/files/user.txt
    rm -f /run/kautuka-db-writer.lock
    echo 'Prepared a real systemd boot-recovery transaction. Shut down and restart this disposable WSL distro, then run verify1.'
    exit 0
fi

if [[ "$phase" == verify1 ]]; then
    [[ "$(systemctl show --property=Result --value kautuka-update-recovery.service)" == success ]]
    [[ -s /var/lib/kautuka-recovery/test-no-userbus.log ]]
    [[ "$(python3 -c 'import json;print(json.load(open("/var/lib/kautuka-recovery/operation.json"))["phase"])')" == recovered ]]
    [[ -f /var/lib/kautuka-recovery/maintenance.json ]]
    for service in manager updateservice webhost gateway channelhost credentialshost; do
        [[ "$(readlink -f "/opt/kautuka/$service/current")" == "/opt/kautuka/$service/versions/$source_version" ]]
    done
    python3 - <<'PY'
import sqlite3
db = sqlite3.connect('/var/lib/kautuka/kautuka.db')
values = {row[0] for row in db.execute('select value from registry')}
assert values == {'source-row'}, values
db.execute("insert into registry values ('write-after-recovery')")
db.commit(); db.close()
agent = sqlite3.connect('/var/lib/kautuka/agents/agent-123/agent.db')
assert agent.execute('select value from runs').fetchone()[0] == 'source-agent'
agent.close()
assert open('/var/lib/kautuka/agents/agent-123/files/user.txt').read() == 'target file store remains outside database recovery'
assert open('/var/lib/kautuka/credentialsHost/vault/vault-keys/A1B2C3D4.key', 'rb').read() == b'k' * 32
assert open('/var/lib/kautuka/security/channel/offline-authority/channel-ca.key', 'rb').read() == b''
PY
    mkdir -p /var/lib/kautuka/manager
    python3 - <<'PY'
import json
with open('/var/lib/kautuka/manager/manager-desired-state.json', 'w') as stream:
    json.dump({'schemaVersion': 1, 'acceptedVersion': 1, 'runtimeSettings': {}}, stream)
PY
    sleep 1
    KAUTUKA_APPLY_DETACHED=1 /usr/local/lib/kautuka/apply-release --complete-recovery --version "$source_version"
    [[ ! -e /var/lib/kautuka-recovery/maintenance.json ]]
    [[ "$(python3 -c 'import json;print(json.load(open("/var/lib/kautuka-recovery/operation.json"))["phase"])')" == completed ]]
    curl --fail --silent --show-error --max-time 2 --unix-socket /run/kautuka/gateway/gateway.sock http://localhost/health >/dev/null
    python3 - <<'PY'
import fcntl, os
fd = os.open('/run/kautuka-db-writer.lock', os.O_RDONLY)
try:
    try:
        fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        pass
    else:
        raise AssertionError('writer lease was not held by active host services')
finally:
    os.close(fd)
PY
    python3 - <<'PY'
import sqlite3
db = sqlite3.connect('/var/lib/kautuka/kautuka.db')
db.execute("insert into registry values ('write-after-completion')")
db.commit(); db.close()
PY
    echo 'Real unit environment, live process identities and Unix-socket health passed. Shut down and restart the distro again, then run verify2.'
    exit 0
fi

if [[ "$phase" == verify2 ]]; then
    [[ "$(systemctl show --property=Result --value kautuka-update-recovery.service)" == success ]]
    [[ "$(python3 -c 'import json;print(json.load(open("/var/lib/kautuka-recovery/operation.json"))["phase"])')" == completed ]]
    [[ ! -e /var/lib/kautuka-recovery/maintenance.json ]]
    [[ "$(wc -l < /var/lib/kautuka-recovery/test-no-userbus.log)" -ge 2 ]]
    python3 - <<'PY'
import sqlite3
values = {row[0] for row in sqlite3.connect('/var/lib/kautuka/kautuka.db').execute('select value from registry')}
assert {'source-row', 'write-after-recovery', 'write-after-completion'} <= values, values
PY
    [[ "$(readlink /var/lib/kautuka-recovery/backups/last-known-good)" == /var/lib/kautuka-recovery/backups/11111111111111111111111111111111 ]]
    mkdir -p /var/lib/kautuka/.config/systemd/user/kautuka-manager.service.d
    cat > /var/lib/kautuka/.config/systemd/user/kautuka-manager.service.d/90-test-exec-override.conf <<'UNIT'
[Service]
ExecStart=
ExecStart=/usr/bin/env sleep infinity
UNIT
    chown kautuka:kautuka /var/lib/kautuka/.config/systemd/user/kautuka-manager.service.d/90-test-exec-override.conf
    runuser -u kautuka -- env HOME=/var/lib/kautuka XDG_RUNTIME_DIR="/run/user/$(id -u kautuka)" \
        DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/$(id -u kautuka)/bus" systemctl --user daemon-reload
    set +e
    KAUTUKA_APPLY_DETACHED=1 /usr/local/lib/kautuka/apply-release --boot-recover >/tmp/kautuka-exec-override-reject.log 2>&1
    exec_override_status=$?
    set -e
    [[ $exec_override_status -ne 0 ]]
    grep -q 'must launch only the supported Kautuka executable' /tmp/kautuka-exec-override-reject.log
    rm -f /var/lib/kautuka/.config/systemd/user/kautuka-manager.service.d/90-test-exec-override.conf
    rmdir /var/lib/kautuka/.config/systemd/user/kautuka-manager.service.d
    runuser -u kautuka -- env HOME=/var/lib/kautuka XDG_RUNTIME_DIR="/run/user/$(id -u kautuka)" \
        DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/$(id -u kautuka)/bus" systemctl --user daemon-reload
    [[ ! -e /var/lib/kautuka-recovery/maintenance.json ]]
    python3 - <<'PY'
import json
operation = json.load(open('/var/lib/kautuka-recovery/operation.json'))
operation['phase'] = 'verifying'
for path in ('/var/lib/kautuka-recovery/operation.json', '/var/lib/kautuka-recovery/maintenance.json'):
    with open(path, 'w', encoding='utf-8') as stream:
        json.dump(operation, stream, sort_keys=True); stream.write('\n')
PY
    set +e
    KAUTUKA_APPLY_DETACHED=1 /usr/local/lib/kautuka/apply-release --boot-recover >/tmp/kautuka-live-writer-reject.log 2>&1
    live_writer_status=$?
    set -e
    [[ $live_writer_status -ne 0 ]]
    grep -q 'Kautuka database writer/runtime process remains before restore' /tmp/kautuka-live-writer-reject.log
    python3 - <<'PY'
import sqlite3
values = {row[0] for row in sqlite3.connect('/var/lib/kautuka/kautuka.db').execute('select value from registry')}
assert {'source-row', 'write-after-recovery', 'write-after-completion'} <= values, values
PY
    echo 'Second real systemd reboot preserved post-recovery writes; live-process boot restore was refused.'
    exit 0
fi

if [[ "$phase" == verify-committed ]]; then
    [[ "$(systemctl show --property=Result --value kautuka-update-recovery.service)" == success ]]
    [[ "$(python3 -c 'import json;print(json.load(open("/var/lib/kautuka-recovery/operation.json"))["phase"])')" == committed ]]
    [[ -f /var/lib/kautuka-recovery/maintenance.json ]]
    for service in manager updateservice webhost gateway channelhost credentialshost; do
        [[ "$(readlink -f "/opt/kautuka/$service/current")" == "/opt/kautuka/$service/versions/$target_version" ]]
    done
    python3 - <<'PY'
import sqlite3
values = {row[0] for row in sqlite3.connect('/var/lib/kautuka/kautuka.db').execute('select value from registry')}
assert values == {'source-row', 'target-migration'}, values
PY
    set +e
    KAUTUKA_APPLY_DETACHED=1 /usr/local/lib/kautuka/apply-release --recover > /tmp/kautuka-recover-committed.log 2>&1
    recover_status=$?
    set -e
    [[ $recover_status -eq 0 ]]
    grep -q 'Committed update found' /tmp/kautuka-recover-committed.log
    python3 - <<'PY'
import sqlite3
values = {row[0] for row in sqlite3.connect('/var/lib/kautuka/kautuka.db').execute('select value from registry')}
assert values == {'source-row', 'target-migration'}, values
PY
    echo 'Committed v-prefixed release survived real boot and runtime recovery checks without restoring the source snapshot.'
    exit 0
fi

echo 'usage: test-apply-release-systemd-reboot.sh prepare|verify1|verify2|prepare-committed|verify-committed' >&2
exit 2
