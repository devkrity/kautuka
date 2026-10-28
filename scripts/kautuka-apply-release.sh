#!/usr/bin/env bash
set -euo pipefail

# Root helper invoked by UpdateService. It deploys or retargets one release.
# UpdateService never extracts or flips current itself.

usage() {
    cat <<'EOF'
Usage:
  kautuka-apply-release --artifact <tar.gz> --version <version>
  kautuka-apply-release --revert <version>
EOF
}

lock_path="${KAUTUKA_APPLY_LOCK:-/run/kautuka-apply.lock}"
rollback_path="${KAUTUKA_APPLY_ROLLBACK:-/run/kautuka-apply.rollback}"
artifact=''
version=''
revert=''

if [[ ${EUID} -ne 0 ]]; then
    echo 'error: kautuka-apply-release must run as root.' >&2
    exit 1
fi

# UpdateService invokes this helper via sudo from a user-scoped unit. Restarting
# manager (podman Delegate) can tear down user.slice and SIGTERM the whole US
# cgroup — live exit 143 with pointers already flipped. Re-enter as a system
# transient unit so apply/revert finishes outside that cgroup.
# Must run before argument parsing so "$@" is still intact for systemd-run.
if [[ "${KAUTUKA_APPLY_DETACHED:-}" != '1' ]] && command -v systemd-run >/dev/null 2>&1; then
    self="$(readlink -f "$0")"
    unit="kautuka-apply-${RANDOM}$$"
    setenv_args=(
        --setenv=KAUTUKA_APPLY_DETACHED=1
        --setenv="KAUTUKA_APPLY_LOCK=${lock_path}"
        --setenv="KAUTUKA_APPLY_ROLLBACK=${rollback_path}"
    )
    # Forward other KAUTUKA_* overrides (e.g. fault-injection) into the system unit.
    while IFS= read -r line; do
        case "$line" in
            KAUTUKA_APPLY_DETACHED=*|KAUTUKA_APPLY_LOCK=*|KAUTUKA_APPLY_ROLLBACK=*)
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
        --revert)
            revert="$2"
            shift 2
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

require_version_token() {
    local value="$1"
    [[ "$value" =~ ^[A-Za-z0-9._+-]+$ ]] || {
        echo "error: invalid version: $value" >&2
        exit 2
    }
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

host_services=(manager updateservice webhost gateway channelhost credentialshost)
# Do not restart UpdateService here. The apply caller is that unit; a cgroup
# restart kills sudo and leaves the helper unfinished.
user_units=(kautuka-manager.service kautuka-webhost.service)
system_units=(kautuka-credentialshost.service kautuka-channelhost.service kautuka-gateway.service)
service_user='kautuka'

stage_version_roots() {
    local artifact_path="$1"
    local target_version="$2"
    local extract_root
    extract_root="$(mktemp -d /tmp/kautuka-apply-XXXXXX)"
    trap 'rm -rf "$extract_root"' RETURN
    tar -xzf "$artifact_path" -C "$extract_root"

    local manager_source="$extract_root"
    if [[ -d "$extract_root/bin" ]]; then
        manager_source="$extract_root/bin"
    fi

    install_version_root manager "$manager_source" "$target_version" kautuka-manager
    install_version_root gateway "$extract_root/services/gateway" "$target_version" kautuka-gateway
    install_version_root credentialshost "$extract_root/services/credentialshost" "$target_version" kautuka-credentialshost
    install_version_root channelhost "$extract_root/services/channelhost" "$target_version" kautuka-channelhost
    install_version_root updateservice "$extract_root/services/updateservice" "$target_version" kautuka-updateservice
    install_version_root webhost "$extract_root/services/webhost" "$target_version" kautuka-webhost
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

    rm -rf "$version_root"
    mkdir -p "$version_root"
    cp -a "$source_root/." "$version_root/"
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
            exit 1
        }
        ln -sfn "$version_root" "$next_link"
    done

    for service_key in "${host_services[@]}"; do
        current_link="/opt/kautuka/${service_key}/current"
        next_link="/opt/kautuka/${service_key}/current.next"
        mv -Tf "$next_link" "$current_link"
    done

    for service_key in "${host_services[@]}"; do
        current_link="/opt/kautuka/${service_key}/current"
        resolved="$(basename "$(readlink -f "$current_link")")"
        if [[ "$resolved" != "$target_version" ]]; then
            eval "${previous_trap:-trap - HUP TERM INT}"
            echo "error: current pointer mismatch for ${service_key}: expected ${target_version} got ${resolved}" >&2
            exit 1
        fi
    done

    eval "${previous_trap:-trap - HUP TERM INT}"
}

write_rollback_target() {
    local previous="$1"
    printf '%s\n' "$previous" > "$rollback_path"
}

clear_rollback_target() {
    rm -f "$rollback_path"
}

heal_incomplete_apply_if_needed() {
    local previous
    [[ -f "$rollback_path" ]] || return 0
    previous="$(tr -d '\r\n' < "$rollback_path")"
    require_version_token "$previous"
    echo "warning: completing interrupted apply by restoring current pointers to $previous." >&2
    trap '' HUP TERM INT
    retarget_current "$previous"
    clear_rollback_target
    trap - HUP TERM INT
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
}

current_manager_version() {
    local resolved
    resolved="$(readlink -f /opt/kautuka/manager/current)" || {
        echo 'error: cannot resolve /opt/kautuka/manager/current' >&2
        exit 1
    }
    basename "$resolved"
}

restore_current_after_activation_failure() {
    local previous="$1"
    echo "error: activation failed; restoring current pointers to $previous." >&2
    trap '' HUP TERM INT
    retarget_current "$previous"
    clear_rollback_target
    start_units || true
    trap - HUP TERM INT
    exit 1
}

exec 9>"$lock_path"
if ! flock -n 9; then
    echo "error: another apply-release is already running." >&2
    exit 1
fi

heal_incomplete_apply_if_needed

if [[ -n "$revert" ]]; then
    require_version_token "$revert"
    # Do not restore the pre-revert version on activation failure: that undoes the
    # revert and can leave a crashed box on the bad release. Leave pointers on
    # $revert and fail so the operator (or a retry) can finish activation.
    retarget_current "$revert"
    clear_rollback_target
    if ! start_units; then
        echo "error: activation failed after revert to $revert; current pointers remain on $revert." >&2
        exit 1
    fi
    echo "Reverted current pointers to $revert."
    exit 0
fi

[[ -n "$artifact" && -n "$version" ]] || {
    usage >&2
    exit 2
}
require_version_token "$version"
artifact="$(assert_safe_artifact "$artifact")"
previous="$(current_manager_version)"
stage_version_roots "$artifact" "$version"
write_rollback_target "$previous"
retarget_current "$version"
if ! start_units; then
    restore_current_after_activation_failure "$previous"
fi
clear_rollback_target
# The legacy TUI CLI is retired. Remove any copy left by an earlier release.
rm -f /usr/local/bin/kautuka /usr/local/bin/kautuka-tui
rm -rf /opt/kautuka/tui
echo "Applied release $version."
