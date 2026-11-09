#!/usr/bin/env python3
"""Local development in an explicitly owned, dedicated Linux appliance.

Uses the real image producer, installer preparation and Manager lifecycle. Never
imports Windows state, adopts an installed appliance or signs/publishes a release.
"""
import argparse
import fcntl
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import pwd
import secrets
import shutil
import subprocess
import tempfile
import time
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

CACHE = Path('/var/cache/kautuka-dev')
MARKER = Path('/etc/kautuka-dev.json')
STATE = Path('/var/lib/kautuka')
INITIAL_CHANNEL = 'preview'
SERVICES = {'manager': 'Manager', 'updateservice': 'UpdateService', 'gateway': 'Gateway',
            'webhost': 'WebHost', 'channelhost': 'ChannelHost', 'credentialshost': 'CredentialsHost'}
SUPPORT_INPUTS = ('src/images/runtime-release-inputs.json', 'src/images/kautuka-egress-setup/Containerfile',
                  'src/images/kautuka-base/release.json', 'src/images/kautuka-pkgs-proxy/release.json',
                  'src/images/openobserve/release.json')


def save_record(path, value):
    temporary = path.with_name(path.name + '.next')
    temporary.write_text(json.dumps(value, indent=2))
    os.replace(temporary, path)


def _read_dev_build_record(path, identity):
    try:
        if path.is_symlink() or not path.is_file(): return None
        value = json.loads(path.read_text())
    except (OSError, ValueError, json.JSONDecodeError):
        return None
    if (not isinstance(value, dict) or value.get('schemaVersion') != 1 or value.get('workspaceId') != identity
            or value.get('publication') != 'native-aot'
            or not isinstance(value.get('fingerprint'), str)
            or len(value['fingerprint']) != 64
            or any(character not in '0123456789abcdef' for character in value['fingerprint'])
            or not isinstance(value.get('version'), str)
            or not value['version']):
        return None
    return value


def cleanup_superseded_dev_builds(identity, cache_root=CACHE):
    """Remove only completed prepared payloads owned by this dev workspace."""
    cache_root = Path(cache_root)
    if cache_root.is_symlink() or not cache_root.is_dir(): return

    active_path = cache_root / 'build.json'
    pending_path = cache_root / 'pending-build.json'
    active = _read_dev_build_record(active_path, identity)
    pending = _read_dev_build_record(pending_path, identity)
    if (active_path.is_symlink() or pending_path.is_symlink()
            or (active_path.exists() and active is None) or (pending_path.exists() and pending is None)):
        return
    protected_fingerprints = {record['fingerprint'] for record in (active, pending) if record}
    protected_versions = {record['version'] for record in (active, pending) if record}

    # Keep prepared payloads named by the active or pending build. Ignore
    # interrupted staging directories and anything without a complete record.
    try:
        for child in cache_root.iterdir():
            if child.is_symlink() or not child.is_dir(): continue
            if not child.name.startswith('prepared-') or child.name.startswith('prepared-next-'): continue
            record = _read_dev_build_record(child / 'build.json', identity)
            if record is None or record['fingerprint'] in protected_fingerprints or record['version'] in protected_versions:
                continue
            if child.name != 'prepared-' + record['fingerprint']: continue
            if child.resolve().parent != cache_root.resolve(): continue
            shutil.rmtree(child)
    except OSError:
        # Cache cleanup is best effort; it must not make an otherwise valid
        # development install fail.
        pass


def switch_pointers(root, version):
    prior = {role: os.readlink(root / role / 'current') for role in SERVICES}
    try:
        for role in SERVICES:
            pointer = root / role / 'current'
            temporary = pointer.with_name('dev-next')
            temporary.unlink(missing_ok=True)
            temporary.symlink_to('versions/' + version)
            os.replace(temporary, pointer)
    except BaseException:
        for role, target in prior.items():
            pointer = root / role / 'current'
            temporary = pointer.with_name('dev-next')
            temporary.unlink(missing_ok=True)
            temporary.symlink_to(target)
            os.replace(temporary, pointer)
        raise


def command(args, **kwargs):
    return subprocess.check_output(list(map(str, args)), text=True, cwd='/var/tmp', **kwargs).strip()


def ownership(identity):
    if os.geteuid() != 0 or MARKER.is_symlink():
        raise ValueError('Run only as root inside the dedicated development distro')
    info = MARKER.stat()
    marker = json.loads(MARKER.read_text())
    if info.st_uid != 0 or info.st_mode & 0o022 or marker != {'schemaVersion': 1, 'workspaceId': identity}:
        raise ValueError('Development distro ownership differs; no application was changed')
    for root in (CACHE, STATE, Path('/opt/kautuka'), Path('/etc/kautuka')):
        if root.is_symlink(): raise ValueError('Development paths cannot be symlinks')


def as_runtime(*args):
    u = pwd.getpwnam('kautuka')
    return command(['runuser', '-u', u.pw_name, '--', 'env', 'HOME=' + u.pw_dir,
                    'XDG_RUNTIME_DIR=/run/user/' + str(u.pw_uid),
                    'DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/' + str(u.pw_uid) + '/bus', *args])


def stop(session=None):
    CACHE.mkdir(mode=0o700, exist_ok=True)
    with (CACHE / 'stop.lock').open('a') as lease:
        fcntl.flock(lease, fcntl.LOCK_EX)
        stop_locked(session)


def stop_locked(session):
    CACHE.mkdir(mode=0o700, exist_ok=True)
    owner = CACHE / 'launcher-session'
    if session and (not owner.is_file() or owner.read_text() != session): return
    (CACHE / 'stop').touch()
    if not Path('/etc/kautuka/service-scope.json').exists(): return
    # Stop controllers before changing their resources. Never use system prune.
    as_runtime('systemctl', '--user', 'stop', 'kautuka.target', 'kautuka-manager.service', 'kautuka-updateservice.service')
    ids = as_runtime('podman', 'ps', '--quiet').splitlines()
    if ids:
        containers = json.loads(as_runtime('podman', 'inspect', *ids))
        if any(not c.get('IsInfra') and c['Config'].get('Labels', {}).get('kautuka.managed') != 'manager' for c in containers):
            raise ValueError('Unexpected container in development runtime account; retained')
        # Fail closed before stopping workloads and trusted sidecars.
        proxies = [c['Id'] for c in containers if c['Name'].lstrip('/') in ('kautuka-egress-envoy', 'kautuka-egress-dns')]
        if proxies: as_runtime('podman', 'stop', '--time', '5', *proxies)
        applications = [c['Id'] for c in containers if not c.get('IsInfra') and c['Id'] not in proxies]
        if applications: as_runtime('podman', 'stop', '--time', '10', *applications)
        infra = [c['Id'] for c in containers if c.get('IsInfra')]
        if infra: as_runtime('podman', 'kill', '--signal', 'KILL', *infra)
    owner.unlink(missing_ok=True)


def module(repo, name):
    spec = importlib.util.spec_from_file_location(name.replace('-', '_'), repo / 'scripts' / (name + '.py'))
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


def support_fingerprint(root):
    digest = hashlib.sha256()
    for name in SUPPORT_INPUTS:
        path = root / name
        if not path.is_file(): return None
        digest.update(name.encode()); digest.update(b'\0'); digest.update(hashlib.sha256(path.read_bytes()).digest())
    return digest.hexdigest()


def can_reuse_support(contract_exists, fingerprint, previous_record):
    return contract_exists and fingerprint is not None and fingerprint == previous_record.get('supportFingerprint')


def save_local_output(role, image, source):
    cache = CACHE / 'local-outputs' / role
    cache.mkdir(parents=True, exist_ok=True)
    saved = cache / image.removeprefix('sha256:')
    if not saved.exists():
        with tempfile.TemporaryDirectory(prefix='output-', dir=cache) as temporary:
            staging = Path(temporary) / 'payload'
            shutil.copytree(source, staging)
            os.rename(staging, saved)


def repair_local_images(repo, payload, record, inventory_path=None):
    """Recreate missing dev images from retained AOT outputs before activation."""
    if not record.get('localImages'): return False
    prepared_inventory = payload / 'bin/runtime-images.json'
    inventory_path = inventory_path or prepared_inventory
    inventory = json.loads(inventory_path.read_text())
    prepared = json.loads(prepared_inventory.read_text())
    if inventory.get('developmentLocalImages') is not True:
        raise ValueError('Prepared development image inventory is invalid')
    missing = []
    for role in ('credentialshost', 'channelhost', 'gateway', 'webhost'):
        entries = [entry for entry in inventory['images'] if entry.get('id') == 'core-' + role]
        if len(entries) != 1: raise ValueError('Expected one development core image: ' + role)
        entry = entries[0]
        image = entry.get('localImageId', '')
        if not isinstance(image, str) or len(image) != 71 or not image.startswith('sha256:') or any(c not in '0123456789abcdef' for c in image[7:]):
            raise ValueError('Invalid immutable development image ID')
        try:
            as_runtime('podman', 'image', 'exists', image)
        except subprocess.CalledProcessError as error:
            if error.returncode != 1: raise
            missing.append((role, entry))
            continue
        inspected = json.loads(as_runtime('podman', 'image', 'inspect', image))
        if (len(inspected) != 1 or inspected[0].get('Id', '').removeprefix('sha256:') != image[7:]
                or inspected[0].get('Os') != 'linux' or inspected[0].get('Architecture') != 'amd64'):
            raise ValueError('Development image identity or platform differs from its inventory')
    if not missing: return False
    print('[launch-kautuka] Restoring missing development images from retained Native AOT outputs...', flush=True)
    producer = module(repo, 'package-runtime-images')
    local = module(repo, 'dev-local-images')
    inputs = json.loads((repo / 'src/images/runtime-release-inputs.json').read_text())
    with tempfile.TemporaryDirectory(prefix='repair-images-', dir=CACHE) as temporary:
        for role, previous in missing:
            cached_output = CACHE / 'local-outputs' / role / previous['localImageId'][7:]
            if not cached_output.is_dir():
                archived = [entry for entry in prepared['images'] if entry.get('id') == previous['id']]
                if len(archived) != 1 or archived[0].get('localImageId') != previous['localImageId']:
                    raise ValueError('No retained Native AOT output for the missing development image: ' + role)
                cached_output = payload / 'services' / role
            replacement = local.build_one(producer, CACHE / 'podman-build', inputs, Path(temporary),
                                          cached_output, role, record['version'])
            save_local_output(role, replacement['localImageId'], cached_output)
            inventory['images'][inventory['images'].index(previous)] = replacement
    save_record(inventory_path, inventory)
    return True


def inputs(repo):
    names = command(['git', '-c', 'safe.directory=' + str(repo), '-C', repo, 'ls-files', '-co', '--exclude-standard']).splitlines()
    files = sorted({n for n in names if n.startswith(('src/backend/', 'src/shared/', 'src/images/', 'src/Directory.', 'scripts/'))
                    and not {'bin', 'obj', 'node_modules', '__pycache__'}.intersection(Path(n).parts)
                    and (repo / n).is_file()})
    assets = {'kautuka-web-tools.mcpb': repo / 'artifacts/mcpb/kautuka-web-tools-dev.mcpb'}
    web = repo / 'src/frontend/web/dist'
    if not (web / 'index.html').is_file() or any(not p.is_file() for p in assets.values()):
        raise ValueError('Build frontend and built-in bundles with npm run launch first')
    payload_scripts = {'scripts/package-runtime-images.py', 'scripts/bind-runtime-image-archives.py', 'scripts/dev-local-images.py',
                       'scripts/kautuka-apply-release.sh', 'scripts/kautuka-update-recovery.py', 'scripts/kautuka-runtime-images.py'}
    hashed = [(n, repo / n) for n in files if not n.startswith('scripts/') or n in payload_scripts]
    hashed += [('bundle/' + n, p) for n, p in assets.items()]
    hashed += [('web/' + str(p.relative_to(web)), p) for p in sorted(web.rglob('*')) if p.is_file()]
    digest = hashlib.sha256()
    # Bump for payload-generation/compilation changes in this driver. Controller
    # startup fixes alone can reuse an already completed Native AOT payload.
    digest.update(b'kautuka-native-aot-development-v1\0')
    for n, p in hashed:
        digest.update(n.encode()); digest.update(b'\0'); digest.update(hashlib.sha256(p.read_bytes()).digest())
    return files, assets, web, digest.hexdigest()


def installer(repo, body, *args):
    # Script content is fixed here; paths/values are positional parameters.
    script = 'export KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1\nsource "$1/scripts/kautuka-install-linux.sh.in"\n' + body
    subprocess.run(['bash', '-c', script, str(repo / 'scripts/kautuka-install-linux.sh.in'), str(repo), *map(str, args)], check=True, cwd='/var/tmp')


def build(repo, identity):
    CACHE.mkdir(mode=0o700, exist_ok=True)
    if shutil.disk_usage(CACHE).free < 12 * 1024**3:
        raise ValueError('Development build needs at least 12 GiB free in its Linux filesystem. Stop this dev distro and expand it with wsl --manage <dev-distro> --resize 64GB; no data was deleted.')
    files, assets, web, fingerprint = inputs(repo)
    active = CACHE / 'build.json'
    pending = CACHE / 'pending-build.json'
    prepared = CACHE / ('prepared-' + fingerprint)
    if active.is_file() and not pending.exists() and json.loads(active.read_text()).get('fingerprint') == fingerprint:
        record = json.loads(active.read_text())
        if record.get('localImages'):
            repair_local_images(repo, prepared / 'payload', record,
                                Path('/opt/kautuka/manager/current/runtime-images.json'))
        print('[launch-kautuka] Development build unchanged; reusing installed images.', flush=True)
        return
    contract_exists = Path('/etc/kautuka/service-scope.json').exists()
    support = support_fingerprint(repo)
    previous_record = json.loads(active.read_text()) if active.is_file() else {}
    reuse_support = can_reuse_support(contract_exists, support, previous_record)
    if contract_exists and not active.exists():
        unfinished = json.loads(pending.read_text()) if pending.is_file() else {}
        if unfinished.get('workspaceId') != identity or any(
                (Path('/opt/kautuka') / role / 'current').resolve() != Path('/opt/kautuka') / role / 'versions' / unfinished.get('version', '') for role in SERVICES):
            raise ValueError('An existing installation has no matching development build record; it will not be adopted')
    if (prepared / 'build.json').is_file():
        record = json.loads((prepared / 'build.json').read_text())
        if record.get('workspaceId') != identity or record.get('publication') != 'native-aot':
            raise ValueError('Prepared build is not this checkout Native AOT payload')
        repair_local_images(repo, prepared / 'payload', record)
        print('[launch-kautuka] Reusing the completed Native AOT payload for installation.', flush=True)
        install_prepared(repo, prepared / 'payload', record, contract_exists)
        return
    source = CACHE / 'source'
    source.mkdir(exist_ok=True)
    previous = CACHE / 'source-files.json'
    if previous.exists():
        for n in set(json.loads(previous.read_text())) - set(files):
            p = source / n
            if p.is_file() and not p.is_symlink(): p.unlink()
    for n in files:
        target = source / n; target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(repo / n, target)
    previous.write_text(json.dumps(files))
    properties = ET.parse(source / 'src/Directory.Build.props').getroot().find('PropertyGroup')
    version = properties.findtext('KautukaDevVersion') + '.' + fingerprint[:12] + '-' + secrets.token_hex(3)
    numeric_version = properties.findtext('KautukaDevNumericVersion')
    sha = command(['git', '-c', 'safe.directory=' + str(repo), '-C', repo, 'rev-parse', 'HEAD'])
    producer = module(source, 'package-runtime-images')
    wrapper = CACHE / 'podman-build'
    wrapper.write_text('#!/bin/sh\nexec /usr/bin/podman --root /var/cache/kautuka-dev/engine --runroot /run/kautuka-dev-build --events-backend=file --storage-driver=overlay --cgroup-manager=cgroupfs "$@"\n')
    wrapper.chmod(0o700)
    config = json.loads((source / 'src/images/runtime-release-inputs.json').read_text())
    if contract_exists:
        try: sdk = producer.inspect(str(wrapper), config['sdkImage'])
        except subprocess.CalledProcessError: sdk = producer.pull(str(wrapper), config['sdkImage'])
    else:
        sdk = producer.pull(str(wrapper), config['sdkImage'])
    with tempfile.TemporaryDirectory(prefix='build-', dir=CACHE) as temp:
        work = Path(temp); payload = work / 'payload'
        (payload / 'bin').mkdir(parents=True); (payload / 'oci-images').mkdir()
        for source_name, target_name in (('kautuka-apply-release.sh', 'kautuka-apply-release'),
                                         ('kautuka-update-recovery.py', 'kautuka-update-recovery'),
                                         ('kautuka-runtime-images.py', 'kautuka-runtime-images')):
            target = payload / 'bin' / target_name
            target.write_bytes((source / 'scripts' / source_name).read_bytes())
            target.chmod(0o755)
        (CACHE / 'nuget').mkdir(exist_ok=True)
        bundles = payload / 'services/gateway/built-in-mcpb'; bundles.mkdir(parents=True)
        for role in ('manager', 'updateservice', 'm365-mail', 'google-mail'):
            connector = role.endswith('-mail')
            project = 'kautuka-' + role if connector else 'Kautuka.' + SERVICES[role]
            project_path = 'backend/connectors/' + role + '/' + project + '.csproj' if connector else 'backend/' + project + '/' + project + '.csproj'
            output = work / role if connector else payload / ('bin' if role == 'manager' else 'services/updateservice')
            output.mkdir(parents=True, exist_ok=True)
            print('[launch-kautuka] Publishing Native AOT ' + role + '...', flush=True)
            producer.run(str(wrapper), 'run', '--rm', '--pull=never', '--volume', str(source / 'src') + ':/src:rw',
                         '--volume', str(output) + ':/out:rw', '--volume', str(CACHE / 'nuget') + ':/nuget:rw',
                         '--env', 'NUGET_PACKAGES=/nuget', '--workdir', '/src', sdk,
                         'dotnet', 'publish', project_path, '-c', 'Release', '-r', 'linux-x64',
                         '--self-contained', 'true', '-p:PublishAot=true', '-p:PublishSingleFile=true', '-p:InvariantGlobalization=true',
                         '-p:KautukaReleaseArtifactBuild=true', '-p:Version=' + version, '-o', '/out')
            if (output / (project + '.dll')).exists() or (output / 'libcoreclr.so').exists() or (output / project).read_bytes()[:4] != b'\x7fELF':
                raise ValueError('Native AOT publication did not produce a native-only Linux executable: ' + role)
            if connector:
                with zipfile.ZipFile(bundles / (project + '.mcpb'), 'w', zipfile.ZIP_DEFLATED) as archive:
                    archive.write(source / 'src/backend/connectors' / role / 'manifest.json', 'manifest.json')
                    for file in sorted(output.rglob('*')):
                        if file.is_file() and file.suffix not in ('.pdb', '.dbg'):
                            archive.write(file, str(file.relative_to(output)))
            else:
                shutil.copy2(output / project, output / ('kautuka-' + role))
        for n, p in assets.items(): shutil.copy2(p, bundles / n)
        shutil.copytree(web, payload / 'services/webhost/wwwroot')
        if reuse_support:
            module(source, 'dev-local-images').prepare(producer, wrapper, sdk, source, payload, work, version, sha, config)
            record = {'schemaVersion': 1, 'workspaceId': identity, 'fingerprint': fingerprint,
                      'version': version, 'sourceSha': sha, 'publication': 'native-aot', 'localImages': True,
                      'supportFingerprint': support}
            stage = Path(tempfile.mkdtemp(prefix='prepared-next-', dir=CACHE))
            shutil.copytree(payload, stage / 'payload')
            save_record(stage / 'build.json', record)
            os.rename(stage, prepared)
            install_prepared(repo, prepared / 'payload', record, True)
            print('[launch-kautuka] Development images installed; application data retained.', flush=True)
            return
        helper = producer.pull(str(wrapper), config['acquisitionImage'])
        entries = []
        for role, directory in (('kautuka-base', 'kautuka-base'), ('git-pkgs-proxy', 'kautuka-pkgs-proxy'), ('openobserve', 'openobserve')):
            reference = json.loads((source / 'src/images' / directory / 'release.json').read_text())['image']
            image = producer.pull(str(wrapper), reference)
            producer.export(str(wrapper), helper, image, payload / 'oci-images' / (role + '.oci'), work)
            entries.append({'id': role, 'image': reference, 'archive': role + '.oci'})
        inventory = producer.binding.bind_inventory({'schemaVersion': 1, 'images': entries}, payload / 'oci-images', 'linux/amd64')
        (payload / 'bin/runtime-images.json').write_text(json.dumps(inventory))
        producer.package(argparse.Namespace(source_root=source, payload=payload, work_root=work, runtime=str(wrapper),
                                           version=version, numeric_version=numeric_version, source_sha=sha,
                                           nuget_cache=CACHE / 'nuget'))
        inventory = json.loads((payload / 'bin/runtime-images.json').read_text())
        for entry in inventory['images']:
            if not entry['id'].startswith('core-'): continue
            role = entry['id'][5:]; dest = payload / 'services' / role; dest.mkdir(parents=True, exist_ok=True)
            cid = producer.run(str(wrapper), 'create', '--pull=never', '--network', 'none', entry['localImageId']).strip()
            try: producer.run(str(wrapper), 'cp', cid + ':/app/.', str(dest))
            finally: producer.run(str(wrapper), 'rm', cid)
            shutil.copy2(dest / ('Kautuka.' + SERVICES[role]), dest / ('kautuka-' + role))
        record = {'schemaVersion': 1, 'workspaceId': identity, 'fingerprint': fingerprint,
                  'version': version, 'sourceSha': sha, 'publication': 'native-aot', 'supportFingerprint': support}
        # Preserve a completed native build independently of installation retries.
        # Application source changes invalidate this cache; launcher-only fixes do not.
        stage = Path(tempfile.mkdtemp(prefix='prepared-next-', dir=CACHE))
        shutil.copytree(payload, stage / 'payload')
        save_record(stage / 'build.json', record)
        os.rename(stage, prepared)
        install_prepared(repo, prepared / 'payload', record, contract_exists)
    print('[launch-kautuka] Development images installed; application data retained.', flush=True)


def install_prepared(repo, payload, record, existing):
    # Only a completed AOT build may replace the stopped development cohort.
    stop()
    save_record(CACHE / 'pending-build.json', record)
    publish(repo, payload, record['version'], existing, record.get('localImages', False))
    save_record(CACHE / 'build.json', record)
    (CACHE / 'pending-build.json').unlink()


def publish(repo, payload, version, existing, local_images=False):
    if not existing:
        key = CACHE / 'local-development-key.pem'; public = CACHE / 'local-development-public.pem'
        subprocess.run(['openssl', 'genpkey', '-algorithm', 'EC', '-pkeyopt', 'ec_paramgen_curve:P-256', '-out', key], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        key.chmod(0o600)
        subprocess.run(['openssl', 'pkey', '-in', key, '-pubout', '-out', public], check=True, stdout=subprocess.DEVNULL)
        installer(repo, '''
service_user=kautuka; service_group=kautuka
internal_state_root=/var/lib/kautuka; visible_root=/var/lib/kautuka; install_root=/opt/kautuka/manager
version="$3"; release_channel="$4"; manifest_url=''; github_release_candidate=false; skip_enable=true
KAUTUKA_DEFAULT_MANIFEST_PUBLIC_KEY_PEM="$(cat /var/cache/kautuka-dev/local-development-public.pem)"
install_fresh_container_appliance "$2"
''', payload, version, INITIAL_CHANNEL)
    else:
        for role in SERVICES:
            src = payload / ('bin' if role == 'manager' else 'services/' + role)
            dest = Path('/opt/kautuka') / role / 'versions' / version
            if dest.exists():
                pending = json.loads((CACHE / 'pending-build.json').read_text())
                if pending.get('version') != version or dest.resolve() != dest:
                    raise ValueError('Development version path does not belong to this pending build')
            shutil.copytree(src, dest, dirs_exist_ok=True)
        if local_images:
            marker = Path('/etc/kautuka/development-local-images')
            if marker.is_symlink(): raise ValueError('Development image marker cannot be a symlink')
            marker.write_text('kautuka-dev-local-images-v1')
            marker.chmod(0o644)
            dropin = Path('/etc/systemd/user/kautuka-manager.service.d')
            dropin.mkdir(parents=True, exist_ok=True)
            (dropin / 'development-local-images.conf').write_text('[Service]\nEnvironment=KAUTUKA_DEV_LOCAL_IMAGE_INVENTORY=1\n')
        else:
            subprocess.run(['python3', '/usr/local/lib/kautuka/runtime-images.py', payload], check=True)
        installer(repo, 'install_apply_release_helper "$2"\n', payload)
        switch_pointers(Path('/opt/kautuka'), version)
    settings = Path('/etc/kautuka/manager.json')
    value = json.loads(settings.read_text()); value['Update'] = {'Enabled': False}
    settings.write_text(json.dumps(value, indent=2))


def run(repo, port, session):
    identifiers = ('kautuka-manager', 'kautuka-updateservice', 'kautuka-gateway',
                   'kautuka-webhost', 'kautuka-channelhost', 'kautuka-credentialshost')
    matches = []
    for identifier in identifiers:
        if matches: matches.append('+')
        matches.append('SYSLOG_IDENTIFIER=' + identifier)
    logs = subprocess.Popen(['journalctl', '--follow', '--since=now', '--no-pager', '--output=short', *matches])
    try:
        run_stack(repo, port, session)
    finally:
        logs.terminate()
        logs.wait()


def run_stack(repo, port, session):
    started = time.monotonic()
    record = CACHE / 'build.json'
    if not record.is_file(): raise ValueError('No successful development build; run npm run launch -- --build')
    if json.loads(record.read_text()).get('publication') != 'native-aot':
        raise ValueError('The previous build is not Native AOT; run npm run launch -- --build')
    if (CACHE / 'pending-build.json').exists(): raise ValueError('The previous build was interrupted; run npm run launch -- --build')
    version = json.loads(record.read_text())['version']
    if any((Path('/opt/kautuka') / role / 'current').resolve() != Path('/opt/kautuka') / role / 'versions' / version for role in SERVICES):
        raise ValueError('Development service pointers differ from the completed build; run npm run launch -- --build')
    (CACHE / 'launcher-session').write_text(session)
    (CACHE / 'stop').unlink(missing_ok=True)
    settings = Path('/etc/kautuka/manager.json'); value = json.loads(settings.read_text())
    value['CoreServices']['WebHostPort'] = port
    settings.write_text(json.dumps(value, indent=2))
    print('[launch-kautuka] Preparing the existing rootless runtime...', flush=True)
    installer(repo, '''
ensure_rootless_podman_ready kautuka kautuka /var/lib/kautuka /run/user/$(id -u kautuka) containers
prepare_runtime_telemetry_directory /var/lib/kautuka/telemetry kautuka kautuka
install_socket_runtime_dirs kautuka containers kautuka
''')
    as_runtime('systemctl', '--user', 'daemon-reload')
    as_runtime('systemctl', '--user', 'start', 'kautuka-manager.service')
    deadline = time.monotonic() + 360
    while not (STATE / 'manager/.manager-updateservice-client.pfx').is_file():
        if (CACHE / 'stop').exists(): return
        if time.monotonic() >= deadline: raise ValueError('Manager did not publish its local bootstrap identity')
        time.sleep(1)
    as_runtime('systemctl', '--user', 'start', 'kautuka-updateservice.service', 'kautuka.target')
    print('[launch-kautuka] Manager started; waiting for core containers and WebHost...', flush=True)
    next_progress = time.monotonic() + 10
    # Do not enable boot-time activation: this is a foreground development stack.
    while True:
        if (CACHE / 'stop').exists(): return
        try:
            raw = command(['curl', '--noproxy', '*', '--fail', '--silent', '--max-time', '2', '--unix-socket',
                           '/run/kautuka/gateway/gateway.sock', 'http://localhost/api/runtime/system-health'], stderr=subprocess.DEVNULL)
            health = json.loads(raw)
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
            with opener.open('http://127.0.0.1:' + str(port) + '/', timeout=2) as response:
                ready = response.status == 200
            if ready and health.get('lifecycle', {}).get('coreReady') and health.get('workspace', {}).get('status') == 'healthy': break
        except (OSError, ValueError, subprocess.CalledProcessError): pass
        if time.monotonic() >= deadline: raise ValueError('Development containers did not become healthy; inspect the Manager user-service journal inside this development distro')
        if time.monotonic() >= next_progress:
            print(f'[launch-kautuka] Still waiting for runtime health ({time.monotonic() - started:.0f}s); no compilation is running.', flush=True)
            next_progress = time.monotonic() + 10
        time.sleep(1)
    print(f'\n[launch-kautuka] Network-controlled development stack ready in {time.monotonic() - started:.1f}s: http://localhost:{port}', flush=True)
    cleanup_superseded_dev_builds(json.loads(record.read_text())['workspaceId'])
    token = STATE / 'gateway-data/setup-token'
    if token.is_file(): print('[launch-kautuka] First setup token is stored inside this distro at /var/lib/kautuka/gateway-data/setup-token.', flush=True)
    print('[launch-kautuka] Ctrl+C stops this stack. Data is retained in the development distro.', flush=True)
    while not (CACHE / 'stop').exists(): time.sleep(1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', required=True, type=Path)
    parser.add_argument('--workspace-id', required=True)
    parser.add_argument('--port', type=int, default=20229)
    parser.add_argument('--session', default='')
    parser.add_argument('--service')
    parser.add_argument('--service-action', choices=['start', 'restart', 'rebuild'], default='restart')
    parser.add_argument('action', choices=['build', 'run', 'stop', 'service'])
    args = parser.parse_args()
    ownership(args.workspace_id)
    if args.action == 'service':
        CACHE.mkdir(mode=0o700, exist_ok=True)
        # The foreground launch holds launch.lock for its lifetime. Service
        # builds have their own lease; activation also serializes with shutdown.
        with (CACHE / 'service.lock').open('a') as lease:
            fcntl.flock(lease, fcntl.LOCK_EX)
            module(args.repo.resolve(strict=True), 'dev-appliance-service').operate(
                args.repo.resolve(strict=True), args.workspace_id, args.service, args.service_action)
        return
    if args.action == 'stop': stop(args.session); return
    CACHE.mkdir(mode=0o700, exist_ok=True)
    with (CACHE / 'launch.lock').open('a') as lease:
        fcntl.flock(lease, fcntl.LOCK_EX | fcntl.LOCK_NB)
        if args.action == 'build': build(args.repo.resolve(strict=True), args.workspace_id)
        else:
            try:
                run(args.repo.resolve(strict=True), args.port, args.session)
            except KeyboardInterrupt:
                pass
            finally:
                stop(args.session)


if __name__ == '__main__': main()
