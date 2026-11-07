#!/usr/bin/env python3
"""Small packaging contract tests; actual systemd first boot is a separate proof."""
import importlib.util
import io
import json
import os
import subprocess
import sys
from pathlib import Path
import tarfile
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('seal', Path(__file__).with_name('test-wsl-staged-rootfs.py'))
seal = importlib.util.module_from_spec(spec)
spec.loader.exec_module(seal)


class StagedApplianceTests(unittest.TestCase):
    def fixture(self, extra=None, omit=None):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        path = Path(temporary.name) / 'rootfs.tar'
        files = {seal.ROOT + name: b'fixture' for name in
                 ('start', 'installer.sh', 'release.json', 'manifest-public-key.pem', 'payload/bin/kautuka-runtime-images')}
        files['etc/systemd/system/kautuka-wsl-firstboot.service'] = ('ExecStart=/' + seal.ROOT + 'start').encode()
        images = [dict(id=role, archive=role+'.oci') for role in sorted(seal.ROLES)]
        files[seal.ROOT + 'payload/bin/runtime-images.json'] = json.dumps(dict(images=images)).encode()
        files.update({seal.ROOT + 'payload/oci-images/' + image['archive']: b'archive' for image in images})
        if extra: files.update(extra)
        if omit: files.pop(omit)
        with tarfile.open(path, 'w') as archive:
            for name, data in files.items():
                item = tarfile.TarInfo(name); item.size = len(data); item.mode = 0o644
                archive.addfile(item, io.BytesIO(data))
            link = tarfile.TarInfo('etc/systemd/system/multi-user.target.wants/kautuka-wsl-firstboot.service')
            link.type = tarfile.SYMTYPE; link.linkname = '../kautuka-wsl-firstboot.service'
            archive.addfile(link)
        return path

    def test_staged_container_contract(self):
        seal.validate(self.fixture())

    def test_no_generated_keys_or_legacy_core_units(self):
        for name in ('var/lib/kautuka/security/channel/channel-private/key.pfx',
                     'etc/systemd/system/kautuka-gateway.service', 'etc/kautuka/wsl-firstboot-complete'):
            with self.subTest(name=name), self.assertRaises(ValueError):
                seal.validate(self.fixture({name: b'bad'}))

    def test_missing_archive_rejected(self):
        with self.assertRaises(KeyError):
            seal.validate(self.fixture(omit=seal.ROOT + 'payload/oci-images/core-gateway.oci'))


@unittest.skipUnless(sys.platform == 'linux' and os.geteuid() == 0, 'requires private Linux root mounts')
class FirstBootDispatchTests(unittest.TestCase):
    def test_managed_telemetry_configuration_is_private_and_can_be_disabled(self):
        scripts = Path(__file__).resolve().parent
        with tempfile.TemporaryDirectory(prefix='kautuka-telemetry-settings-', dir='/var/tmp') as temporary:
            script = r'''
set -euo pipefail
mount --make-rprivate /
mount -t tmpfs -o size=8m,mode=0755 tmpfs /etc
mkdir -p /etc/kautuka "$2/payload"
printf 'root:x:0:\n' > /etc/group
printf 'nameserver 192.0.2.53\n' > /etc/resolv.conf
export KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1
source "$1/kautuka-install-linux.sh.in"
write_container_deployment_configuration "$2/payload" root
python3 - <<'PY'
import json,pathlib
p=pathlib.Path('/etc/kautuka/manager.json');d=json.loads(p.read_text())
assert d['ContainerEgress']['TelemetryEnabled'] is True
o=d['Kautuka']['Telemetry']['Otlp']
for name in ['Logs','Metrics','Traces']:
 assert o[name+'Endpoint']=='http://kautuka-telemetry.internal/api/default/v1/'+name.lower()
assert o['UnixSocketPath']=='/var/lib/kautuka/manager/egress/host/connect.sock'
assert p.stat().st_mode&0o777==0o640
d['ContainerEgress']['TelemetryEnabled']=False
for name in ['Logs','Metrics','Traces']:o[name+'Endpoint']=''
o['UnixSocketPath']='';p.write_text(json.dumps(d))
PY
write_container_deployment_configuration "$2/payload" root
python3 - <<'PY'
import json,pathlib
p=pathlib.Path('/etc/kautuka/manager.json');d=json.loads(p.read_text())
d['ContainerEgress']['TelemetryEnabled']=True
d['Kautuka']['Telemetry']['Otlp']['LogsEndpoint']='https://elsewhere.invalid/v1/logs'
p.write_text(json.dumps(d))
PY
if write_container_deployment_configuration "$2/payload" root; then
 echo 'External host telemetry endpoint was accepted' >&2; exit 1
fi
'''
            result = subprocess.run(['unshare','--mount','--fork','bash','-c',script,'fixture',str(scripts),temporary],
                                    capture_output=True,text=True,timeout=30)
            self.assertEqual(result.returncode,0,result.stdout+result.stderr)

    def test_real_runuser_and_su_close_child_leases_retain_parent_locks(self):
        scripts = Path(__file__).resolve().parent
        with tempfile.TemporaryDirectory(prefix='kautuka-installer-fds-',dir='/var/tmp') as temporary:
            script = r"""
set -euo pipefail
export KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1
source "$1/kautuka-install-linux.sh.in"
exec 8>"$2/writer.lock"
exec 9>"$2/apply.lock"
flock -x 8
flock -x 9
probe=$'import os,errno\nfor fd in (8,9):\n try: os.fstat(fd)\n except OSError as e: assert e.errno==errno.EBADF\n else: raise SystemExit("lease inherited")\nassert os.geteuid()==65534'
run_as_service_user nobody /tmp /run/user/65534 python3 -c "$probe"
# Force only the documented fallback selection; execute the real su binary.
command() { if [[ "$*" == '-v runuser' ]]; then return 1; fi; builtin command "$@"; }
run_as_service_user nobody /tmp /run/user/65534 python3 -c "$probe"
[[ -e /proc/self/fd/8 && -e /proc/self/fd/9 ]]
if flock -n "$2/writer.lock" true; then exit 1; fi
if flock -n "$2/apply.lock" true; then exit 1; fi
"""
            result = subprocess.run(['bash','-c',script,'fixture',str(scripts),temporary],
                                    capture_output=True,text=True,timeout=30)
            self.assertEqual(result.returncode,0,result.stdout+result.stderr)

    def test_uninstall_removes_only_staged_payload_after_cohort_retirement(self):
        scripts = Path(__file__).resolve().parent
        with tempfile.TemporaryDirectory(prefix='kautuka-wsl-uninstall-test-', dir='/var/tmp') as temporary:
            program = r"""
import pathlib,subprocess,sys
root,scripts=map(pathlib.Path,sys.argv[1:])
subprocess.run(['mount','--make-rprivate','/'],check=True)
for name,target in [('local','/usr/local'),('state','/var/lib'),('run','/run'),('opt','/opt')]:
    p=root/name;p.mkdir();subprocess.run(['mount','--bind',str(p),target],check=True)
upper=root/'upper';work=root/'work';upper.mkdir();work.mkdir()
subprocess.run(['mount','-t','overlay','overlay','-o',f'lowerdir=/etc,upperdir={upper},workdir={work}','/etc'],check=True)
stage=pathlib.Path('/usr/local/lib/kautuka/firstboot/payload');stage.mkdir(parents=True)
(stage/'archive.oci').write_text('fixture')
sibling=stage.parent.parent/'unrelated-helper';sibling.write_text('keep')
config=pathlib.Path('/etc/kautuka');config.mkdir(exist_ok=True)
(config/'wsl-firstboot-complete').write_text('')
unit=pathlib.Path('/etc/systemd/system/kautuka-wsl-firstboot.service');unit.write_text('fixture')
wants=unit.parent/'multi-user.target.wants';wants.mkdir(exist_ok=True)
link=wants/unit.name
link.unlink(missing_ok=True);link.symlink_to('../'+unit.name)
state=pathlib.Path('/var/lib/kautuka/gateway-data');state.mkdir(parents=True)
(state/'gateway-auth.key').write_text('preserved')
pathlib.Path('/run/kautuka-db-writer.lock').write_text('')
script=r'''set -euo pipefail
export KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1
source "$1/kautuka-install-linux.sh.in"
guard_uninstall_transaction() { :; }
uninstall_container_layout() { printf true; }
get_service_user_home() { printf /var/lib/kautuka; }
resolve_user_runtime_dir() { printf /run/user/0; }
retire_uninstall_containers() {
    [[ -f /usr/local/lib/kautuka/firstboot/payload/archive.oci ]]
    printf retired > /run/retirement-observed
}
best_effort_systemctl() { printf '%s\n' "$*" >> /run/systemctl-events; }
best_effort_user_systemctl() { :; }
best_effort_loginctl() { :; }
remove_service_account_if_safe() { :; }
clear_package_import_temp_state() { :; }
uninstall_release /opt/kautuka/manager /var/lib/kautuka /var/lib/kautuka kautuka-manager.service root false
'''
r=subprocess.run(['bash','-c',script,'fixture',str(scripts)],capture_output=True,text=True)
assert r.returncode==0,r.stdout+r.stderr
assert pathlib.Path('/run/retirement-observed').read_text()=='retired'
assert 'disable --now kautuka-wsl-firstboot.service' in pathlib.Path('/run/systemctl-events').read_text()
assert not stage.parent.exists() and not unit.exists() and not link.is_symlink()
assert not (config/'wsl-firstboot-complete').exists()
assert sibling.read_text()=='keep'
assert (state/'gateway-auth.key').read_text()=='preserved'
"""
            result = subprocess.run(['unshare','--mount','--fork','python3','-c',program,temporary,str(scripts)],
                                    capture_output=True,text=True,timeout=30)
            self.assertEqual(result.returncode,0,result.stdout+result.stderr)

    def test_prepared_retry_preserves_state_and_does_not_inherit_lock(self):
        scripts = Path(__file__).resolve().parent
        with tempfile.TemporaryDirectory(prefix='kautuka-wsl-firstboot-test-', dir='/var/tmp') as temporary:
            fixture = Path(temporary)
            program = r"""
import json,os,pathlib,subprocess,sys
root,scripts=map(pathlib.Path,sys.argv[1:])
subprocess.run(['mount','--make-rprivate','/'],check=True)
for name,target in [('local','/usr/local/lib'),('state','/var/lib'),('run','/run'),('opt','/opt')]:
    p=root/name;p.mkdir();subprocess.run(['mount','--bind',str(p),target],check=True)
upper=root/'upper';work=root/'work';upper.mkdir();work.mkdir()
subprocess.run(['mount','-t','overlay','overlay','-o',f'lowerdir=/etc,upperdir={upper},workdir={work}','/etc'],check=True)
for name in ['service-scope.json','manager.json','wsl-firstboot-complete']:
    pathlib.Path('/etc/kautuka',name).unlink(missing_ok=True)
pathlib.Path('/etc/kautuka').mkdir(exist_ok=True)
pathlib.Path('/var/lib/kautuka-recovery').mkdir()
stage=pathlib.Path('/usr/local/lib/kautuka/firstboot');stage.mkdir(parents=True)
(stage/'payload/bin').mkdir(parents=True)
(stage/'release.json').write_text(json.dumps(dict(version='1.2.3',channel='preview')))
(stage/'manifest-public-key.pem').write_text('fixture public key')
(stage/'start').write_text((scripts/'kautuka-wsl-firstboot.sh').read_text());(stage/'start').chmod(0o755)
(stage/'real-installer.sh').write_text((scripts/'kautuka-install-linux.sh.in').read_text())
(stage/'installer.sh').write_text(r'''source /usr/local/lib/kautuka/firstboot/real-installer.sh
ensure_rootless_podman_ready() { printf 'runtime\n' >> /run/events; }
install_socket_runtime_dirs() { :; }
resolve_user_runtime_dir() { printf /run/user/0; }
install_fresh_container_appliance() {
    printf 'prepared\n' >> /run/events
    mkdir -p /var/lib/kautuka/gateway-data
    printf preserved > /var/lib/kautuka/gateway-data/state
    for service in manager updateservice gateway channelhost credentialshost webhost; do
        mkdir -p "/opt/kautuka/$service/versions/$version"
        ln -s "versions/$version" "/opt/kautuka/$service/current"
    done
    printf '{"applicationServices":"containers"}' > /etc/kautuka/service-scope.json
    printf '{"CoreServices":{"Enabled":true},"ContainerEgress":{"Enabled":true}}' > /etc/kautuka/manager.json
    return 77
}
activate_container_appliance() {
    [[ "$container_first_boot" == true ]]
    # The real parent flock still holds the lease, but no open lock descriptor
    # reaches this child (including descriptor numbers other than 8/9).
    if flock -n /run/kautuka-apply.lock true; then return 78; fi
    python3 - <<'PYFD'
import os
for name in os.listdir('/proc/self/fd'):
    try: target=os.readlink('/proc/self/fd/'+name)
    except FileNotFoundError: continue
    assert target != '/run/kautuka-apply.lock', target
PYFD
    printf 'activated\n' >> /run/events
}
''')
first=subprocess.run([str(stage/'start')],capture_output=True,text=True)
assert first.returncode==77,first.stdout+first.stderr
assert not pathlib.Path('/etc/kautuka/wsl-firstboot-complete').exists()
second=subprocess.run([str(stage/'start')],capture_output=True,text=True)
assert second.returncode==0,second.stdout+second.stderr
assert pathlib.Path('/run/events').read_text().splitlines()==['prepared','runtime','activated']
assert pathlib.Path('/var/lib/kautuka/gateway-data/state').read_text()=='preserved'
assert pathlib.Path('/etc/kautuka/wsl-firstboot-complete').is_file()
# Model an ordinary successful update: current links advance, while the old
# packaged firstboot payload remains. Even if the marker is removed, it must
# never activate/reinstall that older staged version.
pathlib.Path('/etc/kautuka/wsl-firstboot-complete').unlink()
for service in ('manager','updateservice','gateway','channelhost','credentialshost','webhost'):
    parent=pathlib.Path('/opt/kautuka')/service
    (parent/'versions/2.0.0').mkdir();(parent/'current').unlink()
    (parent/'current').symlink_to('versions/2.0.0')
third=subprocess.run([str(stage/'start')],capture_output=True,text=True)
assert third.returncode!=0
assert pathlib.Path('/run/events').read_text().splitlines()==['prepared','runtime','activated']
"""
            result = subprocess.run(['unshare','--mount','--fork','python3','-c',program,str(fixture),str(scripts)],
                                    capture_output=True,text=True,timeout=30)
            self.assertEqual(result.returncode,0,result.stdout+result.stderr)


if __name__ == '__main__':
    unittest.main()
