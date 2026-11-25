#!/usr/bin/env bash
set -euo pipefail
[[ $(id -u) == 0 ]] || { echo 'Run as root in a disposable Linux environment.' >&2; exit 2; }
script_dir="$(cd "$(dirname "$0")" && pwd)"
test_root="$(mktemp -d /tmp/kautuka-host-tools-test.XXXXXXXX)"
trap 'rm -rf -- "$test_root"' EXIT
chmod 0755 "$test_root"
mkdir -p "$test_root/versions/1.0.0" "$test_root/versions/2.0.0" "$test_root/tools"
for version in 1.0.0 2.0.0; do
    install -m 0755 "$script_dir/kautuka-apply-release.sh" "$test_root/versions/$version/kautuka-apply-release"
    install -m 0755 "$script_dir/kautuka-update-recovery.py" "$test_root/versions/$version/kautuka-update-recovery"
done
host_tools_selector="$test_root/tools/host-tools-current"
ln -s "$test_root/versions/1.0.0" "$host_tools_selector"
original_args=(--consume-appliance-request)
# Execute real validation, publication, post-lock recheck, and recovery ordering
# with all privileged production paths redirected into this private fixture.
source <(sed -n '/^validate_appliance_host_tools() {/,/^}/p; /^publish_appliance_host_tools() {/,/^}/p; /^recheck_host_tools_after_lock() {/,/^}/p; /^recover_appliance_operation() {/,/^}/p' "$script_dir/kautuka-apply-release.sh" | \
    sed -e "s@/opt/kautuka/manager/versions@$test_root/versions@g" \
        -e "s@/opt/kautuka/manager/current@$test_root/current@g" \
        -e "s@/usr/local/lib/kautuka/apply-release@$test_root/tools/dispatch@g" \
        -e "s@/usr/local/lib/kautuka@$test_root/tools@g")
validate_appliance_host_tools "$test_root/versions/2.0.0"
chmod g+w "$test_root/versions/2.0.0/kautuka-update-recovery"
if validate_appliance_host_tools "$test_root/versions/2.0.0"; then echo 'accepted writable tool' >&2; exit 1; fi
chmod g-w "$test_root/versions/2.0.0/kautuka-update-recovery"
# Recovery before the durable commit must retain the source generation.
journal_path="$test_root/journal"
maintenance_path="$test_root/fence"
: > "$journal_path"
: > "$maintenance_path"
ln -s "$test_root/versions/1.0.0" "$test_root/current"
TEST_PHASE=verifying
read_journal_field() { case "$1" in phase) echo "$TEST_PHASE";; sourceRelease) echo 1.0.0;; targetRelease) echo 2.0.0;; snapshotPruneStatus) echo succeeded;; *) return 1;; esac; }
ensure_maintenance_fence_from_journal() { :; }
restore_operation() { TEST_PHASE=recovered; }
retain_appliance_snapshot() { :; }
set_operation_phase() { TEST_PHASE="$1"; }
release_database_writer_exclusion() { :; }
clear_maintenance_fence() { rm -f "$maintenance_path"; }
test_crash_at() { :; }
recover_appliance_operation true
[[ $(readlink "$host_tools_selector") == "$test_root/versions/1.0.0" ]]
rm "$test_root/current"
# A killed earlier publisher may leave its private staging directory behind.
# The next publisher must not collide with that orphan.
mkdir "$test_root/tools/.host-tools-stage.orphan01"
ln -s "$test_root/versions/1.0.0" "$test_root/tools/.host-tools-stage.orphan01/current"
# Model the committed journal and a crash after publication but before unfencing.
journal_path="$test_root/journal"
maintenance_path="$test_root/fence"
: > "$journal_path"
: > "$maintenance_path"
ln -s "$test_root/versions/2.0.0" "$test_root/current"
TEST_PHASE=committed
read_journal_field() { case "$1" in phase) echo committed;; targetRelease) echo 2.0.0;; snapshotPruneStatus) echo succeeded;; *) return 1;; esac; }
ensure_maintenance_fence_from_journal() { :; }
release_database_writer_exclusion() { :; }
clear_maintenance_fence() { [[ $(readlink "$host_tools_selector") == "$test_root/versions/2.0.0" ]]; rm -f "$maintenance_path"; }
test_crash_at() { [[ "${CRASH_AFTER_PUBLISH:-0}" != 1 ]] || exit 90; }
if (CRASH_AFTER_PUBLISH=1; recover_appliance_operation true); then echo 'expected crash' >&2; exit 1; else [[ $? == 90 ]]; fi
[[ -f "$maintenance_path" && $(readlink "$host_tools_selector") == "$test_root/versions/2.0.0" ]]
recover_appliance_operation true
[[ ! -e "$maintenance_path" && $(readlink "$host_tools_selector") == "$test_root/versions/2.0.0" ]]
# A source-generation process that waited on fd 9 must redispatch before it can
# consume the request after another process has selected the new generation.
cat > "$test_root/tools/dispatch" <<'DISPATCH'
#!/usr/bin/env bash
printf '%s\n' "$*"
DISPATCH
chmod 0755 "$test_root/tools/dispatch"
host_tools_directory="$test_root/versions/1.0.0"
result=$(exec 9>"$test_root/lock"; flock 9; recheck_host_tools_after_lock; echo 'incorrectly continued')
[[ "$result" == '--consume-appliance-request' ]]
# The stable dispatcher selects only a protected explicit generation.
droot="$test_root/dispatch-root"
mkdir -p "$droot/usr/local/lib/kautuka" "$droot/opt/kautuka/manager/versions/3.0.0"
printf '#!/usr/bin/env bash\nprintf "selected %%s\\n" "$*"\n' > "$droot/opt/kautuka/manager/versions/3.0.0/kautuka-apply-release"
printf '#!/usr/bin/env bash\nexit 0\n' > "$droot/opt/kautuka/manager/versions/3.0.0/kautuka-update-recovery"
chmod 0755 "$droot/opt/kautuka/manager/versions/3.0.0/"kautuka-*
ln -s "$droot/opt/kautuka/manager/versions/3.0.0" "$droot/usr/local/lib/kautuka/host-tools-current"
python3 - "$script_dir/kautuka-apply-release-dispatch.sh" "$droot" "$test_root/dispatcher" <<'PY'
import pathlib, re, sys
source, root, output = sys.argv[1:]
pattern = r'/usr/local/lib/kautuka|/usr/local/lib|/usr/local|/usr(?= )|/opt/kautuka/manager/versions|/opt/kautuka/manager(?= )|/opt/kautuka(?= )|/opt(?= )'
text = re.sub(pattern, lambda match: root + match.group(), pathlib.Path(source).read_text())
pathlib.Path(output).write_text(text)
PY
[[ $(bash "$test_root/dispatcher" --probe) == 'selected --probe' ]]
chmod g+w "$droot/opt/kautuka/manager/versions/3.0.0/kautuka-update-recovery"
if bash "$test_root/dispatcher" --probe >/dev/null 2>&1; then echo 'dispatcher accepted writable tool' >&2; exit 1; fi
chmod g-w "$droot/opt/kautuka/manager/versions/3.0.0/kautuka-update-recovery"
ln -sfn /tmp/untrusted "$droot/usr/local/lib/kautuka/host-tools-current"
if bash "$test_root/dispatcher" --probe >/dev/null 2>&1; then echo 'dispatcher accepted untrusted selector' >&2; exit 1; fi
# The no-journal first-boot path stays lock-free while the installer holds it.
{
    printf '%s\n' 'set -euo pipefail' 'boot_recover=true' \
        "journal_path=$test_root/no-journal" "maintenance_path=$test_root/no-fence" \
        "lock_path=$test_root/install-lock" \
        'assert_no_legacy_recovery_markers() { :; }' 'configure_appliance_layout() { return 1; }'
    awk '/^# Installing or starting the early-boot recovery unit/ {capture=1} capture && /^exec 9>/ {exit} capture {print}' "$script_dir/kautuka-apply-release.sh" | \
        sed "s@/opt/kautuka@$test_root/nonexistent-appliance@g"
} > "$test_root/boot-fast-path"
exec 8>"$test_root/install-lock"
flock 8
timeout 2 bash "$test_root/boot-fast-path"
flock -u 8
exec 8>&-
# A versioned Bash helper is visible in /proc under the Manager release path.
# A separate live process under the same path must still prevent recovery.
process_root="$test_root/process-scan"
version_dir="$process_root/manager/versions/1.0.0"
mkdir -p "$version_dir"
{
    printf '%s\n' 'set -euo pipefail'
    sed -n '/^assert_database_writers_absent() {/,/^}/p' "$script_dir/kautuka-apply-release.sh" | \
        sed "s@/opt/kautuka/@$process_root/@g"
    printf '%s\n' 'assert_database_writers_absent'
} > "$version_dir/kautuka-apply-release"
bash "$version_dir/kautuka-apply-release"
mkfifo "$test_root/writer-ready" "$test_root/writer-hold"
cat > "$version_dir/kautuka-manager" <<'WRITER'
printf 'ready\n' > "$1"
IFS= read -r _ < "$2"
WRITER
bash "$version_dir/kautuka-manager" "$test_root/writer-ready" "$test_root/writer-hold" &
writer_pid=$!
trap 'kill "$writer_pid" 2>/dev/null || true; rm -rf -- "$test_root"' EXIT
[[ $(timeout 5 head -n 1 "$test_root/writer-ready") == ready ]]
if bash "$version_dir/kautuka-apply-release" 2> "$test_root/writer.log"; then
    echo 'recovery accepted a running database writer' >&2
    exit 1
fi
grep -Fq "pid $writer_pid (" "$test_root/writer.log"

# The timer is a system service already. A redundant systemd-run process would
# look like a live writer because its command names the versioned host helper.
mkdir "$test_root/bin"
cat > "$test_root/bin/systemd-run" <<'DETACH'
#!/usr/bin/env bash
printf 'detached\n'
DETACH
chmod 0755 "$test_root/bin/systemd-run"
{
    printf '%s\n' 'set -euo pipefail' "lock_path=$test_root/lock"
    awk '/^# UpdateService invokes this helper/ {capture=1} capture && /^while \[\[/ {exit} capture {print}' \
        "$script_dir/kautuka-apply-release.sh"
    printf '%s\n' "printf 'direct\\n'"
} > "$test_root/dispatch-check"
[[ "$(PATH="$test_root/bin:$PATH" KAUTUKA_APPLY_DETACHED=0 bash "$test_root/dispatch-check" --renew-channel-certificates)" == direct ]]
[[ "$(PATH="$test_root/bin:$PATH" KAUTUKA_APPLY_DETACHED=0 bash "$test_root/dispatch-check" --consume-appliance-request)" == detached ]]

# Drive the production timer decision without touching the host lock or certificates.
renewal="$test_root/renewal"
mkdir -p "$renewal/security"
install -m 0644 /dev/null "$renewal/run.lock"
{
    cat <<'PREAMBLE'
set -euo pipefail
renew_channel_certificates=true
database_root="$1"
helper_run_lock="$1/run.lock"
maintenance_path="$1/fence"
journal_path="$1/journal"
configure_appliance_layout() { :; }
channel_certificate_status() { return "$RENEW_STATUS"; }
renew_appliance_channel_certificates() {
    if [[ "$RENEW_RESULT" == 0 ]]; then
        rm -f -- "$database_root/security/.channel-renewal-restart"
    fi
    return "$RENEW_RESULT"
}
flock() { return "$BUSY"; }
PREAMBLE
    sed -n '/^if \[\[ "\$renew_channel_certificates" == '\''true'\'' \]\]; then$/,/^fi$/p' "$script_dir/kautuka-apply-release.sh"
} > "$test_root/renewal-timer"
test_renewal() {
    local current_status="$1" busy="$2" outcome="$3" expected_rc="$4" expected_flag="$5" rc=0
    RENEW_STATUS="$current_status" BUSY="$busy" RENEW_RESULT="$outcome" \
        bash "$test_root/renewal-timer" "$renewal" || rc=$?
    [[ "$rc" == "$expected_rc" && "$(cat "$renewal/run.lock")" == "$expected_flag" ]]
}
lock_inode="$(stat -c '%i' "$renewal/run.lock")"
test_renewal 11 1 0 0 1
test_renewal 11 0 1 1 2
test_renewal 11 1 0 0 2
test_renewal 11 0 1 1 2
test_renewal 12 1 1 1 2
test_renewal 1 0 0 1 2
touch "$renewal/security/.channel-renewal-restart"
test_renewal 0 0 1 1 2
test_renewal 0 0 0 0 0
[[ "$(stat -c '%i' "$renewal/run.lock")" == "$lock_inode" ]]
echo 'Host tool publication survives interruption and stale selection redispatches.'
