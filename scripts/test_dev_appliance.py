import importlib.util
import json
import os
import subprocess
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch, MagicMock

spec = importlib.util.spec_from_file_location('dev_appliance', Path(__file__).with_name('dev-appliance.py'))
driver = importlib.util.module_from_spec(spec)
spec.loader.exec_module(driver)


class DevelopmentPublicationTests(unittest.TestCase):
    def test_missing_local_image_is_rebuilt_from_retained_output(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            payload = root / 'payload'; (payload / 'bin').mkdir(parents=True)
            (payload / 'services/credentialshost').mkdir(parents=True)
            (root / 'src/images').mkdir(parents=True)
            (root / 'src/images/runtime-release-inputs.json').write_text('{}')
            image = 'sha256:' + 'a' * 64
            entries = [{'id': 'core-' + role, 'localImageId': image} for role in
                       ('credentialshost', 'channelhost', 'gateway', 'webhost')]
            (payload / 'bin/runtime-images.json').write_text(json.dumps({'developmentLocalImages': True, 'images': entries}))
            calls = []
            def runtime(*args):
                if args[1:3] == ('image', 'exists'):
                    calls.append(args[-1])
                    if len(calls) == 1: raise subprocess.CalledProcessError(1, args)
                    return ''
                return json.dumps([{'Id': image[7:], 'Os': 'linux', 'Architecture': 'amd64'}])
            builder = MagicMock()
            builder.build_one.return_value = {'id': 'core-credentialshost', 'localImageId': 'sha256:' + 'b' * 64}
            with patch.object(driver, 'CACHE', root), patch.object(driver, 'as_runtime', side_effect=runtime), \
                 patch.object(driver, 'module', side_effect=lambda repo, name: builder if name == 'dev-local-images' else MagicMock()):
                self.assertTrue(driver.repair_local_images(root, payload, {'localImages': True, 'version': 'dev'}))
            self.assertEqual(json.loads((payload / 'bin/runtime-images.json').read_text())['images'][0]['localImageId'], 'sha256:' + 'b' * 64)
            self.assertEqual(builder.build_one.call_args.args[4], payload / 'services/credentialshost')

    def test_missing_individually_rebuilt_image_uses_its_own_retained_output(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            payload = root / 'payload'; (payload / 'bin').mkdir(parents=True)
            (root / 'src/images').mkdir(parents=True)
            (root / 'src/images/runtime-release-inputs.json').write_text('{}')
            old = 'sha256:' + 'a' * 64; current = 'sha256:' + 'b' * 64
            roles = ('credentialshost', 'channelhost', 'gateway', 'webhost')
            prepared = [{'id': 'core-' + role, 'localImageId': old} for role in roles]
            active = [dict(entry, localImageId=current if entry['id'] == 'core-webhost' else old) for entry in prepared]
            (payload / 'bin/runtime-images.json').write_text(json.dumps({'developmentLocalImages': True, 'images': prepared}))
            active_path = root / 'active.json'
            active_path.write_text(json.dumps({'developmentLocalImages': True, 'images': active}))
            saved = root / 'local-outputs/webhost' / current[7:]; saved.mkdir(parents=True)
            def runtime(*args):
                if args[1:3] == ('image', 'exists'):
                    if args[-1] == current: raise subprocess.CalledProcessError(1, args)
                    return ''
                return json.dumps([{'Id': old[7:], 'Os': 'linux', 'Architecture': 'amd64'}])
            builder = MagicMock()
            builder.build_one.return_value = {'id': 'core-webhost', 'localImageId': 'sha256:' + 'c' * 64}
            with patch.object(driver, 'CACHE', root), patch.object(driver, 'as_runtime', side_effect=runtime), \
                 patch.object(driver, 'module', side_effect=lambda repo, name: builder if name == 'dev-local-images' else MagicMock()):
                self.assertTrue(driver.repair_local_images(root, payload, {'localImages': True, 'version': 'dev'}, active_path))
            self.assertEqual(builder.build_one.call_args.args[4], saved)
            self.assertEqual(json.loads(active_path.read_text())['images'][-1]['localImageId'], 'sha256:' + 'c' * 64)
            self.assertEqual(json.loads((payload / 'bin/runtime-images.json').read_text())['images'][-1]['localImageId'], old)
            replacement = 'sha256:' + 'c' * 64
            def runtime_again(*args):
                if args[1:3] == ('image', 'exists'):
                    if args[-1] == replacement: raise subprocess.CalledProcessError(1, args)
                    return ''
                return json.dumps([{'Id': old[7:], 'Os': 'linux', 'Architecture': 'amd64'}])
            builder.build_one.return_value = {'id': 'core-webhost', 'localImageId': 'sha256:' + 'd' * 64}
            with patch.object(driver, 'CACHE', root), patch.object(driver, 'as_runtime', side_effect=runtime_again), \
                 patch.object(driver, 'module', side_effect=lambda repo, name: builder if name == 'dev-local-images' else MagicMock()):
                self.assertTrue(driver.repair_local_images(root, payload, {'localImages': True, 'version': 'dev'}, active_path))
            self.assertEqual(builder.build_one.call_args.args[4], root / 'local-outputs/webhost' / replacement[7:])

    def test_supporting_image_fingerprint_changes_with_inputs(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.assertIsNone(driver.support_fingerprint(root))
            for name in driver.SUPPORT_INPUTS:
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(name)
            before = driver.support_fingerprint(root)
            (root / driver.SUPPORT_INPUTS[-1]).write_text('changed')
            self.assertNotEqual(before, driver.support_fingerprint(root))

    def test_support_reuse_requires_previous_successful_fingerprint(self):
        self.assertFalse(driver.can_reuse_support(True, 'new-inputs', {}))
        self.assertFalse(driver.can_reuse_support(True, 'new-inputs', {'supportFingerprint': 'old-inputs'}))
        self.assertFalse(driver.can_reuse_support(False, 'new-inputs', {'supportFingerprint': 'new-inputs'}))
        self.assertTrue(driver.can_reuse_support(True, 'new-inputs', {'supportFingerprint': 'new-inputs'}))

    def test_log_stream_is_stopped_when_stack_exits(self):
        process = MagicMock()
        with patch.object(driver.subprocess, 'Popen', return_value=process) as start, \
             patch.object(driver, 'run_stack', side_effect=RuntimeError('failed')):
            with self.assertRaisesRegex(RuntimeError, 'failed'):
                driver.run(Path('/repo'), 20229, 'session')
        self.assertIn('SYSLOG_IDENTIFIER=kautuka-gateway', start.call_args.args[0])
        process.terminate.assert_called_once()
        process.wait.assert_called_once()

    def prepare(self, root):
        for role in driver.SERVICES:
            version = root / role / 'versions/old'
            version.mkdir(parents=True)
            (version / 'original').write_text(role)
            (root / role / 'versions/new').mkdir()
            (root / role / 'current').symlink_to('versions/old')

    def test_interrupted_pointer_switch_restores_every_original_version(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.prepare(root)
            replace = os.replace
            calls = 0

            def fail_once(source, target):
                nonlocal calls
                calls += 1
                if calls == 3: raise OSError('injected publication failure')
                replace(source, target)

            with patch.object(driver.os, 'replace', side_effect=fail_once):
                with self.assertRaisesRegex(OSError, 'injected'):
                    driver.switch_pointers(root, 'new')
            for role in driver.SERVICES:
                self.assertEqual(os.readlink(root / role / 'current'), 'versions/old')
                self.assertEqual((root / role / 'current/original').read_text(), role)

    def test_successful_pointer_switch_keeps_previous_payload(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.prepare(root)
            driver.switch_pointers(root, 'new')
            for role in driver.SERVICES:
                self.assertEqual(os.readlink(root / role / 'current'), 'versions/new')
                self.assertEqual((root / role / 'versions/old/original').read_text(), role)

    def test_stale_launcher_session_cannot_stop_a_new_launcher(self):
        with tempfile.TemporaryDirectory() as temporary:
            cache = Path(temporary)
            (cache / 'launcher-session').write_text('new-launcher')
            with patch.object(driver, 'CACHE', cache), patch.object(driver, 'as_runtime') as runtime:
                driver.stop('old-launcher')
            runtime.assert_not_called()
            self.assertFalse((cache / 'stop').exists())

    @staticmethod
    def build_record(identity, version, fingerprint_char):
        return {'schemaVersion': 1, 'workspaceId': identity, 'fingerprint': fingerprint_char * 64,
                'version': version, 'sourceSha': 'a' * 40, 'publication': 'native-aot'}

    def test_cleanup_removes_only_superseded_owned_prepared_payloads(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            cache = root / 'cache'; cache.mkdir()
            active = self.build_record('workspace', 'dev-current', 'a')
            pending = self.build_record('workspace', 'dev-pending', 'b')
            stale = self.build_record('workspace', 'dev-stale', 'c')
            foreign = self.build_record('other-workspace', 'dev-foreign', 'd')
            driver.save_record(cache / 'build.json', active)
            driver.save_record(cache / 'pending-build.json', pending)
            for name, record in ((active['fingerprint'], active), (pending['fingerprint'], pending),
                                 (stale['fingerprint'], stale), (foreign['fingerprint'], foreign)):
                prepared = cache / ('prepared-' + name); prepared.mkdir()
                driver.save_record(prepared / 'build.json', record)
            malformed = cache / ('prepared-' + 'e' * 64); malformed.mkdir()
            (malformed / 'build.json').write_text('{broken')
            interrupted = cache / 'prepared-next-interrupted'; interrupted.mkdir()
            driver.save_record(interrupted / 'build.json', stale)
            (cache / 'user-data.json').write_text('{"keep":true}')

            driver.cleanup_superseded_dev_builds('workspace', cache)

            self.assertTrue((cache / ('prepared-' + active['fingerprint'])).exists())
            self.assertTrue((cache / ('prepared-' + pending['fingerprint'])).exists())
            self.assertFalse((cache / ('prepared-' + stale['fingerprint'])).exists())
            self.assertTrue((cache / ('prepared-' + foreign['fingerprint'])).exists())
            self.assertTrue(malformed.exists())
            self.assertTrue(interrupted.exists())
            self.assertEqual((cache / 'user-data.json').read_text(), '{"keep":true}')

    def test_cleanup_retains_prepared_payloads_when_build_records_are_untrusted(self):
        with tempfile.TemporaryDirectory() as temporary:
            cache = Path(temporary) / 'cache'; cache.mkdir()
            prepared_record = self.build_record('workspace', 'dev-stale', 'b')
            prepared = cache / ('prepared-' + prepared_record['fingerprint']); prepared.mkdir()
            driver.save_record(prepared / 'build.json', prepared_record)
            (cache / 'build.json').write_text('{malformed')

            driver.cleanup_superseded_dev_builds('workspace', cache)

            self.assertTrue(prepared.exists())

    def test_previous_managed_runtime_build_cannot_start(self):
        with tempfile.TemporaryDirectory() as temporary:
            cache = Path(temporary)
            (cache / 'build.json').write_text(json.dumps({'publication': 'self-contained-jit', 'version': 'old'}))
            with patch.object(driver, 'CACHE', cache), patch.object(driver, 'installer') as installer, patch.object(driver, 'as_runtime') as runtime:
                with self.assertRaisesRegex(ValueError, 'not Native AOT'):
                    driver.run_stack(Path(temporary), 20229, 'new-launcher')
            installer.assert_not_called()
            runtime.assert_not_called()

    def test_development_channel_is_accepted_by_actual_installer_policy(self):
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(__file__).with_name('kautuka-install-linux.sh.in').resolve()
            subprocess.run(['bash', '-c', 'export KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1; source "$1"; write_initial_update_runtime_policy "$2" "$3" "$(id -un)" "$(id -gn)"',
                            str(source), str(source), temporary, driver.INITIAL_CHANNEL], check=True)
            value = json.loads((Path(temporary) / 'updateService/update-runtime-policy.json').read_text())
            self.assertEqual(value['releaseChannel'], driver.INITIAL_CHANNEL)


if __name__ == '__main__': unittest.main()
