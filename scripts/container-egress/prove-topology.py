#!/usr/bin/env python3
"""Fedora 44 rootless topology proof; creates only disposable accounts and networks.

Requires Linux root, systemd, Podman, iproute, nftables, curl, OpenSSL and ldd.
Supply offline OCI archives of the documented stock Envoy and CoreDNS images.
The outside TLS/DNS containers simulate remote endpoints; they are test fixtures.
This is not installed application enforcement or a production container launcher.
"""
import argparse
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
import tarfile
import tempfile
import uuid

def command(args, timeout=60):
    p = subprocess.run(args, capture_output=True, text=True, timeout=timeout, cwd="/var/tmp")
    if p.returncode: raise RuntimeError(repr(args) + ': ' + p.stderr[-3000:])
    return p.stdout.strip()

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--image-cache', required=True, type=pathlib.Path, help='Directory containing envoy.oci and dns.oci')
parser.add_argument('--report', required=True, type=pathlib.Path, help='New output JSON file')
options = parser.parse_args()
if sys.platform != 'linux' or os.geteuid() != 0:
    parser.error('requires Linux root in a disposable test distro')
for role in ('envoy','dns'):
    if not (options.image_cache/(role+'.oci')).is_file():
        parser.error('missing offline '+role+'.oci image archive')
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
root = pathlib.Path(tempfile.mkdtemp(prefix='kautuka-egress-v6-', dir='/var/tmp'))
root.chmod(0o711)
created = False
started = False
pod_created = False
network_names = []
containers = []
report = {'prefix':prefix, 'passed':False, 'cleanupErrors':[]}
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
    def podman(*args):
        if args[0]=='run': args=(args[0],'--pull=never',*args[1:])
        return command(base+['podman',*args])
    info = json.loads(podman('info','--format=json'))
    report['runtime'] = {'rootless':info['host']['security']['rootless'],'version':info['version']['Version'],
                         'networkBackend':info['host']['networkBackend']}
    if not report['runtime']['rootless']: raise RuntimeError('not rootless')
    names = {role:prefix+'-'+role for role in ('outside','a','b')}
    for index,role in enumerate(('outside','a','b')):
        args = ['network','create','--subnet',f'10.247.{90+index}.0/24','--disable-dns']
        if role != 'outside': args += ['--internal']
        podman(*args,names[role])
        network_names.append(names[role])
    pod = prefix+'-egress'
    podman('pod','create','--sysctl','net.ipv4.ip_unprivileged_port_start=0','--name',pod,'--dns','127.0.0.1','--network',names['outside']+':ip=10.247.90.2',
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
    archive = root/'home'/'tools.tar'
    files = set()
    for binary in ('/usr/sbin/ip','/usr/sbin/nft','/usr/bin/sleep','/usr/bin/curl','/usr/bin/openssl'):
        files.add(binary)
        for line in command(['ldd',binary]).splitlines():
            files.update(token for token in line.split() if token.startswith('/'))
    with tarfile.open(archive,'w',dereference=True) as tar:
        for file in sorted(files): tar.add(file,arcname=file.lstrip('/'),recursive=False)
    archive.chmod(0o644)
    tools = podman('import','--quiet',str(archive))
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
    for role in ('envoy','dns'):
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
    fixture = root/'home'/'fixture'
    fixture.mkdir(mode=0o755)
    command(['openssl','req','-x509','-newkey','ec','-pkeyopt','ec_paramgen_curve:P-256','-nodes',
             '-keyout',str(fixture/'key.pem'),'-out',str(fixture/'cert.pem'),'-days','1',
             '-subj','/CN=allowed.kautuka.test','-addext','subjectAltName=DNS:allowed.kautuka.test,DNS:denied.kautuka.test'])
    (fixture/'key.pem').chmod(0o644)
    (fixture/'cert.pem').chmod(0o644)
    sink = prefix+'-sink'
    podman('run','-d','--name',sink,'--network',names['outside']+':ip=10.247.90.10',
           '--cap-drop=ALL','--user','65532:65532','--volume',str(fixture)+':/fixture:ro',
           tools,'/usr/bin/openssl','s_server','-accept','8443','-cert','/fixture/cert.pem','-key','/fixture/key.pem','-www')
    containers.append(sink)
    corefile = '.:53 {\n hosts {\n  10.247.90.10 allowed.kautuka.test denied.kautuka.test\n }\n log\n errors\n}\n'
    (fixture/'Corefile').write_text(corefile)
    log = {'name':'envoy.access_loggers.stdout','typed_config':{'@type':'type.googleapis.com/envoy.extensions.access_loggers.stream.v3.StdoutAccessLog',
           'log_format':{'json_format':{'source':'%DOWNSTREAM_REMOTE_ADDRESS_WITHOUT_PORT%','destination':'%DOWNSTREAM_LOCAL_ADDRESS%',
                                       'sni':'%REQUESTED_SERVER_NAME%','details':'%CONNECTION_TERMINATION_DETAILS%'}}}}
    listener = {'name':'transparent','address':{'socket_address':{'address':'0.0.0.0','port_value':15001}},
                'use_original_dst':True,'access_log':[log],
                'listener_filters':[{'name':'envoy.filters.listener.original_dst','typed_config':{'@type':'type.googleapis.com/envoy.extensions.filters.listener.original_dst.v3.OriginalDst'}},
                                    {'name':'envoy.filters.listener.tls_inspector','typed_config':{'@type':'type.googleapis.com/envoy.extensions.filters.listener.tls_inspector.v3.TlsInspector'}}],
                'filter_chains':[{'filter_chain_match':{'destination_port':8443,'server_names':['allowed.kautuka.test'],'source_prefix_ranges':[{'address_prefix':'10.247.91.10','prefix_len':32},{'address_prefix':'10.247.92.10','prefix_len':32}]},
                                  'filters':[{'name':'envoy.filters.network.tcp_proxy','typed_config':{'@type':'type.googleapis.com/envoy.extensions.filters.network.tcp_proxy.v3.TcpProxy','stat_prefix':'allowed','cluster':'allowed','access_log':[log]}}]}],
                'default_filter_chain':{'filters':[{'name':'envoy.filters.network.direct_response','typed_config':{'@type':'type.googleapis.com/envoy.extensions.filters.network.direct_response.v3.Config','response':{'inline_string':''}}}]}}
    config = {'admin':{'address':{'socket_address':{'address':'127.0.0.1','port_value':9901}}},'static_resources':{'listeners':[listener],
              'clusters':[{'name':'allowed','type':'STRICT_DNS','connect_timeout':'2s','dns_lookup_family':'V4_ONLY',
                           'load_assignment':{'cluster_name':'allowed','endpoints':[{'lb_endpoints':[{'endpoint':{'address':{'socket_address':{'address':'allowed.kautuka.test','port_value':8443}}}}]}]}}]}}
    (fixture/'envoy.json').write_text(json.dumps(config))
    for file in fixture.iterdir(): file.chmod(0o644)
    dns = prefix+'-dns'
    podman('run','-d','--name',dns,'--pod',pod,'--read-only','--cap-add=NET_BIND_SERVICE','--cap-drop=ALL','--security-opt=no-new-privileges','--user','65532:65532','--volume',str(fixture/'Corefile')+':/Corefile:ro',
           dns_image,'-conf','/Corefile')
    containers.append(dns)
    envoy = prefix+'-envoy'
    podman('run','-d','--name',envoy,'--pod',pod,'--read-only','--cap-drop=ALL','--security-opt=no-new-privileges','--user','65532:65532','--volume',str(fixture/'envoy.json')+':/envoy.json:ro',
           envoy_image,'-c','/envoy.json','--concurrency','1')
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
    before = packets(sink)
    report['allowedA'] = request(client,'https://allowed.kautuka.test:8443/')
    report['allowedFreshSyns'] = packets(sink)-before
    if report['allowedFreshSyns'] <= 0: raise RuntimeError('sink counter positive control failed')
    before = packets(sink)
    report['deniedName'] = request(client,'https://denied.kautuka.test:8443/')
    if packets(sink)!=before: raise RuntimeError('denied name reached upstream')
    report['deniedLiteral'] = request(client,'https://10.247.90.10:8443/')
    if packets(sink)!=before: raise RuntimeError('denied literal reached upstream')
    report['beforeAttach'] = snapshot()
    podman('network','connect','--ip','10.247.92.2',names['b'],infra)
    report['afterAttach'] = snapshot()
    second = prefix+'-client-b'
    podman('run','--detach','--name',second,'--network',names['b']+':ip=10.247.92.10','--dns','10.247.92.2',
           '--sysctl','net.ipv6.conf.all.disable_ipv6=1','--user','65532:65532','--cap-drop=ALL','--security-opt=no-new-privileges','--volume',str(fixture)+':/fixture:ro',tools,'/usr/bin/openssl','s_server','-accept','18080','-cert','/fixture/cert.pem','-key','/fixture/key.pem','-www')
    containers.append(second)
    podman('run','--rm','--network','container:'+second,'--cap-drop=ALL','--cap-add=NET_ADMIN',tools,'/usr/sbin/ip','route','replace','default','via','10.247.92.2')
    external_dns = prefix+'-external-dns'
    podman('run','-d','--name',external_dns,'--network',names['outside']+':ip=10.247.90.20',
           '--cap-drop=ALL','--cap-add=NET_BIND_SERVICE','--security-opt=no-new-privileges','--user','65532:65532',
           '--volume',str(fixture/'Corefile')+':/Corefile:ro',dns_image,'-conf','/Corefile')
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
    report['limitations'] = ['WSL Fedora 44 only; native Linux and Mac not yet validated.', 'UDP namespace probes use ordinary sockets after root enters the network namespace and drops to the runtime UID; they do not establish container privilege confinement.', 'IPv6 absence is a disabled-client address/route check, not an observed IPv6 firewall-hit test.', 'Static policy and fixture DNS only; no file-xDS activation, recursive DNS or installed application/update proof.']
    report['scope'] = 'rootless-transparent-TLS-scope-isolation-external-DNS-denial-and-IPv4-only-client-layout' 
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
            cleanup_commands += [base+['podman','rm','--force',name] for name in reversed(containers)]
            if pod_created: cleanup_commands.append(base+['podman','pod','rm','--force',prefix+'-egress'])
            cleanup_commands += [base+['podman','network','rm',name] for name in reversed(network_names)]
        for args in cleanup_commands:
            try: command(args)
            except Exception as error: report['cleanupErrors'].append(str(error))
        try: command(['systemctl','stop','user@'+uid+'.service','user-runtime-dir@'+uid+'.service'])
        except Exception as error: report['cleanupErrors'].append(str(error))
        try:
            if pwd.getpwnam(prefix).pw_dir != str(root/'home'): raise RuntimeError('account home changed')
            command(['userdel',prefix])
        except Exception as error: report['cleanupErrors'].append(str(error))
    try:
        if root.parent != pathlib.Path('/var/tmp') or not root.name.startswith('kautuka-egress-v6-'): raise RuntimeError('unsafe cleanup path')
        if not report['cleanupErrors']: shutil.rmtree(root)
    except Exception as error: report['cleanupErrors'].append(str(error))
    if report['cleanupErrors']: report['passed']=False
json.dump(report,report_stream,indent=2)
report_stream.write('\n')
report_stream.close()
print(json.dumps({'passed':report['passed'],'error':report.get('error'),'cleanupErrors':report['cleanupErrors'],'report':str(options.report)}))
raise SystemExit(0 if report['passed'] else 1)
