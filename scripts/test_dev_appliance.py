import importlib.util
import json
import os
import subprocess
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('dev_appliance', Path(__file__).with_name('dev-appliance.py'))
driver = importlib.util.module_from_spec(spec)
spec.loader.exec_module(driver)


class DevelopmentPublicationTests(unittest.TestCase):
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

    def test_previous_managed_runtime_build_cannot_start(self):
        with tempfile.TemporaryDirectory() as temporary:
            cache = Path(temporary)
            (cache / 'build.json').write_text(json.dumps({'publication': 'self-contained-jit', 'version': 'old'}))
            with patch.object(driver, 'CACHE', cache), patch.object(driver, 'installer') as installer, patch.object(driver, 'as_runtime') as runtime:
                with self.assertRaisesRegex(ValueError, 'not Native AOT'):
                    driver.run(Path(temporary), 20229, 'new-launcher')
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
