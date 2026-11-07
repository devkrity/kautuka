#!/usr/bin/env python3
"""Exercise the apply helper's actual offline staging code in private directories."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

HELPER = Path(__file__).with_name('kautuka-apply-release.sh').read_text()
START = HELPER.index("    python3 - \"$1/bin/runtime-images.json\" \"$1/oci-images\" <<'PY'\n")
CODE = HELPER[START:].split("<<'PY'\n", 1)[1].split('\nPY\n', 1)[0]
ROLES = ('kautuka-base', 'openobserve', 'git-pkgs-proxy', 'core-credentialshost',
         'core-channelhost', 'core-gateway', 'core-webhost', 'egress-setup',
         'egress-envoy', 'egress-dns', 'image-acquisition')

@unittest.skipUnless(os.name == 'posix' and os.geteuid() == 0, 'Linux root-owned cache checks')
class OfflineStageTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory(prefix='kautuka-offline-stage-')
        self.root = Path(self.scratch.name)
        self.source = self.root / 'source'; self.source.mkdir()
        self.state = self.root / 'state'; self.state.mkdir(mode=0o755)
        self.inventory = self.root / 'inventory.json'
        self.entries = []
        for role in ROLES:
            data = ('archive ' + role).encode()
            digest = hashlib.sha256(data).hexdigest()
            name = f'{role}-{digest}.oci'
            (self.source / name).write_bytes(data)
            self.entries.append(dict(id=role, archive=name, archiveSha256=digest, platform='linux/amd64'))
        self.inventory.write_text(json.dumps(dict(schemaVersion=1, images=self.entries)))
        # Substitute only fixture locations and group lookup; execute the exact
        # production validation, copy, publication and durability operations.
        self.code = CODE.replace("('/var', '/var/lib', '/var/lib/kautuka')", repr((str(self.state),)))
        self.code = self.code.replace("'/var/lib/kautuka/oci-images'", repr(str(self.state / 'oci-images')))
        self.code = self.code.replace("'/var/lib/kautuka'", repr(str(self.state)))
        self.code = self.code.replace("grp.getgrnam('kautuka').gr_gid", 'os.getgid()')

    def tearDown(self):
        self.scratch.cleanup()

    def stage(self, success=True):
        result = subprocess.run(['python3', '-c', self.code, str(self.inventory), str(self.source)], capture_output=True, text=True)
        self.assertEqual(result.returncode == 0, success, result.stderr)
        return result

    def test_retains_old_release_and_reuses_verified_archives(self):
        self.stage()
        cache = self.state / 'oci-images'
        previous = cache / 'previous-release.oci'; previous.write_bytes(b'previous')
        inode = (cache / self.entries[0]['archive']).stat().st_ino
        self.stage()
        self.assertEqual(inode, (cache / self.entries[0]['archive']).stat().st_ino)
        self.assertEqual(b'previous', previous.read_bytes())
        self.assertEqual(0o640, (cache / self.entries[0]['archive']).stat().st_mode & 0o777)
        self.assertFalse(list(cache.glob('.stage-*')))

    def test_restrictive_umask_does_not_prevent_readable_cache(self):
        self.code = 'import os; os.umask(0o077)\n' + self.code
        self.stage()
        self.assertEqual(0o750, (self.state / 'oci-images').stat().st_mode & 0o777)

    def test_tampered_incoming_bytes_fail(self):
        (self.source / self.entries[0]['archive']).write_bytes(b'changed')
        self.stage(False)
        self.assertFalse(list((self.state / 'oci-images').iterdir()))

    def test_existing_wrong_bytes_are_never_overwritten(self):
        self.stage()
        target = self.state / 'oci-images' / self.entries[0]['archive']
        target.write_bytes(b'changed')
        self.stage(False)
        self.assertEqual(b'changed', target.read_bytes())

    def test_symlinked_store_is_rejected_without_writing_target(self):
        outside = self.root / 'outside'; outside.mkdir()
        (self.state / 'oci-images').symlink_to(outside, target_is_directory=True)
        self.stage(False)
        self.assertEqual([], list(outside.iterdir()))

    def test_service_writable_parent_is_rejected(self):
        self.state.chmod(0o775)
        self.stage(False)
        self.assertFalse((self.state / 'oci-images').exists())

    def test_generic_archive_name_is_rejected_before_store_creation(self):
        self.entries[0]['archive'] = 'base.tar'
        self.inventory.write_text(json.dumps(dict(schemaVersion=1, images=self.entries)))
        self.stage(False)
        self.assertFalse((self.state / 'oci-images').exists())

if __name__ == '__main__': unittest.main()
