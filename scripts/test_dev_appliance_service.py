import contextlib
import importlib.util
import json
from pathlib import Path
import tempfile
import types
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('service', Path(__file__).with_name('dev-appliance-service.py'))
service = importlib.util.module_from_spec(spec)
spec.loader.exec_module(service)


class ServiceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.inventory = {'images': [{'id': 'core-webhost', 'localImageId': 'sha256:old'}, {'id': 'egress', 'localImageId': 'sha256:keep'}]}
        self.path = self.root / 'runtime-images.json'
        self.path.write_text(json.dumps(self.inventory))
        (self.root / 'build.json').write_text(json.dumps({'publication': 'native-aot', 'workspaceId': 'owner'}))
        self.current = {'Id': 'exact-old-id', 'Image': 'sha256:old', 'State': {'Running': True},
                        'Config': {'Labels': {'kautuka.managed': 'manager', 'kautuka.role': 'core', 'kautuka.core-service': 'webhost'}}}
        self.calls = []
        def command(*args):
            self.calls.append(args)
            if args[:2] == ('podman', 'inspect'): return json.dumps([self.current])
            if 'is-active' in args: return 'active'
            return ''
        self.driver = types.SimpleNamespace(CACHE=self.root, STATE=self.root, as_runtime=command, ownership=lambda identity: None)
        for name, value in [('INVENTORY', self.path), ('MAINTENANCE', self.root / 'maintenance'),
                            ('load_driver', lambda repo: self.driver), ('activation_lease', lambda driver: contextlib.nullcontext())]:
            item = patch.object(service, name, value); item.start(); self.addCleanup(item.stop)

    def test_only_selected_inventory_entry_changes(self):
        new = {'id': 'core-webhost', 'localImageId': 'sha256:new'}
        result = service.replace_inventory(self.inventory, 'webhost', new)
        self.assertEqual(result['images'][1], self.inventory['images'][1])
        self.assertEqual(self.inventory['images'][0]['localImageId'], 'sha256:old')
        with self.assertRaises(ValueError): service.replace_inventory(self.inventory, 'gateway', new)

    def test_build_failure_leaves_manager_and_inventory_untouched(self):
        before = self.path.read_bytes()
        with patch.object(service, 'build_image', side_effect=RuntimeError('compiler failed')):
            with self.assertRaisesRegex(RuntimeError, 'compiler failed'):
                service.operate(self.root, 'owner', 'frontend', 'rebuild')
        self.assertEqual(self.path.read_bytes(), before)
        self.assertFalse(any('stop' in call for call in self.calls))

    def test_changed_inventory_during_build_is_not_overwritten(self):
        replacement = b'{"images": [], "changed": true}'
        def changed(*args):
            self.path.write_bytes(replacement)
            return self.inventory
        with patch.object(service, 'build_image', side_effect=changed):
            with self.assertRaisesRegex(ValueError, 'inventory changed'):
                service.operate(self.root, 'owner', 'frontend', 'rebuild')
        self.assertEqual(self.path.read_bytes(), replacement)
        self.assertFalse(any('stop' in call for call in self.calls))

    def test_inventory_replacement_retains_permissions_and_owner(self):
        self.path.chmod(0o640)
        before = self.path.stat()
        service.write_inventory(self.path, b'{"images": []}')
        after = self.path.stat()
        self.assertEqual(after.st_mode, before.st_mode)
        self.assertEqual((after.st_uid, after.st_gid), (before.st_uid, before.st_gid))

    def test_restart_stops_only_exact_selected_id(self):
        with patch.object(service, 'wait_ready', return_value='new-id'):
            service.operate(self.root, 'owner', 'webhost', 'restart')
        self.assertEqual([call for call in self.calls if call[:2] == ('podman', 'stop')], [('podman', 'stop', '--time', '10', 'exact-old-id')])
        self.assertFalse(any('kautuka.target' in call for call in self.calls))

    def test_activation_failure_restores_manifest(self):
        before = self.path.read_bytes()
        updated = service.replace_inventory(self.inventory, 'webhost', {'id': 'core-webhost', 'localImageId': 'sha256:new'})
        with patch.object(service, 'build_image', return_value=updated), patch.object(service, 'wait_ready', side_effect=RuntimeError('unhealthy')):
            with self.assertRaisesRegex(RuntimeError, 'unhealthy'): service.operate(self.root, 'owner', 'webhost', 'rebuild')
        self.assertEqual(self.path.read_bytes(), before)
        self.assertEqual(self.calls[-1], ('systemctl', '--user', 'start', 'kautuka-manager.service'))

    def test_foreign_label_rejected_before_stop(self):
        self.current['Config']['Labels']['kautuka.managed'] = 'foreign'
        with self.assertRaisesRegex(ValueError, 'owned'): service.operate(self.root, 'owner', 'webhost', 'restart')
        self.assertFalse(any('stop' in call for call in self.calls))

    def test_host_rebuild_rejected(self):
        with self.assertRaisesRegex(ValueError, 'Host controller rebuild'): service.operate(self.root, 'owner', 'manager', 'rebuild')
        self.assertEqual(self.calls, [])

    def test_stopped_during_build_not_resurrected(self):
        def stopped(driver): raise ValueError('launcher stopped')
        with patch.object(service, 'build_image', return_value=self.inventory), patch.object(service, 'activation_lease', side_effect=stopped):
            with self.assertRaisesRegex(ValueError, 'launcher stopped'): service.operate(self.root, 'owner', 'frontend', 'rebuild')
        self.assertFalse(any('stop' in call or 'start' in call for call in self.calls))

    def test_frontend_rebuild_retains_native_binary_without_sdk(self):
        import hashlib
        import shutil
        archive = self.root / 'oci-images/old.oci'; archive.parent.mkdir(); archive.write_bytes(b'archive')
        entry = self.inventory['images'][0]
        entry.update(archive='old.oci', archiveSha256=hashlib.sha256(b'archive').hexdigest())
        inputs = self.root / 'src/images'; inputs.mkdir(parents=True)
        (inputs / 'runtime-release-inputs.json').write_text(json.dumps({'coreRuntimeImage': 'pinned-base', 'acquisitionImage': 'pinned-skopeo'}))
        web = self.root / 'src/frontend/web/dist'; web.mkdir(parents=True); (web / 'index.html').write_text('new')
        (self.root / 'podman-build').touch()
        work = self.root / 'work'; work.mkdir()
        commands = []
        def run(engine, *args):
            commands.append(args)
            if args[0] == 'create': return 'isolated-image-id'
            if args[0] == 'cp':
                target = args[2]
                (target / 'Kautuka.WebHost').write_bytes(b'\x7fELFnative')
                (target / 'wwwroot').mkdir(); (target / 'wwwroot/obsolete.js').write_text('old')
            return ''
        def build(*args):
            context = args[1]
            self.assertFalse((context / 'wwwroot/obsolete.js').exists())
            self.assertEqual((context / 'Kautuka.WebHost').read_bytes(), b'\x7fELFnative')
            self.assertEqual((context / 'wwwroot/index.html').read_text(), 'new')
            raise RuntimeError('build boundary reached')
        producer = types.SimpleNamespace(run=run, inspect=lambda *args: 'sha256:old', pull=lambda *args: 'pinned-base', build=build)
        self.driver.module = lambda *args: producer
        with self.assertRaisesRegex(RuntimeError, 'build boundary reached'):
            service.build_image(self.driver, self.root, work, 'webhost', True, self.inventory, self.current)
        self.assertFalse(any('dotnet' in args for args in commands))
        self.assertIn(('rm', 'isolated-image-id'), commands)


if __name__ == '__main__': unittest.main()
