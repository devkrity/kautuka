#!/usr/bin/env python3
"""Fixed-service development operations through the existing Manager reconciler."""
import copy
from contextlib import contextmanager
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
import xml.etree.ElementTree as ET

CORES = {'credentialshost': 'CredentialsHost', 'channelhost': 'ChannelHost',
         'gateway': 'Gateway', 'webhost': 'WebHost'}
HOSTS = {'manager', 'updateservice'}
INVENTORY = Path('/opt/kautuka/manager/current/runtime-images.json')
MAINTENANCE = Path('/var/lib/kautuka-recovery/maintenance.json')


@contextmanager
def activation_lease(driver):
    import fcntl
    with (driver.CACHE / 'stop.lock').open('a') as lease:
        fcntl.flock(lease, fcntl.LOCK_EX)
        if (driver.CACHE / 'stop').exists():
            raise ValueError('The development launcher stopped during this operation')
        yield


def load_driver(repo):
    spec = importlib.util.spec_from_file_location('kautuka_dev_driver', repo / 'scripts/dev-appliance.py')
    driver = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(driver)
    return driver


def replace_inventory(inventory, role, replacement):
    result = copy.deepcopy(inventory)
    matching = [entry for entry in result['images'] if entry['id'] == 'core-' + role]
    if len(matching) != 1 or replacement['id'] != matching[0]['id']:
        raise ValueError('Expected one exact core image inventory entry')
    result['images'] = [replacement if entry['id'] == replacement['id'] else entry for entry in result['images']]
    return result


def write_inventory(path, content):
    temporary = path.with_name(path.name + '.dev-service-next')
    try:
        with temporary.open('wb') as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        shutil.copystat(path, temporary)
        if hasattr(os, 'chown'):
            info = path.stat()
            os.chown(temporary, info.st_uid, info.st_gid)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def inspect_core(driver, service):
    values = json.loads(driver.as_runtime('podman', 'inspect', 'kautuka-' + service))
    if len(values) != 1:
        raise ValueError('Expected one selected service container')
    container = values[0]
    labels = container.get('Config', {}).get('Labels', {})
    if (labels.get('kautuka.managed'), labels.get('kautuka.role'), labels.get('kautuka.core-service')) != ('manager', 'core', service):
        raise ValueError('Selected container is not the owned core service')
    return container


def wait_ready(driver, service, expected_image, old_id=None):
    deadline = time.monotonic() + 180
    while time.monotonic() < deadline:
        try:
            current = inspect_core(driver, service)
            if (current['State']['Running'] and current['Image'].removeprefix('sha256:') == expected_image.removeprefix('sha256:')
                    and (old_id is None or current['Id'] != old_id)):
                raw = driver.command(['curl', '--noproxy', '*', '--fail', '--silent', '--max-time', '2',
                                      '--unix-socket', '/run/kautuka/gateway/gateway.sock',
                                      'http://localhost/api/runtime/system-health'], stderr=subprocess.DEVNULL)
                health = json.loads(raw)
                if health.get('lifecycle', {}).get('coreReady') and health.get('workspace', {}).get('status') == 'healthy':
                    return current['Id']
        except (subprocess.CalledProcessError, OSError, ValueError):
            pass
        time.sleep(1)
    raise RuntimeError('Selected service did not become healthy after Manager reconciliation')


def copy_runtime_image(driver, image, destination):
    user = pwd.getpwnam('kautuka')
    with tempfile.TemporaryDirectory(prefix='kautuka-dev-copy-', dir='/var/tmp') as temporary:
        os.chown(temporary, user.pw_uid, user.pw_gid)
        cid = driver.as_runtime('podman', 'create', '--pull=never', '--network=none', image)
        try:
            driver.as_runtime('podman', 'cp', cid + ':/app/.', temporary)
            shutil.copytree(temporary, destination, dirs_exist_ok=True)
        finally:
            driver.as_runtime('podman', 'rm', cid)


def build_image(driver, repo, work, service, frontend_only, inventory, current):
    producer = driver.module(repo, 'package-runtime-images')
    engine = str(driver.CACHE / 'podman-build')
    if not Path(engine).is_file():
        raise ValueError('Initialize the development build engine with npm run launch first')
    inputs = json.loads((repo / 'src/images/runtime-release-inputs.json').read_text())
    context = work / 'image'; context.mkdir()
    original = next(entry for entry in inventory['images'] if entry['id'] == 'core-' + service)
    if current['Image'].removeprefix('sha256:') != original['localImageId'].removeprefix('sha256:'):
        raise ValueError('Running core image differs from the installed immutable inventory')
    local_images = inventory.get('developmentLocalImages') is True
    if frontend_only and local_images:
        copy_runtime_image(driver, original['localImageId'], context)
        binary = context / 'Kautuka.WebHost'
        if binary.read_bytes()[:4] != b'\x7fELF' or (context / 'Kautuka.WebHost.dll').exists() or (context / 'libcoreclr.so').exists():
            raise ValueError('Installed WebHost is not Native AOT')
        binary_hash = hashlib.sha256(binary.read_bytes()).hexdigest()
        shutil.rmtree(context / 'wwwroot')
    elif frontend_only:
        # Import the verified archive into the separate build engine, never trust a mutable tag.
        archive = driver.STATE / 'oci-images' / original['archive']
        with archive.open('rb') as stream:
            if hashlib.file_digest(stream, 'sha256').hexdigest() != original['archiveSha256']:
                raise ValueError('Installed WebHost archive hash differs')
        producer.run(engine, 'load', '-i', archive)
        original_id = producer.inspect(engine, original['localImageId'])
        cid = producer.run(engine, 'create', '--pull=never', '--network=none', original_id).strip()
        try:
            producer.run(engine, 'cp', cid + ':/app/.', context)
        finally:
            producer.run(engine, 'rm', cid)
        binary = context / 'Kautuka.WebHost'
        if binary.read_bytes()[:4] != b'\x7fELF' or (context / 'Kautuka.WebHost.dll').exists() or (context / 'libcoreclr.so').exists():
            raise ValueError('Installed WebHost is not Native AOT')
        binary_hash = hashlib.sha256(binary.read_bytes()).hexdigest()
        shutil.rmtree(context / 'wwwroot')
        base = inputs['coreRuntimeImage']; producer.pull(engine, base)
        recipe = 'FROM ' + base + '\nCOPY . /app\nWORKDIR /app\nENTRYPOINT ["/app/Kautuka.WebHost"]\n'
    else:
        print('[launch-kautuka] Preparing source for Native AOT ' + service + '...', flush=True)
        source = work / 'source'
        files, _, _, _ = driver.inputs(repo)
        for name in files:
            if not name.startswith('src/'): continue
            target = source / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(repo / name, target)
        if local_images:
            try: sdk = producer.inspect(engine, inputs['sdkImage'])
            except subprocess.CalledProcessError: sdk = producer.pull(engine, inputs['sdkImage'])
        else:
            sdk = producer.pull(engine, inputs['sdkImage'])
            base = inputs['coreRuntimeImage']; producer.pull(engine, base)
        properties = ET.parse(repo / 'src/Directory.Build.props').getroot().find('PropertyGroup')
        project = 'Kautuka.' + CORES[service]
        (driver.CACHE / 'nuget').mkdir(exist_ok=True)
        print('[launch-kautuka] Publishing Native AOT ' + service + '; this can take several minutes.', flush=True)
        producer.run(engine, 'run', '--rm', '--pull=never', '--volume', str(source / 'src') + ':/src:rw',
                     '--volume', str(context) + ':/out:rw', '--volume', str(driver.CACHE / 'nuget') + ':/nuget:rw',
                     '--env', 'NUGET_PACKAGES=/nuget', '--workdir', '/src', sdk, 'dotnet', 'publish',
                     'backend/' + project + '/' + project + '.csproj', '-c', 'Release', '-r', 'linux-x64',
                     '--self-contained', 'true', '-p:PublishAot=true', '-p:PublishSingleFile=true',
                     '-p:InvariantGlobalization=false', '-p:KautukaReleaseArtifactBuild=true',
                     '-p:Version=' + properties.findtext('KautukaDevVersion'), '-o', '/out')
        if (context / (project + '.dll')).exists() or (context / 'libcoreclr.so').exists() or (context / project).read_bytes()[:4] != b'\x7fELF':
            raise ValueError('Selected service publication is not Native AOT')
        if service == 'gateway' and local_images:
            previous = work / 'previous-image'
            copy_runtime_image(driver, original['localImageId'], previous)
            shutil.copytree(previous / 'built-in-mcpb', context / 'built-in-mcpb')
        elif service == 'gateway':
            archive = driver.STATE / 'oci-images' / original['archive']
            with archive.open('rb') as stream:
                if hashlib.file_digest(stream, 'sha256').hexdigest() != original['archiveSha256']:
                    raise ValueError('Installed Gateway archive hash differs')
            producer.run(engine, 'load', '-i', archive)
            cid = producer.run(engine, 'create', '--pull=never', '--network=none', original['localImageId']).strip()
            try: producer.run(engine, 'cp', cid + ':/app/built-in-mcpb', context / 'built-in-mcpb')
            finally: producer.run(engine, 'rm', cid)
        if not local_images:
            recipe = 'FROM ' + base + '\nCOPY . /app\nWORKDIR /app\nENTRYPOINT ' + json.dumps(['/app/' + project]) + '\n'
    if service == 'webhost':
        web = repo / 'src/frontend/web/dist'
        if not (web / 'index.html').is_file(): raise ValueError('Build frontend assets first')
        shutil.copytree(web, context / 'wwwroot', dirs_exist_ok=True)
        if frontend_only and hashlib.sha256((context / 'Kautuka.WebHost').read_bytes()).hexdigest() != binary_hash:
            raise ValueError('Frontend rebuild changed the Native AOT executable')
    if local_images:
        replacement = driver.module(repo, 'dev-local-images').build_one(
            producer, engine, inputs, work, context, service, 'service-' + secrets.token_hex(8))
        driver.save_local_output(service, replacement['localImageId'], context)
        return replace_inventory(inventory, service, replacement)
    (context / 'Containerfile').write_text(recipe)
    (context / '.dockerignore').write_text('Containerfile\nimage-id.txt\n.dockerignore\n')
    image = producer.build(engine, context, context / 'Containerfile')
    payload = work / 'payload'; archives = payload / 'oci-images'; archives.mkdir(parents=True)
    helper = producer.pull(engine, inputs['acquisitionImage'])
    name = 'core-' + service + '.oci'
    digest, metadata = producer.export(engine, helper, image, archives / name, work)
    entry = {'id': 'core-' + service, 'image': 'ghcr.io/devkrity/kautuka-' + service + '@' + digest,
             'archive': name, **metadata}
    producer.address_archives({'images': [entry]}, archives)
    (archives / name).unlink(missing_ok=True)
    updated = replace_inventory(inventory, service, entry)
    for existing in updated['images']:
        target = archives / existing['archive']
        if target.exists(): continue
        source = driver.STATE / 'oci-images' / existing['archive']
        try: os.link(source, target)
        except OSError: shutil.copyfile(source, target)
    (payload / 'bin').mkdir()
    (payload / 'bin/runtime-images.json').write_text(json.dumps(updated, indent=2))
    # Existing root utility validates all eleven archive bindings before caching.
    subprocess.run(['python3', '/usr/local/lib/kautuka/runtime-images.py', str(payload)], check=True)
    return updated


def operate(repo, identity, service, action):
    repo = Path(repo).resolve(strict=True)
    frontend_only = service == 'frontend'
    if frontend_only: service = 'webhost'
    if service not in CORES and service not in HOSTS or action not in ('start', 'restart', 'rebuild'):
        raise ValueError('Use a fixed core service, frontend, manager or updateservice and start/restart/rebuild')
    if service in HOSTS and action == 'rebuild':
        raise ValueError('Host controller rebuild requires npm run launch -- --build; individual host actions are start/restart only')
    driver = load_driver(repo)
    driver.ownership(identity)
    record = json.loads((driver.CACHE / 'build.json').read_text())
    if record.get('publication') != 'native-aot' or record.get('workspaceId') != identity or (driver.CACHE / 'pending-build.json').exists():
        raise ValueError('Complete the Native AOT development installation before individual operations')
    if MAINTENANCE.exists():
        raise ValueError('Cannot change development services during maintenance')
    if service in HOSTS:
        with activation_lease(driver):
            driver.as_runtime('systemctl', '--user', action, 'kautuka-' + service + '.service')
        return
    if driver.as_runtime('systemctl', '--user', 'is-active', 'kautuka-manager.service') != 'active':
        raise ValueError('Start the initialized development stack before individual core operations')
    current = inspect_core(driver, service)
    inventory_path = INVENTORY.resolve(strict=True)
    before = inventory_path.read_bytes(); inventory = json.loads(before)
    expected = next(entry['localImageId'] for entry in inventory['images'] if entry['id'] == 'core-' + service)
    if current['Image'].removeprefix('sha256:') != expected.removeprefix('sha256:'):
        raise ValueError('Selected service differs from the installed image inventory')
    if action == 'start' and current['State']['Running']:
        print('[launch-kautuka] Selected service is already running.', flush=True)
        return
    with tempfile.TemporaryDirectory(prefix='service-', dir=driver.CACHE) as temporary:
        updated = build_image(driver, repo, Path(temporary), service, frontend_only, inventory, current) if action == 'rebuild' else None
        if updated:
            expected = next(entry['localImageId'] for entry in updated['images'] if entry['id'] == 'core-' + service)
        with activation_lease(driver):
            if MAINTENANCE.exists() or driver.as_runtime('systemctl', '--user', 'is-active', 'kautuka-manager.service') != 'active':
                raise ValueError('Manager is no longer available for the prepared operation')
            if INVENTORY.resolve(strict=True) != inventory_path or inventory_path.read_bytes() != before:
                raise ValueError('The installed image inventory changed during this build; retry the service operation')
            activate(driver, service, current, inventory_path, before, updated, expected)
        if updated and updated.get('developmentLocalImages') is True:
            cache = driver.CACHE / 'local-outputs' / service
            for item in cache.iterdir():
                if (not item.is_symlink() and item.is_dir() and len(item.name) == 64
                        and all(c in '0123456789abcdef' for c in item.name)
                        and item.name != expected.removeprefix('sha256:')):
                    shutil.rmtree(item)


def activate(driver, service, current, inventory_path, before, updated, expected):
    driver.as_runtime('systemctl', '--user', 'stop', 'kautuka-manager.service')
    try:
        if updated: write_inventory(inventory_path, (json.dumps(updated, indent=2) + '\n').encode())
        # Re-read ownership after quiescing the controller; never stop by name.
        selected = inspect_core(driver, service)
        if selected['Id'] != current['Id']: raise ValueError('Selected service changed while preparing the operation; retry')
        driver.as_runtime('podman', 'stop', '--time', '10', current['Id'])
        driver.as_runtime('systemctl', '--user', 'start', 'kautuka-manager.service')
        result = wait_ready(driver, service, expected, current['Id'])
    except BaseException:
        driver.as_runtime('systemctl', '--user', 'stop', 'kautuka-manager.service')
        if updated: write_inventory(inventory_path, before)
        driver.as_runtime('systemctl', '--user', 'start', 'kautuka-manager.service')
        raise
    print('[launch-kautuka] ' + service + ' is healthy: ' + result, flush=True)
