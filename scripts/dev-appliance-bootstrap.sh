#!/usr/bin/env bash
set -euo pipefail
[[ "$EUID" == 0 && "$#" == 1 && "$1" =~ ^[a-f0-9]{64}$ ]] || exit 2
python3 - "$1" <<'PY'
import json,pathlib,sys
p=pathlib.Path('/etc/kautuka-dev.json')
assert not p.is_symlink() and p.stat().st_uid==0
d=json.loads(p.read_text())
assert d.get('schemaVersion')==1 and d.get('workspaceId')==sys.argv[1], 'Not this checkout development distro'
assert pathlib.Path('/etc/os-release').read_text().find('ID=fedora')>=0, 'Fedora is required'
assert pathlib.Path('/proc/1/comm').read_text().strip()=='systemd', 'Enable systemd in the dedicated WSL distro and restart it'
PY
if [[ ! -f /var/cache/kautuka-dev/prerequisites-v1 ]]; then
  dnf install -y podman passt slirp4netns fuse-overlayfs shadow-utils-subid sudo \
    python3 git openssl curl tar gzip util-linux libicu zlib openssl-libs policycoreutils
  install -d -m 0700 /var/cache/kautuka-dev
  touch /var/cache/kautuka-dev/prerequisites-v1
fi
