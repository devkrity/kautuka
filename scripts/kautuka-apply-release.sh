#!/usr/bin/env bash
# Kautuka-Apply-Protocol: 6
set -euo pipefail

# Root helper invoked by UpdateService. It deploys or retargets one release.
# UpdateService never extracts or flips current itself.

usage() {
    cat <<'EOF'
Usage:
  kautuka-apply-release --artifact <tar.gz> --version <version>
  kautuka-apply-release --stage-appliance --artifact <tar.gz> --version <version>
  kautuka-apply-release --activate-appliance --artifact <tar.gz> --version <version>
  kautuka-apply-release --consume-appliance-request
  kautuka-apply-release --recover
  kautuka-apply-release --boot-recover
  kautuka-apply-release --renew-channel-certificates
  kautuka-apply-release --commit --version <version>
  kautuka-apply-release --complete-recovery --version <source-version>
  kautuka-apply-release --capabilities
EOF
}

lock_path="${KAUTUKA_APPLY_LOCK:-/run/kautuka-apply.lock}"
recovery_root='/var/lib/kautuka-recovery'
maintenance_path="$recovery_root/maintenance.json"
journal_path="$recovery_root/operation.json"
backups_root="$recovery_root/backups"
recovery_tool='/usr/local/lib/kautuka/update-recovery.py'
artifact=''
version=''
recover='false'
boot_recover='false'
renew_channel_certificates='false'
commit='false'
complete_recovery='false'
capabilities='false'
stage_appliance='false'
activate_appliance='false'
consume_appliance_request='false'
recovery_layout=host-service

if [[ ${EUID} -ne 0 ]]; then
    echo 'error: kautuka-apply-release must run as root.' >&2
    exit 1
fi

install -d -o root -g root -m 0755 "$recovery_root"
[[ ! -L "$recovery_root" && "$(stat -c '%u' "$recovery_root")" == 0 \
    && -z "$(find "$recovery_root" -maxdepth 0 -perm /022 -print -quit)" ]] || {
    echo 'error: recovery journal directory must be a root-owned, non-writable-by-others real directory' >&2
    exit 1
}

is_disposable_test() {
    [[ "${KAUTUKA_DISPOSABLE_TEST:-}" == '1' \
        && -f /etc/kautuka/.allow-recovery-test \
        && ! -L /etc/kautuka/.allow-recovery-test \
        && "$(stat -c '%u' /etc/kautuka/.allow-recovery-test 2>/dev/null || true)" == 0 \
        && -z "$(find /etc/kautuka/.allow-recovery-test -maxdepth 0 -perm /022 -print -quit 2>/dev/null)" ]]
}

# UpdateService invokes this helper via sudo from a user-scoped unit. Restarting
# manager (podman Delegate) can tear down user.slice and SIGTERM the whole US
# cgroup — live exit 143 with pointers already flipped. Re-enter as a system
# transient unit so apply/revert finishes outside that cgroup.
# Must run before argument parsing so "$@" is still intact for systemd-run.
if [[ "${KAUTUKA_APPLY_DETACHED:-}" != '1' && "${1:-}" != '--boot-recover' && "${1:-}" != '--capabilities' ]] && command -v systemd-run >/dev/null 2>&1; then
    self="$(readlink -f "$0")"
    unit="kautuka-apply-${RANDOM}$$"
    setenv_args=(
        --setenv=KAUTUKA_APPLY_DETACHED=1
        --setenv="KAUTUKA_APPLY_LOCK=${lock_path}"
    )
    # Forward other KAUTUKA_* overrides (e.g. fault-injection) into the system unit.
    while IFS= read -r line; do
        case "$line" in
            KAUTUKA_APPLY_DETACHED=*|KAUTUKA_APPLY_LOCK=*)
                continue
                ;;
            KAUTUKA_*=*)
                setenv_args+=(--setenv="$line")
                ;;
        esac
    done < <(env | grep '^KAUTUKA_' || true)
    exec systemd-run \
        --wait \
        --collect \
        --unit="$unit" \
        "${setenv_args[@]}" \
        "$self" "$@"
fi

while [[ $# -gt 0 ]]; do
    case "$1" in
        --artifact)
            artifact="$2"
            shift 2
            ;;
        --version)
            version="$2"
            shift 2
            ;;
        --stage-appliance)
            stage_appliance='true'
            shift
            ;;
        --activate-appliance)
            activate_appliance='true'
            shift
            ;;
        --consume-appliance-request)
            consume_appliance_request='true'
            shift
            ;;
        --recover)
            recover='true'
            shift
            ;;
        --boot-recover)
            boot_recover='true'
            shift
            ;;
        --renew-channel-certificates)
            renew_channel_certificates='true'
            shift
            ;;
        --commit)
            commit='true'
            shift
            ;;
        --complete-recovery)
            complete_recovery='true'
            shift
            ;;
        --capabilities)
            capabilities='true'
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "error: unknown argument: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

if [[ "$renew_channel_certificates" == 'true' && ( $# != 0 || -n "$artifact" || -n "$version" ||
       "$activate_appliance" == 'true' || "$stage_appliance" == 'true' || "$consume_appliance_request" == 'true' ||
       "$recover" == 'true' || "$boot_recover" == 'true' || "$commit" == 'true' ||
       "$complete_recovery" == 'true' || "$capabilities" == 'true' ) ]] ||
    [[ "$activate_appliance" == 'true' && "$stage_appliance" == 'true' ]] ||
    [[ "$consume_appliance_request" == 'true' && ( -n "$artifact" || -n "$version" ||
       "$activate_appliance" == 'true' || "$stage_appliance" == 'true' || "$recover" == 'true' ||
       "$boot_recover" == 'true' || "$commit" == 'true' || "$complete_recovery" == 'true' || "$capabilities" == 'true' ) ]] ||
    [[ ( "$stage_appliance" == 'true' || "$activate_appliance" == 'true' ) &&
       ( "$recover" == 'true' || "$boot_recover" == 'true' || "$commit" == 'true' ||
         "$complete_recovery" == 'true' || "$capabilities" == 'true' ) ]]; then
    usage >&2
    exit 2
fi

require_version_token() {
    local value="$1"
    [[ "$value" =~ ^[A-Za-z0-9._+-]+$ && "$value" != '.' && "$value" != '..' ]] || {
        echo "error: invalid version: $value" >&2
        exit 2
    }
}

assert_forward_version() {
    python3 - "$1" "$2" <<'PY'
import re, sys

def parse(value):
    match = re.fullmatch(r"v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?", value)
    if not match:
        raise SystemExit(f"error: release identity is not strict semver: {value}")
    major, minor, patch, prerelease, _build = match.groups()
    pre = None if prerelease is None else tuple(
        (0, int(part)) if part.isdigit() else (1, part)
        for part in prerelease.split(".")
    )
    if prerelease:
        for part in prerelease.split("."):
            if part.isdigit() and len(part) > 1 and part.startswith("0"):
                raise SystemExit(f"error: release identity has a leading-zero prerelease number: {value}")
    return (int(major), int(minor), int(patch)), pre

def compare(left, right):
    lc, lp = left
    rc, rp = right
    if lc != rc:
        return (lc > rc) - (lc < rc)
    if lp is None or rp is None:
        return (lp is None) - (rp is None)
    for a, b in zip(lp, rp):
        if a != b:
            return (a > b) - (a < b)
    return (len(lp) > len(rp)) - (len(lp) < len(rp))

source, target = sys.argv[1:]
if compare(parse(target), parse(source)) <= 0:
    raise SystemExit(f"error: target release {target} must be strictly newer than active release {source}")
PY
}

assert_safe_artifact() {
    local raw="$1"
    local resolved
    resolved="$(readlink -f "$raw")"
    [[ -f "$resolved" ]] || {
        echo "error: artifact not found: $raw" >&2
        exit 2
    }
    case "$resolved" in
        /var/lib/kautuka/updateService/updates/*)
            printf '%s\n' "$resolved"
            ;;
        *)
            echo "error: artifact must be under /var/lib/kautuka/updateService/updates/: $resolved" >&2
            exit 2
            ;;
    esac
}

# UpdateService runs as an unprivileged account and its staging directory is
# writable by that account. Copy the inputs into a root-only directory, then
# verify the signature and copied archive independently before extracting it.
verify_staged_artifact() {
    local source="$1"
    local target_version="$2"
    local stage_dir key_dir key manifest signature
    for command in openssl python3 base64; do
        command -v "$command" >/dev/null 2>&1 || {
            echo "error: required verification command not found: $command" >&2
            return 1
        }
    done
    stage_dir="$(dirname "$source")"
    key_dir='/etc/kautuka'
    key="$key_dir/manifest-public-key.pem"
    manifest="$stage_dir/kautuka-platform-release.json"
    signature="$stage_dir/kautuka-platform-release.sig"
    [[ -d "$key_dir" && ! -L "$key_dir" && -f "$key" && ! -L "$key" ]] || {
        echo 'error: trusted manifest key is missing or is a symbolic link; rerun the installer as root' >&2
        return 1
    }
    [[ "$(stat -c '%u' "$key_dir")" == 0 && "$(stat -c '%u' "$key")" == 0 ]] || {
        echo 'error: trusted manifest key and directory must be root-owned; rerun the installer as root' >&2
        return 1
    }
    if [[ -n "$(find "$key_dir" "$key" -maxdepth 0 -perm /022 -print -quit)" ]]; then
        echo 'error: trusted manifest key and directory must not be group or world writable' >&2
        return 1
    fi
    [[ -f "$manifest" && -f "$signature" ]] || {
        echo 'error: staged signed release manifest or signature is missing; check for updates again' >&2
        return 1
    }

    python3 - "$source" <<'PY'
import os, sys
archive_size = os.stat(sys.argv[1]).st_size
fs = os.statvfs('/var/tmp')
available = fs.f_bavail * fs.f_frsize
required = archive_size + 256 * 1024 * 1024
if available < required:
    raise SystemExit(f'error: insufficient free space for the private verified archive: need {required} bytes, have {available} bytes')
PY
    verified_root="$(mktemp -d /var/tmp/kautuka-verified-XXXXXX)"
    cp -- "$manifest" "$verified_root/manifest.json"
    cp -- "$signature" "$verified_root/signature.b64"
    cp -- "$source" "$verified_root/artifact.tar.gz"
    base64 --decode "$verified_root/signature.b64" > "$verified_root/signature.der" || {
        echo 'error: release manifest signature is not valid base64' >&2
        return 1
    }
    openssl dgst -sha256 -verify "$key" -signature "$verified_root/signature.der" "$verified_root/manifest.json" >/dev/null || {
        echo 'error: release manifest signature verification failed in privileged helper' >&2
        return 1
    }
    python3 - "$verified_root/manifest.json" "$verified_root/artifact.tar.gz" "$target_version" "$recovery_layout" <<'PY'
import hashlib, json, os, sys

manifest_path, artifact_path, requested, layout = sys.argv[1:]
with open(manifest_path, encoding='utf-8') as f:
    manifest = json.load(f)
has_appliance = any(key.casefold() == 'appliancedeployment' for key in manifest)
if layout == 'appliance' and 'applianceDeployment' not in manifest:
    raise SystemExit('error: staging requires a signed appliance deployment')
if layout != 'appliance' and has_appliance:
    raise SystemExit('error: appliance deployment requires a Manager appliance activation transaction')
expected_tag = requested if requested.startswith('v') else 'v' + requested
if manifest.get('releaseTag') != expected_tag or manifest.get('version') != expected_tag[1:]:
    raise SystemExit('error: signed manifest identity does not match requested version')
signature = manifest.get('signature') or {}
if signature.get('algorithm') != 'ecdsa-p256-sha256' or signature.get('format') != 'base64':
    raise SystemExit('error: unsupported signed manifest format')
artifacts = [a for a in manifest.get('artifacts', []) if isinstance(a, dict) and a.get('name') == 'kautuka-linux-amd64.tar.gz']
if len(artifacts) != 1:
    raise SystemExit('error: signed manifest must contain exactly one Linux artifact')
entry = artifacts[0]
if not isinstance(entry.get('size'), int) or isinstance(entry.get('size'), bool) or entry['size'] != os.path.getsize(artifact_path):
    raise SystemExit('error: copied artifact size does not match signed manifest')
digest = hashlib.sha256()
with open(artifact_path, 'rb') as f:
    for chunk in iter(lambda: f.read(1024 * 1024), b''):
        digest.update(chunk)
if not isinstance(entry.get('sha256'), str) or digest.hexdigest() != entry['sha256'].lower():
    raise SystemExit('error: copied artifact hash does not match signed manifest')
PY
}

host_services=(manager updateservice webhost gateway channelhost credentialshost)
# Do not restart UpdateService here. The apply caller is that unit; a cgroup
# restart kills sudo and leaves the helper unfinished.
# WebHost does not write application databases. Keep it alive to serve the
# maintenance page while the database writers and Gateway are stopped.
user_units=(kautuka-manager.service)
system_units=(kautuka-credentialshost.service kautuka-channelhost.service kautuka-gateway.service)
service_user='kautuka'
database_root='/var/lib/kautuka'
database_writer_lock='/run/kautuka-db-writer.lock'
database_writer_lease_active='false'

write_durable_record() {
    local destination="$1" operation_id="$2" phase="$3" source_release="$4" target_release="$5" snapshot="$6"
    shift 6
    python3 - "$destination" "$operation_id" "$phase" "$source_release" "$target_release" "$snapshot" "$database_root" "$@" <<'PY'
import json, os, sys, tempfile, time
path, operation, phase, source, target, snapshot, data_root, *pairs = sys.argv[1:]
pointers = {}
manifest_sha256 = None
snapshot_prune_status = None
for pair in pairs:
    name, value = pair.split('=', 1)
    if name == 'snapshotManifestSha256':
        manifest_sha256 = value
    elif name == 'snapshotPruneStatus':
        snapshot_prune_status = value
    else:
        pointers[name] = value
document = {
    'format': 1,
    'operationId': operation,
    'phase': phase,
    'sourceRelease': source,
    'targetRelease': target,
    'snapshot': snapshot,
    'dataRoot': data_root,
    'sourcePointers': pointers,
    'updatedUnixMilliseconds': time.time_ns() // 1_000_000,
}
if manifest_sha256 is not None:
    document['snapshotManifestSha256'] = manifest_sha256
if snapshot_prune_status is not None:
    document['snapshotPruneStatus'] = snapshot_prune_status
os.makedirs(os.path.dirname(path), mode=0o755, exist_ok=True)
fd, temporary = tempfile.mkstemp(prefix='.record-', dir=os.path.dirname(path))
try:
    with os.fdopen(fd, 'w', encoding='utf-8') as stream:
        json.dump(document, stream, sort_keys=True, separators=(',', ':'))
        stream.write('\n')
        stream.flush()
        os.fsync(stream.fileno())
    os.chmod(temporary, 0o644)
    os.replace(temporary, path)
    directory = os.open(os.path.dirname(path), os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(directory)
    finally:
        os.close(directory)
finally:
    if os.path.exists(temporary):
        os.unlink(temporary)
PY
}

read_journal_field() {
    local field="$1"
    python3 - "$journal_path" "$field" <<'PY'
import json, sys
with open(sys.argv[1], encoding='utf-8') as stream:
    document = json.load(stream)
value = document.get(sys.argv[2])
if value is None:
    raise SystemExit('error: update recovery journal is missing ' + sys.argv[2])
print(value)
PY
}

validate_journal_source_pointers() {
    local source_release="$1"
    python3 - "$journal_path" "$source_release" "${host_services[@]}" <<'PY'
import json, sys
path, source, *expected_services = sys.argv[1:]
with open(path, encoding='utf-8') as stream:
    journal = json.load(stream)
pointers = journal.get('sourcePointers')
if not isinstance(pointers, dict) or set(pointers) != set(expected_services):
    raise SystemExit('error: update journal source pointer inventory is invalid')
if any(value != source for value in pointers.values()):
    raise SystemExit('error: update journal does not bind every service to its recorded source release')
PY
}

set_operation_phase() {
    local phase="$1" prune_status="${2:-}" write_fence="${3:-true}" manifest_sha256
    local operation_id source_release target_release snapshot
    local pointer_pairs=()
    operation_id="$(read_journal_field operationId)"
    source_release="$(read_journal_field sourceRelease)"
    target_release="$(read_journal_field targetRelease)"
    snapshot="$(read_journal_field snapshot)"
    while IFS='=' read -r key value; do
        pointer_pairs+=("$key=$value")
    done < <(python3 - "$journal_path" <<'PY'
import json, sys
with open(sys.argv[1], encoding='utf-8') as stream:
    pointers = json.load(stream).get('sourcePointers')
if not isinstance(pointers, dict):
    raise SystemExit('error: update recovery journal source pointers are invalid')
for key, value in pointers.items():
    print(f'{key}={value}')
PY
)
    manifest_sha256="$(python3 - "$journal_path" <<'PY'
import json, sys
with open(sys.argv[1], encoding='utf-8') as stream:
    print(json.load(stream).get('snapshotManifestSha256', ''))
PY
)"
    [[ -z "$manifest_sha256" ]] || pointer_pairs+=("snapshotManifestSha256=$manifest_sha256")
    [[ -z "$prune_status" ]] || pointer_pairs+=("snapshotPruneStatus=$prune_status")
    write_durable_record "$journal_path" "$operation_id" "$phase" "$source_release" "$target_release" "$snapshot" "${pointer_pairs[@]}" || return 1
    if [[ "$write_fence" == 'true' ]]; then
        write_durable_record "$maintenance_path" "$operation_id" "$phase" "$source_release" "$target_release" "$snapshot" "${pointer_pairs[@]}"
    fi
}

ensure_maintenance_fence_from_journal() {
    local operation_id phase source_release target_release snapshot
    local validate_only="${1:-false}"
    local pointer_pairs=()
    python3 - "$journal_path" "$backups_root" "$database_root" "${host_services[@]}" <<'PY' || return 1
import hashlib, json, re, sys
path, backups_root, data_root, *services = sys.argv[1:]
with open(path, encoding='utf-8') as stream:
    journal = json.load(stream)
if journal.get('format') != 1 or journal.get('dataRoot') != data_root:
    raise SystemExit('error: update journal format or data root is unsupported')
if journal.get('phase') not in {'capturing', 'verified', 'verifying', 'starting', 'restoring', 'committed', 'recovered', 'completed', 'aborted'}:
    raise SystemExit('error: update journal phase is unsupported for recovery')
operation = journal.get('operationId')
source = journal.get('sourceRelease')
target = journal.get('targetRelease')
snapshot = journal.get('snapshot')
if not isinstance(operation, str) or re.fullmatch(r'[A-Fa-f0-9]{32}', operation) is None:
    raise SystemExit('error: update journal operation id is invalid')
if not isinstance(source, str) or re.fullmatch(r'v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-preview\.(0|[1-9][0-9]*))?', source) is None:
    raise SystemExit('error: update journal source release is invalid')
if not isinstance(target, str) or re.fullmatch(r'v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-preview\.(0|[1-9][0-9]*))?', target) is None:
    raise SystemExit('error: update journal target release is invalid')
if snapshot != f'{backups_root}/{operation}':
    raise SystemExit('error: update journal snapshot path is invalid')
manifest_digest = journal.get('snapshotManifestSha256')
if manifest_digest is not None and (not isinstance(manifest_digest, str) or re.fullmatch(r'[0-9a-f]{64}', manifest_digest) is None):
    raise SystemExit('error: update journal snapshot manifest binding is invalid')
if __import__('os').path.lexists(snapshot):
    if __import__('os').path.islink(snapshot) or not __import__('os').path.isdir(snapshot):
        raise SystemExit('error: update journal snapshot is not a real directory')
    manifest_path = f'{snapshot}/manifest.json'
    if __import__('os').path.islink(manifest_path) or not __import__('os').path.isfile(manifest_path) \
            or manifest_digest is None or hashlib.sha256(open(manifest_path, 'rb').read()).hexdigest() != manifest_digest:
        raise SystemExit('error: snapshot manifest does not match its durable journal binding')
elif journal.get('phase') not in {'capturing', 'aborted'}:
    raise SystemExit('error: verified update journal snapshot is missing')
pointers = journal.get('sourcePointers')
if not isinstance(pointers, dict) or set(pointers) != set(services) or any(value != source for value in pointers.values()):
    raise SystemExit('error: update journal source pointer inventory is invalid')
PY
    operation_id="$(read_journal_field operationId)"
    phase="$(read_journal_field phase)"
    source_release="$(read_journal_field sourceRelease)"
    target_release="$(read_journal_field targetRelease)"
    snapshot="$(read_journal_field snapshot)"
    while IFS='=' read -r key value; do
        pointer_pairs+=("$key=$value")
    done < <(python3 - "$journal_path" <<'PY'
import json, sys
with open(sys.argv[1], encoding='utf-8') as stream:
    pointers = json.load(stream).get('sourcePointers')
if not isinstance(pointers, dict):
    raise SystemExit('error: update recovery journal source pointers are invalid')
for key, value in pointers.items():
    print(f'{key}={value}')
PY
)
    local manifest_sha256
    manifest_sha256="$(python3 - "$journal_path" <<'PY'
import json, sys
with open(sys.argv[1], encoding='utf-8') as stream:
    print(json.load(stream).get('snapshotManifestSha256', ''))
PY
)"
    [[ -z "$manifest_sha256" ]] || pointer_pairs+=("snapshotManifestSha256=$manifest_sha256")
    [[ "$validate_only" != 'true' ]] || return 0
    write_durable_record "$maintenance_path" "$operation_id" "$phase" "$source_release" "$target_release" "$snapshot" "${pointer_pairs[@]}"
}

write_emergency_maintenance_fence() {
    python3 - "$maintenance_path" <<'PY'
import json, os, sys, tempfile
path = sys.argv[1]
directory = os.path.dirname(path)
fd, temporary = tempfile.mkstemp(prefix='.maintenance-unreadable-', dir=directory)
try:
    with os.fdopen(fd, 'w', encoding='utf-8') as stream:
        json.dump({'format': 1, 'phase': 'journal-unreadable'}, stream, separators=(',', ':'))
        stream.write('\n')
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)
    descriptor = os.open(directory, os.O_RDONLY | getattr(os, 'O_DIRECTORY', 0))
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)
finally:
    if os.path.exists(temporary):
        os.unlink(temporary)
PY
}

test_crash_at() {
    local phase="$1"
    if is_disposable_test && [[ "${KAUTUKA_TEST_CRASH_AT:-}" == "$phase" ]]; then
        kill -KILL "$$"
    fi
}

assert_supported_data_configuration() {
    local boot_only="${1:-false}"
    [[ "$database_root" == '/var/lib/kautuka' && -d "$database_root" && ! -L "$database_root" ]] || {
        echo 'error: safe update recovery supports only the standard /var/lib/kautuka application data root' >&2
        return 1
    }
    [[ -f "$recovery_tool" && ! -L "$recovery_tool" ]] || {
        echo 'error: this installation has no database recovery capability; run the current signed installer bootstrap before updating' >&2
        return 1
    }
    mkdir -p "$backups_root"
    [[ ! -L "$backups_root" && "$(stat -c '%u' "$backups_root")" == 0 \
        && -z "$(find "$backups_root" -maxdepth 0 -perm /022 -print -quit)" ]] || {
        echo 'error: recovery backup directory must be root-owned and not writable by group or other users' >&2
        return 1
    }
    for command in python3 curl findmnt; do
        command -v "$command" >/dev/null 2>&1 || {
            echo "error: required safe update recovery command not found: $command" >&2
            return 1
        }
    done
    python3 -c 'import sqlite3' >/dev/null 2>&1 || {
        echo 'error: Python SQLite support is required for safe update recovery' >&2
        return 1
    }
    if ! is_disposable_test && [[ "$boot_only" != 'true' ]]; then
      for required_material in \
        security/channel/channel-private/channel-server.pfx \
        security/channel/gateway-private/gateway-channel-client.pfx \
        security/channel/gateway-private/gateway-channel-delegation.pfx \
        security/channel/gateway-trust/channel-ca.cer \
        security/channel/channel-trust/channel-ca.cer \
        security/channel/channel-trust/gateway-channel-client.cer \
        security/channel/channel-trust/gateway-channel-delegation.cer \
        security/channel/offline-authority/channel-ca.key \
        security/channel/offline-authority/channel-ca.cer; do
        [[ -f "$database_root/$required_material" && ! -L "$database_root/$required_material" ]] || {
            echo "error: required production channel authentication material is missing or unsupported: $required_material" >&2
              return 1
          }
      done
    fi
    if ! is_disposable_test; then
        local service_user service_home service_uid xdg_runtime_dir unit env_text exec_text expected_exec material_name material_value
        local -a material_values required_material_roots
        service_user='kautuka'
        service_uid="$(id -u "$service_user")" || { echo 'error: Kautuka service user is missing' >&2; return 1; }
        service_home="$(getent passwd "$service_user" | cut -d: -f6)"
        xdg_runtime_dir="/run/user/${service_uid}"
        for unit in kautuka-gateway.service kautuka-channelhost.service kautuka-credentialshost.service kautuka-manager.service kautuka-updateservice.service kautuka-webhost.service; do
            if [[ -f "/etc/systemd/system/${unit}" || "$unit" == 'kautuka-gateway.service' || "$unit" == 'kautuka-channelhost.service' || "$unit" == 'kautuka-credentialshost.service' ]]; then
                [[ -f "/etc/systemd/system/${unit}" ]] || { echo "error: supported system service unit is missing: $unit" >&2; return 1; }
                [[ "$(systemctl show --property=LoadState --value "$unit" 2>/dev/null || true)" == 'loaded' ]] || { echo "error: systemd did not load required service unit: $unit" >&2; return 1; }
                env_text="$(systemctl show --property=Environment --value "$unit" 2>/dev/null || true)"
                exec_text="$(systemctl show --property=ExecStart --value "$unit" 2>/dev/null || true)"
                [[ -z "$(systemctl show --property=EnvironmentFiles --value "$unit" 2>/dev/null || true)" ]] || { echo "error: EnvironmentFile overrides are unsupported for $unit" >&2; return 1; }
            else
                if [[ ! -S "${xdg_runtime_dir}/bus" && "$boot_only" != 'true' ]]; then
                    echo 'error: cannot validate user service configuration without its systemd bus' >&2
                    return 1
                fi
                if [[ "$boot_only" == 'true' && ! -S "${xdg_runtime_dir}/bus" ]]; then
                    local unit_file="$service_home/.config/systemd/user/$unit" unit_dropin
                    [[ -f "$unit_file" && ! -L "$unit_file" ]] || { echo "error: generated user service unit is missing or unsupported at boot: $unit_file" >&2; return 1; }
                    env_text=''
                    exec_text=''
                    for unit_dropin in "$unit_file" "$service_home/.config/systemd/user/$unit.d"/*.conf; do
                        [[ -f "$unit_dropin" ]] || continue
                        grep -Eq '^[[:space:]]*EnvironmentFile=' "$unit_dropin" && { echo "error: EnvironmentFile overrides are unsupported for $unit" >&2; return 1; }
                        if [[ "$unit_dropin" != "$unit_file" ]] \
                            && grep -Eq '^[[:space:]]*ExecStart=' "$unit_dropin"; then
                            echo "error: ExecStart overrides are unsupported for $unit" >&2
                            return 1
                        fi
                        env_text+=" $(sed -n 's/^[[:space:]]*Environment=//p' "$unit_dropin" | tr '\n' ' ')"
                        [[ "$unit_dropin" == "$unit_file" ]] \
                            && exec_text+=$'\n'"$(sed -n 's/^[[:space:]]*\(ExecStart=.*\)$/\1/p' "$unit_dropin")"
                    done
                else
                local user_systemctl=(runuser -u "$service_user" -- env HOME="$service_home" XDG_RUNTIME_DIR="$xdg_runtime_dir" DBUS_SESSION_BUS_ADDRESS="unix:path=${xdg_runtime_dir}/bus" systemctl --user)
                [[ "$("${user_systemctl[@]}" show --property=LoadState --value "$unit" 2>/dev/null || true)" == 'loaded' ]] || { echo "error: systemd user unit is missing or unloaded: $unit" >&2; return 1; }
                env_text="$("${user_systemctl[@]}" show --property=Environment --value "$unit" 2>/dev/null || true) $("${user_systemctl[@]}" show-environment 2>/dev/null || true)"
                exec_text="$("${user_systemctl[@]}" show --property=ExecStart --value "$unit" 2>/dev/null || true)"
                [[ -z "$("${user_systemctl[@]}" show --property=EnvironmentFiles --value "$unit" 2>/dev/null || true)" ]] || { echo "error: EnvironmentFile overrides are unsupported for $unit" >&2; return 1; }
                fi
            fi
            local service_key="${unit#kautuka-}"
            service_key="${service_key%.service}"
            expected_exec="/opt/kautuka/${service_key}/current/kautuka-${service_key}"
            assert_service_executable "$unit" "$expected_exec" "$exec_text" || return 1
            assert_config_value "$unit" Kautuka__ContainerStateRoot /var/lib/kautuka "$env_text" || return 1
            assert_config_value "$unit" Kautuka__Mode prod "$env_text" || return 1
            required_material_roots=()
            case "$unit" in
                kautuka-gateway.service)
                    required_material_roots=(
                        'Kautuka__ChannelGateway__ProductionMaterial__GatewayPrivateRoot=/var/lib/kautuka/security/channel/gateway-private'
                        'Kautuka__ChannelGateway__ProductionMaterial__GatewayTrustRoot=/var/lib/kautuka/security/channel/gateway-trust'
                    )
                    ;;
                kautuka-channelhost.service)
                    required_material_roots=(
                        'Kautuka__ChannelGateway__ProductionMaterial__ChannelPrivateRoot=/var/lib/kautuka/security/channel/channel-private'
                        'Kautuka__ChannelGateway__ProductionMaterial__ChannelTrustRoot=/var/lib/kautuka/security/channel/channel-trust'
                    )
                    ;;
            esac
            for material_value in "${required_material_roots[@]}"; do
                material_name="${material_value%%=*}"
                mapfile -t material_values < <(grep -oE "(^|[[:space:]])${material_name}=[^[:space:]]+" <<<"$env_text" | sed "s/^[[:space:]]*${material_name}=//")
                [[ ${#material_values[@]} -eq 1 && "${material_values[0]}" == "${material_value#*=}" ]] || {
                    echo "error: $unit must use the supported channel authentication material root $material_name=${material_value#*=}" >&2
                    return 1
                }
            done
            if grep -Eq '(^|[[:space:]])Kautuka__SocketRoot=([^[:space:]]+)' <<<"$env_text" \
                && ! grep -Fq 'Kautuka__SocketRoot=/run/kautuka' <<<"$env_text"; then
                echo "error: unsupported custom socket root in $unit" >&2; return 1
            fi
            if grep -Eq '(^|[[:space:]])Kautuka__Transport=([^[:space:]]+)' <<<"$env_text" \
                && ! grep -Eq 'Kautuka__Transport=(unix|unixsocket)' <<<"$env_text"; then
                echo "error: unsupported custom host transport in $unit" >&2; return 1
            fi
        done
    fi
    if ! is_disposable_test; then
        local root_mount check_path check_mount
        root_mount="$(findmnt --noheadings --output TARGET --target "$database_root" 2>/dev/null)" || { echo 'error: cannot resolve application data mount' >&2; return 1; }
        for check_path in "$database_root"/agents "$database_root"/credentialsHost "$database_root"/channelHost "$database_root"/manager "$database_root"/gateway "$database_root"/security; do
            [[ -d "$check_path" ]] || continue
            check_mount="$(findmnt --noheadings --output TARGET --target "$check_path" 2>/dev/null)" || { echo "error: cannot resolve application-state mount for $check_path" >&2; return 1; }
            [[ "$check_mount" == "$root_mount" ]] || { echo "error: separately mounted application-state paths are unsupported: $check_path" >&2; return 1; }
        done
    fi
}

assert_maintenance_webhost() {
    local marker="/opt/kautuka/webhost/current/.maintenance-page-v1"
    [[ -f "$marker" && ! -L "$marker" && "$(stat -c '%u' "$marker")" == 0 \
        && -z "$(find "$marker" -maxdepth 0 -perm /022 -print -quit)" \
        && "$(cat "$marker")" == 'kautuka-maintenance-page-v1' ]] || {
        echo 'error: installed WebHost cannot serve the update maintenance page; run the current signed installer bootstrap' >&2
        return 1
    }
}

assert_no_legacy_recovery_markers() {
    local marker
    for marker in /var/lib/kautuka-recovery/kautuka-apply.rollback /run/kautuka-apply.rollback; do
        if [[ -e "$marker" || -L "$marker" ]]; then
            echo "error: legacy recovery marker requires administrator review before safe updates can continue: $marker" >&2
            return 1
        fi
    done
}

assert_config_value() {
    local unit="$1" key="$2" expected="$3" env_text="$4" value
    local -a values
    mapfile -t values < <(grep -oE "(^|[[:space:]])${key}=[^[:space:]]+" <<<"$env_text" | sed "s/^[[:space:]]*${key}=//")
    [[ ${#values[@]} -gt 0 ]] || { echo "error: $unit does not declare required $key=$expected" >&2; return 1; }
    for value in "${values[@]}"; do
        [[ "$value" == "$expected" ]] || { echo "error: $unit has unsupported $key=$value" >&2; return 1; }
    done
}

assert_service_executable() {
    local unit="$1" expected="$2" unit_text="$3"
    python3 - "$unit" "$expected" "$unit_text" <<'PY'
import re, sys
unit, expected, text = sys.argv[1:]
paths = re.findall(r'\bpath=([^ ;}]+)', text)
if paths:
    if paths == [expected]:
        raise SystemExit(0)
else:
    starts = [line.strip()[len('ExecStart='):] for line in text.splitlines()
              if line.strip().startswith('ExecStart=')]
    if starts == [expected]:
        raise SystemExit(0)
print(f'error: {unit} must launch only the supported Kautuka executable {expected}', file=sys.stderr)
raise SystemExit(1)
PY
}

record_source_pointers() {
    local service_key current_link resolved
    declare -gA source_pointers=()
    for service_key in "${host_services[@]}"; do
        current_link="/opt/kautuka/${service_key}/current"
        [[ -L "$current_link" ]] || {
            echo "error: unsupported non-symbolic current pointer: $current_link" >&2
            return 1
        }
        resolved="$(readlink -f "$current_link")" || return 1
        [[ "$resolved" == "/opt/kautuka/${service_key}/versions/"* ]] || {
            echo "error: unsupported current pointer target for $service_key: $resolved" >&2
            return 1
        }
        source_pointers["$service_key"]="$(basename "$resolved")"
    done
    local manager_source="${source_pointers[manager]}"
    for service_key in "${host_services[@]}"; do
        [[ "${source_pointers[$service_key]}" == "$manager_source" ]] || {
            echo "error: inconsistent source release pointers; refusing recovery snapshot ($service_key=${source_pointers[$service_key]}, manager=$manager_source)" >&2
            return 1
        }
    done
}

stop_database_writers() {
    local unit active service_passwd service_home service_uid xdg_runtime_dir pid
    local live_user_units=("${user_units[@]}")
    if is_disposable_test; then
        return 0
    fi
    if [[ "$recovery_layout" == appliance ]]; then
        local running id
        appliance_systemctl stop kautuka-manager.service || return 1
        running="$(appliance_user podman ps --no-trunc --filter label=io.kautuka.appliance.service --format '{{.ID}}')" || return 1
        while IFS= read -r id; do
            [[ -n "$id" ]] || continue
            [[ "$id" =~ ^[a-f0-9]{64}$ ]] || return 1
            appliance_user podman stop --ignore --time 10 "$id" >/dev/null || return 1
        done <<< "$running"
        running="$(appliance_user podman ps --filter label=io.kautuka.appliance.service --format '{{.ID}}')" || return 1
        [[ -z "$running" ]] || return 1
        assert_database_writers_absent
        return
    fi

    for unit in "${system_units[@]}"; do
        if systemctl is-active --quiet "$unit" 2>/dev/null; then
            systemctl stop "$unit" || return 1
        fi
    done

    service_passwd="$(getent passwd "$service_user")" || return 1
    service_home="${service_passwd#*:*:*:*:*:}"
    service_home="${service_home%%:*}"
    service_uid="$(id -u "$service_user")"
    xdg_runtime_dir="/run/user/${service_uid}"
    [[ -S "${xdg_runtime_dir}/bus" ]] || {
        echo 'error: service user systemd bus is unavailable; cannot drain Manager reconciliation' >&2
        return 1
    }
    [[ -f /etc/systemd/system/kautuka-credentialshost.service ]] || live_user_units+=(kautuka-credentialshost.service)
    for unit in "${live_user_units[@]}"; do
        active='false'
        if runuser -u "$service_user" -- env HOME="$service_home" XDG_RUNTIME_DIR="$xdg_runtime_dir" \
            DBUS_SESSION_BUS_ADDRESS="unix:path=${xdg_runtime_dir}/bus" systemctl --user is-active --quiet "$unit" 2>/dev/null; then
            active='true'
        fi
        [[ "$active" == 'true' ]] && runuser -u "$service_user" -- env HOME="$service_home" XDG_RUNTIME_DIR="$xdg_runtime_dir" \
            DBUS_SESSION_BUS_ADDRESS="unix:path=${xdg_runtime_dir}/bus" systemctl --user stop "$unit" || true
    done

    for unit in "${system_units[@]}"; do
        if systemctl is-active --quiet "$unit" 2>/dev/null; then
            echo "error: database writer remains active after stop: $unit" >&2
            return 1
        fi
        pid="$(systemctl show --property=MainPID --value "$unit" 2>/dev/null || true)"
        [[ -z "$pid" || "$pid" == '0' ]] || {
            echo "error: database writer process remains after stop: $unit (pid $pid)" >&2
            return 1
        }
    done
    for unit in "${user_units[@]}"; do
        if runuser -u "$service_user" -- env HOME="$service_home" XDG_RUNTIME_DIR="$xdg_runtime_dir" \
            DBUS_SESSION_BUS_ADDRESS="unix:path=${xdg_runtime_dir}/bus" systemctl --user is-active --quiet "$unit" 2>/dev/null; then
            echo "error: user-scope reconciliation or dispatch remains active after stop: $unit" >&2
            return 1
        fi
    done
    for unit in "${live_user_units[@]}"; do
        pid="$(runuser -u "$service_user" -- env HOME="$service_home" XDG_RUNTIME_DIR="$xdg_runtime_dir" \
            DBUS_SESSION_BUS_ADDRESS="unix:path=${xdg_runtime_dir}/bus" systemctl --user show --property=MainPID --value "$unit" 2>/dev/null || true)"
        [[ -z "$pid" || "$pid" == '0' ]] || { echo "error: database writer remains after user-service stop: $unit (pid $pid)" >&2; return 1; }
    done
    assert_database_writers_absent || return 1
}

acquire_database_writer_exclusion() {
    [[ "$database_writer_lease_active" == 'false' ]] || return 0
    [[ -f "$database_writer_lock" && ! -L "$database_writer_lock" \
        && "$(stat -c '%u' "$database_writer_lock")" == 0 \
        && -z "$(find "$database_writer_lock" -maxdepth 0 -perm /022 -print -quit)" ]] || {
        echo 'error: root-owned Kautuka database writer lock is missing or unsafe; rerun the current installer' >&2
        return 1
    }
    exec 8<>"$database_writer_lock"
    flock -x -w 30 8 || {
        exec 8>&-
        echo 'error: could not obtain exclusive Kautuka database writer lease; keeping maintenance fence' >&2
        return 1
    }
    database_writer_lease_active='true'
}

assert_database_writer_lock_ready() {
    [[ -f "$database_writer_lock" && ! -L "$database_writer_lock" \
        && "$(stat -c '%u' "$database_writer_lock")" == 0 \
        && -z "$(find "$database_writer_lock" -maxdepth 0 -perm /022 -print -quit)" ]] || {
        echo 'error: root-owned Kautuka database writer lock is missing or unsafe; rerun the current installer' >&2
        return 1
    }
}

release_database_writer_exclusion() {
    [[ "$database_writer_lease_active" == 'true' ]] || return 0
    flock -u 8
    exec 8>&-
    database_writer_lease_active='false'
}

# Boot recovery runs before service managers are available. Never trust the
# --boot-recover flag or a systemctl/mock result when deciding whether it is
# safe to replace SQLite files: inspect the actual process table instead.
assert_database_writers_absent() {
    python3 - <<'PY'
import os, sys
services = ('manager', 'gateway', 'channelhost', 'credentialshost')
prefix = '/opt/kautuka/'
found = []
for entry in os.scandir('/proc'):
    if not entry.name.isdigit():
        continue
    pid = entry.name
    try:
        exe = os.readlink(f'/proc/{pid}/exe').removesuffix(' (deleted)')
        with open(f'/proc/{pid}/cmdline', 'rb') as stream:
            command = stream.read().replace(b'\0', b' ').decode(errors='replace')
    except (FileNotFoundError, PermissionError, ProcessLookupError):
        continue
    if any(f'{prefix}{service}/' in candidate and '/versions/' in candidate
           for candidate in (exe, command) for service in services):
        found.append((pid, exe))
if found:
    for pid, exe in found:
        print(f'error: Kautuka database writer/runtime process remains before restore: pid {pid} ({exe})', file=sys.stderr)
    raise SystemExit(1)
PY
}

# These directories were owned by service accounts in older installations.
# Root must not copy into or switch entries beneath a service-writable parent.
protect_service_roots() {
    local service_key install_root versions_root
    for service_key in "${host_services[@]}"; do
        install_root="/opt/kautuka/$service_key"
        versions_root="$install_root/versions"
        [[ -d "$install_root" && ! -L "$install_root" ]] || {
            echo "error: invalid service installation root: $install_root" >&2
            return 1
        }
        chown root:root "$install_root"
        chmod go-w "$install_root"
        [[ -d "$versions_root" && ! -L "$versions_root" ]] || {
            echo "error: invalid service versions root: $versions_root" >&2
            return 1
        }
        chown root:root "$versions_root"
        chmod go-w "$versions_root"
    done
}

ensure_selinux_runtime_exec_policy() {
    local selinux_state=''
    if ! command -v getenforce >/dev/null 2>&1; then
        return 0
    fi
    selinux_state="$(getenforce 2>/dev/null || true)"
    if [[ "$selinux_state" == 'Disabled' || -z "$selinux_state" ]]; then
        return 0
    fi
    if ! command -v semanage >/dev/null 2>&1 || ! command -v restorecon >/dev/null 2>&1; then
        echo 'error: SELinux is enabled; install policycoreutils-python-utils and policycoreutils to label Kautuka service executables' >&2
        exit 1
    fi
    local rule='/opt/kautuka/[^/]+/versions/[^/]+/kautuka-[^/]+'
    if ! semanage fcontext -a -t bin_t "$rule" >/dev/null 2>&1; then
        semanage fcontext -m -t bin_t "$rule" >/dev/null 2>&1 || {
            echo 'error: could not register SELinux executable labeling rule for Kautuka' >&2
            exit 1
        }
    fi
}

stage_extract_root=''
stage_version_in_progress=''
appliance_stage_root=''

cleanup_staging() {
    [[ -z "$appliance_stage_root" ]] || rm -rf -- "$appliance_stage_root"
    [[ -z "$stage_extract_root" ]] || rm -rf -- "$stage_extract_root"
    if [[ -n "$stage_version_in_progress" ]]; then
        local service current
        # Never remove a release that any current pointer has reached.
        for service in "${host_services[@]}"; do
            current="$(readlink -f "/opt/kautuka/${service}/current" 2>/dev/null || true)"
            [[ "$current" != "/opt/kautuka/${service}/versions/${stage_version_in_progress}" ]] || return 0
        done
        for service in "${host_services[@]}"; do
            rm -rf -- "/opt/kautuka/${service}/versions/${stage_version_in_progress}"
        done
    fi
}

same_file_contents() {
    python3 -c '
from pathlib import Path
import sys
try:
    left = sys.stdin.buffer.read() if sys.argv[1] == "-" else Path(sys.argv[1]).read_bytes()
    sys.exit(left != Path(sys.argv[2]).read_bytes())
except OSError:
    sys.exit(1)
' "$1" "$2"
}

stage_appliance_version() {
    local target="$1" manager_root=/opt/kautuka/manager
    local versions="$manager_root/versions" directory role archive selinux_state
    local destination="$versions/$target"
    selinux_state=''
    if command -v getenforce >/dev/null 2>&1; then selinux_state="$(getenforce 2>/dev/null || true)"; fi
    for directory in /opt /opt/kautuka "$manager_root" "$versions"; do
        [[ -d "$directory" && ! -L "$directory" && "$(stat -c '%u' -- "$directory")" == 0 &&
            -z "$(find "$directory" -maxdepth 0 -perm /022 -print -quit)" ]] || {
            echo "error: unsafe appliance installation directory: $directory" >&2
            return 1
        }
    done
    if [[ -e "$destination" || -L "$destination" ]]; then
        [[ -d "$destination" && ! -L "$destination" ]] &&
            same_file_contents "$verified_root/manifest.json" "$destination/kautuka-platform-release.json" &&
            same_file_contents "$verified_root/signature.b64" "$destination/kautuka-platform-release.sig" || {
            echo 'error: the appliance version is already staged with different release metadata' >&2
            return 1
        }
    else
        install -d -o root -g root -m 0755 "$backups_root"
        [[ ! -L "$backups_root" && "$(stat -c '%u' "$backups_root")" == 0 &&
            -z "$(find "$backups_root" -maxdepth 0 -perm /022 -print -quit)" ]] || return 1
        check_staging_space "$verified_root/artifact.tar.gz" appliance || return 1
        stage_extract_root="$(mktemp -d /var/tmp/kautuka-appliance-stage.XXXXXXXX)"
        (umask 022
         tar --no-same-owner --no-same-permissions --no-overwrite-dir \
            -xzf "$verified_root/artifact.tar.gz" -C "$stage_extract_root")
        local payload="$stage_extract_root/bin"
        [[ -d "$payload" && ! -L "$payload" && -f "$payload/kautuka-manager" && ! -L "$payload/kautuka-manager" &&
            -x "$payload/kautuka-manager" && "$(head -c 4 -- "$payload/kautuka-manager")" == $'\x7fELF' &&
            -d "$payload/scripts" && ! -L "$payload/scripts" &&
            -f "$payload/scripts/kautuka-oci-hook" && ! -L "$payload/scripts/kautuka-oci-hook" &&
            -x "$payload/scripts/kautuka-oci-hook" && "$(head -c 4 -- "$payload/scripts/kautuka-oci-hook")" == $'\x7fELF' ]] || {
            echo 'error: appliance bundle must contain executables for Manager and the native OCI hook' >&2
            return 1
        }
        verify_nac_hook_compatibility "$payload" "$appliance_service_home" || return 1
        verify_envoy_selinux_policy_compatibility "$payload" "$manager_root/current" "$selinux_state" || return 1
        verify_envoy_selinux_policy_compatibility "$payload" "$manager_root/current" "$selinux_state" kautuka_manager_peer || return 1
        appliance_stage_root="$(mktemp -d "$versions/.stage-appliance.XXXXXXXX")"
        cp -a --no-preserve=context "$payload/." "$appliance_stage_root/"
        [[ ! -e "$appliance_stage_root/service-images" && ! -L "$appliance_stage_root/service-images" ]] || return 1
        for role in kautuka-platform-release.json kautuka-platform-release.sig release-id.txt; do
            [[ ! -L "$appliance_stage_root/$role" ]] || return 1
        done
        if [[ -e "$stage_extract_root/service-images" || -L "$stage_extract_root/service-images" ]]; then
            [[ -d "$stage_extract_root/service-images" && ! -L "$stage_extract_root/service-images" ]] || return 1
            install -d -m 0755 "$appliance_stage_root/service-images"
            for role in credentialshost channelhost gateway webhost updateservice; do
                archive="$stage_extract_root/service-images/$role.tar"
                [[ -e "$archive" || -L "$archive" ]] || continue
                [[ -f "$archive" && ! -L "$archive" && -s "$archive" ]] || return 1
                install -m 0644 -- "$archive" "$appliance_stage_root/service-images/$role.tar"
            done
        fi
        install -T -m 0644 "$verified_root/manifest.json" "$appliance_stage_root/kautuka-platform-release.json"
        install -T -m 0644 "$verified_root/signature.b64" "$appliance_stage_root/kautuka-platform-release.sig"
        printf 'v%s\n' "$target" > "$appliance_stage_root/release-id.txt"
        chmod 0644 "$appliance_stage_root/release-id.txt"
        chown -R root:root "$appliance_stage_root"
        chmod -R go-w "$appliance_stage_root"
        chmod 0755 "$appliance_stage_root"
        sync -f "$appliance_stage_root"
        mv -T --no-clobber "$appliance_stage_root" "$destination"
        [[ ! -d "$appliance_stage_root" ]] || return 1
        appliance_stage_root=''
        stage_version_in_progress="$target"
        sync -f "$versions"
    fi
    if [[ -n "$selinux_state" && "$selinux_state" != 'Disabled' ]]; then
        restorecon -RF "$destination" || return 1
    fi
    appliance_user env KAUTUKA_APPLIANCE_VERIFY=1 \
        "$destination/kautuka-manager" --prepare-appliance-images --candidate-version "$target" || return 1
    stage_version_in_progress=''
}

check_staging_space() {
    local test_free_bytes=''
    if is_disposable_test; then test_free_bytes="${KAUTUKA_TEST_FREE_BYTES:-}"; fi
    python3 - "$1" "$database_root" "$backups_root" "$recovery_tool" "$test_free_bytes" "${2:-host-service}" <<'PY'
import importlib.util, os, sys, tarfile
from pathlib import Path
archive, data_root, backup_root, recovery_path, test_free_bytes, layout = sys.argv[1:]
with tarfile.open(archive, 'r:gz') as bundle:
    payload = sum(item.size for item in bundle if item.isfile())
if payload <= 0:
    raise SystemExit('error: signed update archive contains no files')
spec = importlib.util.spec_from_file_location('kautuka_recovery', recovery_path)
recovery = importlib.util.module_from_spec(spec)
spec.loader.exec_module(recovery)
database_paths = [os.path.join(data_root, name) for name, kind in recovery.inventory(Path(data_root), layout) if kind == "database"]
databases = []
for path in database_paths:
    for suffix in ('', '-wal', '-shm'):
        try: databases.append(os.stat(path + suffix, follow_symlinks=False).st_size)
        except FileNotFoundError: pass
needs = {}
for path, amount in (('/var/tmp', payload), ('/opt/kautuka', payload), (backup_root, 2 * sum(databases))):
    stat = os.stat(path)
    entry = needs.setdefault(stat.st_dev, [path, 256 * 1024 * 1024])
    entry[1] += amount
for path, required in needs.values():
    fs = os.statvfs(path)
    available = int(test_free_bytes) if test_free_bytes else fs.f_bavail * fs.f_frsize
    if available < required:
        raise SystemExit(f'error: insufficient free space for safe update staging at {path}: need {required} bytes, have {available} bytes')
PY
}

verify_envoy_selinux_policy_compatibility() {
    local candidate="$1" current="$2" selinux_state="$3" policy="${4:-kautuka_envoy}"
    case "$selinux_state" in Enforcing|Permissive) ;; *) return 0 ;; esac
    if [[ ! -f "$candidate/scripts/$policy.cil" || ! -f "$current/scripts/$policy.cil" ]] ||
       ! same_file_contents "$candidate/scripts/$policy.cil" "$current/scripts/$policy.cil"; then
        echo "error: the $policy SELinux policy differs from this release; run the current signed Linux installer before updating." >&2
        return 1
    fi
    local modules loaded expected
    modules="$(semodule -lfull -m)" || { echo "error: cannot inspect the loaded $policy SELinux policy." >&2; return 1; }
    loaded="$(printf '%s\n' "$modules" | awk -v policy="$policy" '$2 == policy && $3 == "cil" && $1 ~ /^[0-9]+$/ { if ($1 > priority) { priority=$1; checksum=$4; disabled=($0 ~ /disabled/) } } END { if (!disabled) print checksum }')"
    expected="sha256:$(sha256sum "$current/scripts/$policy.cil" | cut -d' ' -f1)"
    if [[ "$loaded" != "$expected" ]]; then
        echo "error: the loaded $policy SELinux policy differs from the installed release; run the current signed Linux installer before updating." >&2
        return 1
    fi
}

verify_nac_hook_compatibility() {
    local candidate="$1" service_home="$2"
    local hooks_dir="$service_home/.config/containers/hooks.d" file
    local manager_current="${3:-/opt/kautuka/manager/current}"
    # Native installations point at the versioned Manager payload. The update
    # transaction switches that stable pointer only after this preflight passes.
    if python3 - "$hooks_dir/kautuka_hook.json" "$manager_current/scripts/kautuka-oci-hook" <<'PY'
import json, sys
try:
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result: raise ValueError('duplicate property')
            result[key] = value
        return result
    with open(sys.argv[1], encoding='utf-8') as source:
        hook = json.load(source, object_pairs_hook=unique)
    valid = (hook == {'version': '1.0.0', 'hook': {'path': sys.argv[2], 'timeout': 45},
                      'when': {'always': True}, 'stages': ['createRuntime']}
             and type(hook['hook']['timeout']) is int and hook['when']['always'] is True)
except (OSError, ValueError, TypeError, KeyError):
    valid = False
sys.exit(0 if valid else 1)
PY
    then
        local manager_root resolved active candidate_hook checked mode owner
        manager_root="$(dirname "$manager_current")"
        [[ -L "$manager_current" && "$(stat -c '%u' "$manager_current")" == 0 ]] || {
            echo 'error: native NAC hook Manager pointer is not trusted.' >&2; return 1;
        }
        resolved="$(readlink -f "$manager_current")" || return 1
        [[ "$resolved" == "$manager_root/versions/"* && "${resolved#"$manager_root/versions/"}" != */* ]] || {
            echo 'error: native NAC hook Manager pointer is outside its version directory.' >&2; return 1;
        }
        active="$resolved/scripts/kautuka-oci-hook"
        candidate_hook="$candidate/scripts/kautuka-oci-hook"
        for checked in "$(dirname "$manager_root")" "$manager_root" "$manager_root/versions" "$resolved" "$resolved/scripts" "$active"; do
            [[ ! -L "$checked" && -e "$checked" ]] || { echo 'error: native NAC hook path is missing or symbolic.' >&2; return 1; }
            owner="$(stat -c '%u' "$checked")" || return 1
            mode="$(stat -c '%a' "$checked")" || return 1
            [[ "$owner" == 0 && "$mode" =~ ^[0-7]{3,4}$ ]] && (( (8#$mode & 8#022) == 0 )) || {
                echo 'error: native NAC hook Manager path is not root-owned and protected.' >&2; return 1;
            }
        done
        for checked in "$active" "$candidate_hook"; do
            [[ -f "$checked" && ! -L "$checked" && -x "$checked" ]] || {
                echo 'error: native NAC hook executable is missing or not executable.' >&2; return 1;
            }
        done
        [[ ! -L "$candidate/scripts" ]] || { echo 'error: native NAC hook candidate scripts directory is symbolic.' >&2; return 1; }
        if ! python3 - "$active" "$candidate_hook" <<'PY'
import os, platform, struct, sys
machine = {'x86_64': 62, 'aarch64': 183}.get(platform.machine())
try:
    if machine is None: raise ValueError('unsupported architecture')
    for path in sys.argv[1:]:
        with open(path, 'rb') as source:
            header = source.read(64)
            if (len(header) != 64 or header[:4] != b'\x7fELF' or header[4:7] != b'\x02\x01\x01'
                or struct.unpack_from('<H', header, 16)[0] not in (2, 3)
                or struct.unpack_from('<H', header, 18)[0] != machine
                or struct.unpack_from('<I', header, 20)[0] != 1
                or struct.unpack_from('<H', header, 52)[0] != 64
                or struct.unpack_from('<H', header, 54)[0] != 56):
                raise ValueError('invalid ELF header')
            offset = struct.unpack_from('<Q', header, 32)[0]
            count = struct.unpack_from('<H', header, 56)[0]
            if count == 0 or offset < 64 or offset + count * 56 > os.fstat(source.fileno()).st_size:
                raise ValueError('invalid ELF program headers')
            source.seek(offset)
            program_headers = source.read(count * 56)
            if not any(struct.unpack_from('<I', program_headers, i * 56)[0] == 1
                       and struct.unpack_from('<I', program_headers, i * 56 + 4)[0] & 1
                       for i in range(count)):
                raise ValueError('missing executable ELF segment')
except (OSError, ValueError, struct.error):
    sys.exit(1)
PY
        then
            echo 'error: native NAC hook is not a supported ELF executable.' >&2; return 1
        fi
        if ! printf '[engine]\nhooks_dir = ["%s", { append = true }]\n' "$hooks_dir" | same_file_contents - "$service_home/.config/containers/containers.conf.d/99-kautuka-hooks.conf"; then
            echo 'error: NAC hook configuration is missing or incompatible; run the current signed Linux installer before updating.' >&2
            return 1
        fi
        return 0
    fi
    for file in kautuka_hook.py kautuka_workload.nft.json.tmpl kautuka_package_proxy.nft.json.tmpl; do
        if [[ ! -f "$candidate/scripts/$file" || ! -f "$hooks_dir/$file" ]] || ! same_file_contents "$candidate/scripts/$file" "$hooks_dir/$file"; then
            echo 'error: the installed NAC hook differs from this release; run the current signed Linux installer before updating.' >&2
            return 1
        fi
    done
    if ! printf '[engine]\nhooks_dir = ["%s", { append = true }]\n' "$hooks_dir" | same_file_contents - "$service_home/.config/containers/containers.conf.d/99-kautuka-hooks.conf"; then
        echo 'error: NAC hook configuration is missing or incompatible; run the current signed Linux installer before updating.' >&2
        return 1
    fi
    if ! python3 - "$hooks_dir/kautuka_hook.json" "$hooks_dir/kautuka_hook.py" <<'PY'
import json, sys
try:
    with open(sys.argv[1], encoding='utf-8') as source:
        hook = json.load(source)
    valid = (hook.get('version') == '1.0.0' and hook.get('hook', {}).get('path') == sys.argv[2]
             and hook.get('hook', {}).get('timeout') == 45 and hook.get('when', {}).get('always') is True
             and 'createRuntime' in hook.get('stages', []))
except (OSError, ValueError, TypeError, AttributeError):
    valid = False
sys.exit(0 if valid else 1)
PY
    then
        echo 'error: NAC hook registration is missing or incompatible; run the current signed Linux installer before updating.' >&2
        return 1
    fi
}

stage_version_roots() {
    local artifact_path="$1"
    local target_version="$2"
    local extract_root
    check_staging_space "$artifact_path" || return 1
    stage_version_in_progress="$target_version"
    stage_extract_root="$(mktemp -d /var/tmp/kautuka-apply-XXXXXX)"
    extract_root="$stage_extract_root"
    tar --no-same-owner --no-same-permissions --no-overwrite-dir -xzf "$artifact_path" -C "$extract_root"

    ensure_selinux_runtime_exec_policy

    local manager_source="$extract_root"
    if [[ -d "$extract_root/bin" ]]; then
        manager_source="$extract_root/bin"
    fi

    # Preserve the next helper directly from the verified, root-private
    # extraction. Do not read it back from a service-writable version root.
    [[ -f "$manager_source/kautuka-apply-release" \
        && ! -L "$manager_source/kautuka-apply-release" \
        && "$(head -n 2 "$manager_source/kautuka-apply-release" | tail -n 1)" == '# Kautuka-Apply-Protocol: 6' ]] || {
        echo 'error: signed target bundle lacks the required safe-state-recovery-v6 apply/admission protocol' >&2
        exit 1
    }
    grep -Fq 'safe-state-recovery-v6' "$manager_source/kautuka-apply-release" || {
        echo 'error: signed target bundle lacks the required safe-state-recovery-v6 apply/admission protocol' >&2
        exit 1
    }
    local nac_service_home
    nac_service_home="$(getent passwd "$service_user" | cut -d: -f6)"
    [[ -n "$nac_service_home" ]] || { echo 'error: Kautuka service home is unavailable; run the current signed Linux installer.' >&2; return 1; }
    verify_nac_hook_compatibility "$manager_source" "$nac_service_home" || return 1
    local selinux_state=''
    if command -v getenforce >/dev/null 2>&1; then selinux_state="$(getenforce 2>/dev/null || true)"; fi
    verify_envoy_selinux_policy_compatibility "$manager_source" /opt/kautuka/manager/current "$selinux_state" || return 1

    [[ -f "$extract_root/services/webhost/.maintenance-page-v1" \
        && ! -L "$extract_root/services/webhost/.maintenance-page-v1" \
        && "$(cat "$extract_root/services/webhost/.maintenance-page-v1")" == 'kautuka-maintenance-page-v1' ]] || {
        echo 'error: signed target bundle cannot serve the maintenance page' >&2
        return 1
    }
    cp -- "$manager_source/kautuka-apply-release" "$verified_root/next-helper"

    install_version_root manager "$manager_source" "$target_version" kautuka-manager
    if is_disposable_test && [[ "${KAUTUKA_TEST_STAGE_FAIL_AFTER_MANAGER:-0}" == 1 ]]; then
        [[ "$(stat -c '%u:%a' "$extract_root")" == '0:700' ]] || {
            echo 'error: extracted staging root lost its private ownership or permissions' >&2
            return 1
        }
        echo 'error: injected failure after staging Manager' >&2
        return 1
    fi
    install_version_root gateway "$extract_root/services/gateway" "$target_version" kautuka-gateway
    install_version_root credentialshost "$extract_root/services/credentialshost" "$target_version" kautuka-credentialshost
    install_version_root channelhost "$extract_root/services/channelhost" "$target_version" kautuka-channelhost
    install_version_root updateservice "$extract_root/services/updateservice" "$target_version" kautuka-updateservice
    install_version_root webhost "$extract_root/services/webhost" "$target_version" kautuka-webhost
    rm -rf -- "$stage_extract_root"
    stage_extract_root=''
    stage_version_in_progress=''
}

install_version_root() {
    local service_key="$1"
    local source_root="$2"
    local target_version="$3"
    local executable_name="$4"
    local version_root="/opt/kautuka/${service_key}/versions/${target_version}"

    [[ -d "$source_root" ]] || {
        echo "error: payload missing for ${service_key}: $source_root" >&2
        exit 1
    }

    [[ -f "$source_root/$executable_name" ]] || {
        echo "error: payload missing executable for ${service_key}: $source_root/$executable_name" >&2
        exit 1
    }

    rm -rf "$version_root"
    mkdir -p "$version_root"
    cp -a --no-preserve=context "$source_root/." "$version_root/"
    chown -R root:root "$version_root"
    chmod -R go-w "$version_root"
    local selinux_state=''
    if command -v getenforce >/dev/null 2>&1; then
        selinux_state="$(getenforce 2>/dev/null || true)"
    fi
    if command -v restorecon >/dev/null 2>&1 && [[ "$selinux_state" != 'Disabled' ]]; then
        restorecon -RF "$version_root"
    fi
    if [[ -f "$version_root/$executable_name" ]]; then
        chmod +x "$version_root/$executable_name"
    fi
}

retarget_current() {
    local target_version="$1"
    local service_key
    local version_root
    local current_link
    local next_link
    local resolved
    local previous_trap
    previous_trap="$(trap -p HUP TERM INT || true)"
    # Ignore HUP/TERM/INT while flipping pointers so a timed-out UpdateService kill
    # cannot leave a half-applied current set. Exit 129 observed live was SIGHUP.
    trap '' HUP TERM INT

    for service_key in "${host_services[@]}"; do
        version_root="/opt/kautuka/${service_key}/versions/${target_version}"
        next_link="/opt/kautuka/${service_key}/current.next"
        [[ -d "$version_root" ]] || {
            eval "${previous_trap:-trap - HUP TERM INT}"
            echo "error: missing version directory: $version_root" >&2
            return 1
        }
        ln -sfn "$version_root" "$next_link" || {
            eval "${previous_trap:-trap - HUP TERM INT}"
            return 1
        }
    done

    for service_key in "${host_services[@]}"; do
        current_link="/opt/kautuka/${service_key}/current"
        next_link="/opt/kautuka/${service_key}/current.next"
        mv -Tf "$next_link" "$current_link" || {
            eval "${previous_trap:-trap - HUP TERM INT}"
            return 1
        }
    done

    for service_key in "${host_services[@]}"; do
        current_link="/opt/kautuka/${service_key}/current"
        resolved="$(basename "$(readlink -f "$current_link")")"
        if [[ "$resolved" != "$target_version" ]]; then
            eval "${previous_trap:-trap - HUP TERM INT}"
            echo "error: current pointer mismatch for ${service_key}: expected ${target_version} got ${resolved}" >&2
            return 1
        fi
    done

    eval "${previous_trap:-trap - HUP TERM INT}"
    if [[ "$recovery_layout" == appliance ]]; then sync -f /opt/kautuka/manager; fi
}

start_units() {
    local service_home
    local service_uid
    local xdg_runtime_dir
    local unit
    local live_system_units=()
    local live_user_units=("${user_units[@]}")

    for unit in "${system_units[@]}"; do
        if [[ -f "/etc/systemd/system/${unit}" ]]; then
            live_system_units+=("$unit")
        elif [[ "$unit" == 'kautuka-credentialshost.service' ]]; then
            live_user_units+=("$unit")
        fi
    done

    # Prefer getent field splitting over awk — the appliance rootfs intentionally
    # omits gawk to keep the dependency surface small.
    service_passwd="$(getent passwd "$service_user")" || return 1
    service_home="${service_passwd#*:*:*:*:*:}"
    service_home="${service_home%%:*}"
    [[ -n "$service_home" ]] || return 1
    service_uid="$(id -u "$service_user")"
    xdg_runtime_dir="/run/user/${service_uid}"

    systemctl daemon-reload || return 1

    # System-scoped systemd-run apply can race a missing user bus after manager
    # restarts. Bring the linger session back before touching user units.
    if [[ ! -S "${xdg_runtime_dir}/bus" ]]; then
        loginctl enable-linger "$service_user" 2>/dev/null || true
        systemctl reset-failed "user@${service_uid}.service" 2>/dev/null || true
        systemctl start "user-runtime-dir@${service_uid}.service" 2>/dev/null || true
        systemctl start "user@${service_uid}.service" 2>/dev/null || true
        local wait_i=0
        while (( wait_i < 45 )); do
            [[ -S "${xdg_runtime_dir}/bus" ]] && break
            wait_i=$((wait_i + 1))
            sleep 1
        done
    fi

    if (( ${#live_system_units[@]} > 0 )); then
        systemctl enable "${live_system_units[@]}" >/dev/null || return 1
        # Stop dependents before restarting dependencies. A plain restart of
        # channelhost while gateway is running cancels the gateway job and
        # cascades into "Job for … canceled" (live exit 143).
        # CredentialsHost must be stopped too: `systemctl start` on an
        # already-active unit is a no-op, so Fedora apply 48→49 left that
        # process on the 46 inode while `current` pointed at 49.
        if systemctl list-unit-files kautuka-gateway.service >/dev/null 2>&1; then
            systemctl stop kautuka-gateway.service 2>/dev/null || true
        fi
        if systemctl list-unit-files kautuka-channelhost.service >/dev/null 2>&1; then
            systemctl stop kautuka-channelhost.service 2>/dev/null || true
        fi
        if systemctl list-unit-files kautuka-credentialshost.service >/dev/null 2>&1; then
            systemctl stop kautuka-credentialshost.service 2>/dev/null || true
        fi
        for unit in "${live_system_units[@]}"; do
            local attempt=0
            while true; do
                if systemctl start "$unit" 2>/dev/null || systemctl restart "$unit"; then
                    systemctl is-active --quiet "$unit" && break
                fi
                attempt=$((attempt + 1))
                if (( attempt >= 3 )); then
                    return 1
                fi
                systemctl reset-failed "$unit" 2>/dev/null || true
                sleep "$attempt"
            done
        done
    fi

    if [[ -d "$xdg_runtime_dir" ]]; then
        runuser -u "$service_user" -- env \
            HOME="$service_home" \
            XDG_RUNTIME_DIR="$xdg_runtime_dir" \
            DBUS_SESSION_BUS_ADDRESS="unix:path=${xdg_runtime_dir}/bus" \
            systemctl --user daemon-reload || return 1
        for unit in "${live_user_units[@]}"; do
            local attempt=0
            while true; do
                if runuser -u "$service_user" -- env \
                    HOME="$service_home" \
                    XDG_RUNTIME_DIR="$xdg_runtime_dir" \
                    DBUS_SESSION_BUS_ADDRESS="unix:path=${xdg_runtime_dir}/bus" \
                    systemctl --user restart "$unit"; then
                    break
                fi
                attempt=$((attempt + 1))
                if (( attempt >= 3 )); then
                    return 1
                fi
                runuser -u "$service_user" -- env \
                    HOME="$service_home" \
                    XDG_RUNTIME_DIR="$xdg_runtime_dir" \
                    DBUS_SESSION_BUS_ADDRESS="unix:path=${xdg_runtime_dir}/bus" \
                    systemctl --user reset-failed "$unit" 2>/dev/null || true
                sleep "$attempt"
            done
        done
    fi

    for unit in "${live_system_units[@]}"; do
        systemctl is-active --quiet "$unit" || {
            echo "error: system unit is not active: $unit" >&2
            return 1
        }
    done
    if [[ -d "$xdg_runtime_dir" ]]; then
        for unit in "${live_user_units[@]}"; do
            runuser -u "$service_user" -- env \
                HOME="$service_home" \
                XDG_RUNTIME_DIR="$xdg_runtime_dir" \
                DBUS_SESSION_BUS_ADDRESS="unix:path=${xdg_runtime_dir}/bus" \
                systemctl --user is-active --quiet "$unit" || {
                    echo "error: user unit is not active: $unit" >&2
                    return 1
                }
        done
    fi

    # Leave the existing WebHost serving the maintenance page while the core
    # stack is stopped. Once Gateway and Manager are ready, switch it to the
    # selected version only when its running executable differs.
    if ! is_disposable_test; then
        check_gateway_health || return 1
        local web_pid web_executable web_expected
        web_pid="$(runuser -u "$service_user" -- env HOME="$service_home" XDG_RUNTIME_DIR="$xdg_runtime_dir" \
            DBUS_SESSION_BUS_ADDRESS="unix:path=${xdg_runtime_dir}/bus" systemctl --user show --property=MainPID --value kautuka-webhost.service 2>/dev/null || true)"
        web_executable="$(readlink -f "/proc/${web_pid}/exe" 2>/dev/null || true)"
        web_expected="$(readlink -f /opt/kautuka/webhost/current/kautuka-webhost)" || return 1
        if [[ "$web_executable" != "$web_expected" ]]; then
            runuser -u "$service_user" -- env HOME="$service_home" XDG_RUNTIME_DIR="$xdg_runtime_dir" \
                DBUS_SESSION_BUS_ADDRESS="unix:path=${xdg_runtime_dir}/bus" systemctl --user restart kautuka-webhost.service || return 1
        fi
    fi
}

check_gateway_health() {
    if is_disposable_test; then
        [[ "${KAUTUKA_TEST_HEALTH_OK:-1}" == '1' ]]
        return
    fi
    local attempt
    for attempt in {1..90}; do
        if curl --fail --silent --show-error --max-time 2 \
            --unix-socket /run/kautuka/gateway/gateway.sock http://localhost/api/runtime/system-health 2>/dev/null \
            | python3 -c 'import json,sys; d=json.load(sys.stdin); sys.exit(0 if (d.get("lifecycle") or {}).get("coreReady") is True and (d.get("workspace") or {}).get("status") == "healthy" else 1)' 2>/dev/null; then
            return 0
        fi
        sleep 1
    done
    echo 'error: Gateway and Manager core runtime did not become ready' >&2
    return 1
}

verify_runtime_health() {
    local version="$1" service_key current_exe pid unit
    for service_key in "${host_services[@]}"; do
        [[ "$(basename "$(readlink -f "/opt/kautuka/${service_key}/current")")" == "$version" ]] || {
            echo "error: $service_key current pointer does not target $version" >&2
            return 1
        }
    done
    for unit in "${system_units[@]}"; do
        systemctl is-active --quiet "$unit" || return 1
        is_disposable_test && continue
        pid="$(systemctl show --property=MainPID --value "$unit" 2>/dev/null || true)"
        [[ "$pid" =~ ^[1-9][0-9]*$ ]] || { echo "error: service has no live process: $unit" >&2; return 1; }
        current_exe="$(readlink -f "/proc/${pid}/exe" 2>/dev/null || true)"
        [[ "$current_exe" == "/opt/kautuka/$(case "$unit" in kautuka-gateway*) echo gateway;; kautuka-channelhost*) echo channelhost;; *) echo credentialshost;; esac)/versions/${version}/"* ]] || {
            echo "error: running process for $unit is not from release $version" >&2
            return 1
        }
    done
    if ! is_disposable_test; then
        local service_uid service_home xdg_runtime_dir manager_pid update_pid web_pid
        service_uid="$(id -u "$service_user")"
        service_home="$(getent passwd "$service_user" | cut -d: -f6)"
        xdg_runtime_dir="/run/user/${service_uid}"
        for unit in kautuka-manager.service kautuka-webhost.service; do
            runuser -u "$service_user" -- env HOME="$service_home" XDG_RUNTIME_DIR="$xdg_runtime_dir" \
                DBUS_SESSION_BUS_ADDRESS="unix:path=${xdg_runtime_dir}/bus" systemctl --user is-active --quiet "$unit" || return 1
            pid="$(runuser -u "$service_user" -- env HOME="$service_home" XDG_RUNTIME_DIR="$xdg_runtime_dir" \
                DBUS_SESSION_BUS_ADDRESS="unix:path=${xdg_runtime_dir}/bus" systemctl --user show --property=MainPID --value "$unit")"
            current_exe="$(readlink -f "/proc/${pid}/exe" 2>/dev/null || true)"
            service_key="${unit#kautuka-}"; service_key="${service_key%.service}"; [[ "$service_key" == 'manager' ]] || service_key="$service_key"
            [[ "$current_exe" == "/opt/kautuka/${service_key}/versions/${version}/"* ]] || { echo "error: running process for $unit is not from release $version" >&2; return 1; }
        done
    fi
    check_gateway_health
}

verify_updater_version() {
    is_disposable_test && return 0
    local service_uid service_home xdg_runtime_dir pid executable
    service_uid="$(id -u "$service_user")"
    service_home="$(getent passwd "$service_user" | cut -d: -f6)"
    xdg_runtime_dir="/run/user/${service_uid}"
    runuser -u "$service_user" -- env HOME="$service_home" XDG_RUNTIME_DIR="$xdg_runtime_dir" \
        DBUS_SESSION_BUS_ADDRESS="unix:path=${xdg_runtime_dir}/bus" systemctl --user is-active --quiet kautuka-updateservice.service || return 1
    pid="$(runuser -u "$service_user" -- env HOME="$service_home" XDG_RUNTIME_DIR="$xdg_runtime_dir" \
        DBUS_SESSION_BUS_ADDRESS="unix:path=${xdg_runtime_dir}/bus" systemctl --user show --property=MainPID --value kautuka-updateservice.service)"
    executable="$(readlink -f "/proc/${pid}/exe" 2>/dev/null || true)"
    [[ "$executable" == "/opt/kautuka/updateservice/versions/$(read_journal_field targetRelease)/kautuka-updateservice" ]]
}

wait_for_manager_desired_state() {
    if is_disposable_test; then
        return 0
    fi
    local desired_state_path="$database_root/manager/manager-desired-state.json"
    local attempt
    for attempt in {1..90}; do
        if python3 - "$desired_state_path" "$maintenance_path" <<'PY'
import json, os, stat, sys
state_path, fence_path = sys.argv[1:]
try:
    state = os.lstat(state_path)
    fence = os.stat(fence_path)
    if not stat.S_ISREG(state.st_mode) or state.st_mtime_ns <= fence.st_mtime_ns:
        raise SystemExit(1)
    with open(state_path, encoding='utf-8') as stream:
        document = json.load(stream)
    schema = document.get('schemaVersion', document.get('SchemaVersion'))
    accepted = document.get('acceptedVersion', document.get('AcceptedVersion'))
    runtime = document.get('runtimeSettings', document.get('RuntimeSettings'))
    if schema != 1 or not isinstance(accepted, int) or accepted <= 0 or not isinstance(runtime, dict):
        raise SystemExit(1)
except (OSError, ValueError, json.JSONDecodeError):
    raise SystemExit(1)
PY
        then
            return 0
        fi
        sleep 1
    done
    echo 'error: Gateway did not regenerate Manager desired state while the maintenance fence was active' >&2
    return 1
}

clear_maintenance_fence() {
    rm -f "$maintenance_path"
    sync -f "$recovery_root"
}

restore_operation() {
    local boot_only="${1:-false}"
    local operation_id source_release target_release snapshot phase snapshot_manifest_sha256
    [[ -f "$journal_path" ]] || { echo 'error: recovery journal is missing' >&2; return 1; }
    operation_id="$(read_journal_field operationId)"
    source_release="$(read_journal_field sourceRelease)"
    target_release="$(read_journal_field targetRelease)"
    snapshot="$(read_journal_field snapshot)"
    snapshot_manifest_sha256="$(python3 - "$journal_path" <<'PY'
import json, sys
with open(sys.argv[1], encoding='utf-8') as stream:
    print(json.load(stream).get('snapshotManifestSha256', ''))
PY
)"
    phase="$(read_journal_field phase)"
    require_version_token "$source_release"
    require_version_token "$target_release"
    [[ "$operation_id" =~ ^[A-Fa-f0-9]{32}$ ]] || { echo 'error: recovery operation id is invalid' >&2; return 1; }
    validate_journal_source_pointers "$source_release" || return 1
    [[ "$snapshot" == "$backups_root/$operation_id" && -d "$snapshot" && ! -L "$snapshot" ]] || {
        if [[ "$phase" == 'capturing' && ! -d "$snapshot" ]]; then :; else
            echo 'error: recovery snapshot path does not match the recorded operation' >&2
            return 1
        fi
    }
    [[ "$phase" != 'committed' ]] || { echo 'error: committed updates are repaired by rolling forward and cannot be restored' >&2; return 1; }
    if [[ "$phase" == 'recovered' || "$phase" == 'completed' || "$phase" == 'aborted' ]]; then
        echo "Recovery journal is already at terminal-safe phase '$phase'; refusing to replay its database snapshot."
        return 0
    fi
    local service_key current_pointer
    for service_key in "${host_services[@]}"; do
        current_pointer="$(readlink -f "/opt/kautuka/${service_key}/current")" || {
            echo "error: cannot resolve current pointer while recovering $service_key" >&2
            return 1
        }
        [[ "$current_pointer" == "/opt/kautuka/${service_key}/versions/${source_release}" \
            || "$current_pointer" == "/opt/kautuka/${service_key}/versions/${target_release}" ]] || {
            echo "error: $service_key current pointer is outside the recorded source/target operation; keeping maintenance fence" >&2
            return 1
        }
    done
    if [[ "$phase" == 'capturing' && ! -d "$snapshot" ]]; then
        # The target was never pointed at and no state migrations could have run.
        # Verify every pointer is still the exact source before reopening service.
        local key
        for key in "${host_services[@]}"; do
            [[ "$(readlink -f "/opt/kautuka/${key}/current")" == "/opt/kautuka/${key}/versions/${source_release}" ]] || {
                echo 'error: incomplete pre-snapshot operation changed release pointers; keeping maintenance fence' >&2
                return 1
            }
        done
        rm -rf "$backups_root/.candidate-${operation_id}"
        set_operation_phase aborted
        if [[ "$boot_only" != 'true' && "$recovery_layout" != appliance ]]; then
            start_units || return 1
        fi
        clear_maintenance_fence
        return
    fi
    if [[ "$boot_only" == 'true' ]]; then
        assert_database_writers_absent || return 1
    else
        stop_database_writers || return 1
    fi
    acquire_database_writer_exclusion || return 1
    [[ "$snapshot_manifest_sha256" =~ ^[a-f0-9]{64}$ ]] || { echo 'error: recovery journal lacks an immutable snapshot manifest binding' >&2; return 1; }
    python3 "$recovery_tool" verify --layout "$recovery_layout" --data-root "$database_root" --snapshot "$snapshot" \
        --operation "$operation_id" --source "$source_release" --target "$target_release" \
        --manifest-sha256 "$snapshot_manifest_sha256" || return 1
    set_operation_phase restoring || return 1
    python3 "$recovery_tool" restore --layout "$recovery_layout" --data-root "$database_root" --snapshot "$snapshot" \
        --operation "$operation_id" --source "$source_release" --target "$target_release" \
        --manifest-sha256 "$snapshot_manifest_sha256" \
        --lock-journal "$recovery_root/restore-metadata.json" || return 1
    retarget_current "$source_release" || return 1
    # Regenerate desired runtime state from the restored Gateway database before
    # Manager is allowed to reconcile. This file is generated state, not a user file store.
    rm -f "$database_root/manager/manager-desired-state.json"
    set_operation_phase recovered || return 1
    release_database_writer_exclusion
    if [[ "$recovery_layout" == appliance ]]; then
        return 0
    fi
    if [[ "$boot_only" == 'true' ]]; then
        echo "Restored verified pre-update snapshot for operation $operation_id before application services started; maintenance remains active."
        return 0
    fi
    start_units || return 1
    wait_for_manager_desired_state || return 1
    verify_runtime_health "$source_release" || return 1
    echo "Restored verified pre-update state for operation $operation_id; maintenance fence remains until UpdateService completes recovery."
}

configure_appliance_layout() {
    local scope=/etc/kautuka/service-scope.json
    [[ -f "$scope" && ! -L "$scope" && "$(stat -c '%u' "$scope")" == 0 &&
        -z "$(find "$scope" -maxdepth 0 -perm /022 -print -quit)" ]] || return 1
    python3 - "$scope" <<'PY' || return 1
import json, sys
with open(sys.argv[1], encoding='utf-8') as f:
    scope = json.load(f)
if (scope.get('schemaVersion') != 2 or scope.get('username') != 'kautuka'
    or scope.get('userUnits') != ['kautuka-manager.service'] or scope.get('systemUnits') != []):
    raise SystemExit(1)
PY
    appliance_service_home="$(getent passwd "$service_user" | cut -d: -f6)"
    [[ "$appliance_service_home" == "$database_root" ]] || return 1
    appliance_xdg_runtime_dir="/run/user/$(id -u "$service_user")"
    host_services=(manager)
    system_units=()
    recovery_layout=appliance
}

appliance_user() {
    runuser -u "$service_user" -- env HOME="$appliance_service_home" XDG_RUNTIME_DIR="$appliance_xdg_runtime_dir" \
        DBUS_SESSION_BUS_ADDRESS="unix:path=$appliance_xdg_runtime_dir/bus" "$@"
}

appliance_systemctl() {
    appliance_user systemctl --user "$@"
}

channel_certificate_status() {
    local manager=/opt/kautuka/manager/current/kautuka-manager
    [[ -x "$manager" && "$(stat -c '%u:%a' "$database_root")" == '0:1771' &&
        "$(realpath -e "$database_root/security/channel")" == "$database_root/security/channel" &&
        "$(stat -c '%u:%a' "$database_root/security")" == '0:711' &&
        "$(stat -c '%u:%a' "$database_root/security/channel")" == '0:711' ]] || return 1
    "$manager" --maintain-channel-certificates check
}

renew_appliance_channel_certificates() {
    local boot_only="$1" status=0
    local manager=/opt/kautuka/manager/current/kautuka-manager
    local security="$database_root/security"
    local current="$security/channel" candidate="$security/.channel-renewal"
    local restart="$security/.channel-renewal-restart" role material selected_release
    [[ "$(realpath -e "$security")" == "$security" && "$(stat -c '%u:%a' "$security")" == '0:711' &&
        "$(realpath -e "$current")" == "$current" && "$(stat -c '%u:%a' "$current")" == '0:711' &&
        "$(realpath -e "$current/offline-authority")" == "$current/offline-authority" &&
        "$(stat -c '%u:%a' "$current/offline-authority")" == '0:700' ]] || return 1
    channel_certificate_status || status=$?
    if [[ "$status" == 10 || "$status" == 11 || "$status" == 12 ]]; then
        if [[ -e "$candidate" || -L "$candidate" ]]; then
            [[ ! -L "$candidate" && "$(stat -c '%u' "$candidate")" == 0 ]] || return 1
            rm -rf -- "$candidate" || return 1
        fi
        install -d -o root -g root -m 0700 "$candidate" "$candidate/offline-authority" || return 1
        for material in channel-ca.key channel-ca.cer; do
            [[ -f "$current/offline-authority/$material" && ! -L "$current/offline-authority/$material" &&
                "$(stat -c '%u:%a' "$current/offline-authority/$material")" == '0:600' ]] || return 1
            ln -- "$current/offline-authority/$material" "$candidate/offline-authority/$material" || return 1
        done
        "$manager" --maintain-channel-certificates stage || return 1
        for role in gateway-private gateway-trust channel-private channel-trust; do
            find "$candidate/$role" -maxdepth 1 -type f -exec chmod 0600 {} + || return 1
            chown -R -- "$service_user:$(id -gn "$service_user")" "$candidate/$role" || return 1
        done
        sync -f "$candidate" || return 1
        if [[ "$boot_only" != 'true' ]]; then
            if [[ ! -e "$restart" ]]; then
                (umask 077; set -o noclobber; : > "$restart") || return 1
                sync -f "$security" || return 1
            fi
            stop_database_writers || return 1
        fi
        chmod 0711 "$candidate" || return 1
        "$manager" --maintain-channel-certificates publish || return 1
        chmod 0700 "$candidate" || return 1
        sync -f "$security" || return 1
        echo 'Renewed the ChannelHost server and Gateway client and delegation certificates.'
    elif [[ "$status" != 0 ]]; then
        return 1
    fi
    if [[ "$boot_only" != 'true' && -f "$restart" ]]; then
        selected_release="$(readlink -f /opt/kautuka/manager/current)" || return 1
        start_appliance_manager normal && wait_for_appliance_release "${selected_release##*/}" normal || return 1
    fi
    if [[ -e "$candidate" || -L "$candidate" ]]; then
        [[ ! -L "$candidate" && "$(stat -c '%u' "$candidate")" == 0 ]] || return 1
        rm -rf -- "$candidate" || return 1
    fi
    if [[ -f "$restart" ]]; then
        rm -- "$restart" || return 1
        sync -f "$security" || return 1
    fi
}

start_appliance_manager() {
    if [[ "$1" == verifying ]]; then
        appliance_systemctl set-environment KAUTUKA_APPLIANCE_VERIFY=1 || return 1
    else
        appliance_systemctl unset-environment KAUTUKA_APPLIANCE_VERIFY || return 1
    fi
    appliance_systemctl reset-failed kautuka-manager.service 2>/dev/null || true
    appliance_systemctl start kautuka-manager.service
}

wait_for_appliance_release() {
    local expected="$1" mode="$2" probe="${3:-/opt/kautuka/manager/current/kautuka-manager}" attempt
    local verify_env=''
    if [[ "$mode" == verifying ]]; then verify_env=1; fi
    for ((attempt = 0; attempt < 30; attempt++)); do
        if appliance_user env KAUTUKA_APPLIANCE_VERIFY="$verify_env" "$probe" \
            --check-appliance-ready --candidate-version "$expected" >/dev/null 2>&1; then
            return 0
        fi
        sleep 2
    done
    echo "error: Manager did not report release $expected ready in $mode mode" >&2
    return 1
}

retain_appliance_snapshot() {
    local snapshot operation_id link
    snapshot="$(read_journal_field snapshot)" || return 1
    operation_id="$(read_journal_field operationId)" || return 1
    link="$backups_root/.last-known-good-$operation_id"
    ln -sfn -- "$snapshot" "$link" || return 1
    mv -Tf -- "$link" "$backups_root/last-known-good" || return 1
    sync -f "$backups_root"
}

recover_appliance_operation() {
    local boot_only="$1" phase expected operation_id manifest_digest source_release target_release prune_status
    [[ -e "$journal_path" || -L "$journal_path" || -e "$maintenance_path" || -L "$maintenance_path" ]] || return 0
    [[ -f "$journal_path" && ! -L "$journal_path" && "$(stat -c '%u' "$journal_path")" == 0 &&
        -z "$(find "$journal_path" -maxdepth 0 -perm /022 -print -quit)" ]] || return 1
    ensure_maintenance_fence_from_journal true || return 1
    phase="$(read_journal_field phase)" || return 1
    case "$phase" in
        committed|completed|recovered|aborted) ;;
        *)
            ensure_maintenance_fence_from_journal || return 1
            restore_operation "$boot_only" || return 1
            phase="$(read_journal_field phase)" || return 1
            ;;
    esac
    if [[ "$phase" == committed ]]; then
        expected="$(read_journal_field targetRelease)" || return 1
    else
        expected="$(read_journal_field sourceRelease)" || return 1
    fi
    [[ "$(readlink -f /opt/kautuka/manager/current)" == "/opt/kautuka/manager/versions/$expected" ]] || return 1
    if [[ "$phase" == recovered ]]; then
        retain_appliance_snapshot || return 1
        set_operation_phase completed || return 1
        phase=completed
    fi
    release_database_writer_exclusion || return 1
    clear_maintenance_fence || return 1
    if [[ "$boot_only" != 'true' ]]; then
        start_appliance_manager normal && wait_for_appliance_release "$expected" normal || return 1
    fi
    if [[ "$phase" == committed || "$phase" == completed ]]; then
        prune_status="$(read_journal_field snapshotPruneStatus 2>/dev/null || true)"
        if [[ "$boot_only" != 'true' || "$prune_status" == pending ]]; then
            operation_id="$(read_journal_field operationId)" || return 1
            manifest_digest="$(read_journal_field snapshotManifestSha256)" || return 1
            source_release="$(read_journal_field sourceRelease)" || return 1
            target_release="$(read_journal_field targetRelease)" || return 1
            if python3 "$recovery_tool" prune-appliance --backups "$backups_root" --operation "$operation_id" --manifest-sha256 "$manifest_digest" --data-root "$database_root" --source "$source_release" --target "$target_release"; then
                prune_status=succeeded
            else
                prune_status=failed
            fi
            if [[ "$phase" == committed ]]; then
                set_operation_phase committed "$prune_status" false || return 1
            fi
            if [[ "$prune_status" == failed ]]; then
                echo 'error: the selected appliance release was retained, but superseded backups could not be pruned' >&2
                [[ "$boot_only" == 'true' ]] || return 1
            fi
        fi
    fi
    echo "Appliance recovery selected release $expected."
}

capture_appliance_snapshot() {
    local source="$1" target="$2" operation snapshot pending digest
    operation="$(python3 -c 'import secrets; print(secrets.token_hex(16))')" || return 1
    snapshot="$backups_root/$operation"
    pending="$backups_root/.candidate-$operation"
    write_durable_record "$journal_path" "$operation" capturing "$source" "$target" "$snapshot" "manager=$source" || return 1
    ensure_maintenance_fence_from_journal || return 1
    test_crash_at capturing
    stop_database_writers && acquire_database_writer_exclusion || return 1
    python3 "$recovery_tool" capture --layout appliance --data-root "$database_root" --destination "$pending" \
        --operation "$operation" --source "$source" --target "$target" || return 1
    digest="$(sha256sum "$pending/manifest.json" | cut -d' ' -f1)" || return 1
    [[ "$digest" =~ ^[a-f0-9]{64}$ ]] || return 1
    write_durable_record "$journal_path" "$operation" capturing "$source" "$target" "$snapshot" \
        "manager=$source" "snapshotManifestSha256=$digest" || return 1
    mv -- "$pending" "$snapshot" && sync -f "$backups_root" && set_operation_phase verified
}

activate_appliance_version() {
    local source="$1" target="$2"
    wait_for_appliance_release "$source" normal "/opt/kautuka/manager/versions/$target/kautuka-manager" || return 1
    if ! capture_appliance_snapshot "$source" "$target" ||
        ! retarget_current "$target" || ! set_operation_phase verifying; then
        recover_appliance_operation false || echo 'error: appliance recovery remains pending' >&2
        return 1
    fi
    test_crash_at switched
    release_database_writer_exclusion
    if ! start_appliance_manager verifying || ! wait_for_appliance_release "$target" verifying ||
        ! stop_database_writers || ! retain_appliance_snapshot; then
        recover_appliance_operation false || echo 'error: appliance recovery remains pending' >&2
        return 1
    fi
    set_operation_phase committed pending || return 1
    test_crash_at committed
    recover_appliance_operation false || return 1
    echo "Committed signed appliance release $target."
}

commit_operation() {
    local target_release source_release operation_id snapshot snapshot_manifest_sha256 previous_link tmp_link phase
    [[ -f "$journal_path" ]] || { echo 'error: no update operation is awaiting commit' >&2; return 1; }
    operation_id="$(read_journal_field operationId)"
    target_release="$(read_journal_field targetRelease)"
    source_release="$(read_journal_field sourceRelease)"
    snapshot="$(read_journal_field snapshot)"
    snapshot_manifest_sha256="$(python3 - "$journal_path" <<'PY'
import json, sys
with open(sys.argv[1], encoding='utf-8') as stream:
    print(json.load(stream).get('snapshotManifestSha256', ''))
PY
)"
    require_version_token "$target_release"
    phase="$(read_journal_field phase)"
    [[ "$phase" == 'verifying' || "$phase" == 'committed' ]] || { echo 'error: update is not in a committable verification phase' >&2; return 1; }
    python3 "$recovery_tool" verify --data-root "$database_root" --snapshot "$snapshot" \
        --operation "$operation_id" --source "$source_release" --target "$target_release" \
        --manifest-sha256 "$snapshot_manifest_sha256" || return 1
    verify_runtime_health "$target_release" && verify_updater_version || { echo 'error: target release health verification failed; automatic precommit recovery is required' >&2; restore_operation; return 1; }
    if [[ "$phase" != 'committed' ]]; then
        # Verify startup with outbound workers suppressed. A failure here is
        # still precommit and can restore the source snapshot safely.
        stop_database_writers || { echo 'error: target could not be drained for final startup; automatic recovery required' >&2; restore_operation; return 1; }
        set_operation_phase starting
        test_crash_at starting
        if ! start_units || { is_disposable_test && [[ "${KAUTUKA_TEST_FINAL_START_FAIL:-0}" == 1 ]]; } \
            || ! verify_runtime_health "$target_release" || ! verify_updater_version; then
            echo 'error: target final startup failed; restoring the pre-update state' >&2
            restore_operation || echo 'error: automatic recovery failed; maintenance remains active' >&2
            return 1
        fi
        set_operation_phase committed
        test_crash_at committed
    fi
    # Only the durable commit permits outbound workers. If this restart fails,
    # retain the fence and retry forward; never restore the old database.
    # Reconcile the marker too if a crash occurred between the journal and
    # marker writes that recorded the commit decision.
    set_operation_phase committed
    stop_database_writers || { echo 'error: committed update could not stop verification services; maintenance fence retained' >&2; return 1; }
    start_units && verify_runtime_health "$target_release" && verify_updater_version || {
        echo 'error: committed update worker startup failed; maintenance fence retained for forward recovery' >&2
        return 1
    }
    mkdir -p "$backups_root"
    previous_link="$backups_root/last-known-good"
    tmp_link="$backups_root/.last-known-good-${operation_id}"
    ln -sfn "$snapshot" "$tmp_link"
    mv -Tf "$tmp_link" "$previous_link"
    sync -f "$backups_root"
    clear_maintenance_fence
    # Keep exactly one completed verified database backup. Never prune it before
    # the newly committed candidate has been installed as last-known-good.
    find "$backups_root" -mindepth 1 -maxdepth 1 -type d ! -path "$snapshot" ! -name '.candidate-*' -exec rm -rf -- {} +
    echo "Committed release $target_release and retained its verified pre-update database snapshot."
}

complete_recovery_operation() {
    local source_release target_release operation_id snapshot snapshot_manifest_sha256 tmp_link
    [[ -f "$journal_path" && -f "$maintenance_path" ]] || { echo 'error: no recovered operation is awaiting completion' >&2; return 1; }
    [[ "$(read_journal_field phase)" == 'recovered' ]] || { echo 'error: operation is not in the recovered phase' >&2; return 1; }
    source_release="$(read_journal_field sourceRelease)"
    operation_id="$(read_journal_field operationId)"
    snapshot="$(read_journal_field snapshot)"
    target_release="$(read_journal_field targetRelease)"
    snapshot_manifest_sha256="$(python3 - "$journal_path" <<'PY'
import json, sys
with open(sys.argv[1], encoding='utf-8') as stream:
    print(json.load(stream).get('snapshotManifestSha256', ''))
PY
)"
    [[ "$version" == "$source_release" ]] || { echo 'error: recovery version does not match the recorded source release' >&2; return 1; }
    python3 "$recovery_tool" verify --data-root "$database_root" --snapshot "$snapshot" \
        --operation "$operation_id" --source "$source_release" --target "$target_release" \
        --manifest-sha256 "$snapshot_manifest_sha256" || return 1
    wait_for_manager_desired_state || return 1
    verify_runtime_health "$source_release" || { echo 'error: restored source services are not healthy; maintenance fence remains active' >&2; return 1; }
    stop_database_writers || { echo 'error: restored source could not be drained for worker restart; maintenance fence remains active' >&2; return 1; }
    tmp_link="$backups_root/.last-known-good-${operation_id}"
    ln -sfn "$snapshot" "$tmp_link"
    mv -Tf "$tmp_link" "$backups_root/last-known-good"
    sync -f "$backups_root"
    set_operation_phase completed
    if { is_disposable_test && [[ "${KAUTUKA_TEST_RECOVERY_START_FAIL:-0}" == 1 ]]; } || ! start_units; then
        echo 'error: restored source worker restart failed; maintenance fence retained' >&2
        return 1
    fi
    verify_runtime_health "$source_release" || { echo 'error: restored source did not become healthy after worker restart; maintenance fence retained' >&2; return 1; }
    clear_maintenance_fence
    find "$backups_root" -mindepth 1 -maxdepth 1 -type d ! -path "$snapshot" ! -name '.candidate-*' -exec rm -rf -- {} +
    echo "Completed automatic recovery to $source_release."
}

# Installing or starting the early-boot recovery unit during a first-time or
# bootstrap install is a no-op when no recovery decision exists. Avoid taking
# the update lock if certificates are current: the installer may already hold
# it while systemd orders a just-created user@ unit behind this recovery service.
if [[ "$boot_recover" == 'true' \
    && ! -e "$journal_path" && ! -L "$journal_path" \
    && ! -e "$maintenance_path" && ! -L "$maintenance_path" ]]; then
    assert_no_legacy_recovery_markers || exit 1
    if ! configure_appliance_layout; then
        # A fresh or legacy install has no appliance payload to renew.
        [[ ! -d /opt/kautuka/manager/current/service-images ]] || exit 1
        exit 0
    fi
    if [[ ! -x /opt/kautuka/manager/current/kautuka-manager ]]; then
        [[ -f /var/lib/kautuka-recovery/first-install.pending &&
            ! -e /opt/kautuka/manager/current && ! -L /opt/kautuka/manager/current ]] || exit 1
        exit 0
    fi
    certificate_status=0
    channel_certificate_status || certificate_status=$?
    if [[ "$certificate_status" == 0 ]]; then
        exit 0
    fi
    [[ "$certificate_status" == 10 || "$certificate_status" == 11 || "$certificate_status" == 12 ]] || exit 1
    exec 9>"$lock_path"
    flock 9
    renew_appliance_channel_certificates true
    exit $?
fi

exec 9>"$lock_path"
if [[ "$consume_appliance_request" == 'true' ]]; then
    flock 9
elif ! flock -n 9; then
    if [[ "$renew_channel_certificates" == 'true' ]]; then
        echo 'Deferred Channel certificate maintenance until the next timer run because an update is active.'
        exit 0
    fi
    echo "error: another apply-release is already running." >&2
    exit 1
fi

if [[ "$consume_appliance_request" == 'true' ]]; then
    activate_appliance='true'
fi

if [[ "$renew_channel_certificates" == 'true' ]]; then
    configure_appliance_layout || exit 1
    if [[ -e "$maintenance_path" || -L "$maintenance_path" ]]; then
        echo 'Deferred Channel certificate maintenance while release recovery is pending.'
        exit 0
    fi
    if [[ -e "$journal_path" || -L "$journal_path" ]]; then
        [[ -f "$journal_path" && ! -L "$journal_path" ]] || exit 1
        case "$(read_journal_field phase)" in
            committed|completed|aborted) ;;
            *) echo 'Deferred Channel certificate maintenance while a release decision is pending.'; exit 0 ;;
        esac
    fi
    # The inode is stable and mounted read-only into Gateway. Writing one byte
    # never replaces it; an admitted shared holder may finish nested work.
    certificate_status=0
    channel_certificate_status || certificate_status=$?
    case "$certificate_status" in
        0)
            if [[ ! -f "$database_root/security/.channel-renewal-restart" ]]; then
                printf 0 | dd of=/run/kautuka-helper-run.lock bs=1 count=1 conv=notrunc status=none || exit 1
                exit 0
            fi ;;
        10|11|12) ;;
        *) exit 1 ;;
    esac
    if [[ "$certificate_status" == 11 || "$certificate_status" == 12 ]]; then
        printf 1 | dd of=/run/kautuka-helper-run.lock bs=1 count=1 conv=notrunc status=none || exit 1
    fi
    (
        exec 7</run/kautuka-helper-run.lock || exit 1
        if [[ "$certificate_status" == 12 ]]; then
            # Expired/invalid identities require recovery even if a run is stuck.
            echo 'Channel identities expired or invalid; recovery may interrupt active helpers.'
        elif ! flock -x -w "$([[ "$certificate_status" == 11 ]] && printf 30 || printf 0)" 7; then
            echo 'Deferred Channel certificate maintenance while a helper is active.'
            exit 0
        fi
        renew_appliance_channel_certificates false || exit $?
        printf 0 | dd of=/run/kautuka-helper-run.lock bs=1 count=1 conv=notrunc status=none
    )
    exit $?
fi

if [[ "$stage_appliance" == 'true' || "$activate_appliance" == 'true' ]]; then
    configure_appliance_layout || {
        echo 'error: this command requires an installed Manager-supervised appliance' >&2
        exit 1
    }
elif [[ "$recover" == 'true' || "$boot_recover" == 'true' ]]; then
    if configure_appliance_layout; then
        assert_no_legacy_recovery_markers
        recover_appliance_operation "$boot_recover" || exit $?
        if [[ "$boot_recover" == 'true' ]]; then
            renew_appliance_channel_certificates true
            exit $?
        fi
        exit 0
    fi
fi

if [[ "$consume_appliance_request" == 'true' ]]; then
    version="$(python3 - <<'PY'
import os, re, stat
from contextlib import ExitStack

name = 'activate-appliance'
with ExitStack() as stack:
    directory = None
    try:
        for part in ('/var/lib', 'kautuka', 'updateService', 'updates'):
            directory = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=directory)
            stack.callback(os.close, directory)
        request = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=directory)
    except FileNotFoundError:
        raise SystemExit(0)
    except OSError as error:
        if directory is not None and error.filename == name:
            os.unlink(name, dir_fd=directory)
        raise SystemExit(f'error: cannot open appliance update request: {error.strerror}')
    stack.callback(os.close, request)
    os.unlink(name, dir_fd=directory)
    metadata = os.fstat(request)
    if not stat.S_ISREG(metadata.st_mode) or not 0 < metadata.st_size <= 128:
        raise SystemExit('error: invalid appliance update request file')
    value = os.read(request, 129)
    if not re.fullmatch(rb'(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\n', value):
        raise SystemExit('error: invalid appliance update version')
    print(value[:-1].decode('ascii'))
PY
)"
    [[ -n "$version" ]] || exit 0
    artifact="/var/lib/kautuka/updateService/updates/staging/v${version}/kautuka-linux-amd64.tar.gz"
fi

if [[ "$capabilities" == 'true' ]]; then
    assert_no_legacy_recovery_markers
    [[ -f "$recovery_tool" && ! -L "$recovery_tool" ]] || { echo 'error: database recovery engine is not installed' >&2; exit 1; }
    assert_database_writer_lock_ready || exit 1
    assert_maintenance_webhost || exit 1
    printf '%s\n' 'safe-state-recovery-v6'
    exit 0
fi

assert_no_legacy_recovery_markers

check_terminal_recovery_decision() {
    local phase
    [[ -f "$journal_path" ]] || return 1
    phase="$(read_journal_field phase)"
    case "$phase" in
        completed|aborted)
            start_units || return 1
            if [[ "$phase" == 'completed' ]]; then
                local snapshot
                snapshot="$(read_journal_field snapshot)"
                [[ "$(readlink -f "$backups_root/last-known-good" 2>/dev/null || true)" == "$snapshot" ]] || {
                    echo 'error: completed recovery does not match the retained verified backup' >&2
                    return 1
                }
                find "$backups_root" -mindepth 1 -maxdepth 1 -type d ! -path "$snapshot" ! -name '.candidate-*' -exec rm -rf -- {} +
            fi
            clear_maintenance_fence
            echo "Recovery operation already has terminal decision '$phase'; no database restore was replayed."
            return 0
            ;;
        recovered)
            echo 'Verified source snapshot was already restored; preserving subsequent writes and continuing source health verification.'
            return 0
            ;;
        *)
            return 1
            ;;
    esac
}

if [[ "$recover" == 'true' ]]; then
    [[ -f "$maintenance_path" || -f "$journal_path" ]] || exit 0
    assert_supported_data_configuration true
    if [[ -f "$journal_path" && "$(read_journal_field phase)" == 'committed' ]]; then
        echo 'Committed update found; leaving it fenced for forward recovery by the current UpdateService.'
        exit 0
    fi
    if [[ -f "$journal_path" ]]; then
        # Re-establish admission suppression before draining writers. This also
        # covers a crash after a verified recovery cleared the marker but before
        # its final service restart completed.
        if ! ensure_maintenance_fence_from_journal; then
            write_emergency_maintenance_fence || true
            echo 'error: update journal is unreadable or invalid; keeping the appliance fenced' >&2
            exit 1
        fi
        test_crash_at recovery-fenced
        if check_terminal_recovery_decision; then
            exit 0
        fi
    fi
    stop_database_writers || exit 1
    restore_operation
    exit $?
fi

if [[ "$boot_recover" == 'true' ]]; then
    [[ -f "$maintenance_path" || -f "$journal_path" ]] || exit 0
    assert_supported_data_configuration true
    if ! assert_no_legacy_recovery_markers; then exit 1; fi
    if [[ -f "$journal_path" ]]; then
        if ! ensure_maintenance_fence_from_journal; then
            write_emergency_maintenance_fence || true
            echo 'error: update journal is unreadable or invalid; keeping the appliance fenced' >&2
            exit 1
        fi
        phase="$(read_journal_field phase)"
        case "$phase" in
            committed)
                echo 'Committed update found at boot; leaving it fenced for UpdateService roll-forward.'
                exit 0
                ;;
            completed|aborted)
                if [[ "$phase" == 'completed' ]]; then
                    snapshot="$(read_journal_field snapshot)"
                    [[ "$(readlink -f "$backups_root/last-known-good" 2>/dev/null || true)" == "$snapshot" ]] || {
                        echo 'error: completed recovery does not match the retained verified backup' >&2
                        exit 1
                    }
                    find "$backups_root" -mindepth 1 -maxdepth 1 -type d ! -path "$snapshot" ! -name '.candidate-*' -exec rm -rf -- {} +
                fi
                clear_maintenance_fence
                echo "Terminal recovery decision '$phase' found at boot; database state was not replayed."
                exit 0
                ;;
            recovered)
                echo 'Source snapshot was already restored; preserving subsequent writes and leaving the maintenance fence for UpdateService.'
                exit 0
                ;;
        esac
        # Recovery service is ordered before the service-user manager and all
        # application units. It performs only the durable database/pointer
        # restore here; runtime health and admission reopening belong to the
        # source UpdateService after the services can actually start.
        if ! restore_operation true; then exit 1; fi
        exit 0
    fi
    echo 'error: maintenance marker has no usable recovery journal; keeping the appliance fenced' >&2
    exit 1
fi

if [[ "$commit" == 'true' ]]; then
    assert_supported_data_configuration
    commit_operation
    exit $?
fi

if [[ "$complete_recovery" == 'true' ]]; then
    assert_supported_data_configuration
    require_version_token "$version"
    complete_recovery_operation
    exit $?
fi

assert_no_unresolved_update_operation() {
    [[ -e "$journal_path" || -L "$journal_path" || -e "$maintenance_path" || -L "$maintenance_path" ]] || return 0
    [[ -f "$journal_path" && ! -L "$journal_path" ]] || {
        echo 'error: an unresolved or unreadable update operation exists; recovery must finish before another update' >&2
        return 1
    }
    local phase
    phase="$(read_journal_field phase 2>/dev/null || true)"
    case "$phase" in
        completed|aborted|committed)
            [[ ! -e "$maintenance_path" && ! -L "$maintenance_path" ]] || {
                echo 'error: terminal update operation still has a maintenance fence; recover it before another update' >&2
                return 1
            }
            ensure_maintenance_fence_from_journal true || return 1
            local operation_id source_release target_release snapshot manifest_digest service_key expected_release current_pointer
            operation_id="$(read_journal_field operationId)"
            source_release="$(read_journal_field sourceRelease)"
            target_release="$(read_journal_field targetRelease)"
            snapshot="$(read_journal_field snapshot)"
            if [[ "$phase" == 'completed' || "$phase" == 'committed' ]]; then
                manifest_digest="$(python3 - "$journal_path" <<'PY'
import json, sys
with open(sys.argv[1], encoding='utf-8') as stream:
    print(json.load(stream).get('snapshotManifestSha256', ''))
PY
)"
                python3 "$recovery_tool" verify --layout "$recovery_layout" --data-root "$database_root" --snapshot "$snapshot" \
                    --operation "$operation_id" --source "$source_release" --target "$target_release" \
                    --manifest-sha256 "$manifest_digest" || return 1
                [[ "$(readlink -f "$backups_root/last-known-good" 2>/dev/null || true)" == "$snapshot" ]] || {
                    echo 'error: terminal update journal does not match the retained verified database backup' >&2
                    return 1
                }
            fi
            expected_release="$source_release"
            [[ "$phase" != 'committed' ]] || expected_release="$target_release"
            for service_key in "${host_services[@]}"; do
                current_pointer="$(readlink -f "/opt/kautuka/${service_key}/current" 2>/dev/null || true)"
                [[ "$current_pointer" == "/opt/kautuka/${service_key}/versions/${expected_release}" ]] || {
                    echo "error: terminal update journal does not match the active $service_key release" >&2
                    return 1
                }
            done
            ;;
        *)
            echo "error: update operation '$phase' is unresolved; recover or complete it before starting another update" >&2
            return 1
            ;;
    esac
}

[[ -n "$artifact" && -n "$version" ]] || {
    usage >&2
    exit 2
}
require_version_token "$version"
if [[ "$recovery_layout" == appliance ]]; then
    assert_no_unresolved_update_operation
    record_source_pointers
    assert_forward_version "${source_pointers[manager]}" "$version"
    artifact="$(assert_safe_artifact "$artifact")"
    verified_root=''
    trap 'cleanup_staging; [[ -z "$verified_root" ]] || rm -rf "$verified_root"' EXIT
    verify_staged_artifact "$artifact" "$version"
    stage_appliance_version "$version"
    echo "Staged signed appliance release $version."
    if [[ "$activate_appliance" == 'true' ]]; then
        assert_database_writer_lock_ready
        activate_appliance_version "${source_pointers[manager]}" "$version"
    fi
    exit 0
fi
assert_no_unresolved_update_operation || exit 1
artifact="$(assert_safe_artifact "$artifact")"
assert_supported_data_configuration
record_source_pointers
previous="${source_pointers[manager]}"
assert_forward_version "$previous" "$version"
assert_database_writer_lock_ready || exit 1
verified_root=''
trap 'cleanup_staging; [[ -z "$verified_root" ]] || rm -rf "$verified_root"' EXIT
verify_staged_artifact "$artifact" "$version"
protect_service_roots
assert_maintenance_webhost || exit 1
stage_version_roots "$verified_root/artifact.tar.gz" "$version"
mkdir -p "$backups_root"
operation_id="$(python3 -c 'import secrets; print(secrets.token_hex(16))')"
snapshot="$backups_root/$operation_id"
pointer_args=("manager=${source_pointers[manager]}" "updateservice=${source_pointers[updateservice]}" "webhost=${source_pointers[webhost]}" "gateway=${source_pointers[gateway]}" "channelhost=${source_pointers[channelhost]}" "credentialshost=${source_pointers[credentialshost]}")
write_durable_record "$journal_path" "$operation_id" capturing "$previous" "$version" "$snapshot" "${pointer_args[@]}"
write_durable_record "$maintenance_path" "$operation_id" capturing "$previous" "$version" "$snapshot" "${pointer_args[@]}"
test_crash_at capturing
stop_database_writers
acquire_database_writer_exclusion
candidate="$backups_root/.candidate-${operation_id}"
python3 "$recovery_tool" capture --data-root "$database_root" --destination "$candidate" --operation "$operation_id" --source "$previous" --target "$version"
snapshot_manifest_sha256="$(sha256sum "$candidate/manifest.json" | cut -d' ' -f1)"
[[ "$snapshot_manifest_sha256" =~ ^[a-f0-9]{64}$ ]] || { echo 'error: snapshot manifest digest could not be created' >&2; exit 1; }
write_durable_record "$journal_path" "$operation_id" capturing "$previous" "$version" "$snapshot" "${pointer_args[@]}" "snapshotManifestSha256=$snapshot_manifest_sha256"
write_durable_record "$maintenance_path" "$operation_id" capturing "$previous" "$version" "$snapshot" "${pointer_args[@]}" "snapshotManifestSha256=$snapshot_manifest_sha256"
mv "$candidate" "$snapshot"
sync -f "$backups_root"
set_operation_phase verified
retarget_current "$version"
test_crash_at switched
release_database_writer_exclusion
if ! start_units; then
    restore_operation || { echo 'error: automatic precommit recovery failed; maintenance fence remains active' >&2; exit 1; }
    echo 'error: target activation failed; verified source state was restored automatically' >&2
    exit 1
fi
set_operation_phase verifying
if ! verify_runtime_health "$version"; then
    restore_operation || { echo 'error: automatic precommit recovery failed; maintenance fence remains active' >&2; exit 1; }
    echo 'error: target health verification failed; verified source state was restored automatically' >&2
    exit 1
fi
if [[ -f "$verified_root/next-helper" ]]; then
    helper_tmp="$(mktemp /usr/local/lib/kautuka/.apply-release.XXXXXX)"
    install -o root -g root -m 0755 "$verified_root/next-helper" "$helper_tmp"
    sync -f "$helper_tmp"
    mv -Tf "$helper_tmp" /usr/local/lib/kautuka/apply-release
    sync -f /usr/local/lib/kautuka
fi
# The legacy TUI CLI is retired. Remove any copy left by an earlier release.
rm -f /usr/local/bin/kautuka /usr/local/bin/kautuka-tui
rm -rf /opt/kautuka/tui
echo "Activated release $version under maintenance; UpdateService must verify its own current executable and commit the operation."
