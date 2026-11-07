#!/usr/bin/env python3
"""Run fresh preparation in private mount trees; no real account or service changes."""
import json
import os
from pathlib import Path
import subprocess
import tempfile

if os.name != 'posix' or os.geteuid() != 0 or os.environ.get('KAUTUKA_DISPOSABLE_TEST') != '1':
    raise SystemExit('requires Linux root and KAUTUKA_DISPOSABLE_TEST=1')

scripts = Path(__file__).resolve().parent
with tempfile.TemporaryDirectory(prefix='kautuka-container-preparation-', dir='/var/tmp') as temporary:
    root = Path(temporary)
    child = r"""
import hashlib, json, os, pathlib, subprocess, sys
root, scripts = map(pathlib.Path, sys.argv[1:])
subprocess.run(['mount', '--make-rprivate', '/'], check=True)
# Every writable production parent is private. Existing /etc is read-only lower
# content; all changes go to this fixture's overlay upper directory.
for name, target in (('opt','/opt'), ('state','/var/lib'), ('local-lib','/usr/local/lib'),
                     ('run','/run'), ('tmpfiles','/usr/lib/tmpfiles.d')):
    directory = root / name; directory.mkdir()
    subprocess.run(['mount', '--bind', str(directory), target], check=True)
upper, work = root/'etc-upper', root/'etc-work'; upper.mkdir(); work.mkdir()
subprocess.run(['mount','-t','overlay','overlay','-o',f'lowerdir=/etc,upperdir={upper},workdir={work}','/etc'],check=True)
resolver=pathlib.Path('/etc/resolv.conf')
if resolver.is_symlink(): resolver.unlink()
resolver.write_text('nameserver 192.0.2.53\n')
with open('/etc/passwd','a') as stream: stream.write('kautuka:x:0:0:Fixture runtime:/var/lib/kautuka:/usr/sbin/nologin\n')
with open('/etc/group','a') as stream: stream.write('kautuka:x:0:\n')
payload=root/'payload'; (payload/'bin').mkdir(parents=True); (payload/'oci-images').mkdir()
roles=('kautuka-base','openobserve','git-pkgs-proxy','core-credentialshost','core-channelhost',
       'core-gateway','core-webhost','egress-setup','egress-envoy','egress-dns','image-acquisition')
entries=[]
for role in roles:
    data=('fixture archive '+role).encode(); digest=hashlib.sha256(data).hexdigest(); name=f'{role}-{digest}.oci'
    (payload/'oci-images'/name).write_bytes(data)
    entries.append(dict(id=role,image='example.invalid/fixture@sha256:'+'b'*64,archive=name,
                        archiveSha256=digest,platform='linux/amd64',localImageId='sha256:'+'a'*64))
(payload/'bin/runtime-images.json').write_text(json.dumps(dict(schemaVersion=1,images=entries)))
for name in ('kautuka-apply-release.sh','kautuka-update-recovery.py','kautuka-runtime-images.py'):
    target={'kautuka-apply-release.sh':'kautuka-apply-release','kautuka-update-recovery.py':'kautuka-update-recovery',
            'kautuka-runtime-images.py':'kautuka-runtime-images'}[name]
    (payload/'bin'/target).write_text((scripts/name).read_text())
for service in ('manager','updateservice','gateway','channelhost','credentialshost','webhost'):
    directory=payload/'bin' if service=='manager' else payload/'services'/service; directory.mkdir(parents=True,exist_ok=True)
    binary=directory/('kautuka-'+service)
    binary.write_text('#!/bin/sh\nexit 0\n'); binary.chmod(0o755)
(payload/'bin/kautuka-manager').write_text('#!/bin/sh\nprintf "%s\\n" core-service-update-v1\n')
channel=payload/'services/channelhost/kautuka-channelhost'
channel.write_text('''#!/usr/bin/python3
import pathlib,sys
files=(('gateway-channel-client.pfx','gateway-channel-delegation.pfx'),('channel-ca.cer',),
       ('channel-server.pfx',),('channel-ca.cer','gateway-channel-client.cer','gateway-channel-delegation.cer'),
       ('channel-ca.key','channel-ca.cer'))
for value,names in zip(sys.argv[2:],files):
    root=pathlib.Path(value);root.mkdir(parents=True)
    for name in names:(root/name).write_bytes(b'fixture material')
'''); channel.chmod(0o755)
(payload/'services/gateway/appsettings.Production.json').write_text('{"Kautuka":{"ConnectorAuth":{"Host":"https://broker.example.test"}}}')
private=root/'private.pem'; public=root/'public.pem'
subprocess.run(['openssl','ecparam','-name','prime256v1','-genkey','-noout','-out',str(private)],check=True)
subprocess.run(['openssl','pkey','-in',str(private),'-pubout','-out',str(public)],check=True,stdout=subprocess.DEVNULL)
# A validated preprovided file retains its policy while gaining runtime-readable
# permissions. This is preparation, so no application process is involved.
etc=pathlib.Path('/etc/kautuka');etc.mkdir(exist_ok=True)
provided={'CoreServices':{'Enabled':True},'ContainerEgress':{'Enabled':True,'DnsUpstreams':['192.0.2.54'],'Grants':[]},
          'Kautuka':{'Telemetry':{'Otlp':dict(LogsEndpoint='',MetricsEndpoint='',TracesEndpoint='')}}}
config=etc/'manager.json';config.write_text(json.dumps(provided));config.chmod(0o600)
configure=root/'configure.sh'
configure.write_text('set -euo pipefail\nexport KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1\nsource "'+str(scripts/'kautuka-install-linux.sh.in')+'"\nwrite_container_deployment_configuration "'+str(payload)+'" kautuka\n')
subprocess.run(['bash',str(configure)],check=True)
assert config.stat().st_mode & 0o777 == 0o640 and json.loads(config.read_text())==provided
config.unlink()
wrapper=root/'run.sh'
wrapper.write_text(r'''#!/bin/bash
set -euo pipefail
export KAUTUKA_INSTALL_MANAGER_RELEASE_SOURCE_ONLY=1
source "$1/kautuka-install-linux.sh.in"
service_user=kautuka; service_group=kautuka; internal_state_root=/var/lib/kautuka
visible_root=/var/lib/kautuka; install_root=/opt/kautuka/manager; version=1.2.3
release_channel=preview; skip_enable=true; manifest_url=''; github_release_candidate=false
KAUTUKA_DEFAULT_MANIFEST_PUBLIC_KEY_PEM="$(cat "$3")"
ensure_service_account() { :; }
ensure_selinux_runtime_exec_policy() { :; }
ensure_dotnet_host_libraries() { :; }
get_service_user_home() { printf /var/lib/kautuka; }
resolve_user_runtime_dir() { printf /run/user/0; }
ensure_rootless_podman_ready() {
    [[ "$5" == containers && "$(stat -c %u "$3")" == 0 ]]
    mkdir -p "$3/.config/containers" "$3/.local/share/containers/storage" "$4"
    [[ "${FAIL_EARLY:-}" != 1 ]] || return 78
}
systemctl() { printf 'systemctl %s\n' "$*" >> "$FIXTURE_EVENTS"; }
run_as_service_user() {
    shift 3
    if [[ "$1" != podman ]]; then "$@"; return; fi
    printf 'podman %s\n' "$*" >> "$FIXTURE_EVENTS"
    case "$*" in
        'podman ps --all --format {{.ID}}') return 0 ;;
        'podman image exists '*) [[ -f "$FIXTURE_CACHE" ]]; return ;;
        'podman load') cat >/dev/null; touch "$FIXTURE_CACHE" ;;
        'podman image inspect '*) printf '[{"Id":"sha256:%064d","Os":"linux","Architecture":"amd64"}]\n' 0 | sed 's/0000000000000000000000000000000000000000000000000000000000000000/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/' ;;
        *) echo 'unexpected runtime operation' >&2; return 99 ;;
    esac
}
if [[ "${FAIL_AFTER_MATERIAL:-}" == 1 ]]; then write_initial_update_runtime_policy() { return 79; }; fi
install_fresh_container_appliance "$2"
''')
events=root/'events'; env=dict(os.environ,FIXTURE_EVENTS=str(events),FIXTURE_CACHE=str(root/'image-cache'))
command=['bash',str(wrapper),str(scripts),str(payload),str(public)]
# An early runtime failure must also leave ancestry suitable for retry.
early=subprocess.run(command,env=dict(env,FAIL_EARLY='1'),capture_output=True,text=True)
assert early.returncode!=0 and pathlib.Path('/var/lib/kautuka').stat().st_uid==0, early.stdout+early.stderr
# Fail after immutable payloads and channel material exist, but before pointers.
failed=subprocess.run(command,env=dict(env,FAIL_AFTER_MATERIAL='1'),capture_output=True,text=True)
assert failed.returncode!=0, failed.stdout+failed.stderr
assert not list(pathlib.Path('/opt/kautuka').glob('*/current'))
assert not list(pathlib.Path('/opt/kautuka').glob('*/versions/1.2.3'))
assert not pathlib.Path('/var/lib/kautuka/security/channel').exists()
assert not pathlib.Path('/etc/kautuka/manager.json').exists()
assert len(list(pathlib.Path('/var/lib/kautuka/oci-images').glob('*.oci')))==11
# Retry the same verified payload. This exercises the actual cleanup result.
success=subprocess.run(command,env=env,capture_output=True,text=True)
assert success.returncode==0, success.stdout+success.stderr
contract=json.loads(pathlib.Path('/etc/kautuka/service-scope.json').read_text())
assert contract['applicationServices']=='containers' and contract['systemUnits']==[]
assert set(contract['userUnits'])=={'kautuka-manager.service','kautuka-updateservice.service'}
units=pathlib.Path('/var/lib/kautuka/.config/systemd/user')
assert {p.name for p in units.glob('kautuka-*.service')}==set(contract['userUnits'])
assert not (units/'default.target.wants/kautuka.target').exists(), '--skip-enable must not enable at boot'
for service in ('manager','updateservice','gateway','channelhost','credentialshost','webhost'):
    assert pathlib.Path('/opt/kautuka',service,'current').resolve()==pathlib.Path('/opt/kautuka',service,'versions/1.2.3')
settings=json.loads(pathlib.Path('/etc/kautuka/manager.json').read_text())
for name in ('sidecars','proxy'):
    directory=pathlib.Path('/var/lib/kautuka')/name
    assert directory.is_dir() and directory.stat().st_mode & 0o777==0o700
assert settings['ContainerEgress']['DnsUpstreams']
assert all(grant['AgentId']==grant['ComponentId'] for grant in settings['ContainerEgress']['Grants'])
assert settings['Kautuka']['Telemetry']['Otlp']==dict(LogsEndpoint='',MetricsEndpoint='',TracesEndpoint='')
before=events.read_text(); rejected=subprocess.run(command,env=env,capture_output=True,text=True)
assert rejected.returncode!=0 and events.read_text()==before, 'existing deployment was mutated'
print('PASS private fresh preparation, pre-activation cleanup/retry, two host units, matching grants and existing-deployment refusal')
print('LIMIT identity/systemd/Podman and channel material are fixtures; no application container was started')
"""
    # JSON transfer avoids shell interpolation of the embedded Bash fixture.
    bootstrap = "import sys,json;data=json.load(sys.stdin);sys.argv=['fixture',data['root'],data['scripts']];exec(data['child'])"
    result = subprocess.run(['unshare','--mount','--fork','python3','-c',bootstrap],
                            input=json.dumps(dict(child=child,root=str(root),scripts=str(scripts))),text=True)
    raise SystemExit(result.returncode)
