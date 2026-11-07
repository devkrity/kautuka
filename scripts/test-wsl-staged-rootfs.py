#!/usr/bin/env python3
"""Fail closed on the bytes of the packaged, not-yet-booted WSL appliance."""
import json
import sys
import tarfile

ROLES = {'kautuka-base', 'openobserve', 'git-pkgs-proxy', 'core-credentialshost',
         'core-channelhost', 'core-gateway', 'core-webhost', 'egress-setup',
         'egress-envoy', 'egress-dns', 'image-acquisition'}
ROOT = 'usr/local/lib/kautuka/firstboot/'


def validate(path):
    with tarfile.open(path) as archive:
        entries = {item.name.removeprefix('./'): item for item in archive}
        def read(name):
            item = entries[name]
            if not item.isfile() or item.uid != 0 or item.mode & 0o022:
                raise ValueError('untrusted staged appliance file: ' + name)
            if item.size > 1024 * 1024:
                raise ValueError('oversized staged metadata: ' + name)
            return archive.extractfile(item).read()
        for name in ('start', 'installer.sh', 'release.json', 'manifest-public-key.pem',
                     'payload/bin/kautuka-runtime-images'):
            read(ROOT + name)
        unit = read('etc/systemd/system/kautuka-wsl-firstboot.service').decode()
        if 'ExecStart=/' + ROOT + 'start' not in unit:
            raise ValueError('missing fixed first-boot entry point')
        link = entries['etc/systemd/system/multi-user.target.wants/kautuka-wsl-firstboot.service']
        if not link.issym() or link.linkname != '../kautuka-wsl-firstboot.service' or link.uid != 0:
            raise ValueError('first boot is not enabled')
        inventory = json.loads(read(ROOT + 'payload/bin/runtime-images.json'))
        images = inventory['images']
        if len(images) != len(ROLES) or {image['id'] for image in images} != ROLES:
            raise ValueError('incomplete container appliance inventory')
        for image in images:
            item = entries[ROOT + 'payload/oci-images/' + image['archive']]
            if not item.isfile() or item.uid != 0 or item.mode & 0o022:
                raise ValueError('untrusted staged image archive')
        for name in entries:
            if name.startswith('var/lib/kautuka/security/') or name.endswith('/wsl-firstboot-complete'):
                raise ValueError('generated installation identity/state was baked into the appliance')
            if any(name.endswith('/kautuka-' + role + '.service') for role in
                   ('gateway', 'channelhost', 'credentialshost', 'webhost')):
                raise ValueError('legacy host core unit was baked into the appliance')


if __name__ == '__main__':
    validate(sys.argv[1])
    print('Packed WSL container first-boot contract passed.')
