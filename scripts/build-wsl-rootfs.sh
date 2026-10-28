#!/usr/bin/env bash
set -euo pipefail

usage() {
    cat <<'EOF'
Build a Kautuka WSL appliance rootfs tarball.

Usage:
  ./scripts/build-wsl-rootfs.sh [options]

Options:
  --output <path>               Output tar.gz path (default: ./kautuka-wsl-appliance.tar.gz)
  --releasever <version>        Fedora release version to install (default: 44)
  --arch <name>                 Target architecture (default: amd64)
  --image <ref>                 Fedora container image used to compose the rootfs (default: quay.io/fedora/fedora:44)
  --container-runtime <name>    Container runtime to use (default: podman)
  --work-dir <path>             Keep build workspace at this path instead of a temporary directory
  --help                        Show this help

Notes:
  - This builder composes a Fedora userspace with dnf --installroot inside a container so it works
    from non-Fedora hosts without depending on host package manager state.
  - The resulting rootfs contains the minimal base OS, WSL hardening config, rootless Podman
    prerequisites, and SSH server required for the appliance runtime.
  - Manager and release payloads are layered into the rootfs later by the release workflow.
EOF
}

fail() {
    echo "error: $*" >&2
    exit 1
}

create_work_dir() {
    local anchor_path="$1"
    local anchor_dir
    anchor_dir="$(dirname "$anchor_path")"

    # The work dir is bind-mounted into the build container, and Docker treats a
    # non-absolute -v source as a named volume, so resolve it to an absolute path.
    mkdir -p "$anchor_dir"
    anchor_dir="$(cd "$anchor_dir" && pwd)"
    mktemp -d "$anchor_dir/.build-wsl-rootfs.XXXXXX"
}

require_command() {
    local cmd="$1"
    command -v "$cmd" >/dev/null 2>&1 || fail "required command not found: $cmd"
}

maybe_sudo() {
    if [[ ${EUID} -eq 0 ]]; then
        "$@"
    elif command -v sudo >/dev/null 2>&1; then
        sudo "$@"
    else
        fail "this script requires root or sudo access"
    fi
}

main() {
    local output_path="$PWD/kautuka-wsl-appliance.tar.gz"
    local releasever='44'
    local arch='amd64'
    local image='quay.io/fedora/fedora:44'
    local container_runtime='podman'
    local work_dir=''

    while [[ $# -gt 0 ]]; do
        case "$1" in
            --output)
                output_path="$2"
                shift 2
                ;;
            --releasever)
                releasever="$2"
                shift 2
                ;;
            --arch)
                arch="$2"
                shift 2
                ;;
            --image)
                image="$2"
                shift 2
                ;;
            --container-runtime)
                container_runtime="$2"
                shift 2
                ;;
            --work-dir)
                work_dir="$2"
                shift 2
                ;;
            --help)
                usage
                exit 0
                ;;
            *)
                fail "unknown argument: $1"
                ;;
        esac
    done

    require_command tar
    require_command "$container_runtime"

    local cleanup='true'
    if [[ -z "$work_dir" ]]; then
        work_dir="$(create_work_dir "$output_path")"
    else
        mkdir -p "$work_dir"
        cleanup='false'
    fi

    local rootfs_dir="$work_dir/rootfs"
    local container_script="$work_dir/build-rootfs.sh"
    local container_arch='x86_64'

    case "$arch" in
        amd64|x86_64)
            container_arch='x86_64'
            ;;
        arm64|aarch64)
            container_arch='aarch64'
            ;;
        *)
            fail "unsupported architecture: $arch"
            ;;
    esac

    if [[ "$cleanup" == 'true' ]]; then
        trap "maybe_sudo rm -rf '$work_dir'" EXIT
    fi

    maybe_sudo rm -rf "$rootfs_dir"
    mkdir -p "$rootfs_dir"

    cat <<'EOF' > "$container_script"
#!/usr/bin/env bash
set -euo pipefail

rootfs_dir="$1"
releasever="$2"
arch="$3"

mkdir -p "$rootfs_dir"
dnf install -y \
    --use-host-config \
  --releasever "$releasever" \
  --forcearch "$arch" \
  --installroot "$rootfs_dir" \
  --setopt=install_weak_deps=False \
  --setopt=tsflags=nodocs \
  --nodocs \
  bash \
  ca-certificates \
  crun \
  dbus \
  dbus-broker \
  dbus-daemon \
  dnf \
  fuse-overlayfs \
  iproute \
  iptables \
  libicu \
  nftables \
  openssh-server \
  passwd \
  podman \
  shadow-utils \
  slirp4netns \
  sudo \
  systemd \
  systemd-container \
  systemd-pam \
  tar \
  util-linux-core \
  curl \
  python3

dnf clean all --use-host-config --installroot "$rootfs_dir"
rm -rf \
  "$rootfs_dir/var/cache/dnf" \
  "$rootfs_dir/var/lib/dnf/history.sqlite"* \
  "$rootfs_dir/usr/share/doc"/* \
  "$rootfs_dir/usr/share/info"/* \
  "$rootfs_dir/usr/share/man"/*

mkdir -p "$rootfs_dir/usr/share/doc/kautuka"
rpm --root "$rootfs_dir" -qa --qf '%{SOURCERPM}\n' \
  | sort -u \
  | grep -v '^[(]none[)]$' \
  > "$rootfs_dir/usr/share/doc/kautuka/FEDORA_SOURCE_RPMS.txt"

# Ensure unix_chkpwd has correct ownership to allow PAM checks inside WSL
chroot "$rootfs_dir" /usr/bin/chown root:root /usr/sbin/unix_chkpwd || true

# Security hardening: set secure umask and clear sensitive files
chroot "$rootfs_dir" /bin/sh -c 'umask 027'
chroot "$rootfs_dir" /bin/sh -c 'rm -rf /root/.ssh /root/.gnupg /root/.cache /root/.config || true'

if [[ -d "$rootfs_dir/usr/share/locale" ]]; then
  find "$rootfs_dir/usr/share/locale" -mindepth 1 -maxdepth 1 \
    ! -name 'en' \
    ! -name 'en_*' \
    ! -name 'locale.alias' \
    -exec rm -rf {} +
fi

install -d -m 0755 \
  "$rootfs_dir/etc/kautuka" \
  "$rootfs_dir/etc/containers/containers.conf.d" \
  "$rootfs_dir/etc/containers/containers.rootless.d" \
    "$rootfs_dir/var/lib/kautuka"

cat <<'WSLCONF' > "$rootfs_dir/etc/wsl.conf"
[boot]
systemd=true

[automount]
enabled=false

[interop]
enabled=false
appendWindowsPath=false
WSLCONF

mkdir -p "$rootfs_dir/etc/ssh/sshd_config.d"
cat <<'SSHCONF' > "$rootfs_dir/etc/ssh/sshd_config.d/kautuka-appliance.conf"
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin no
UsePAM yes
SSHCONF

cat <<'ROOTLESSCONF' > "$rootfs_dir/etc/containers/containers.rootless.d/100-kautuka-wsl.conf"
[engine]
cgroup_manager="cgroupfs"

[network]
firewall_driver="iptables"
ROOTLESSCONF

cat <<'CONTAINERSCONF' > "$rootfs_dir/etc/containers/containers.conf.d/100-kautuka-wsl.conf"
[engine]
cgroup_manager="cgroupfs"

[network]
firewall_driver="iptables"
CONTAINERSCONF

chroot "$rootfs_dir" /usr/sbin/useradd --system --user-group --create-home --home-dir /var/lib/kautuka --shell /usr/sbin/nologin kautuka || true

grep -q '^kautuka:' "$rootfs_dir/etc/subuid" 2>/dev/null || echo 'kautuka:100000:65536' >> "$rootfs_dir/etc/subuid"
grep -q '^kautuka:' "$rootfs_dir/etc/subgid" 2>/dev/null || echo 'kautuka:100000:65536' >> "$rootfs_dir/etc/subgid"

# Ensure runtime directory ownership and permissions are correct for the `kautuka` user
chroot "$rootfs_dir" /bin/sh -c 'chown -R kautuka:kautuka /var/lib/kautuka || true'
chmod 0750 "$rootfs_dir/var/lib/kautuka" || true

# Ensure sudo, sudoers, and user-namespace helpers keep true root ownership and
# setuid bits. Fedora may ship sudo at /usr/bin/sudo (with /usr/sbin → bin), so
# harden every present path rather than assuming a single location.
chroot "$rootfs_dir" /bin/sh -c '
  set -e
  for sudo_bin in /usr/bin/sudo /usr/sbin/sudo; do
    if [ -e "$sudo_bin" ] || [ -L "$sudo_bin" ]; then
      chown root:root "$sudo_bin"
      chmod 4755 "$sudo_bin"
    fi
  done
  for helper in /usr/bin/newuidmap /usr/bin/newgidmap; do
    if [ -e "$helper" ] || [ -L "$helper" ]; then
      chown root:root "$helper"
      chmod 4755 "$helper"
    fi
  done
  if [ -e /etc/sudo.conf ]; then
    chown root:root /etc/sudo.conf
    chmod 0644 /etc/sudo.conf
  fi
  if [ -e /etc/sudoers ]; then
    chown root:root /etc/sudoers
    chmod 0440 /etc/sudoers
  fi
  if [ -d /etc/sudoers.d ]; then
    chown root:root /etc/sudoers.d
    chmod 0750 /etc/sudoers.d
    find /etc/sudoers.d -type f -exec chown root:root {} + -exec chmod 0440 {} +
  fi
'

: > "$rootfs_dir/etc/machine-id"
rm -f "$rootfs_dir/var/lib/dbus/machine-id"

# Attempt to set SELinux contexts where possible (best-effort)
if [[ -x "$rootfs_dir/usr/sbin/restorecon" ]]; then
  chroot "$rootfs_dir" /usr/sbin/restorecon -Rv /var/lib/kautuka 2>/dev/null || true
fi

mkdir -p "$rootfs_dir/etc/systemd/system/multi-user.target.wants"
ln -sfn /usr/lib/systemd/system/sshd.service "$rootfs_dir/etc/systemd/system/multi-user.target.wants/sshd.service"

find "$rootfs_dir/var/log" -type f -delete 2>/dev/null || true

# Pack inside the container so tar headers record the container's UID view
# (root = 0). Host-side packing after a rootless Podman bind-mount remaps those
# files to the invoking user (commonly 1000), which breaks sudo setuid on import.
mkdir -p /work/out
rm -f /work/out/rootfs.tar.gz
tar --numeric-owner --xattrs --acls -czf /work/out/rootfs.tar.gz -C "$rootfs_dir" .
EOF
    chmod +x "$container_script"

    "$container_runtime" run --rm \
        -v "$work_dir:/work" \
        -w /work \
        "$image" \
        bash /work/build-rootfs.sh /work/rootfs "$releasever" "$container_arch"

    mkdir -p "$(dirname "$output_path")"
    maybe_sudo rm -f "$output_path"
    maybe_sudo cp -f "$work_dir/out/rootfs.tar.gz" "$output_path"
    maybe_sudo chown "$(id -u):$(id -g)" "$output_path"

    # Fail closed: refuse a base rootfs whose privileged paths are not root-owned.
    # awk must drain the whole listing; exiting early closes the pipe and makes tar
    # die with a write error, which pipefail would report as a build failure.
    local sudo_owner
    sudo_owner="$(tar -tvf "$output_path" | awk '/\.\/usr\/bin\/sudo$/ { owner = $2 } END { print owner }')"
    [[ "$sudo_owner" == '0/0' ]] || fail "base rootfs sudo owner must be 0/0 (found ${sudo_owner:-missing})"
    local sudoers_owner
    sudoers_owner="$(tar -tvf "$output_path" | awk '/\.\/etc\/sudoers$/ { owner = $2 } END { print owner }')"
    [[ "$sudoers_owner" == '0/0' ]] || fail "base rootfs sudoers owner must be 0/0 (found ${sudoers_owner:-missing})"

    echo "Created WSL appliance rootfs at $output_path"
}

main "$@"
