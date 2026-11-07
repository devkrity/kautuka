#!/usr/bin/env python3
# Kautuka-Offline-Images-Protocol: 1
"""Stage signed, immutable offline archives for installation and recovery."""
import grp, hashlib, json, os, re, secrets, shutil, stat, sys
from pathlib import Path
if len(sys.argv) == 2 and sys.argv[1] == '--capabilities':
    print('offline-image-stage-v1')
    raise SystemExit(0)
if os.geteuid() != 0 or len(sys.argv) != 2:
    raise SystemExit('error: requires root and one verified payload directory')
payload = Path(sys.argv[1])
inventory, source = payload / 'bin/runtime-images.json', payload / 'oci-images'
with inventory.open() as stream:
    document = json.load(stream)
roles = {'kautuka-base', 'openobserve', 'git-pkgs-proxy', 'core-credentialshost',
         'core-channelhost', 'core-gateway', 'core-webhost', 'egress-setup',
         'egress-envoy', 'egress-dns', 'image-acquisition'}
entries = document.get('images')
if document.get('schemaVersion') != 1 or not isinstance(entries, list) or len(entries) != len(roles):
    raise SystemExit('error: incomplete offline container image inventory')
seen = set()
for entry in entries:
    role, digest, name = (entry.get(key) for key in ('id', 'archiveSha256', 'archive'))
    if role not in roles or role in seen or not isinstance(digest, str) or not re.fullmatch('[a-f0-9]{64}', digest):
        raise SystemExit('error: invalid offline image binding')
    if name not in (f'{role}-{digest}.tar', f'{role}-{digest}.oci') or entry.get('platform') != 'linux/amd64':
        raise SystemExit('error: offline image archive must have a content-addressed name and supported platform')
    seen.add(role)
# Never repair or traverse a service-writable cache parent as root.
for path in ('/var', '/var/lib', '/var/lib/kautuka'):
    info = os.lstat(path)
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022:
        raise SystemExit('error: offline image store requires root-owned, non-writable ancestry')
gid = grp.getgrnam('kautuka').gr_gid
cache = '/var/lib/kautuka/oci-images'
try:
    os.mkdir(cache, 0o700)
    created = os.open(cache, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        os.fchown(created, 0, gid)
        os.fchmod(created, 0o750)
        os.fsync(created)
    finally:
        os.close(created)
    parent = os.open('/var/lib/kautuka', os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try: os.fsync(parent)
    finally: os.close(parent)
except FileExistsError:
    pass
info = os.lstat(cache)
if not stat.S_ISDIR(info.st_mode) or info.st_uid != 0 or info.st_gid != gid or stat.S_IMODE(info.st_mode) != 0o750:
    raise SystemExit('error: offline image store must be root:kautuka mode 0750')

def checked_archive(fd, digest, installed=False):
    info = os.fstat(fd)
    if not stat.S_ISREG(info.st_mode) or (installed and (info.st_uid != 0 or info.st_gid != gid or stat.S_IMODE(info.st_mode) != 0o640)):
        raise ValueError('Unsafe offline image archive ownership or type')
    with os.fdopen(os.dup(fd), 'rb') as stream:
        actual = hashlib.file_digest(stream, 'sha256').hexdigest()
    if actual != digest:
        raise ValueError('Offline image archive differs from signed inventory')
    os.lseek(fd, 0, os.SEEK_SET)

directory = os.open(cache, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
try:
    for entry in entries:
        name, digest = entry['archive'], entry['archiveSha256']
        incoming = os.open(source / name, os.O_RDONLY | os.O_NOFOLLOW)
        temporary = None
        try:
            checked_archive(incoming, digest)
            try:
                existing = os.open(name, os.O_RDONLY | os.O_NOFOLLOW, dir_fd=directory)
            except FileNotFoundError:
                existing = None
            if existing is not None:
                try: checked_archive(existing, digest, installed=True)
                finally: os.close(existing)
                continue
            temporary = '.stage-' + secrets.token_hex(16)
            output = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=directory)
            try:
                with os.fdopen(os.dup(incoming), 'rb') as reader, os.fdopen(os.dup(output), 'wb') as writer:
                    shutil.copyfileobj(reader, writer)
                    writer.flush()
                os.fchown(output, 0, gid)
                os.fchmod(output, 0o640)
                os.fsync(output)
            finally:
                os.close(output)
            # No overwrite, even if a concurrent invocation stages this name.
            os.link(temporary, name, src_dir_fd=directory, dst_dir_fd=directory, follow_symlinks=False)
            os.unlink(temporary, dir_fd=directory)
            temporary = None
            os.fsync(directory)
        finally:
            os.close(incoming)
            if temporary is not None: os.unlink(temporary, dir_fd=directory)
finally:
    os.close(directory)
