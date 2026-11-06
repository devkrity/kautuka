#!/usr/bin/env python3
"""Real managed-owner libc and rootless pasta DNS with private NSS/resolver views."""

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import resource
import shutil
import signal
import socket
import sqlite3
import subprocess
import sys


spec = importlib.util.spec_from_file_location("dns_fixture", Path(__file__).with_name("probe-dns.py"))
dns = importlib.util.module_from_spec(spec)
spec.loader.exec_module(dns)
owners, primitives = dns.owners, dns.primitives

VIEW = r"""import hashlib,json,os,socket,sys
def view(files,blocked):
    mounts=[line.split() for line in open('/proc/self/mountinfo')]
    result={'pid':os.getpid(),'mountNamespace':os.readlink('/proc/self/ns/mnt'),'files':[],'sockets':[]}
    for entry in files:
        path=entry['logical']; target=os.path.realpath(path)
        data=open(path,'rb').read(8193)
        mount=[line for line in mounts if line[4]==target]
        if len(data)>8192 or hashlib.sha256(data).hexdigest()!=entry['sha256'] or not mount or 'ro' not in mount[-1][5].split(','):
            raise RuntimeError('Effective resolver/NSS view is not the expected read-only bind: '+path)
        result['files'].append({'logical':path,'target':target,'sha256':hashlib.sha256(data).hexdigest(),'mount':mount[-1]})
    for path in blocked:
        with socket.socket(socket.AF_UNIX,socket.SOCK_STREAM) as client:
            client.settimeout(.5)
            try: client.connect(path)
            except OSError as error: result['sockets'].append({'path':path,'errno':error.errno})
            else: raise RuntimeError('Host proxy socket remains reachable: '+path)
    return result
"""

INSPECT = owners.IDENTITY_INSPECT + VIEW + r"""
import io,tarfile
if any(name in os.environ for name in ('RES_OPTIONS','LOCALDOMAIN','HOSTALIASES')): raise RuntimeError('Resolver override environment survived')
result['dns']={'managedView':view(FILES,BLOCKED),'lookups':[]}
def event(phase,**extra):
    control=os.path.join(os.environ['HOME'],'control.json'); ack=os.path.join(os.environ['HOME'],'control-ack')
    with open(control+'.tmp','w') as output: json.dump({'phase':phase,'managedPid':os.getpid(),**extra},output)
    os.replace(control+'.tmp',control)
    until=time.monotonic()+12
    while not os.path.exists(ack):
        if time.monotonic()>until: raise RuntimeError('DNS fixture root acknowledgement timed out')
        time.sleep(.05)
    if open(ack).read()!=phase: raise RuntimeError('Unexpected DNS fixture acknowledgement')
    os.unlink(control); os.unlink(ack)
def checked(observed,allowed,alternate=False,family=4):
    parsed=json.loads(observed.stdout)
    if allowed:
        if observed.returncode or parsed.get('family')!=family or parsed.get('addresses')!=[('fd7a:6b74:252::2' if family==6 else '10.231.252.2')]:
            raise RuntimeError('Positive libc lookup failed: '+observed.stdout+observed.stderr)
    elif observed.returncode!=3 or (parsed.get('dnsResponse') is not False if alternate else not isinstance(parsed.get('lookupError'),int) or parsed['lookupError']==0):
        raise RuntimeError('Negative lookup did not fail through its expected workload path: '+observed.stdout+observed.stderr)
    return parsed
def host(phase,mode,name,allowed=False):
    args=[WORKLOAD,mode,name] if mode!='dns4' else [WORKLOAD,mode,'10.231.252.2',name]
    observed=subprocess.run(args,capture_output=True,text=True,timeout=5)
    result['dns']['lookups'].append({'phase':phase,'result':checked(observed,allowed,mode=='dns4',6 if mode=='lookup6' else 4)})
    event(phase)
event('ready')
allowed='allowed.'+SUBJECT+'.ktng.test.'; blocked='blocked.'+SUBJECT+'.ktng.test.'
other='allowed.'+('work' if SUBJECT=='core' else 'core')+'.ktng.test.'
host('host4','lookup4',allowed,True); host('host6','lookup6',allowed,True)
host('host-deny','lookup4',blocked); host('host-other','lookup4',other); host('host-alternate','dns4',allowed)
def command(args): return subprocess.run(podman+args,capture_output=True,text=True,timeout=15)
imported=command(['import','--quiet',archive])
if imported.returncode: raise RuntimeError('Offline DNS workload import failed: '+imported.stderr)
image=imported.stdout.strip()
if len(image.removeprefix('sha256:'))!=64: raise RuntimeError('Invalid offline image ID')
result['dns']['imageId']=image
def container_file(name,path):
    archive_result=subprocess.run(podman+['cp',name+':'+path,'-'],capture_output=True,timeout=8)
    if archive_result.returncode: raise RuntimeError('Container config read failed: '+archive_result.stderr.decode())
    with tarfile.open(fileobj=io.BytesIO(archive_result.stdout)) as stream:
        files=[entry for entry in stream.getmembers() if entry.isfile()]
        if len(files)!=1 or files[0].size>8192: raise RuntimeError('Unexpected container config archive')
        return stream.extractfile(files[0]).read(8193).decode()
def container(phase,mode,name,allowed_result=False,held=False):
    container_name=prefix+'-'+phase
    arguments=['/probe',mode,name] if mode!='dns4' else ['/probe',mode,'10.231.252.2',name]
    try:
        started=command(['run','--detach','--name',container_name,'--pull=never','--network=pasta:--no-map-gw',
            '--user=65532:65532','--cap-drop=ALL','--security-opt=no-new-privileges','--read-only','--cgroupns=private',
            '--memory=32m','--memory-swap=32m','--pids-limit=16','--cpu-period=100000','--cpu-quota=25000',image]+arguments)
        if started.returncode: raise RuntimeError('DNS container start failed: '+started.stderr)
        if held:
            state=command(['inspect','--format=json',container_name])
            if state.returncode: raise RuntimeError('DNS container inspect failed: '+state.stderr)
            pid=json.loads(state.stdout)[0]['State']['Pid']
            if pid<=0: raise RuntimeError('DNS workload exited before helper observation')
            root='/proc/'+str(pid)
            held_state={'pid':pid,'netns':os.readlink(root+'/ns/net'),'cgroup':open(root+'/cgroup').read().strip()}
            with open(os.path.join(os.environ['HOME'],'held.json'),'w') as output: json.dump(held_state,output)
            result['dns']['containerFiles']={path:container_file(container_name,path) for path in ('/etc/resolv.conf','/etc/nsswitch.conf','/etc/hosts')}
            if 'hosts: files dns' not in result['dns']['containerFiles']['/etc/nsswitch.conf']:
                raise RuntimeError('Container NSS policy changed')
            if 'ktng.test' in result['dns']['containerFiles']['/etc/hosts']: raise RuntimeError('Container hosts file can satisfy a test name')
            event(phase,containerPid=pid)
        waited=command(['wait',container_name]); logs=command(['logs',container_name])
        if waited.returncode or logs.returncode: raise RuntimeError('DNS workload wait/log retrieval failed')
        observed=type('Observed',(),{'returncode':int(waited.stdout.strip()),'stdout':logs.stdout,'stderr':logs.stderr})()
        result['dns']['lookups'].append({'phase':phase,'result':checked(observed,allowed_result,mode=='dns4',6 if mode=='lookup6' else 4)})
        if not held: event(phase)
    finally:
        removed=command(['rm','--force',container_name])
        if removed.returncode: raise RuntimeError('DNS container cleanup failed: '+removed.stderr)
container('container4','lookup-hold4',allowed,True,True)
container('container6','lookup6',allowed,True)
container('container-deny','lookup4',blocked); container('container-other','lookup4',other)
container('container-alternate','dns4',allowed)
event('stop-admission')
host('host-consumer','lookup4',allowed); container('container-consumer','lookup4',allowed)
event('stop-resolver')
host('host-stopped','lookup4',allowed); container('container-stopped','lookup4',allowed)
print(json.dumps(result),flush=True)
"""


def host_files():
    return {path: {"link": os.readlink(path) if Path(path).is_symlink() else None,
                   "target": str(Path(path).resolve()), "sha256": hashlib.sha256(Path(path).read_bytes()).hexdigest()}
            for path in ("/etc/resolv.conf", "/etc/nsswitch.conf", "/etc/hosts")}


class HelperExperiment:
    phases = ["ready", "host4", "host6", "host-deny", "host-other", "host-alternate", "container4", "container6",
              "container-deny", "container-other", "container-alternate", "stop-admission", "host-consumer",
              "container-consumer", "stop-resolver", "host-stopped", "container-stopped"]

    def __init__(self, probe, owner):
        self.probe, self.owner = probe, owner
        self.observations = []
        self.query_cursor = 0
        self.event_cursor = len(probe.events())
        self.forward = probe.counter("forward_denials")
        self.queued = probe.counter("queued_output")
        self.input = probe.counter("client_queries")
        self.admissions_at_failure = None

    def snapshot(self, pid):
        status = Path(f"/proc/{pid}/status").read_text().splitlines()
        identities = next(line for line in status if line.startswith("Uid:")).split()[1:]
        expected_namespace = f"net:[{os.stat('/run/netns/' + self.owner['name']).st_ino}]"
        if any(int(uid) != self.owner["uid"] for uid in identities) or os.readlink(f"/proc/{pid}/ns/net") != expected_namespace:
            raise RuntimeError("Configuration observer target is not the registered runtime owner")
        if os.readlink(f"/proc/{pid}/root") != "/":
            raise RuntimeError("Expected an unchrooted managed process for configuration inspection")
        args = ["nsenter", "--mount=/proc/" + str(pid) + "/ns/mnt", "--root=/proc/" + str(pid) + "/root", "--wd=/", "--", sys.executable, "-c",
                VIEW + "\nprint(json.dumps(view(json.loads(sys.argv[1]),json.loads(sys.argv[2]))))\n",
                json.dumps(self.owner["resolver_files"]), json.dumps(self.owner["blocked_host_sockets"])]
        observed = json.loads(owners.run(args))
        observed["targetPid"] = pid
        return observed

    def on_event(self, event):
        phase = event["phase"]
        if len(self.observations) >= len(self.phases) or phase != self.phases[len(self.observations)]:
            raise RuntimeError("Unexpected DNS experiment phase: " + phase)
        queries = self.probe.rows(self.probe.querydb, "dns_decisions")
        events = self.probe.events()
        fresh, upstream = queries[self.query_cursor:], events[self.event_cursor:]
        observation = {"phase": phase, "queryDecisions": fresh, "upstreamEvents": upstream,
                       "networkAdmissionCount": len(self.probe.rows(self.probe.topology.ledger, "gateway_admissions")),
                       "countersBefore": {"forward": self.forward, "queued": self.queued, "input": self.input}}
        if phase == "ready":
            if upstream or any(row["kind"] != "Denied" for row in fresh):
                raise RuntimeError("Unexpected pre-workload DNS activity")
        elif phase in ("host4", "host6", "container4", "container6"):
            if len(upstream) != 1 or [row["kind"] for row in fresh] != ["AuthorizedAttempt", "Resolved"]:
                raise RuntimeError("Positive lookup lacked exact durable query/upstream evidence: " + phase)
        elif phase.endswith("-deny") or phase.endswith("-other"):
            if not fresh or upstream or any(row["kind"] != "Denied" for row in fresh):
                raise RuntimeError("Denied libc name was not denied centrally: " + phase)
        elif phase.endswith("-alternate"):
            if fresh or upstream or self.probe.counter("forward_denials") <= self.forward:
                raise RuntimeError("Alternate DNS did not reach the expected boundary: " + phase)
        elif phase.endswith("-consumer"):
            if upstream or not fresh or any(row["kind"] not in ("AuthorizedAttempt", "UpstreamFailed") for row in fresh) or self.probe.counter("queued_output") <= self.queued:
                raise RuntimeError("Helper lookup did not fail closed without admission: " + phase)
            if len(self.probe.rows(self.probe.topology.ledger, "gateway_admissions")) != self.admissions_at_failure:
                raise RuntimeError("Network admission count changed after consumer loss")
        elif phase.endswith("-stopped"):
            if fresh or upstream or self.probe.counter("client_queries") <= self.input:
                raise RuntimeError("Stopped-resolver lookup did not reach the closed query path: " + phase)
        for row in fresh:
            expected = ("blocked." if phase.endswith("-deny") else "allowed.") + (
                ("work" if self.probe.subject == "core" else "core") if phase.endswith("-other") else self.probe.subject) + ".ktng.test"
            if row["subject"] != self.probe.subject or (phase != "ready" and (row["name"] != expected or row["qtype"] != (28 if phase in ("host6", "container6") else 1))):
                raise RuntimeError("Lookup identity/question mismatch: " + phase)
        if phase == "ready":
            observation["managerView"] = self.snapshot(self.owner["manager_observation"]["pid"])
            observation["managedView"] = self.snapshot(int(event["managedPid"]))
            if observation["managerView"]["mountNamespace"] == os.readlink("/proc/self/ns/mnt"):
                raise RuntimeError("User manager did not get a private mount namespace")
        if phase == "container4":
            helpers = []
            for proc in Path("/proc").glob("[0-9]*"):
                try:
                    uid = int(next(line for line in (proc / "status").read_text().splitlines() if line.startswith("Uid:")).split()[1])
                    if uid != self.owner["uid"] or (proc / "comm").read_text().strip() not in ("pasta", "pasta.avx2", "pasta.avx512"):
                        continue
                    helper = {"pid": int(proc.name), "uid": uid, "netns": os.readlink(proc / "ns/net"),
                              "mountNamespace": os.readlink(proc / "ns/mnt"), "root": os.readlink(proc / "root"),
                              "arguments": (proc / "cmdline").read_bytes().split(b"\0")[:-1]}
                    helper["arguments"] = [item.decode() for item in helper["arguments"]]
                    if helper["netns"] != f"net:[{os.stat('/run/netns/' + self.owner['name']).st_ino}]":
                        raise RuntimeError("Pasta helper escaped the registered outer network namespace")
                    helpers.append(helper)
                except (FileNotFoundError, ProcessLookupError):
                    continue
            if len(helpers) != 1:
                raise RuntimeError("Expected one live pasta helper for the held DNS workload")
            observation["helpers"] = helpers
            observation["helperInitialFileViewVerified"] = False
        if phase == "stop-admission":
            consumer = self.probe.topology.consumer
            if consumer.poll() is not None:
                raise RuntimeError("Admission consumer was not live before injection")
            consumer.kill(); consumer.communicate(timeout=3)
            self.admissions_at_failure = len(self.probe.rows(self.probe.topology.ledger, "gateway_admissions"))
            for family in ("ipv4", "ipv6"):
                self.probe.topology.run(["conntrack", "-F", "-f", family], role="gw")
                if self.probe.topology.run(["conntrack", "-L", "-f", family], role="gw"):
                    raise RuntimeError("Fixture conntrack reset did not empty table")
        if phase == "stop-resolver":
            process = self.probe.resolver_process
            if process.poll() is not None:
                raise RuntimeError("Resolver was not live before injection")
            process.terminate(); _, errors = process.communicate(timeout=5)
            if process.returncode:
                raise RuntimeError("Resolver shutdown failed: " + errors)
        self.observations.append(observation)
        self.query_cursor, self.event_cursor = len(queries), len(events)
        self.forward, self.queued, self.input = (self.probe.counter(name) for name in ("forward_denials", "queued_output", "client_queries"))
        observation["countersAfter"] = {"forward": self.forward, "queued": self.queued, "input": self.input}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--admission-binary", type=Path, required=True)
    parser.add_argument("--workload-source", type=Path, required=True)
    options = parser.parse_args()
    if sys.platform != "linux" or os.geteuid() != 0:
        parser.error("requires Linux root; no resources created")
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
    required = ("podman", "pasta", "ip", "nft", "conntrack", "nsenter", "setpriv", "systemd-run", "systemctl", "gcc", "ldd",
                "useradd", "userdel", "usermod", "groupadd", "groupdel")
    if any(shutil.which(tool) is None for tool in required) or not options.admission_binary.is_file() or not options.workload_source.is_file():
        parser.error("missing fixture inputs")
    with options.report.open("x", encoding="utf-8") as report:
        result = {"schemaVersion": 1, "scope": "managed-owner-and-pasta-IPv4-DNS", "gate1Passed": False,
                  "feasibilityPassed": False, "owners": [], "cleanupErrors": [], "hostFilesBefore": host_files(),
                  "limitations": ["Coarse owner only; not per-connector identity, IPv6 DNS transport or complete helper coverage.",
                                  "Manager/workload file views verified; pasta post-sandbox state and traffic observed, not its initial file reads.",
                                  "Known socket paths masked; not exhaustive host IPC isolation or native Linux PAM/SELinux parity."]}
        try:
            with owners.fixture_lock():
                fixture = owners.RuntimeOwners("systemd", True)
                result["createdResources"], result["fixtureRoot"] = fixture.owners, str(fixture.root)
                try:
                    published = fixture.root / "dns-bin"; published.mkdir(mode=0o755)
                    for name in (options.admission_binary.name, "libe_sqlite3.so"):
                        target = published / name; shutil.copyfile(options.admission_binary.parent / name, target)
                        target.chmod(0o755 if name == options.admission_binary.name else 0o644)
                    core, work, resolver = [fixture.create(role) for role in ("core", "work", "resolver")]
                    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as canary:
                        canary_path = fixture.root / "host-proxy.sock"; canary.bind(str(canary_path)); canary_path.chmod(0o666); canary.listen(4); canary.settimeout(2)
                        for owner in (core, work):
                            fixture.configure_resolver(owner, blocked_socket=canary_path)
                            owners.run(["setpriv", "--reuid=" + str(owner["uid"]), "--regid=" + str(owner["uid"]), "--init-groups",
                                        sys.executable, "-c", "import socket,sys; s=socket.socket(socket.AF_UNIX); s.connect(sys.argv[1]); s.close()", str(canary_path)])
                            connection, _ = canary.accept(); connection.close()
                        result["canaryReachableBeforeMasks"] = True
                        result["workload"] = fixture.build_workload(options.workload_source,
                            name_service_files=[entry for entry in core["resolver_files"] if entry["logical"] != "/etc/resolv.conf"])
                        for subject, owner, peer in (("core", core, work), ("work", work, core)):
                            topology = primitives.Topology()
                            probe = dns.DnsProbe(topology, resolver, owner, subject, published / options.admission_binary.name)
                            descriptor = None
                            experiment = None
                            try:
                                descriptor = os.open("/run/netns/" + owner["name"], os.O_RDONLY | os.O_CLOEXEC)
                                topology.create(borrowed_source=(owner["name"], descriptor)); probe.start(options.admission_binary)
                                experiment = HelperExperiment(probe, owner)
                                fixture.inspect_program = "SUBJECT=" + repr(subject) + "\nFILES=" + repr(owner["resolver_files"]) + "\nBLOCKED=" + repr(owner["blocked_host_sockets"]) + "\nWORKLOAD=" + repr(str(fixture.root / "probe")) + "\n" + INSPECT
                                observed = fixture.inspect(owner, peer, on_event=experiment.on_event)
                                if len(experiment.observations) != len(experiment.phases):
                                    raise RuntimeError("DNS experiment did not complete all phases")
                                result["owners"].append({"runtime": observed, "phases": experiment.observations, "resolverFiles": owner["resolver_files"]})
                            finally:
                                result["lastPhases"] = experiment.observations if experiment else []
                                try:
                                    result["cleanupErrors"].extend(probe.stop())
                                    result["lastProcesses"] = probe.process_diagnostics
                                    result["lastUpstreamEvents"] = probe.events()
                                    if Path(probe.querydb).is_file():
                                        try:
                                            result["lastQueryDecisions"] = probe.rows(probe.querydb, "dns_decisions")
                                        except (OSError, sqlite3.Error) as error:
                                            result["lastQueryDecisionsError"] = str(error)
                                finally:
                                    try:
                                        result["cleanupErrors"].extend(topology.cleanup())
                                    finally:
                                        if descriptor is not None: os.close(descriptor)
                            if host_files() != result["hostFilesBefore"]:
                                raise RuntimeError("Host resolver/NSS files changed during private-session experiment")
                        result["feasibilityPassed"] = True
                finally:
                    result["cleanupErrors"].extend(fixture.cleanup())
        except (OSError, RuntimeError, ValueError, KeyError, subprocess.SubprocessError) as error:
            result["feasibilityPassed"] = False; result["error"] = str(error)
        try:
            result["hostFilesAfter"] = host_files()
        except OSError as error:
            result["hostFilesAfterError"] = str(error)
            result["feasibilityPassed"] = False
        if result["cleanupErrors"] or result.get("hostFilesAfter") != result["hostFilesBefore"]:
            result["feasibilityPassed"] = False
        json.dump(result, report, indent=2); report.write("\n"); print(json.dumps(result, indent=2))
        return 0 if result["feasibilityPassed"] else 1


if __name__ == "__main__":
    def interrupted(signum, frame): raise InterruptedError(f"probe interrupted by signal {signum}")
    signal.signal(signal.SIGTERM, interrupted)
    raise SystemExit(main())
