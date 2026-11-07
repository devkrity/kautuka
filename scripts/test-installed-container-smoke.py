#!/usr/bin/env python3
"""Execute packaged smoke validators and the launcher layout guard locally.

No systemd, Podman, WSL or installed appliance is invoked.
"""
import copy
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[1]
SMOKE = (ROOT / 'scripts/installed-linux-smoke.sh').read_text()
SCOPE = SMOKE.split("<<'PY_SCOPE'\n", 1)[1].split('\nPY_SCOPE', 1)[0]
CORE = SMOKE.split("if printf '%s' \"$observed\" | python3 -c '\n", 1)[1].split("\n' 2>/dev/null", 1)[0]
SETUP = (ROOT / 'src/installer/Kautuka.Installer.Windows/Installation/WindowsApplianceInstaller.Setup.cs').read_text()
GUARD = textwrap.dedent(SETUP.split('BootstrapLayoutCommand = """\n', 1)[1].split('""";', 1)[0])


class InstalledContainerSmokeTests(unittest.TestCase):
    def test_remote_smoke_never_reads_local_setup_token(self):
        bash = 'C:/Program Files/Git/bin/bash.exe' if os.name == 'nt' else shutil.which('bash')
        if not bash or not Path(bash).is_file():
            self.skipTest('local bash unavailable')
        fallback = SMOKE.split('if [[ "$needs_setup" == "true" ]]; then\n', 1)[1].split('\n  [[ -n "$setup_token" ]]', 1)[0]
        with tempfile.TemporaryDirectory(prefix='kautuka-smoke-token-') as directory:
            token = Path(directory) / 'setup-token'
            token.write_text('local-test-token', encoding='utf-8')
            fallback = fallback.replace('/var/lib/kautuka/gateway-data/setup-token', token.as_posix())
            for remote, supplied, expected in [('1', '', ''), ('0', '', 'local-test-token'),
                                                ('1', 'explicit-token', 'explicit-token'),
                                                ('0', 'explicit-token', 'explicit-token')]:
                with self.subTest(remote=remote, supplied=supplied):
                    program = f'set -euo pipefail\nskip_systemd={remote}\nsetup_token={supplied}\n' + fallback + '\nprintf "%s" "$setup_token"'
                    result = subprocess.run([bash, '-c', program], capture_output=True, text=True, timeout=5)
                    self.assertEqual(0, result.returncode)
                    self.assertEqual(expected, result.stdout)

    def test_firstboot_wait_requires_completion_and_rejects_failed_or_timed_out_unit(self):
        bash = 'C:/Program Files/Git/bin/bash.exe' if os.name == 'nt' else shutil.which('bash')
        if not bash or not Path(bash).is_file():
            self.skipTest('local bash unavailable')
        wait = SMOKE.split('  firstboot_deadline=', 1)[1].split('\n\n  python3', 1)[0]
        wait = 'firstboot_deadline=' + wait
        with tempfile.TemporaryDirectory(prefix='kautuka-firstboot-wait-') as directory:
            marker = Path(directory) / 'complete'
            wait = wait.replace('/etc/kautuka/wsl-firstboot-complete', marker.as_posix())
            for state, complete, timeout, expected in [('active', True, 2, 0), ('inactive', True, 2, 0),
                                                       ('failed', False, 2, 1), ('activating', False, 0, 1)]:
                with self.subTest(state=state):
                    marker.unlink(missing_ok=True)
                    if complete:
                        marker.touch()
                    prelude = ('set -euo pipefail\nfail() { exit 1; }\n'
                               f'timeout_seconds={timeout}\n'
                               'systemctl() { if [[ "$*" == *LoadState* ]]; then echo loaded; '
                               f'else echo {state}; fi; }}\n')
                    result = subprocess.run([bash, '-c', prelude + wait], capture_output=True, timeout=5)
                    self.assertEqual(expected, result.returncode)

    def run_python(self, program, path, document, stdin=None):
        with tempfile.TemporaryDirectory(prefix='kautuka-smoke-validator-') as directory:
            data = Path(directory) / 'input.json'
            data.write_text(json.dumps(document), encoding='utf-8')
            return subprocess.run([sys.executable, '-c', program.replace(path, data.as_posix()), 'kautuka'],
                                  input=json.dumps(stdin), text=True, capture_output=True, timeout=10)

    def test_scope_requires_two_host_units_and_container_mode(self):
        contract = dict(schemaVersion=2, applicationServices='containers', username='kautuka',
                        target='kautuka.target', userUnits=['kautuka-manager.service', 'kautuka-updateservice.service'],
                        systemUnits=[])
        self.assertEqual(0, self.run_python(SCOPE, '/etc/kautuka/service-scope.json', contract).returncode)
        for key, value in [('applicationServices', 'host'), ('username', 'other'), ('schemaVersion', 1),
                           ('userUnits', ['kautuka-manager.service']), ('systemUnits', ['kautuka-gateway.service'])]:
            with self.subTest(key=key):
                self.assertNotEqual(0, self.run_python(SCOPE, '/etc/kautuka/service-scope.json', contract | {key: value}).returncode)

    def test_exact_owned_running_image_bound_cohort(self):
        roles = ['credentialshost', 'channelhost', 'gateway', 'webhost']
        inventory = {'images': [{'id': 'core-' + role, 'localImageId': 'sha256:' + str(i + 1) * 64}
                                for i, role in enumerate(roles)]}
        containers = [{'Id': str(i + 5) * 64, 'Image': str(i + 1) * 64,
                       'State': {'Status': 'running'},
                       'Config': {'Labels': {'kautuka.managed': 'manager', 'kautuka.core-service': role}}}
                      for i, role in enumerate(roles)]
        path = '/opt/kautuka/manager/current/runtime-images.json'
        self.assertEqual(0, self.run_python(CORE, path, inventory, containers).returncode)
        for failure in ('foreign', 'stopped', 'wrong-image', 'duplicate-role', 'duplicate-id', 'missing'):
            invalid = copy.deepcopy(containers)
            if failure == 'foreign': invalid[0]['Config']['Labels']['kautuka.managed'] = 'foreign'
            if failure == 'stopped': invalid[0]['State']['Status'] = 'exited'
            if failure == 'wrong-image': invalid[0]['Image'] = 'a' * 64
            if failure == 'duplicate-role': invalid[0]['Config']['Labels']['kautuka.core-service'] = 'gateway'
            if failure == 'duplicate-id': invalid[0]['Id'] = invalid[1]['Id']
            if failure == 'missing': invalid.pop()
            with self.subTest(failure=failure):
                self.assertNotEqual(0, self.run_python(CORE, path, inventory, invalid).returncode)
        self.assertNotEqual(0, self.run_python(CORE, path, {'images': inventory['images'][:-1]}, containers).returncode)

    def test_windows_bootstrap_accepts_staged_files_without_current_and_rejects_incomplete(self):
        # Use Git Bash on Windows explicitly: never the Windows WSL launcher.
        bash = 'C:/Program Files/Git/bin/bash.exe' if os.name == 'nt' else shutil.which('bash')
        if not bash or not Path(bash).is_file():
            self.skipTest('local bash unavailable')
        with tempfile.TemporaryDirectory(prefix='kautuka-layout-guard-') as directory:
            root = Path(directory)
            program = GUARD.replace('/opt/', root.as_posix() + '/opt/').replace('/usr/', root.as_posix() + '/usr/').replace('/etc/', root.as_posix() + '/etc/')
            def run():
                return subprocess.run([bash, '-c', program], capture_output=True, text=True, timeout=10).returncode
            self.assertNotEqual(0, run())
            stage = root / 'usr/local/lib/kautuka/firstboot'
            for name in ['start', 'installer.sh', 'release.json', 'manifest-public-key.pem', 'payload/bin/runtime-images.json']:
                file = stage / name
                file.parent.mkdir(parents=True, exist_ok=True)
                file.write_text('#!/bin/sh\n', encoding='utf-8')
                file.chmod(0o755 if name == 'start' else 0o644)
            unit = root / 'etc/systemd/system/kautuka-wsl-firstboot.service'
            unit.parent.mkdir(parents=True)
            unit.write_text('[Service]\n', encoding='utf-8')
            self.assertEqual(0, run())
            (stage / 'manifest-public-key.pem').unlink()
            self.assertNotEqual(0, run())
            (root / 'opt/kautuka/manager/current').mkdir(parents=True)
            self.assertEqual(0, run())

    def test_release_paths_and_remote_smoke_scope(self):
        workflow = (ROOT / '.github/workflows/release-publish-assets.yml').read_text()
        self.assertIn('$archive_type:/src/sbom-input/linux-platform/oci-images/$archive_name', workflow)
        self.assertNotIn('wsl-appliance/var/lib/kautuka/oci-images', workflow)
        caller = (ROOT / '.github/workflows/release-pipeline.yml').read_text().split('uses: ./.github/workflows/installed-linux-smoke.yml', 1)[1]
        called = (ROOT / '.github/workflows/installed-linux-smoke.yml').read_text()
        for secret in ['KAUTUKA_SMOKE_PASSWORD', 'KAUTUKA_SMOKE_SETUP_TOKEN']:
            self.assertIn(secret + ': ${{ secrets.' + secret + ' }}', caller)
            self.assertIn(secret + ':\n        required: false', called)
        self.assertIn('installed-linux-smoke.sh --skip-systemd', called)
        self.assertIn('/var/lib/kautuka/gateway-data/setup-token', SMOKE)
        self.assertNotIn('/var/lib/kautuka/setup-token', SMOKE)
        self.assertLess(SMOKE.index('firstboot_deadline='), SMOKE.index('state="$(systemctl is-system-running'))


if __name__ == '__main__':
    unittest.main()
