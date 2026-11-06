#!/usr/bin/env python3
"""Isolated IPv4 DNS transport, coarse-owner policy and audited upstream fixture."""

import argparse
import importlib.util
import json
import os
from pathlib import Path
import shutil
import signal
import sqlite3
import subprocess
import sys


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


owners = module("owners", "probe-runtime-owners.py")
primitives = module("primitives", "probe-primitives.py")

WIRE = r"""
import ipaddress,json,socket,struct,sys
def question(data):
    if len(data)<12 or struct.unpack('!6H',data[:12])[1:]!=(256,1,0,0,0):
        raise RuntimeError('invalid fixture question header')
    offset=12; labels=[]
    while True:
        length=data[offset]; offset+=1
        if not length: break
        if length>63: raise RuntimeError('invalid fixture label')
        labels.append(data[offset:offset+length].decode('ascii')); offset+=length
    if len(data)!=offset+4: raise RuntimeError('invalid fixture question length')
    kind,cls=struct.unpack('!2H',data[offset:])
    if cls!=1 or kind not in (1,28): raise RuntimeError('unsupported fixture question')
    return struct.unpack('!H',data[:2])[0],'.'.join(labels),kind
def query(identifier,name,kind):
    return struct.pack('!6H',identifier,256,1,0,0,0)+b''.join(bytes([len(label)])+label.encode() for label in name.split('.'))+b'\0'+struct.pack('!2H',kind,1)
def response(data,forged=False):
    identifier,name,kind=question(data)
    address=ipaddress.ip_address('10.231.252.2' if kind==1 else 'fd7a:6b74:252::2').packed
    return struct.pack('!6H',identifier^int(forged),0x8180,1,1,0,0)+data[12:]+b'\xc0\x0c'+struct.pack('!2HIH',kind,1,30,len(address))+address
def exact(connection,length):
    data=b''
    while len(data)<length:
        part=connection.recv(length-len(data))
        if not part: raise RuntimeError('incomplete fixture TCP frame')
        data+=part
    return data
"""

UPSTREAM = WIRE + r"""
import selectors,sqlite3
flowdb,querydb,log,uid=sys.argv[1:5]
seen=set()
def received(data,peer,protocol,port):
    identifier,name,kind=question(data)
    event={'port':port,'peer':list(peer),'protocol':protocol,'id':identifier,'name':name,'type':kind}
    if port==2053:
        with sqlite3.connect('file:'+flowdb+'?mode=ro',uri=True) as db:
            flow=db.execute('SELECT id FROM gateway_admissions WHERE source_address=? AND source_port=? AND destination_port=2053 AND protocol=? AND socket_uid=? AND hook=3 AND subject=?',
                (peer[0],peer[1],protocol,int(uid),'probe-resolver')).fetchall()
        with sqlite3.connect('file:'+querydb+'?mode=ro',uri=True) as db:
            attempts=db.execute("SELECT operation FROM dns_decisions WHERE upstream_id=? AND name=? AND qtype=? AND kind='AuthorizedAttempt'",(identifier,name,kind)).fetchall()
        fresh=[row[0] for row in attempts if row[0] not in seen]
        if len(flow)!=1 or len(fresh)!=1: raise RuntimeError('DNS or network audit missing before upstream payload')
        seen.add(fresh[0]); event['operation']=fresh[0]; event['admissionId']=flow[0][0]
    with open(log,'a') as output: output.write(json.dumps(event)+'\n'); output.flush()
    return response(data,name.startswith('forged.'))
selector=selectors.DefaultSelector()
for port in (53,2053):
    for protocol in (6,17):
        s=socket.socket(socket.AF_INET,socket.SOCK_STREAM if protocol==6 else socket.SOCK_DGRAM)
        s.bind(('10.231.252.2',port))
        if protocol==6: s.listen(8)
        selector.register(s,selectors.EVENT_READ,(protocol,port))
print('ready',flush=True)
while True:
    for event,_ in selector.select():
        protocol,port=event.data; s=event.fileobj
        if protocol==17:
            data,peer=s.recvfrom(1233); s.sendto(received(data,peer,protocol,port),peer)
        else:
            connection,peer=s.accept()
            with connection:
                connection.settimeout(3)
                data=exact(connection,struct.unpack('!H',exact(connection,2))[0])
                reply=received(data,peer,protocol,port)
                connection.sendall(struct.pack('!H',len(reply))+reply)
"""

CLIENT = WIRE + r"""
address,port,protocol,name,kind,identifier,bindaddress,bindport=sys.argv[1:9]
port,protocol,kind,identifier,bindport=map(int,(port,protocol,kind,identifier,bindport))
s=socket.socket(socket.AF_INET,socket.SOCK_STREAM if protocol==6 else socket.SOCK_DGRAM)
s.settimeout(2.5)
s.bind((bindaddress,bindport))
request=query(identifier,name,kind)
result={'response':False,'sourceAddress':s.getsockname()[0],'sourcePort':s.getsockname()[1]}
try:
    s.connect((address,port))
    result['connectedSourceAddress']=s.getsockname()[0]
    if protocol==6:
        s.sendall(struct.pack('!H',len(request))+request)
        reply=exact(s,struct.unpack('!H',exact(s,2))[0])
    else:
        s.send(request); reply=s.recv(1233)
    if len(reply)<12 or struct.unpack('!H',reply[:2])[0]!=identifier: raise RuntimeError('invalid fixture response ID')
    result.update(response=True,rcode=reply[3]&15,answer=str(ipaddress.ip_address(reply[-(4 if kind==1 else 16):])) if reply[3]&15==0 else None)
except (OSError,TimeoutError) as error:
    result['error']=type(error).__name__
print(json.dumps(result))
"""


class DnsProbe:
    def __init__(self, topology, resolver, owner, subject, resolver_binary):
        self.topology = topology
        self.resolver = resolver
        self.owner = owner
        self.subject = subject
        self.resolver_binary = resolver_binary
        self.querydb = str(Path(resolver["home"]) / (subject + "-dns.sqlite"))
        self.log = Path(topology.state.name) / "dns-upstream.jsonl"
        self.checks = []
        self.identifier = 100
        self.resolver_process = None
        self.upstream_process = None
        self.process_diagnostics = []
        self.packet_observation = None
        self.capture_process = None
        self.denials = []
        self.consumer_loss = []

    def events(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []

    def rows(self, path, table):
        with sqlite3.connect("file:" + path + "?mode=ro", uri=True) as db:
            db.row_factory = sqlite3.Row
            return [dict(row) for row in db.execute("SELECT * FROM " + table)]

    def counter(self, name):
        data = json.loads(self.topology.run(["nft", "-j", "list", "counter", "inet", "ktng_dns", name], role="gw"))
        return next(entry["counter"]["packets"] for entry in data["nftables"] if "counter" in entry)

    def client(self, *, protocol=17, name=None, kind=1, address="10.231.251.1", port=53,
               role="src", source="0.0.0.0", source_port=0, uid=None):
        self.identifier += 1
        uid = self.owner["uid"] if uid is None else uid
        command = ["setpriv", "--reuid=" + str(uid), "--regid=" + str(uid), "--init-groups",
                   sys.executable, "-c", CLIENT, address, str(port), str(protocol),
                   name or "allowed." + self.subject + ".ktng.test", str(kind), str(self.identifier), source, str(source_port)]
        return json.loads(self.topology.run(command, role=role, timeout=6))

    def require(self, result, code=0):
        if not result["response"] or result["rcode"] != code:
            raise RuntimeError("Unexpected DNS result: " + json.dumps(result))

    def blocked(self, counter, **kwargs):
        before = self.counter(counter)
        query_count = len(self.rows(self.querydb, "dns_decisions"))
        event_count = len(self.events())
        result = self.client(**kwargs)
        if result["response"] or self.counter(counter) <= before:
            raise RuntimeError("Bypass did not fail at expected kernel boundary: " + json.dumps(result))
        if len(self.rows(self.querydb, "dns_decisions")) != query_count or len(self.events()) != event_count:
            raise RuntimeError("Denied kernel query reached resolver or upstream: " + json.dumps({"client": result, "arguments": kwargs}))
        self.denials.append({"counter": counter, "before": before, "after": self.counter(counter), "client": result, "arguments": kwargs})

    def start(self, binary):
        topology = self.topology
        self.upstream_process = subprocess.Popen(["ip", "netns", "exec", topology.names["sink"], sys.executable,
            "-u", "-c", UPSTREAM, topology.ledger, self.querydb, str(self.log), str(self.resolver["uid"])],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        if topology.read_line(self.upstream_process.stdout) != "ready":
            raise RuntimeError("DNS upstream not ready")
        # A reachable alternate DNS endpoint must work before the boundary is installed.
        for protocol in (6, 17):
            self.require(self.client(protocol=protocol, address="10.231.252.2"))
        self.checks.append("alternate-resolver-reachable-before-enforcement")
        topology.consumer = subprocess.Popen(["ip", "netns", "exec", topology.names["gw"], str(binary),
            "probe-dns-admission", topology.names["gw"], topology.ledger, "23199", str(self.resolver["uid"])],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        try:
            marker = topology.read_line(topology.consumer.stdout, timeout=10)
        except primitives.ProbeFailure:
            if topology.consumer.poll() is not None:
                raise RuntimeError(topology.consumer.stderr.read())
            raise
        if marker != "KTNG_PROBE_READY":
            raise RuntimeError("DNS admission not ready")
        topology.run(["nft", "delete", "table", "inet", "ktng_probe"], role="gw")
        self.resolver_process = subprocess.Popen(["ip", "netns", "exec", topology.names["gw"], "setpriv",
            "--reuid=" + str(self.resolver["uid"]), "--regid=" + str(self.resolver["uid"]), "--init-groups",
            str(self.resolver_binary), "probe-dns-resolver", topology.names["gw"], self.querydb, self.subject],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        try:
            marker = topology.read_line(self.resolver_process.stdout, timeout=10)
        except primitives.ProbeFailure:
            if self.resolver_process.poll() is not None:
                raise RuntimeError(self.resolver_process.stderr.read())
            raise
        if marker != "KTNG_DNS_READY":
            raise RuntimeError("DNS resolver not ready")

    def execute(self, binary):
        self.start(binary)
        for protocol in (6, 17):
            for kind in (1, 28):
                result = self.client(protocol=protocol, kind=kind)
                self.require(result)
                if result["answer"] != ("10.231.252.2" if kind == 1 else "fd7a:6b74:252::2"):
                    raise RuntimeError("DNS answer mismatch")
        events = [event for event in self.events() if event["port"] == 2053]
        if len(events) != 4 or any(not event.get("operation") or not event.get("admissionId") for event in events):
            raise RuntimeError("Missing pre-payload network/query audit evidence")
        self.checks.append("tcp-udp-A-AAAA-two-durable-admissions-before-upstream")
        for protocol in (6, 17):
            for name in ("blocked." + self.subject + ".ktng.test", "allowed." + ("work" if self.subject == "core" else "core") + ".ktng.test"):
                before = len(self.events())
                self.require(self.client(protocol=protocol, name=name), 5)
                if len(self.events()) != before:
                    raise RuntimeError("Denied name reached upstream")
            before = len(self.events())
            self.require(self.client(protocol=protocol, name="forged." + self.subject + ".ktng.test"), 2)
            if len(self.events()) != before + 1 or self.rows(self.querydb, "dns_decisions")[-1]["kind"] != "ResponseRejected":
                raise RuntimeError("Forged response was not observed and rejected by response validation")
        self.checks.append("denied-names-no-upstream-and-forged-response-rejected")
        for protocol in (6, 17):
            self.blocked("query_denials", protocol=protocol, port=2053)
            self.blocked("forward_denials", protocol=protocol, address="10.231.252.2")
            self.blocked("actor_denials", protocol=protocol, role="gw", uid=0)
            self.blocked("actor_denials", protocol=protocol, role="gw", uid=self.owner["uid"])
            self.blocked("local_query_denials", protocol=protocol, role="gw", uid=self.resolver["uid"])
        self.checks.append("direct-listener-alternate-resolver-and-local-query-denied")
        # Replay an already labelled UDP tuple through the other interface.
        self.require(self.client(source_port=55053))
        self.topology.run(["sysctl", "-qw", "net.ipv4.conf.all.rp_filter=0", "net.ipv4.conf.g0.rp_filter=0", "net.ipv4.conf.g1.rp_filter=0"], role="gw")
        self.topology.run(["ip", "address", "add", "10.231.251.2/32", "dev", "lo"], role="sink")
        self.blocked("query_denials", role="sink", source="10.231.251.2", source_port=55053)
        self.topology.run(["ip", "address", "add", "10.231.251.9/32", "dev", "lo"], role="src")
        self.capture_process = subprocess.Popen(["ip", "netns", "exec", self.topology.names["gw"],
            "tcpdump", "-Z", "root", "-nn", "-v", "-l", "-i", "g0", "-c", "1", "udp and dst port 53"],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        if "listening on" not in self.topology.read_line(self.capture_process.stderr):
            raise RuntimeError("DNS source observer not ready")
        try:
            self.blocked("query_denials", source="10.231.251.9")
        finally:
            self.packet_observation, _ = self.capture_process.communicate(timeout=3)
        if "10.231.251.9." not in self.packet_observation:
            raise RuntimeError("Spoofed source did not appear on the observed ingress")
        self.checks.append("labelled-tuple-wrong-interface-and-spoofed-source-denied")
        if self.topology.consumer.poll() is not None:
            raise RuntimeError("Admission consumer exited before the intentional failure injection")
        self.topology.consumer.kill()
        self.topology.consumer.communicate(timeout=3)
        for family in ("ipv4", "ipv6"):
            self.topology.run(["conntrack", "-F", "-f", family], role="gw")
            if self.topology.run(["conntrack", "-L", "-f", family], role="gw"):
                raise RuntimeError("Fixture conntrack reset did not empty table")
        baseline = len(self.events())
        admissions = len(self.rows(self.topology.ledger, "gateway_admissions"))
        for protocol in (6, 17):
            before = self.counter("queued_output")
            self.require(self.client(protocol=protocol), 2)
            if self.counter("queued_output") <= before or len(self.events()) != baseline or len(self.rows(self.topology.ledger, "gateway_admissions")) != admissions:
                raise RuntimeError("Consumer-absence DNS did not fail closed for protocol " + str(protocol))
            self.consumer_loss.append({"protocol": protocol, "queueBefore": before, "queueAfter": self.counter("queued_output"), "admissions": admissions, "events": baseline})
        self.checks.append("consumer-absent-new-upstream-denied")
        if self.resolver_process.poll() is not None:
            raise RuntimeError("Resolver exited before intentional shutdown")
        self.resolver_process.terminate()
        _, errors = self.resolver_process.communicate(timeout=5)
        if self.resolver_process.returncode != 0:
            raise RuntimeError("Resolver graceful shutdown failed: " + errors)
        for protocol in (6, 17):
            if self.client(protocol=protocol)["response"]:
                raise RuntimeError("Stopped resolver answered")
        if len(self.events()) != baseline:
            raise RuntimeError("Stopped resolver emitted upstream traffic")
        self.checks.append("resolver-stopped-no-response-or-upstream")
        rows = self.rows(self.querydb, "dns_decisions")
        if any(row["subject"] != self.subject or row["namespace_generation"] != 1 for row in rows):
            raise RuntimeError("Coarse owner identity missing from DNS audit")
        return {"subject": self.subject, "owner": self.owner["name"], "sourceNamespace": self.topology.names["src"],
                "checks": self.checks, "queryDecisions": rows, "denials": self.denials,
                "spoofIngressPacket": self.packet_observation, "consumerLoss": self.consumer_loss,
                "upstreamEvents": self.events(), "networkAdmissions": self.rows(self.topology.ledger, "gateway_admissions")}

    def stop(self):
        errors = []
        for name, process in (("resolver", self.resolver_process), ("upstream", self.upstream_process),
                              ("admission", self.topology.consumer), ("capture", self.capture_process)):
            try:
                if process is not None:
                    if process.poll() is None:
                        process.terminate()
                    _, stderr = process.communicate(timeout=5)
                    self.process_diagnostics.append({"name": name, "exitCode": process.returncode, "stderr": stderr})
            except subprocess.TimeoutExpired:
                try:
                    process.kill()
                    process.communicate(timeout=3)
                    errors.append("DNS fixture process required forced cleanup")
                except (OSError, subprocess.SubprocessError) as error:
                    errors.append("DNS fixture forced cleanup failed: " + str(error))
            except (OSError, subprocess.SubprocessError) as error:
                errors.append("DNS fixture process cleanup failed: " + str(error))
        return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--admission-binary", type=Path, required=True)
    options = parser.parse_args()
    if sys.platform != "linux" or os.geteuid() != 0:
        parser.error("requires Linux root; no resources created")
    import resource
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
    if any(shutil.which(tool) is None for tool in ("ip", "nft", "setpriv", "conntrack", "systemctl", "tcpdump")) or not options.admission_binary.is_file():
        parser.error("missing tools or AOT binary; no resources created")
    with options.report.open("x", encoding="utf-8") as report:
        result = {"schemaVersion": 1, "scope": "isolated-coarse-owner-IPv4-DNS", "dnsPassed": False,
                  "gate1Passed": False, "cleanupErrors": [], "owners": [], "limitations": [
                      "IPv4 DNS transport with A/AAAA answers; not IPv6 transport or general DNS interoperability.",
                      "Trusted root fixture selects owner/namespace; no helper resolver-config or per-connector identity proof.",
                      "Client conntrack label is query transport classification, not a revocable name or connection grant."]}
        try:
            with owners.fixture_lock():
                fixture = owners.RuntimeOwners("systemd")
                result["createdResources"] = fixture.owners
                result["fixtureRoot"] = str(fixture.root)
                try:
                    # Build roots may be root-private. Expose only this fixture's trusted
                    # published executable/native SQLite dependency, never its source tree.
                    published = fixture.root / "dns-bin"
                    published.mkdir(mode=0o755)
                    resolver_binary = published / options.admission_binary.name
                    shutil.copyfile(options.admission_binary, resolver_binary)
                    resolver_binary.chmod(0o755)
                    library = published / "libe_sqlite3.so"
                    shutil.copyfile(options.admission_binary.parent / library.name, library)
                    library.chmod(0o644)
                    core, work, resolver = [fixture.create(role) for role in ("core", "work", "resolver")]
                    for subject, owner in (("core", core), ("work", work)):
                        topology = primitives.Topology()
                        probe = DnsProbe(topology, resolver, owner, subject, resolver_binary)
                        descriptor = None
                        try:
                            descriptor = os.open("/run/netns/" + owner["name"], os.O_RDONLY)
                            topology.create(borrowed_source=(owner["name"], descriptor))
                            result["owners"].append(probe.execute(options.admission_binary))
                        finally:
                            result["lastChecks"] = probe.checks
                            try:
                                result["cleanupErrors"].extend(probe.stop())
                            finally:
                                try:
                                    result["lastDiagnostics"] = {"processes": probe.process_diagnostics, "events": probe.events()}
                                    result["lastDiagnostics"]["ingressPacket"] = probe.packet_observation
                                    if topology.consumer is not None:
                                        for name, command in (("rules", ["nft", "list", "table", "inet", "ktng_dns"]),
                                                              ("conntrack", ["conntrack", "-L", "-f", "ipv4", "-o", "extended"])):
                                            try:
                                                result["lastDiagnostics"][name] = topology.run(command, role="gw")
                                            except (OSError, RuntimeError, subprocess.SubprocessError) as error:
                                                result["lastDiagnostics"][name] = {"snapshotError": str(error)}
                                    for name, path, table in (("queries", probe.querydb, "dns_decisions"), ("admissions", topology.ledger, "gateway_admissions")):
                                        if Path(path).is_file():
                                            try:
                                                result["lastDiagnostics"][name] = probe.rows(path, table)
                                            except sqlite3.Error as error:
                                                result["lastDiagnostics"][name] = {"error": str(error)}
                                finally:
                                    try:
                                        result["cleanupErrors"].extend(topology.cleanup())
                                    finally:
                                        if descriptor is not None:
                                            os.close(descriptor)
                    result["dnsPassed"] = True
                finally:
                    result["cleanupErrors"].extend(fixture.cleanup())
        except (OSError, RuntimeError, ValueError, KeyError, subprocess.SubprocessError) as error:
            result["dnsPassed"] = False
            result["error"] = str(error)
        if result["cleanupErrors"]:
            result["dnsPassed"] = False
        json.dump(result, report, indent=2)
        report.write("\n")
        print(json.dumps(result, indent=2))
        return 0 if result["dnsPassed"] else 1


if __name__ == "__main__":
    def interrupted(signum, frame):
        raise InterruptedError(f"probe interrupted by signal {signum}")

    signal.signal(signal.SIGTERM, interrupted)
    raise SystemExit(main())
