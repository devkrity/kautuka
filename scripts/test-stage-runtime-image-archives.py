#!/usr/bin/env python3
"""Exercise the shared offline staging utility in private directories."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

HELPER = Path(__file__).with_name('kautuka-apply-release.sh').read_text()
CODE = Path(__file__).with_name('kautuka-runtime-images.py').read_text()
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
        (self.root / 'bin').mkdir()
        self.inventory = self.root / 'bin/runtime-images.json'
        self.source.rename(self.root / 'oci-images')
        self.source = self.root / 'oci-images'
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
        result = subprocess.run(['python3', '-c', self.code, str(self.root)], capture_output=True, text=True)
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

@unittest.skipUnless(os.name == 'posix' and os.geteuid() == 0, 'Linux root-owned contract checks')
class ServiceScopeTests(unittest.TestCase):
    def test_installer_schema_and_container_layout(self):
        scope = HELPER.split("application_services=", 1)[1].split("<<'PY'\n", 1)[1].split("\nPY\n", 1)[0]
        with tempfile.TemporaryDirectory(prefix='kautuka-scope-') as directory:
            root = Path(directory)
            path = root / 'service-scope.json'
            code = scope.replace("'/etc/kautuka/service-scope.json'", repr(str(path))).replace("'/etc/kautuka'", repr(str(root)))
            cases = [
                ({'schemaVersion': 2, 'username': 'kautuka'}, 'host'),
                ({'schemaVersion': 1, 'username': 'kautuka', 'scope': 'user', 'applicationServices': 'containers'}, 'containers'),
                ({'schemaVersion': 2, 'username': 'kautuka', 'target': 'kautuka.target',
                  'userUnits': ['kautuka-manager.service', 'kautuka-updateservice.service'], 'systemUnits': [],
                  'applicationServices': 'containers'}, 'containers'),
                ({'schemaVersion': 2, 'username': 'kautuka', 'applicationServices': 'containers'}, None),
                ({'schemaVersion': 99}, None),
            ]
            for document, expected in cases:
                with self.subTest(document=document):
                    path.write_text(json.dumps(document))
                    result = subprocess.run(['python3', '-c', code], capture_output=True, text=True)
                    self.assertEqual(result.returncode == 0, expected is not None, result.stderr)
                    if expected: self.assertEqual(expected, result.stdout.strip())

@unittest.skipUnless(os.name == 'posix', 'Linux staging filesystem checks')
class SpaceBudgetTests(unittest.TestCase):
    def test_container_cache_and_moved_databases_are_budgeted_on_their_filesystems(self):
        import io
        import tarfile
        space = HELPER.split('check_staging_space() {', 1)[1].split("<<'PY'\n", 1)[1].split('\nPY\n', 1)[0]
        recovery = Path(__file__).with_name('kautuka-update-recovery.py')
        with tempfile.TemporaryDirectory(prefix='kautuka-space-') as directory:
            root = Path(directory)
            data = root / 'data'; (data / 'gateway-data/agents/test').mkdir(parents=True)
            for name, size in [('gateway-data/kautuka.db', 10 * 1024 * 1024), ('gateway-data/agents/test/agent.db', 5 * 1024 * 1024)]:
                with (data / name).open('wb') as stream: stream.truncate(size)
            archive = root / 'payload.tar.gz'
            with tarfile.open(archive, 'w:gz') as stream:
                member = tarfile.TarInfo('./oci-images/test.oci'); member.size = 4096
                stream.addfile(member, io.BytesIO(b'x' * 4096))
            # Distinct fixture devices exercise the same filesystem aggregation
            # used on installations with separate data or backup mounts.
            prefix = """import os,sys,types
original_stat=os.stat
budget={sys.argv[2]: (3, int(sys.argv[7])), sys.argv[3]: (4, int(sys.argv[8])), '/var/tmp': (1, 2**30), '/opt/kautuka': (2, 2**30)}
os.stat=lambda path,*a,**k: types.SimpleNamespace(st_dev=budget[path][0]) if path in budget else original_stat(path,*a,**k)
os.statvfs=lambda path: types.SimpleNamespace(f_bavail=budget[path][1], f_frsize=1)
sys.argv=sys.argv[:7]
"""
            for cache_free, backup_free, succeeds in [(2**30, 2**30, True), (256*1024*1024+1024, 2**30, False), (2**30, 270*1024*1024, False)]:
                with self.subTest(cache_free=cache_free, backup_free=backup_free):
                    result = subprocess.run(['python3', '-c', prefix + space, str(archive), str(data), str(root/'backups'), str(recovery), '', 'containers', str(cache_free), str(backup_free)], capture_output=True, text=True)
                    self.assertEqual(result.returncode == 0, succeeds, result.stderr)
                    if not succeeds: self.assertIn('insufficient free space', result.stderr)

if __name__ == '__main__': unittest.main()
