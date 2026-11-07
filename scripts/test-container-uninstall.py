#!/usr/bin/env python3
"""Exercise the installer's exact native retirement program without real Podman."""
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest
from unittest.mock import patch

SOURCE = (Path(__file__).parent / 'kautuka-install-linux.sh.in').read_text()
RETIRE = SOURCE.split('retire_uninstall_containers() {', 1)[1].split("<<'PY'", 1)[1].split('\n', 1)[1].split('\nPY\n', 1)[0]


class RetirementTests(unittest.TestCase):
    def execute(self, foreign=False, stop_failure=False, removal_failure=False):
        ids = {'envoy': 'a'*64, 'core': 'b'*64, 'infra': 'c'*64}
        pod_id = 'd'*64
        containers = {
            cid: {'Id': cid, 'Name': 'kautuka-egress-envoy' if name == 'envoy' else 'kautuka-'+name,
                  'Config': {'Labels': {'kautuka.managed': 'foreign' if foreign and name == 'core' else 'manager'}},
                  'State': {'Running': True}}
            for name, cid in ids.items()
        }
        pod = {'Id': pod_id, 'InfraContainerID': ids['infra'],
               'Labels': {'kautuka.managed':'manager','kautuka.egress':'true'}}
        calls = []

        def run(command, **kwargs):
            self.assertEqual(kwargs['timeout'], 60)
            args = command[command.index('podman')+1:]
            calls.append(args)
            status, output = 0, ''
            if args[:2] == ['pod','ps']: output = pod_id if pod else ''
            elif args[:2] == ['pod','inspect']:
                output = json.dumps([{**pod, 'Containers':[{'Id':cid} for cid in containers]}])
            elif args[0] == 'ps': output = '\n'.join(containers)
            elif args[0] == 'inspect': output = json.dumps([containers[args[1]]])
            elif args[0] == 'stop':
                if stop_failure: status = 125
                else: containers[args[-1]]['State']['Running'] = False
            elif args[0] == 'rm':
                if removal_failure: status = 1
                else: del containers[args[-1]]
            elif args[:2] == ['container','exists']: status = 0 if args[-1] in containers else 1
            elif args[:2] == ['pod','rm']:
                containers.pop(ids['infra']);pod.clear()
            elif args[:2] == ['pod','exists']: status = 0 if pod else 1
            else: self.fail(f'unexpected native command {args}')
            return subprocess.CompletedProcess(command, status, output, '')

        with patch('subprocess.run', side_effect=run), patch('os.chdir'), patch.object(sys, 'argv', ['fixture','owner','/home/owner','/run/user/123']):
            if foreign or stop_failure or removal_failure:
                with self.assertRaises(RuntimeError): exec(RETIRE, {})
            else: exec(RETIRE, {})
        return calls, containers

    def test_exact_owned_proxy_first_then_containers_then_infra(self):
        calls, remaining = self.execute()
        mutations = [c for c in calls if c[0] in ('stop','rm') or c[:2] == ['pod','rm']]
        self.assertEqual(mutations, [['stop','--time','15','a'*64], ['rm','--force','a'*64],
                                    ['rm','--force','b'*64], ['pod','rm','--force','d'*64]])
        self.assertFalse(remaining)

    def test_foreign_container_prevents_any_container_mutation(self):
        calls, remaining = self.execute(foreign=True)
        self.assertFalse(any(c[0] in ('stop','rm') for c in calls))
        self.assertEqual(len(remaining), 3)

    def test_proxy_failure_prevents_core_retirement(self):
        calls, _ = self.execute(stop_failure=True)
        self.assertFalse(any(c[0] == 'rm' for c in calls))

    def test_failed_removal_requires_absence_and_preserves_pod(self):
        calls, remaining = self.execute(removal_failure=True)
        self.assertIn(['container','exists','a'*64], calls)
        self.assertNotIn(['pod','rm','--force','d'*64], calls)
        self.assertEqual(len(remaining), 3)


@unittest.skipUnless(sys.platform == 'linux' and os.geteuid() == 0, 'requires isolated Linux root mount namespace')
class NativeGuardTests(unittest.TestCase):
    def test_layout_loss_cannot_select_destructive_legacy_fallback(self):
        script = r'''
set -euo pipefail
mount --make-rprivate /
mount -t tmpfs -o size=8m,mode=0755 tmpfs /etc/kautuka
mount -t tmpfs -o size=8m,mode=0755 tmpfs /opt
export KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1
source "$1"
[[ "$(uninstall_container_layout)" == false ]]
printf '{"CoreServices":{"Enabled":true},"ContainerEgress":{"Enabled":true}}' > /etc/kautuka/manager.json
if uninstall_container_layout; then exit 1; fi
printf '{"applicationServices":"containers"}' > /etc/kautuka/service-scope.json
[[ "$(uninstall_container_layout)" == true ]]
rm /etc/kautuka/manager.json
if uninstall_container_layout; then exit 1; fi
printf 'invalid' > /etc/kautuka/manager.json
if uninstall_container_layout; then exit 1; fi
rm /etc/kautuka/manager.json
mkdir /etc/kautuka/manager.json
if uninstall_container_layout; then exit 1; fi
rmdir /etc/kautuka/manager.json
rm /etc/kautuka/service-scope.json
mkdir -p /opt/kautuka/manager/current
printf '{"images":[{"id":"core-gateway"}]}' > /opt/kautuka/manager/current/runtime-images.json
if uninstall_container_layout; then exit 1; fi
'''
        result = subprocess.run(['unshare','--mount','--fork','bash','-c',script,'fixture',
                                 str(Path(__file__).parent / 'kautuka-install-linux.sh.in')],
                                capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_guard_and_child_descriptors_in_private_mount_namespace(self):
        script = r'''
set -euo pipefail
mount --make-rprivate /
mount -t tmpfs -o size=8m,mode=0755 tmpfs /run
mount -t tmpfs -o size=8m,mode=0755 tmpfs /var/lib
export KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1
source "$1"
mkdir -p /var/lib/kautuka-recovery
printf '{}' > /var/lib/kautuka-recovery/maintenance.json
if ( guard_uninstall_transaction ); then echo 'active fence accepted' >&2; exit 1; fi
rm /var/lib/kautuka-recovery/maintenance.json
printf '{"phase":"recovered"}' > /var/lib/kautuka-recovery/operation.json
if ( guard_uninstall_transaction ); then echo 'pending journal accepted' >&2; exit 1; fi
printf '{"phase":"completed"}' > /var/lib/kautuka-recovery/operation.json
guard_uninstall_transaction
if flock -n /run/kautuka-apply.lock true; then echo 'parent lock not retained' >&2; exit 1; fi
if ( guard_uninstall_transaction ); then echo 'concurrent uninstall accepted' >&2; exit 1; fi
exec 8>/run/fixture-writer.lock
run_as_service_user() {
    python3 - <<'PY'
import errno, os
for fd in (8,9):
    try: os.fstat(fd)
    except OSError as error: assert error.errno == errno.EBADF
    else: raise SystemExit('installer lease leaked to child')
open('/run/fd-closure-proven', 'w').close()
PY
    # Stop before the native retirement program; no real Podman is executed.
    return 72
}
if ( retire_uninstall_containers fixture /tmp /run/user/123 ); then exit 1; fi
[[ -e /proc/self/fd/8 && -e /proc/self/fd/9 ]]
[[ -f /run/fd-closure-proven ]]
'''
        result = subprocess.run(['unshare','--mount','--fork','bash','-c',script,'fixture',
                                 str(Path(__file__).parent / 'kautuka-install-linux.sh.in')],
                                capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == '__main__':
    unittest.main()
