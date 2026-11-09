#!/usr/bin/env python3
"""Build development core images directly in Manager's rootless Podman store."""
import json
import os
from pathlib import Path
import pwd
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET

CORES = {'credentialshost': 'CredentialsHost', 'channelhost': 'ChannelHost',
         'gateway': 'Gateway', 'webhost': 'WebHost'}


def build_one(producer, build_engine, inputs, work, output, role, version):
    user = pwd.getpwnam('kautuka')
    runtime = '/run/user/' + str(user.pw_uid)
    command = ['runuser', '-u', user.pw_name, '--', 'env', 'HOME=' + user.pw_dir,
               'XDG_RUNTIME_DIR=' + runtime, 'DBUS_SESSION_BUS_ADDRESS=unix:path=' + runtime + '/bus', 'podman']
    try: base = producer.inspect(str(build_engine), inputs['coreRuntimeImage'])
    except subprocess.CalledProcessError: base = producer.pull(str(build_engine), inputs['coreRuntimeImage'])
    with tempfile.TemporaryDirectory(prefix='kautuka-dev-image-', dir='/var/tmp') as temporary:
        root = Path(temporary)
        os.chown(root, user.pw_uid, user.pw_gid)
        if subprocess.run([*command, 'image', 'exists', base], check=False).returncode != 0:
            archive = root / 'base.tar'
            producer.run(str(build_engine), 'save', '-o', archive, base)
            os.chown(archive, user.pw_uid, user.pw_gid)
            subprocess.run([*command, 'load', '-i', str(archive)], check=True)
            archive.unlink()
        wrapper = work / 'runtime-podman'
        wrapper.write_text('#!/bin/sh\nexec runuser -u kautuka -- env HOME=/var/lib/kautuka XDG_RUNTIME_DIR=' + runtime
                           + ' DBUS_SESSION_BUS_ADDRESS=unix:path=' + runtime + '/bus podman "$@"\n')
        wrapper.chmod(0o700)
        context = root / role
        shutil.copytree(output, context)
        for directory, dirs, files in os.walk(context):
            os.chown(directory, user.pw_uid, user.pw_gid)
            for name in files: os.chown(Path(directory) / name, user.pw_uid, user.pw_gid)
        recipe = context / 'Containerfile'
        recipe.write_text('FROM ' + base + '\nCOPY . /app\nWORKDIR /app\n'
                          + 'LABEL kautuka.dev.image=true\nENTRYPOINT ' + json.dumps(['/app/Kautuka.' + CORES[role]]) + '\n')
        os.chown(recipe, user.pw_uid, user.pw_gid)
        (context / '.dockerignore').write_text('Containerfile\nimage-id.txt\n.dockerignore\n')
        os.chown(context / '.dockerignore', user.pw_uid, user.pw_gid)
        image = producer.build(str(wrapper), context, recipe)
        reference = 'localhost/kautuka-dev-' + role + ':' + version
        subprocess.run([*command, 'tag', image, reference], check=True)
        return {'id': 'core-' + role, 'image': reference, 'archive': None,
                'platform': 'linux/amd64', 'localImageId': image}


def prepare(producer, build_engine, sdk, source, payload, work, version, sha, inputs):
    clean = work / 'core-source'
    clean.mkdir()
    for directory in ('backend', 'shared', 'images'):
        shutil.copytree(source / 'src' / directory, clean / directory,
                        ignore=shutil.ignore_patterns('bin', 'obj', 'node_modules', '.git'))
    for file in (source / 'src').glob('Directory.*'):
        if file.is_file(): shutil.copy2(file, clean / file.name)
    properties = ET.parse(source / 'src/Directory.Build.props').getroot().find('PropertyGroup')
    numeric = properties.findtext('KautukaDevNumericVersion')
    output = work / 'core-output'; output.mkdir()
    inventory = json.loads(Path('/opt/kautuka/manager/current/runtime-images.json').read_text())
    inventory['developmentLocalImages'] = True
    for role, executable in CORES.items():
        print('[launch-kautuka] Publishing Native AOT ' + role + '...', flush=True)
        dest = output / role; dest.mkdir()
        producer.run(str(build_engine), 'run', '--rm', '--pull=never', '--volume', str(clean) + ':/src:rw',
                     '--volume', str(dest) + ':/out:rw', '--volume', '/var/cache/kautuka-dev/nuget:/nuget:rw',
                     '--env', 'NUGET_PACKAGES=/nuget', '--workdir', '/src', sdk, 'dotnet', 'publish',
                     'backend/Kautuka.' + executable + '/Kautuka.' + executable + '.csproj', '-c', 'Release',
                     '-r', 'linux-x64', '--self-contained', 'true', '-p:PublishAot=true',
                     '-p:InvariantGlobalization=false', '-p:KautukaReleaseArtifactBuild=true',
                     '-p:PublishSingleFile=true', '-p:Version=' + version,
                     '-p:InformationalVersion=' + version + '+' + sha[:7], '-p:AssemblyVersion=' + numeric,
                     '-p:FileVersion=' + numeric, '-p:SourceRevisionId=' + sha,
                     '-p:IncludeSourceRevisionInInformationalVersion=false', '-o', '/out')
        binary = dest / ('Kautuka.' + executable)
        if not binary.is_file() or binary.read_bytes()[:4] != b'\x7fELF' or (dest / 'libcoreclr.so').exists():
            raise ValueError('Core development output is not Native AOT: ' + role)
        if role == 'gateway': shutil.copytree(payload / 'services/gateway/built-in-mcpb', dest / 'built-in-mcpb')
        if role == 'webhost': shutil.copytree(payload / 'services/webhost/wwwroot', dest / 'wwwroot', dirs_exist_ok=True)
        saved = payload / 'services' / role
        shutil.copytree(dest, saved, dirs_exist_ok=True)
        shutil.copy2(binary, saved / ('kautuka-' + role))
        replacement = build_one(producer, build_engine, inputs, work, dest, role, version)
        matches = [i for i, entry in enumerate(inventory['images']) if entry['id'] == replacement['id']]
        if len(matches) != 1: raise ValueError('Expected one core image inventory entry: ' + role)
        inventory['images'][matches[0]] = replacement
    (payload / 'bin/runtime-images.json').write_text(json.dumps(inventory, indent=2))
