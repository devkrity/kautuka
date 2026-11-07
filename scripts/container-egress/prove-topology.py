#!/usr/bin/env python3
"""Fedora 44 rootless topology proof; creates only disposable accounts and networks.

Requires Linux root, systemd, Podman, iproute, nftables, curl, OpenSSL and ldd.
Supply offline OCI archives of the documented stock Envoy and CoreDNS images.
The outside TLS/DNS containers simulate remote endpoints; they are test fixtures.
This is not installed application enforcement or a production container launcher.
"""
import argparse
import resource
import fcntl
import grp
import hashlib
import json
import os
import pathlib
import pwd
import shutil
import signal
import stat
import subprocess
import sys
import sysconfig
import tarfile
import tempfile
import uuid

def command(args, timeout=60):
    p = subprocess.run(args, capture_output=True, text=True, timeout=timeout, cwd="/var/tmp")
    if p.returncode: raise RuntimeError(repr(args) + ': ' + p.stderr[-3000:])
    return p.stdout.strip()

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--image-cache', required=True, type=pathlib.Path, help='Directory containing envoy.oci and dns.oci')
parser.add_argument('--policy-fixture', required=True, type=pathlib.Path, help='C# test-exported cds/lds and mixed-cds/mixed-lds JSON files')
parser.add_argument('--manager-smoke', type=pathlib.Path, help='Optional linux-x64 Native AOT Manager fixture executable')
parser.add_argument('--core-build-root', type=pathlib.Path, help='Optional isolated native application-service build snapshot')
parser.add_argument('--core-only', action='store_true', help='Run only core-service Manager acceptance, omitting workload regressions')
parser.add_argument('--host-only', action='store_true', help='Run only native host artifact acceptance, omitting core services and workload regressions')
parser.add_argument('--acquisition-source', help='Optional pinned public registry fixture to acquire through the native private network')
parser.add_argument('--report', required=True, type=pathlib.Path, help='New output JSON file')
parser.add_argument('--work-root', type=pathlib.Path, default=pathlib.Path('/var/tmp'), help='Existing scratch filesystem for the disposable store')
options = parser.parse_args()
if not options.work_root.is_absolute() or options.work_root.is_symlink() or options.work_root.resolve() != options.work_root or not options.work_root.is_dir():
    parser.error('work root must be an existing canonical absolute directory')
if sys.platform != 'linux' or os.geteuid() != 0:
    parser.error('requires Linux root in a disposable test distro')
for role in (('envoy','dns','pkgs','sandbox') if options.manager_smoke is not None else ('envoy','dns')):
    if not (options.image_cache/(role+'.oci')).is_file():
        parser.error('missing offline '+role+'.oci image archive')
if options.manager_smoke is not None and not options.manager_smoke.is_file():
    parser.error('missing Native AOT Manager fixture executable')
if options.core_build_root is not None:
    core_root = options.core_build_root
    if core_root.is_symlink() or core_root.resolve().parent != pathlib.Path('/var/tmp') or not core_root.name.startswith('kautuka-core-native-'):
        parser.error('core build must be an isolated task snapshot under /var/tmp')
    if options.manager_smoke is None or not (options.image_cache/'core-base.oci').is_file():
        parser.error('core test needs a Manager smoke binary and offline core-base.oci')
    for service in ('CredentialsHost','ChannelHost','Gateway','WebHost'):
        if not (core_root/'publish'/service/('Kautuka.'+service)).is_file(): parser.error('missing core native binary '+service)
if options.host_only and (options.manager_smoke is None or options.core_only): parser.error('--host-only needs a Manager fixture and cannot combine with --core-only')
if options.acquisition_source and (options.manager_smoke is None or not (options.image_cache/'skopeo.oci').is_file()): parser.error('acquisition proof requires Manager fixture and offline skopeo.oci')
if options.core_only and options.core_build_root is None: parser.error('--core-only requires --core-build-root')
minimum_scratch = (4 if options.core_build_root is not None and not options.core_only else 2) * 1024**3
if shutil.disk_usage(options.work_root).free < minimum_scratch:
    parser.error('insufficient scratch capacity for this proof; choose a larger --work-root before creating disposable resources')
resource.setrlimit(resource.RLIMIT_CORE,(0,0))
report_stream = options.report.open('x', encoding='utf-8')
lock = os.open('/run/lock/kautuka-egress-proof.lock', os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
lock_status = os.fstat(lock)
if not stat.S_ISREG(lock_status.st_mode) or lock_status.st_uid != 0 or lock_status.st_nlink != 1 or lock_status.st_mode & 0o077:
    parser.error('unsafe fixture lock')
fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)

def interrupted(signum, frame):
    raise InterruptedError(f'fixture interrupted by signal {signum}')

signal.signal(signal.SIGTERM, interrupted)
signal.signal(signal.SIGINT, interrupted)

prefix = 'kev6-' + uuid.uuid4().hex[:8]
root = pathlib.Path(tempfile.mkdtemp(prefix='kautuka-egress-v6-', dir=options.work_root))
root.chmod(0o711)
created = False
started = False
pod_created = False
network_names = []
containers = []
report = {'prefix':prefix, 'passed':False, 'cleanupErrors':[], 'scratchRoot':str(options.work_root),
          'scratchFilesystem':command(['findmnt','--noheadings','--output','FSTYPE','--target',str(options.work_root)])}
try:
    allocations = [line.split(':') for file in ('/etc/subuid','/etc/subgid') for line in pathlib.Path(file).read_text().splitlines() if line and not line.startswith('#')]
    if any(len(x)!=3 or int(x[1])<0 or int(x[2])<=0 or int(x[1])+int(x[2])>0xFFFFFFFF for x in allocations): raise RuntimeError('invalid subordinate allocation')
    used = {x.pw_uid for x in pwd.getpwall()} | {x.gr_gid for x in grp.getgrall()}
    account_uid = 1800000000 + int(uuid.uuid4().hex[:6],16)
    while account_uid in used or any(int(x[1]) <= account_uid < int(x[1])+int(x[2]) for x in allocations): account_uid += 1
    command(['useradd','--system','--uid',str(account_uid),'--create-home','--home-dir',str(root/'home'),'--shell','/usr/sbin/nologin','--no-log-init',prefix])
    created = True
    user = pwd.getpwnam(prefix)
    uid = str(user.pw_uid)
    ranges = [line.split(':') for file in ('/etc/subuid','/etc/subgid') for line in pathlib.Path(file).read_text().splitlines() if line and not line.startswith('#')]
    start = max([100000]+[int(parts[1])+int(parts[2]) for parts in ranges])
    start = (start+65535)//65536*65536
    actual_ids = {x.pw_uid for x in pwd.getpwall()} | {x.gr_gid for x in grp.getgrall()}
    while any(start <= x < start+65536 for x in actual_ids): start += 65536
    if start+65536 > 0xFFFFFFFF: raise RuntimeError('subordinate range exhausted')
    command(['usermod','--add-subuids',f'{start}-{start+65535}','--add-subgids',f'{start}-{start+65535}',prefix])
    for file in ('/etc/subuid','/etc/subgid'):
        entries = [line.split(':') for line in pathlib.Path(file).read_text().splitlines() if line and not line.startswith('#')]
        if [x for x in entries if x[0] == prefix] != [[prefix,str(start),'65536']]:
            raise RuntimeError('unexpected allocated subordinate range')
        if any(x[0] != prefix and int(x[1]) < start+65536 and start < int(x[1])+int(x[2]) for x in entries):
            raise RuntimeError('overlapping subordinate owner allocation')
    command(['systemctl','start','user@'+uid+'.service'])
    started = True
    environment = ['env','-i','PATH=/usr/sbin:/usr/bin:/sbin:/bin','HOME='+str(root/'home'),
                   'XDG_RUNTIME_DIR=/run/user/'+uid,'DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/'+uid+'/bus']
    base = ['runuser','-u',prefix,'--'] + environment
    def podman(*args, timeout=60):
        if args[0]=='run': args=(args[0],'--pull=never',*args[1:])
        return command(base+['podman',*args],timeout=timeout)
    info = json.loads(podman('info','--format=json'))
    report['runtime'] = {'rootless':info['host']['security']['rootless'],'version':info['version']['Version'],
                         'networkBackend':info['host']['networkBackend']}
    if not report['runtime']['rootless']: raise RuntimeError('not rootless')
    names = {role:prefix+'-'+role for role in ('outside','a','b')}
    names['outside']='kautuka-outside'
    for index,role in enumerate(('outside','a','b')):
        args = ['network','create','--subnet',f'10.247.{90+index}.0/24','--disable-dns']
        if role != 'outside': args += ['--internal']
        else: args += ['--label','kautuka.egress=true','--label','kautuka.egress.scope=outside']
        podman(*args,names[role])
        network_names.append(names[role])
    archive = root/'home'/'tools.tar'
    files = set()
    for binary in ('/usr/sbin/ip','/usr/sbin/nft','/usr/bin/sleep','/usr/bin/curl','/usr/bin/openssl','/usr/bin/bash','/usr/bin/sh','/usr/bin/tail','/usr/bin/cat','/usr/bin/python3'):
        files.add(binary)
        for line in command(['ldd',binary]).splitlines():
            files.update(token for token in line.split() if token.startswith('/'))
    stdlib = pathlib.Path(sysconfig.get_path('stdlib'))
    python_files = [path for path in stdlib.rglob('*') if path.is_file() and not any(part in ('site-packages','__pycache__','test','tests','idlelib','tkinter','ensurepip') for part in path.relative_to(stdlib).parts)]
    for module in python_files:
        if module.suffix == '.so':
            for line in command(['ldd',str(module)]).splitlines():
                files.update(token for token in line.split() if token.startswith('/'))
    with tarfile.open(archive,'w',dereference=True) as tar:
        for file in sorted(files): tar.add(file,arcname=file.lstrip('/'),recursive=False)
        for file in python_files: tar.add(file,arcname=str(file).lstrip('/'),recursive=False)
    archive.chmod(0o644)
    tools = podman('import','--quiet',str(archive))
    pod = prefix+'-egress'
    podman('pod','create','--infra-image',tools,'--infra-command','/usr/bin/sleep infinity','--security-opt','no-new-privileges','--sysctl','net.ipv4.ip_unprivileged_port_start=0','--name',pod,'--dns','127.0.0.1','--network',names['outside']+':ip=10.247.90.2',
           '--network',names['a']+':ip=10.247.91.2')
    pod_created = True
    podman('pod','start',pod)
    state = json.loads(podman('pod','inspect',pod))[0]
    infra = state['InfraContainerID']
    def snapshot():
        state = json.loads(podman('inspect',infra))[0]
        pid = state['State']['Pid']
        return {'pid':pid,'networks':state['NetworkSettings']['Networks'],
                'routes':command(['nsenter','--net=/proc/'+str(pid)+'/ns/net','ip','-j','route']),
                'addresses':command(['nsenter','--net=/proc/'+str(pid)+'/ns/net','ip','-j','address'])}
    client = prefix+'-client'
    podman('run','--detach','--name',client,'--network',names['a']+':ip=10.247.91.10',
           '--dns','10.247.91.2','--sysctl','net.ipv6.conf.all.disable_ipv6=1','--user','65532:65532','--cap-drop=ALL','--security-opt=no-new-privileges',tools,'/usr/bin/sleep','120')
    containers.append(client)
    report['clientBefore'] = podman('exec',client,'/usr/sbin/ip','-j','route')
    report['routeSetup'] = podman('run','--rm','--network','container:'+client,'--cap-drop=ALL','--cap-add=NET_ADMIN',
                                tools,'/usr/sbin/ip','route','replace','default','via','10.247.91.2')
    report['clientAfter'] = podman('exec',client,'/usr/sbin/ip','-j','route')
    route = [r for r in json.loads(report['clientAfter']) if r['dst']=='default']
    if len(route)!=1 or route[0]['gateway']!='10.247.91.2': raise RuntimeError('client route setup failed')
    report['workloadCannotChangeRoute'] = subprocess.run(base+['podman','exec',client,'/usr/sbin/ip','route','del','default'],capture_output=True,text=True,timeout=10).returncode
    if report['workloadCannotChangeRoute']==0: raise RuntimeError('workload changed route')
    # A one-shot container in the actual egress pod installs only a denying filter.
    report['filterSetup'] = podman('run','--rm','--pod',pod,'--cap-drop=ALL','--cap-add=NET_ADMIN',
                                 tools,'/usr/sbin/nft','add','table','inet','kautuka_test')
    report['filterObserved'] = command(['nsenter','--net=/proc/'+str(snapshot()['pid'])+'/ns/net','nft','list','table','inet','kautuka_test'])
    import time
    def logs(name):
        p = subprocess.run(base+['podman','logs',name],capture_output=True,text=True,cwd='/var/tmp',timeout=10)
        return p.stdout+p.stderr
    for role in (('envoy','dns','pkgs','sandbox') if options.manager_smoke is not None else ('envoy','dns')):
        with open(options.image_cache/(role+'.oci'),'rb') as stream:
            report[role+'ArchiveSha256'] = hashlib.file_digest(stream, 'sha256').hexdigest()
            stream.seek(0)
            p = subprocess.run(base+['podman','load','--quiet'],stdin=stream,capture_output=True,text=True,cwd='/var/tmp',timeout=120)
            report[role+'ImageLoad'] = {'stdout':p.stdout,'stderr':p.stderr}
            if p.returncode: raise RuntimeError('cached image load: '+p.stderr)
    report['loadedImages'] = json.loads(podman('images','--format=json'))
    envoy_image = 'docker.io/envoyproxy/envoy:distroless-v1.40.2'
    dns_image = 'docker.io/coredns/coredns:1.14.6'
    report['resolvedEnvoy'] = json.loads(podman('image','inspect',envoy_image))[0]['Id']
    report['resolvedDns'] = json.loads(podman('image','inspect',dns_image))[0]['Id']
    managed_images={}
    if options.manager_smoke is not None:
        for role in ('pkgs','sandbox'):
            with tarfile.open(options.image_cache/(role+'.oci')) as cached_archive:
                index=json.load(cached_archive.extractfile('index.json'))
                manifest=json.load(cached_archive.extractfile('blobs/sha256/'+index['manifests'][0]['digest'].split(':')[1]))
                config=manifest['config']['digest']
            image='localhost/kautuka-'+role+'-fixture:'+prefix
            podman('tag',config,image)
            managed_images[role]=image
            report['resolved'+role.title()]=json.loads(podman('image','inspect',image))[0]['Id']
    fixture = root/'home'/'fixture'
    fixture.mkdir(mode=0o755)
    command(['openssl','req','-x509','-newkey','ec','-pkeyopt','ec_paramgen_curve:P-256','-nodes',
             '-keyout',str(fixture/'key.pem'),'-out',str(fixture/'cert.pem'),'-days','1',
             '-subj','/CN=allowed.kautuka.test','-addext','subjectAltName=DNS:allowed.kautuka.test,DNS:denied.kautuka.test,DNS:artifact.kautuka.test'])
    (fixture/'key.pem').chmod(0o644)
    (fixture/'cert.pem').chmod(0o644)
    sink = prefix+'-sink'
    podman('run','-d','--name',sink,'--network',names['outside']+':ip=10.247.90.10',
           '--cap-drop=ALL','--user','65532:65532','--volume',str(fixture)+':/fixture:ro',
           tools,'/usr/bin/openssl','s_server','-accept','8443','-cert','/fixture/cert.pem','-key','/fixture/key.pem','-www')
    containers.append(sink)
    corefile = '.:53 {\n hosts {\n  10.247.90.10 allowed.kautuka.test denied.kautuka.test\n }\n log\n errors\n}\n'
    (fixture/'Corefile').write_text(corefile)
    generated_lds=json.loads((options.policy_fixture/'lds.json').read_text())
    generated_cds=json.loads((options.policy_fixture/'cds.json').read_text())
    mixed_lds=json.loads((options.policy_fixture/'mixed-lds.json').read_text())
    mixed_cds=json.loads((options.policy_fixture/'mixed-cds.json').read_text())
    listener=generated_lds['resources'][0]
    policy_version=generated_lds['version_info']
    envoy_dir = fixture/'envoy-config'
    envoy_dir.mkdir(mode=0o755)
    clusters=generated_cds['resources']
    def publish(kind, version, resources):
        target=envoy_dir/(kind+'.json')
        temporary=envoy_dir/(kind+'.next')
        temporary.write_text(json.dumps({'version_info':version,'resources':resources}))
        temporary.chmod(0o644)
        os.replace(temporary,target)
    def listener_resource(value, version): return {'@type':'type.googleapis.com/envoy.config.listener.v3.Listener',**value,'metadata':{'filter_metadata':{'kautuka.egress':{'policy_version':version}}}}
    publish('cds',policy_version,clusters)
    initial=json.loads(json.dumps(listener));initial['filter_chains']=[chain for chain in initial['filter_chains'] if chain['name']=='deny'];initial['filter_chain_matcher']={'on_no_match':{'action':{'name':'deny','typed_config':{'@type':'type.googleapis.com/google.protobuf.StringValue','value':'deny'}}}}
    publish('lds','deny-1',[listener_resource(initial,'deny-1')])
    config={'node':{'id':'kautuka-proof','cluster':'proof'},'admin':{'address':{'socket_address':{'address':'127.0.0.1','port_value':9901}}},
            'dynamic_resources':{'cds_config':{'resource_api_version':'V3','path_config_source':{'path':'/envoy/cds.json'}},
                                 'lds_config':{'resource_api_version':'V3','path_config_source':{'path':'/envoy/lds.json'}}}}
    (envoy_dir/'bootstrap.json').write_text(json.dumps(config))
    for file in fixture.iterdir():
        if file.is_file(): file.chmod(0o644)
    (envoy_dir/'bootstrap.json').chmod(0o644)
    dns = prefix+'-dns'
    podman('run','-d','--name',dns,'--pod',pod,'--read-only','--cap-add=NET_BIND_SERVICE','--cap-drop=ALL','--security-opt=no-new-privileges','--user','65532:65532','--volume',str(fixture/'Corefile')+':/Corefile:ro',
           dns_image,'-conf','/Corefile')
    containers.append(dns)
    envoy = prefix+'-envoy'
    podman('run','-d','--name',envoy,'--pod',pod,'--read-only','--cap-drop=ALL','--security-opt=no-new-privileges','--user','65532:65532','--volume',str(envoy_dir)+':/envoy:ro',
           envoy_image,'-c','/envoy/bootstrap.json','--concurrency','1')
    containers.append(envoy)
    # Prove the upstream is actually available before interpreting denied clients.
    def pid(name): return json.loads(podman('inspect',name))[0]['State']['Pid']
    def network_command(name,args,content=None):
        p = subprocess.run(['nsenter','--net=/proc/'+str(pid(name))+'/ns/net',*args],input=content,capture_output=True,text=True,cwd='/var/tmp',timeout=10)
        if p.returncode: raise RuntimeError('namespace command failed: '+p.stderr)
        return p.stdout
    def install_counter(name,port):
        network_command(name,['nft','-f','-'],f"""table ip fixture_count {{
 chain observe {{
 type filter hook input priority filter; policy accept;
 tcp dport {port} tcp flags & (syn | ack) == syn counter
 }}
}}
""")
    def packets(name):
        data=json.loads(network_command(name,['nft','-j','list','chain','ip','fixture_count','observe']))
        return sum(e['counter']['packets'] for x in data['nftables'] if 'rule' in x for e in x['rule']['expr'] if 'counter' in e)
    install_counter(sink,8443)
    report['upstreamBaseline'] = podman('run','--rm','--pod',pod,'--cap-drop=ALL',tools,'/usr/bin/curl','--max-time','5','--insecure','https://10.247.90.10:8443/')[:100]
    rules = '''table ip kautuka_egress {
 chain capture_tcp { type nat hook prerouting priority dstnat; policy accept;
  ip saddr { 10.247.91.0/24, 10.247.92.0/24 } ip daddr { 10.247.91.2, 10.247.92.2 } tcp dport 53 return
  ip saddr { 10.247.91.0/24, 10.247.92.0/24 } meta l4proto tcp redirect to :15001
 }
 chain forward { type filter hook forward priority filter; policy drop; }
 chain input { type filter hook input priority filter; policy drop;
  iifname "lo" accept
  ct state established,related accept
  ip saddr { 10.247.91.0/24, 10.247.92.0/24 } tcp dport 15001 accept
  ip saddr { 10.247.91.0/24, 10.247.92.0/24 } ip daddr { 10.247.91.2, 10.247.92.2 } udp dport 53 accept
  ip saddr { 10.247.91.0/24, 10.247.92.0/24 } ip daddr { 10.247.91.2, 10.247.92.2 } tcp dport 53 accept
 }
}
table ip6 kautuka_no_ipv6 {
 chain input { type filter hook input priority filter; policy drop; }
 chain forward { type filter hook forward priority filter; policy drop; }
 chain output { type filter hook output priority filter; policy drop; }
}
'''
    (fixture/'rules.nft').write_text(rules)
    podman('run','--rm','--pod',pod,'--cap-drop=ALL','--cap-add=NET_ADMIN','--volume',str(fixture/'rules.nft')+':/rules.nft:ro',tools,'/usr/sbin/nft','-f','/rules.nft')
    time.sleep(2)
    for name in (dns,envoy):
        if not json.loads(podman('inspect',name))[0]['State']['Running']: raise RuntimeError('service exited: '+name+' '+logs(name))
    def request(container,url):
        p = subprocess.run(base+['podman','exec',container,'/usr/bin/curl','--max-time','4','--insecure',url],capture_output=True,text=True,cwd='/var/tmp',timeout=10)
        return {'exit':p.returncode,'stdout':p.stdout[:80],'stderr':p.stderr[-300:]}
    report['capabilities'] = {}
    for name in (envoy,dns):
        status=dict(line.split(':',1) for line in pathlib.Path('/proc/'+str(pid(name))+'/status').read_text().splitlines() if ':' in line)
        report['capabilities'][name] = {k:status[k].strip() for k in ('NoNewPrivs','CapEff','CapPrm','CapBnd','CapAmb')}
        expected=0x400 if name==dns else 0
        if int(status['CapBnd'],16)!=expected or status['NoNewPrivs'].strip()!='1' or int(status['CapAmb'],16): raise RuntimeError('unexpected service capability boundary')
        if int(status['CapEff'],16) & ~expected or int(status['CapPrm'],16) & ~expected: raise RuntimeError('unexpected effective capability')
    def admin(path): return json.loads(network_command(infra,['curl','--silent','--fail','http://127.0.0.1:9901/'+path]))
    def active_version():
        for config in admin('config_dump')['configs']:
            if config.get('@type','').endswith('ListenersConfigDump'):
                for item in config.get('dynamic_listeners',[]):
                    if item.get('name')=='transparent' and not item.get('warming_state'):
                        marker=item.get('active_state',{}).get('listener',{}).get('metadata',{}).get('filter_metadata',{}).get('kautuka.egress',{}).get('policy_version')
                        if marker==config.get('version_info'): return marker
    def await_active(version):
        deadline=time.monotonic()+10
        while active_version()!=version:
            if time.monotonic()>deadline:
                report['acceptanceTimeoutDump']=admin('config_dump');report['acceptanceTimeoutStats']=admin('stats?format=json');raise RuntimeError('listener version not accepted: '+version)
            time.sleep(0.05)
    def rejected():
        return next((item['value'] for item in admin('stats?format=json')['stats'] if item.get('name')=='listener_manager.lds.update_rejected'),0)
    def await_clusters(version, expected):
        deadline=time.monotonic()+10
        while True:
            configs=[config for config in admin('config_dump')['configs'] if config.get('@type','').endswith('ClustersConfigDump')]
            if configs:
                config=configs[0]
                active=config.get('dynamic_active_clusters',[])
                markers={item['cluster']['name']:item['cluster'].get('metadata',{}).get('filter_metadata',{}).get('kautuka.egress',{}).get('policy_version') for item in active}
                if config.get('version_info')==version and not config.get('dynamic_warming_clusters') and markers=={item['name']:item['name'] for item in expected}: return
            if time.monotonic()>deadline:
                report['clusterTimeoutDump']=admin('config_dump');raise RuntimeError('cluster version not active: '+version)
            time.sleep(0.05)
    await_clusters(policy_version,clusters)
    await_active('deny-1')
    report['beforePolicyActive']=request(client,'https://allowed.kautuka.test:8443/')
    if report['beforePolicyActive']['exit']==0: raise RuntimeError('initial deny policy allowed request')
    publish('lds',policy_version,[listener_resource(listener,policy_version)])
    await_active(policy_version)
    report['acceptedListenerVersion']=active_version()
    report['dynamicConfigDump']=admin('config_dump')
    rejected_before=rejected()
    invalid=json.loads(json.dumps(listener));invalid['address']['socket_address']['port_value']=70000
    publish('lds','invalid-3',[listener_resource(invalid,'invalid-3')])
    deadline=time.monotonic()+10
    while rejected()<=rejected_before:
        if time.monotonic()>deadline: raise RuntimeError('invalid listener rejection not observed')
        time.sleep(0.05)
    report['rejectedListener']={'activeVersion':active_version(),'rejectedCount':rejected()}
    report['rejectedConfigDump']=admin('config_dump')
    if active_version()!=policy_version: raise RuntimeError('invalid update replaced last accepted listener')
    publish('lds',policy_version,[listener_resource(listener,policy_version)])
    before = packets(sink)
    report['allowedA'] = request(client,'https://allowed.kautuka.test:8443/')
    report['allowedFreshSyns'] = packets(sink)-before
    if report['allowedFreshSyns'] <= 0: raise RuntimeError('sink counter positive control failed')
    before = packets(sink)
    report['deniedName'] = request(client,'https://denied.kautuka.test:8443/')
    if packets(sink)!=before: raise RuntimeError('denied name reached upstream')
    report['deniedLiteral'] = request(client,'https://10.247.90.10:8443/')
    if packets(sink)!=before: raise RuntimeError('denied literal reached upstream')
    staged_dir = root/'home'/'staged'
    staged_dir.mkdir(mode=0o777)
    staged_dir.chmod(0o777)
    staged = prefix+'-staged'
    stage_command = 'printf started > /stage/marker; IFS= read -r line; printf "stdout:%s\\n" "$line"; printf "stderr:%s\\n" "$line" >&2; IFS= read -r finish; exit 7'
    staged_id = podman('create','--interactive','--pull=never','--sysctl','net.ipv6.conf.all.disable_ipv6=1','--name',staged,'--network',names['a'],'--dns','10.247.91.2',
                       '--cap-drop=ALL','--security-opt=no-new-privileges','--user','65532:65532',
                       '--volume',str(staged_dir)+':/stage:rw',tools,'/usr/bin/bash','-c',stage_command)
    containers.append(staged)
    podman('init',staged_id)
    before_stage = json.loads(podman('inspect',staged_id))[0]
    before_pid = before_stage['State']['Pid']
    report['nativeStagePrepared'] = {'state':before_stage['State']['Status'],'pid':before_pid,'sandboxKey':before_stage['NetworkSettings'].get('SandboxKey'),'marker':(staged_dir/'marker').exists()}
    before_ns = os.readlink('/proc/'+str(before_pid)+'/ns/net')
    before_ip = before_stage['NetworkSettings']['Networks'][names['a']]['IPAddress']
    if (staged_dir/'marker').exists() or before_stage['State']['Running'] or not before_ip: raise RuntimeError('init ran application or omitted address')
    podman('run','--rm','--pull=never','--read-only','--security-opt=no-new-privileges','--network','ns:'+before_stage['NetworkSettings']['SandboxKey'],'--cap-drop=ALL','--cap-add=NET_ADMIN',tools,'/usr/sbin/ip','route','replace','default','via','10.247.91.2')
    report['nativeAfterHelper'] = {'state':json.loads(podman('inspect',staged_id))[0]['State'],'marker':(staged_dir/'marker').exists()}
    if (staged_dir/'marker').exists(): raise RuntimeError('routing helper started application')
    attached_process = subprocess.Popen(base+['podman','start','--attach','--interactive',staged_id],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,cwd='/var/tmp')
    try:
        deadline = time.monotonic()+10
        while not (staged_dir/'marker').exists():
            if attached_process.poll() is not None or time.monotonic()>deadline: raise RuntimeError('staged application did not start')
            time.sleep(0.02)
        after_stage=json.loads(podman('inspect',staged_id))[0]
        after_ns=os.readlink('/proc/'+str(after_stage['State']['Pid'])+'/ns/net')
        after_ip=after_stage['NetworkSettings']['Networks'][names['a']]['IPAddress']
        routes=json.loads(podman('exec',staged_id,'/usr/sbin/ip','-j','route'))
        stage_stdout,stage_stderr=attached_process.communicate('hello\nfinish\n'.replace('\\n','\n'),timeout=15)
        report['nativeStagedStart']={'beforeState':before_stage['State']['Status'],'beforeRunning':before_stage['State']['Running'],
                                    'beforeIp':before_ip,'afterIp':after_ip,'namespacePreserved':before_ns==after_ns,
                                    'routes':routes,'stdout':stage_stdout,'stderr':stage_stderr,'exit':attached_process.returncode}
        if before_ns!=after_ns or before_ip!=after_ip or not any(r.get('gateway')=='10.247.91.2' and r.get('dst')=='default' for r in routes): raise RuntimeError('start replaced prepared network')
        if stage_stdout.strip()!='stdout:hello' or 'stderr:hello' not in stage_stderr or attached_process.returncode!=7: raise RuntimeError('staged stdio/exit behavior changed')
    finally:
        if attached_process.poll() is None:
            attached_process.kill();attached_process.communicate(timeout=10)
    report['beforeAttach'] = snapshot()
    podman('network','connect','--ip','10.247.92.2',names['b'],infra)
    report['afterAttach'] = snapshot()
    second = prefix+'-client-b'
    podman('run','--detach','--name',second,'--network',names['b']+':ip=10.247.92.10','--dns','10.247.92.2',
           '--sysctl','net.ipv6.conf.all.disable_ipv6=1','--user','65532:65532','--cap-drop=ALL','--security-opt=no-new-privileges','--volume',str(fixture)+':/fixture:ro',tools,'/usr/bin/openssl','s_server','-accept','18080','-cert','/fixture/cert.pem','-key','/fixture/key.pem','-www')
    containers.append(second)
    podman('run','--rm','--network','container:'+second,'--cap-drop=ALL','--cap-add=NET_ADMIN',tools,'/usr/sbin/ip','route','replace','default','via','10.247.92.2')
    resolvers=[line.split()[1] for line in pathlib.Path('/etc/resolv.conf').read_text().splitlines() if line.startswith('nameserver ') and ':' not in line]
    if not resolvers: raise RuntimeError('fixture needs an IPv4 recursive DNS upstream')
    dns_hosts=fixture/'dns-hosts'
    dns_hosts.write_text('10.247.90.10 allowed.kautuka.test denied.kautuka.test\n10.247.90.11 artifact.kautuka.test\n')
    os.chown(dns_hosts,user.pw_uid,user.pw_gid)
    (fixture/'ExternalCorefile').write_text('.:53 {\n hosts /fixture/dns-hosts {\n reload 1s\n fallthrough\n }\n forward . '+resolvers[0]+'\n log\n errors\n}\n')
    external_dns = prefix+'-external-dns'
    podman('run','-d','--name',external_dns,'--network',names['outside']+':ip=10.247.90.20',
           '--cap-drop=ALL','--cap-add=NET_BIND_SERVICE','--security-opt=no-new-privileges','--user','65532:65532',
           '--volume',str(fixture)+':/fixture:ro',dns_image,'-conf','/fixture/ExternalCorefile')
    containers.append(external_dns)
    time.sleep(0.2)
    network_command(external_dns,['nft','-f','-'],"table ip fixture_count {\n chain observe {\n type filter hook input priority filter; policy accept;\n udp dport 53 counter\n }\n}\n")
    udp_probe = r"""import json,os,socket,sys
os.setgroups([]);os.setgid(int(sys.argv[2]));os.setuid(int(sys.argv[3]))
q=b'\x12\x34\x01\x00\x00\x01\x00\x00\x00\x00\x00\x00'+b''.join(bytes([len(p)])+p.encode() for p in 'allowed.kautuka.test'.split('.'))+b'\x00\x00\x01\x00\x01'
s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);s.settimeout(1)
try:
 s.sendto(q,(sys.argv[1],53));r,_=s.recvfrom(512);print(json.dumps({'answered':r[:2]==q[:2] and int.from_bytes(r[6:8],'big')>0}))
except OSError as e:print(json.dumps({'answered':False,'error':str(e)}))
finally:s.close()
"""
    def dns_probe(name):
        return json.loads(network_command(name,['python3','-c',udp_probe,'10.247.90.20',str(user.pw_gid),str(user.pw_uid)]))
    report['externalDnsControl'] = dns_probe(infra)
    if not report['externalDnsControl']['answered'] or packets(external_dns)<=0: raise RuntimeError('alternate DNS control failed')
    dns_before = packets(external_dns)
    report['externalDnsDenied'] = dns_probe(client)
    if report['externalDnsDenied']['answered'] or packets(external_dns)!=dns_before: raise RuntimeError('external DNS reached alternate resolver')
    report['clientIpv6Addresses'] = json.loads(podman('exec',client,'/usr/sbin/ip','-j','-6','address'))
    report['clientIpv6Routes'] = json.loads(podman('exec',client,'/usr/sbin/ip','-j','-6','route'))
    if any(x.get('addr_info') for x in report['clientIpv6Addresses']) or report['clientIpv6Routes']: raise RuntimeError('client IPv6 enabled unexpectedly')
    report['allowedB'] = request(second,'https://allowed.kautuka.test:8443/')
    report['allowedAAfterAttach'] = request(client,'https://allowed.kautuka.test:8443/')
    mixed_version=mixed_lds['version_info']
    publish('cds',mixed_version,mixed_cds['resources'])
    await_clusters(mixed_version,mixed_cds['resources'])
    publish('lds',mixed_version,mixed_lds['resources'])
    await_active(mixed_version)
    report['mixedPolicyDump']=admin('config_dump')
    report['mixedHostnameB']=request(second,'https://allowed.kautuka.test:8443/')
    report['mixedLiteralA']=request(client,'https://10.247.90.10:8443/')
    if report['mixedHostnameB']['exit'] or report['mixedLiteralA']['exit']: raise RuntimeError('overlapping IP/hostname policy broke source-specific admission')
    # Revoke the extra literal permission before retiring its no-longer-referenced cluster.
    publish('lds',policy_version,generated_lds['resources'])
    await_active(policy_version)
    publish('cds',policy_version,clusters)
    await_clusters(policy_version,clusters)
    # A server-first endpoint proves literal TCP does not deadlock waiting for a ClientHello.
    banner_code = r"""import os,socket,sys
os.setgroups([]);os.setgid(int(sys.argv[1]));os.setuid(int(sys.argv[2]))
s=socket.socket();s.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1);s.bind(('0.0.0.0',9443));s.listen(4)
print('ready',flush=True)
while True:
 c,_=s.accept();c.sendall(b'fixture-banner\n');c.close()
"""
    banner_process=subprocess.Popen(['nsenter','--net=/proc/'+str(pid(sink))+'/ns/net','python3','-c',banner_code,str(user.pw_gid),str(user.pw_uid)],
                                    stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,cwd='/var/tmp')
    try:
        if banner_process.stdout.readline().strip()!='ready': raise RuntimeError('server-first endpoint did not start')
        network_command(sink,['nft','add','rule','ip','fixture_count','observe','tcp','dport','9443','tcp','flags','&','(syn | ack)','==','syn','counter'])
        def read_banner(container):
            script='exec 3<>/dev/tcp/10.247.90.10/9443; IFS= read -r -t 6 line <&3; test "$line" = fixture-banner'
            p=subprocess.run(base+['podman','exec',container,'/usr/bin/bash','-c',script],capture_output=True,text=True,cwd='/var/tmp',timeout=10)
            return {'exit':p.returncode,'stderr':p.stderr[-300:]}
        banner_before=packets(sink)
        report['serverFirstLiteralA']=read_banner(client)
        if report['serverFirstLiteralA']['exit'] or packets(sink)<=banner_before: raise RuntimeError('explicit-IP server-first TCP failed')
        if banner_process.poll() is not None: raise RuntimeError('server-first endpoint exited before denied control')
        banner_before=packets(sink)
        report['serverFirstDeniedB']=read_banner(second)
        if not report['serverFirstDeniedB']['exit'] or packets(sink)!=banner_before: raise RuntimeError('unapproved source reached server-first endpoint')
    finally:
        banner_process.terminate();banner_process.communicate(timeout=10)
    unknown = prefix+'-unknown'
    podman('run','-d','--name',unknown,'--network',names['b']+':ip=10.247.92.11','--dns','10.247.92.2',
           '--sysctl','net.ipv6.conf.all.disable_ipv6=1','--user','65532:65532','--cap-drop=ALL','--security-opt=no-new-privileges',tools,'/usr/bin/sleep','120')
    containers.append(unknown)
    podman('run','--rm','--network','container:'+unknown,'--cap-drop=ALL','--cap-add=NET_ADMIN',tools,'/usr/sbin/ip','route','replace','default','via','10.247.92.2')
    before = packets(sink)
    report['unknownSource'] = request(unknown,'https://allowed.kautuka.test:8443/')
    if report['unknownSource']['exit'] != 35 or packets(sink)!=before: raise RuntimeError('unknown source denial unproved')
    install_counter(second,18080)
    report['sameScope'] = request(unknown,'https://10.247.92.10:18080/')
    if report['sameScope']['exit'] or packets(second)<=0: raise RuntimeError('same-scope target control failed')
    cross_before = packets(second)
    report['crossScope'] = request(client,'https://10.247.92.10:18080/')
    if report['crossScope']['exit'] != 35 or packets(second)!=cross_before: raise RuntimeError('cross-scope denial unproved')
    for name in (unknown,second):
        podman('rm','--force',name)
        containers.remove(name)
    podman('network','disconnect',names['b'],infra)
    report['afterDetach'] = snapshot()
    report['allowedAfterDetach'] = request(client,'https://allowed.kautuka.test:8443/')
    before = packets(sink)
    podman('stop',envoy)
    report['proxyStopped'] = request(client,'https://allowed.kautuka.test:8443/')
    if packets(sink)!=before: raise RuntimeError('proxy-stopped attempt reached upstream')
    report['envoyLogs'] = logs(envoy)
    report['dnsLogs'] = logs(dns)
    report['finalRules'] = command(['nsenter','--net=/proc/'+str(snapshot()['pid'])+'/ns/net','nft','list','ruleset'])
    if any(report[key]['exit'] != 0 for key in ('allowedA','allowedB','allowedAAfterAttach','allowedAfterDetach')): raise RuntimeError('allowed TLS failed')
    if any(report[key]['exit'] == 0 for key in ('deniedName','deniedLiteral','proxyStopped')): raise RuntimeError('denied TLS succeeded')
    events = []
    for line in report['envoyLogs'].splitlines():
        try: events.append(json.loads(line))
        except ValueError: pass
    if not any(e.get('source')=='10.247.91.10' and e.get('destination')=='10.247.90.10:8443' and e.get('sni')=='allowed.kautuka.test' for e in events): raise RuntimeError('matching identity/original destination event absent')
    for key in ('beforeAttach','afterAttach','afterDetach'):
        defaults = [r for r in json.loads(report[key]['routes']) if r['dst']=='default']
        if len(defaults)!=1 or defaults[0].get('gateway')!='10.247.90.1': raise RuntimeError('egress default changed')
    if options.manager_smoke is not None:
        import io
        mcp_script=r"""#!/usr/bin/bash
    while IFS= read -r line; do
     if [[ ! $line =~ \"id\"[[:space:]]*:[[:space:]]*(\"[^\"]*\"|[0-9]+) ]]; then continue; fi
     request_id=${BASH_REMATCH[1]}
     if [[ $line == *'"initialize"'* ]]; then
      [[ $line =~ \"protocolVersion\"[[:space:]]*:[[:space:]]*\"([^\"]+)\" ]]; version=${BASH_REMATCH[1]}
      printf '{"jsonrpc":"2.0","id":%s,"result":{"protocolVersion":"%s","capabilities":{"tools":{}},"serverInfo":{"name":"native-egress-fixture","version":"1"}}}\n' "$request_id" "$version"
     elif [[ $line == *'"tools/list"'* ]]; then
      printf '{"jsonrpc":"2.0","id":%s,"result":{"tools":[{"name":"allowed","inputSchema":{"type":"object"}},{"name":"denied","inputSchema":{"type":"object"}},{"name":"hold","inputSchema":{"type":"object"}}]}}\n' "$request_id"
     elif [[ $line == *'"tools/call"'* ]]; then
      if [[ $line == *'"hold"'* ]]; then /usr/bin/sleep 2; value=held
      else
       host=allowed.kautuka.test; [[ $line == *'"denied"'* ]] && host=denied.kautuka.test
       /usr/bin/curl --silent --show-error --output /dev/null --max-time 4 --insecure "https://$host:8443/"
       value=exit:$?
      fi
      printf '{"jsonrpc":"2.0","id":%s,"result":{"content":[{"type":"text","text":"%s"}]}}\n' "$request_id" "$value"
     else printf '{"jsonrpc":"2.0","id":%s,"error":{"code":-32601,"message":"Method not found"}}\n' "$request_id"
     fi
     done
    """
        with tarfile.open(archive,'a') as tar:
            raw=mcp_script.encode();item=tarfile.TarInfo('mcp-fixture.sh');item.size=len(raw);item.mode=0o644;tar.addfile(item,io.BytesIO(raw))
        mcp_image='localhost/kautuka-mcp-fixture:'+prefix
        podman('import','--change','ENTRYPOINT ["/usr/bin/bash","/mcp-fixture.sh"]',str(archive),mcp_image)
        helper_image='localhost/kautuka-egress-fixture:'+prefix
        podman('tag',tools,helper_image)
        archive_root=root/'home'/'archive-proof'
        archive_root.mkdir()
        os.chown(archive_root,user.pw_uid,user.pw_gid)
        (archive_root/'Containerfile').write_text('FROM scratch\nLABEL kautuka.archive.fixture='+prefix+'\n')
        archive_image='localhost/kautuka-archive-proof:'+prefix
        podman('build','--pull=never','--network=none','-t',archive_image,str(archive_root),timeout=120)
        archive_info=json.loads(podman('image','inspect',archive_image))[0]
        archive_file=archive_root/'image.oci'
        podman('save','--format','oci-archive','-o',str(archive_file),archive_image,timeout=120)
        with tarfile.open(archive_file) as source:
            archive_index=json.load(source.extractfile('index.json'))
            source_digest=archive_index['manifests'][0]['digest']
            archive_manifest=json.load(source.extractfile('blobs/sha256/'+source_digest.split(':')[1]))
            archive_config=archive_manifest['config']['digest']
        with archive_file.open('rb') as content: archive_hash=hashlib.file_digest(content,'sha256').hexdigest()
        archive_proof={'root':str(archive_root),'source':'ghcr.io/devkrity/native-archive-fixture@'+source_digest,
            'sha256':archive_hash,'platform':archive_info['Os']+'/'+archive_info['Architecture'],'localImageId':archive_config}
        report['archiveProofInput']=archive_proof
        podman('rmi',archive_info['Id'])
        acquisition_images={}
        if options.acquisition_source:
            acquisition_archive=root/'home'/'skopeo.oci'
            shutil.copyfile(options.image_cache/'skopeo.oci',acquisition_archive)
            os.chown(acquisition_archive,user.pw_uid,user.pw_gid)
            with tarfile.open(acquisition_archive) as source:
                index=json.load(source.extractfile('index.json'))
                manifest=json.load(source.extractfile('blobs/sha256/'+index['manifests'][0]['digest'].split(':')[1]))
                config_id=manifest['config']['digest']
            podman('load','-i',str(acquisition_archive))
            acquisition_image='localhost/kautuka-acquisition-fixture:'+prefix
            podman('tag',config_id,acquisition_image)
            acquisition_images={'acquisitionHelper':acquisition_image,'acquisitionSource':options.acquisition_source}
        core_images={}
        if options.core_build_root is not None:
            base_archive=root/'home'/'core-base.oci'
            shutil.copyfile(options.image_cache/'core-base.oci',base_archive)
            os.chown(base_archive,user.pw_uid,user.pw_gid)
            with tarfile.open(base_archive) as source:
                index=json.load(source.extractfile('index.json'))
                manifest=json.load(source.extractfile('blobs/sha256/'+index['manifests'][0]['digest'].split(':')[1]))
                config_id=manifest['config']['digest']
            podman('load','-i',str(base_archive))
            core_base='localhost/kautuka-core-base:'+prefix
            podman('tag',config_id,core_base)
            for service in ('CredentialsHost','ChannelHost','Gateway','WebHost'):
                context=root/'home'/('core-build-'+service)
                context.mkdir()
                shutil.copytree(options.core_build_root/'publish'/service,context/'payload')
                recipe=context/'Containerfile'
                recipe.write_text('FROM '+core_base+'\nCOPY payload/ /app/\nWORKDIR /app\nENTRYPOINT ["/app/Kautuka.'+service+'"]\n')
                core_image='localhost/kautuka-core-'+service.lower()+':'+prefix
                podman('build','--pull=never','--network=none','-t',core_image,'-f',str(recipe),str(context),timeout=180)
                core_images['core'+service]=core_image
        artifact_server = fixture/'artifact-server.py'
        artifact_server.write_text("""import http.server,ssl
class Handler(http.server.BaseHTTPRequestHandler):
 def do_GET(self):
  if self.path.startswith('/redirect-'):
   host='artifact.kautuka.test' if self.path=='/redirect-allowed' else 'denied.kautuka.test'
   self.send_response(302);self.send_header('Location','https://'+host+'/artifact');self.end_headers()
  else:
   self.send_response(200);self.end_headers();self.wfile.write(b'artifact-ok')
server=http.server.HTTPServer(('0.0.0.0',443),Handler)
context=ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER);context.load_cert_chain('/fixture/cert.pem','/fixture/key.pem')
server.socket=context.wrap_socket(server.socket,server_side=True);server.serve_forever()
""")
        artifact_server.chmod(0o644)
        artifact_sink=prefix+'-artifact-sink'
        podman('run','-d','--name',artifact_sink,'--network',names['outside']+':ip=10.247.90.11',
            '--cap-drop=ALL','--security-opt=no-new-privileges','--user','65532:65532','--sysctl','net.ipv4.ip_unprivileged_port_start=0',
            '--read-only','--volume',str(fixture)+':/fixture:ro',tools,'/usr/bin/python3','-I','/fixture/artifact-server.py')
        containers.append(artifact_sink)
        binary=root/'home'/'manager-smoke'
        shutil.copyfile(options.manager_smoke,binary);binary.chmod(0o755)
        native_input=root/'home'/'manager-input.json'
        native_input.write_text(json.dumps({'stateRoot':str(root/'home'/'managed'),'disposableUser':prefix,'connector':mcp_image,'helper':helper_image,
            'envoy':json.loads(podman('image','inspect',envoy_image))[0]['RepoDigests'][0],
            'dns':json.loads(podman('image','inspect',dns_image))[0]['RepoDigests'][0],
            'pkgs':managed_images['pkgs'], 'sandbox':managed_images['sandbox'], 'dnsHosts':str(dns_hosts), 'coreOnly':options.core_only, 'hostOnly':options.host_only, 'artifactCertificate':str(fixture/'cert.pem'), 'archiveProof':archive_proof, **core_images, **acquisition_images}))
        native_input.chmod(0o644)
        p=subprocess.run(base+[str(binary),str(native_input)],capture_output=True,text=True,cwd='/var/tmp',timeout=780)
        report['managerRuntimeProcess']={'exit':p.returncode,'stdout':p.stdout,'stderr':p.stderr[-8000:]}
        # Account/store are disposable; capture immutable IDs even after a failed preparation.
        # Capture non-infra containers; pods own their infra container lifecycle.
        for item in json.loads(podman('ps','--all','--format=json')):
            labels=item.get('Labels') or {}
            if not item.get('IsInfra') and (labels.get('kautuka.managed')=='manager' or labels.get('kautuka.preparation-helper')=='true'):
                id=item.get('Id') or item.get('ID') or item.get('id')
                if id and id not in containers: containers.append(id)
        for name in podman('network','ls','--filter','label=kautuka.egress=true','--format','{{.Name}}').splitlines():
            if name and name not in network_names: network_names.append(name)
        observed=root/'home'/'managed'/'egress'/'config'/'unconfirmed-admin-dump.json'
        if observed.exists(): report['managerUnconfirmedDump']=json.loads(observed.read_text())
        report['managerEnvoyLogs']=logs('kautuka-egress-envoy')
        proxy_info=json.loads(podman('inspect','kautuka-egress-envoy'))[0]
        proxy_pid=proxy_info['State']['Pid']
        report['managerEnvoyLogging']={'logConfig':proxy_info['HostConfig'].get('LogConfig'),
            'stdio':{str(fd):os.readlink('/proc/'+str(proxy_pid)+'/fd/'+str(fd)) for fd in (1,2)} if proxy_pid else {}}
        if p.returncode: raise RuntimeError('Native Manager lifecycle failed: '+p.stdout[-3000:]+' '+p.stderr[-8000:])
        report['managerRuntime']=json.loads(p.stdout.strip().splitlines()[-1])
        workspace_file=root/'home'/'managed'/'alpha'/'native-fixture.txt'
        if not (options.core_only or options.host_only):
            if not workspace_file.is_file() or workspace_file.stat().st_uid!=user.pw_uid: raise RuntimeError('sandbox file is not owned by the runtime host account')
            report['sandboxFileOwnedByRuntime']=True
        report['managerEnvoyLogs']=logs('kautuka-egress-envoy')
        report['managerDnsLogs']=logs('kautuka-egress-dns')
    report['limitations'] = ['WSL Fedora 44 only; native Linux and Mac not yet validated.', 'UDP namespace probes use ordinary sockets after root enters the network namespace and drops to the runtime UID; they do not establish container privilege confinement.', 'IPv6 absence is a disabled-client address/route check, not an observed IPv6 firewall-hit test.', 'Fixture DNS; no recursive DNS or installed application/update proof.']
    if report['scratchFilesystem']=='tmpfs': report['limitations'].append('RAM-backed disposable store: no disk throughput, disk-capacity or persistence evidence.')
    report['scope'] = ('rootless-host-artifacts-and-registry-acquisition-only' if options.acquisition_source else 'rootless-host-artifacts-only') if options.host_only else 'rootless-host-and-core-services-only' if options.core_only else 'rootless-compiled-file-xds-admission-mixed-source-policy-and-native-staged-start'
    report['passed'] = True
except Exception as error:
    report['error'] = str(error)
    if started:
        try: report['containerStates'] = podman('ps','--all','--format=json')
        except Exception: pass
        for name in containers:
            try: report[name+'-logs'] = logs(name)
            except Exception: pass
finally:
    if created:
        cleanup_commands = []
        if started:
            # Discover partial Manager resources even when its executable timed out.
            try:
                for item in json.loads(podman('ps','--all','--format=json')):
                    labels=item.get('Labels') or {}
                    if not item.get('IsInfra') and (labels.get('kautuka.managed')=='manager' or labels.get('kautuka.preparation-helper')=='true'):
                        id=item.get('Id') or item.get('ID') or item.get('id')
                        if id and id not in containers: containers.append(id)
                for name in podman('network','ls','--filter','label=kautuka.egress=true','--format','{{.Name}}').splitlines():
                    if name and name not in network_names: network_names.append(name)
            except Exception as error: report['cleanupErrors'].append(str(error))
            # Use the disposable store's actual pod IDs, including partially-created Manager pods.
            pod_ids=[]
            try:
                for item in json.loads(podman('pod','ls','--no-trunc','--format=json')):
                    labels=item.get('Labels') or {}
                    if item.get('Name')==prefix+'-egress' or labels.get('kautuka.egress')=='true':
                        id=item.get('Id') or item.get('ID') or item.get('id')
                        if not id or len(id)!=64: raise RuntimeError('invalid disposable pod identity')
                        pod_ids.append(id)
            except Exception as error:report['cleanupErrors'].append(str(error))
            cleanup_commands += [base+['podman','rm','--force',name] for name in reversed(containers)]
            cleanup_commands += [base+['podman','pod','rm','--force',id] for id in pod_ids]
            cleanup_commands += [base+['podman','network','rm',name] for name in reversed(network_names)]
        for args in cleanup_commands:
            try: command(args)
            except Exception as error: report['cleanupErrors'].append(str(error))
        try: command(['systemctl','stop','user@'+uid+'.service','user-runtime-dir@'+uid+'.service'])
        except Exception as error: report['cleanupErrors'].append(str(error))
        try:
            if pwd.getpwnam(prefix).pw_dir != str(root/'home'): raise RuntimeError('account home changed')
            if not report['cleanupErrors']: command(['userdel',prefix])
        except Exception as error: report['cleanupErrors'].append(str(error))
    try:
        if root.parent != options.work_root or root.resolve() != root or root.is_symlink() or not root.name.startswith('kautuka-egress-v6-'): raise RuntimeError('unsafe cleanup path')
        if not report['cleanupErrors']: shutil.rmtree(root)
    except Exception as error: report['cleanupErrors'].append(str(error))
    if report['cleanupErrors']: report['passed']=False
json.dump(report,report_stream,indent=2)
report_stream.write('\n')
report_stream.close()
print(json.dumps({'passed':report['passed'],'error':report.get('error'),'cleanupErrors':report['cleanupErrors'],'report':str(options.report)}))
raise SystemExit(0 if report['passed'] else 1)
