#!/usr/bin/env python3
"""Gate 1 kernel probe, not an appliance installer or an admission implementation.

Only newly created namespaces contain rules, interfaces, routes, and test traffic.
No host routes, firewall rules, application accounts, or Podman stores are changed.
Run as root on a disposable Linux/WSL test environment. Exit nonzero on any failure.
"""

import argparse
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import uuid


SINK_PROGRAM = r"""import json,os,selectors,socket,sqlite3,sys
selector=selectors.DefaultSelector()
peers={}
buffers={}
def received(data,peer,proto):
    value=data.decode().rstrip('\n')
    if os.path.exists(sys.argv[1]):
        with sqlite3.connect('file:'+sys.argv[1]+'?mode=ro',uri=True) as db:
            present=db.execute('SELECT EXISTS(SELECT 1 FROM gateway_admissions WHERE source_address=? AND source_port=? AND destination_port=20199 AND protocol=?)',(peer[0],peer[1],proto)).fetchone()[0]
        if not present:
            value='AUDIT_MISSING:'+value
    print(value,flush=True)
for family,address in [(socket.AF_INET,'10.231.252.2'),(socket.AF_INET6,'fd7a:6b74:252::2')]:
    for kind,proto in [(socket.SOCK_DGRAM,17),(socket.SOCK_STREAM,6)]:
        s=socket.socket(family,kind)
        if family==socket.AF_INET6:
            s.setsockopt(socket.IPPROTO_IPV6,socket.IPV6_V6ONLY,1)
        s.bind((address,20199))
        if kind==socket.SOCK_STREAM:
            s.listen(8)
        selector.register(s,selectors.EVENT_READ,('listener' if proto==6 else 'udp',proto,None))
selector.register(sys.stdin,selectors.EVENT_READ,('control',0,None))
print('ready',flush=True)
while True:
    for event,_ in selector.select():
        s=event.fileobj
        kind,proto,peer=event.data
        if kind=='control':
            line=sys.stdin.readline()
            if not line:
                selector.unregister(sys.stdin)
                continue
            request=json.loads(line)
            target=peers[(request['address'],request['port'],request['protocol'])]
            target_socket,target_peer=target
            data=request['payload'].encode()
            if request['protocol']==6:
                target_socket.sendall(data+b'\n')
            else:
                target_socket.sendto(data,target_peer)
            print('REVERSE_SENT',flush=True)
        elif kind=='listener':
            client,peer=s.accept()
            buffers[client]=b''
            peers[(peer[0],peer[1],6)]=(client,peer)
            selector.register(client,selectors.EVENT_READ,('tcp',6,peer))
        elif kind=='udp':
            data,peer=s.recvfrom(1024)
            peers[(peer[0],peer[1],17)]=(s,peer)
            received(data,peer,17)
            s.sendto(data,peer)
        else:
            data=s.recv(1024)
            if not data:
                selector.unregister(s)
                buffers.pop(s,None)
                peers.pop((peer[0],peer[1],6),None)
                s.close()
                continue
            buffers[s]+=data
            while b'\n' in buffers[s]:
                value,buffers[s]=buffers[s].split(b'\n',1)
                received(value,peer,6)
                s.sendall(value+b'\n')
"""


CLIENT_PROGRAM = r"""import json,socket,sys
address,protocol=sys.argv[1],int(sys.argv[2])
s=socket.socket(socket.AF_INET6 if ':' in address else socket.AF_INET,socket.SOCK_STREAM if protocol==6 else socket.SOCK_DGRAM)
s.settimeout(0.6)
s.connect((address,20199))
print(json.dumps({'ready':True,'address':s.getsockname()[0],'port':s.getsockname()[1],'protocol':protocol}),flush=True)
for line in sys.stdin:
    request=json.loads(line)
    result=None
    if request['action']=='exchange':
        s.sendall(request['payload'].encode()+(b'\n' if protocol==6 else b''))
    try:
        data=s.recv(1024)
        if protocol==6:
            while data and not data.endswith(b'\n'):
                chunk=s.recv(1024)
                if not chunk:
                    break
                data+=chunk
        result=data.decode().rstrip('\n')
    except TimeoutError:
        pass
    print(json.dumps({'response':result}),flush=True)
"""


class ProbeFailure(RuntimeError):
    pass


class Topology:
    def __init__(self):
        self.prefix = "ktng-" + uuid.uuid4().hex[:10]
        self.names = {role: self.prefix + "-" + role for role in ("src", "gw", "sink")}
        self.created = []
        self.echo = None
        self.consumer = None
        self.state = tempfile.TemporaryDirectory(prefix="ktng-admission-")
        self.ledger = str(Path(self.state.name) / "admissions.sqlite")
        self.last_admitted_port = None
        self.retained_source_fd = None
        self.clients = []
        self.steps = []
        self.borrowed_source = False
        self.borrowed_source_fd = None
        self.borrowed_source_link = False

    def run(self, args, *, role=None, data=None, timeout=10):
        if role == "src" and self.borrowed_source_fd is not None:
            prefix = ["nsenter", f"--net=/proc/{os.getpid()}/fd/{self.borrowed_source_fd}", "--"]
        elif role == "src" and self.retained_source_fd is not None:
            prefix = ["nsenter", f"--net=/proc/{os.getpid()}/fd/{self.retained_source_fd}", "--"]
        else:
            prefix = ["ip", "netns", "exec", self.names[role]] if role else []
        command = prefix + args
        result = subprocess.run(command, input=data, text=True, capture_output=True, timeout=timeout)
        if result.returncode:
            raise ProbeFailure(f"{command!r}: exit {result.returncode}: {result.stderr.strip()}")
        return result.stdout.strip()

    def record(self, name, action):
        started = time.monotonic()
        action()
        self.steps.append({"check": name, "passed": True, "seconds": round(time.monotonic() - started, 3)})

    def create(self, *, borrowed_source=None):
        if borrowed_source is not None:
            name, descriptor = borrowed_source
            if os.fstat(descriptor).st_ino != os.stat("/run/netns/" + name).st_ino:
                raise ProbeFailure("Borrowed source namespace identity changed")
            self.names["src"] = name
            self.borrowed_source_fd = descriptor
            interfaces = json.loads(self.run(["ip", "-j", "link", "show"], role="src"))
            if [interface["ifname"] for interface in interfaces] != ["lo"]:
                raise ProbeFailure("Borrowed fixture source is not empty")
            for family in ("-4", "-6"):
                if json.loads(self.run(["ip", family, "-j", "route", "show", "table", "main"], role="src")):
                    raise ProbeFailure("Borrowed fixture source has existing routes")
            self.borrowed_source = True
        for name in self.names.values():
            if self.borrowed_source and name == self.names["src"]:
                continue
            self.run(["ip", "netns", "add", name])
            # Record only after successful creation: never delete a pre-existing name.
            self.created.append(name)
        for role in self.names:
            self.run(["ip", "link", "set", "lo", "up"], role=role)
        for role, local, peer, subnet in (("src", "s0", "g0", "10.231.251"), ("sink", "u0", "g1", "10.231.252")):
            self.run(["ip", "link", "add", local, "type", "veth", "peer", "name", peer], role=role)
            if role == "src" and self.borrowed_source:
                self.borrowed_source_link = True
            self.run(["ip", "link", "set", peer, "netns", self.names["gw"]], role=role)
            self.run(["ip", "address", "add", subnet + ".2/30", "dev", local], role=role)
            self.run(["ip", "address", "add", subnet + ".1/30", "dev", peer], role="gw")
            self.run(["ip", "link", "set", local, "up"], role=role)
            self.run(["ip", "link", "set", peer, "up"], role="gw")
            self.run(["ip", "route", "add", "default", "via", subnet + ".1"], role=role)
            prefix = "fd7a:6b74:" + ("251" if role == "src" else "252") + "::"
            self.run(["ip", "-6", "address", "add", prefix + "2/64", "dev", local, "nodad"], role=role)
            self.run(["ip", "-6", "address", "add", prefix + "1/64", "dev", peer, "nodad"], role="gw")
            self.run(["ip", "-6", "route", "add", "default", "via", prefix + "1"], role=role)
        self.run(["sysctl", "-qw", "net.ipv4.ip_forward=1", "net.ipv6.conf.all.forwarding=1"], role="gw")
        self.rules("ct state established,related accept\niifname g0 accept")
        self.echo = subprocess.Popen(
            ["ip", "netns", "exec", self.names["sink"], sys.executable, "-u", "-c", SINK_PROGRAM, self.ledger],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
        )
        # A bounded readiness wait; do not block forever on a child that failed to bind.
        import selectors
        with selectors.DefaultSelector() as selector:
            selector.register(self.echo.stdout, selectors.EVENT_READ)
            if not selector.select(timeout=5) or self.echo.stdout.readline().strip() != "ready":
                raise ProbeFailure("simulated uplink echo endpoint did not become ready")

    def rules(self, forward, output="accept"):
        self.run(["nft", "-f", "-"], role="gw", data=f"""destroy table inet ktng_probe
table inet ktng_probe {{
 chain forward {{ type filter hook forward priority filter; policy drop;
 {forward}
 }}
 chain output {{ type filter hook output priority filter; policy drop;
 {output}
 }}
}}
""")

    def echo_check(self, *, role="src", allowed, source_port=None, protocol=17, ipv6=False):
        payload = uuid.uuid4().hex
        code = r"""import json,socket,sys
protocol=int(sys.argv[3])
address=sys.argv[4]
s=socket.socket(socket.AF_INET6 if ':' in address else socket.AF_INET,socket.SOCK_STREAM if protocol==6 else socket.SOCK_DGRAM)
s.settimeout(0.5)
if sys.argv[2] != 'none':
    s.bind(('fd7a:6b74:251::2' if ':' in address else '10.231.251.2',int(sys.argv[2])))
payload=sys.argv[1].encode()
try:
    if protocol==6:
        s.connect((address,20199))
        s.sendall(payload+b'\n')
        data=s.recv(1024).rstrip(b'\n')
    else:
        s.sendto(payload,(address,20199))
        data,_=s.recvfrom(1024)
    outcome='delivered' if data == payload else 'corrupt'
except TimeoutError:
    outcome='blocked'
print(json.dumps({'outcome':outcome,'port':s.getsockname()[1]}))
"""
        address = "fd7a:6b74:252::2" if ipv6 else "10.231.252.2"
        response = json.loads(self.run([sys.executable, "-c", code, payload, str(source_port) if source_port is not None else "none", str(protocol), address], role=role))
        actual = response["outcome"]
        expected = "delivered" if allowed else "blocked"
        import selectors
        with selectors.DefaultSelector() as selector:
            selector.register(self.echo.stdout, selectors.EVENT_READ)
            readable = selector.select(timeout=1)
            received = self.echo.stdout.readline().strip() if readable else None
        if allowed and received != payload:
            raise ProbeFailure(f"sink did not observe allowed {role} payload")
        if not allowed and received is not None:
            raise ProbeFailure(f"sink received denied {role} payload: {received}")
        if actual != expected:
            raise ProbeFailure(f"{role} packet expected {expected}, observed {actual}")
        if allowed and self.consumer is not None:
            self.last_admitted_port = response["port"]

    def queue_absence(self):
        self.rules("ct state established,related accept\niifname g0 queue num 23199")
        self.echo_check(allowed=False)
        # An explicit diagnostic read proves the installed rule did not gain 'bypass'.
        rules = json.loads(self.run(["nft", "--json", "list", "table", "inet", "ktng_probe"], role="gw"))
        queues = [
            expression["queue"]
            for entry in rules.get("nftables", [])
            for expression in entry.get("rule", {}).get("expr", [])
            if "queue" in expression
        ]
        if queues != [{"num": 23199}]:
            raise ProbeFailure("queue rule missing or bypass enabled")

    def output_queue_absence(self):
        self.rules("ct state established,related accept\niifname g0 accept", "udp dport 20199 queue num 23199")
        self.echo_check(role="gw", allowed=False)

    def default_drop(self):
        self.rules("")
        self.echo_check(allowed=False)

    def start_admission(self, binary):
        self.consumer = subprocess.Popen(
            ["ip", "netns", "exec", self.names["gw"], str(binary), "probe-admission", self.names["gw"], self.ledger, "23199"],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
        )
        import selectors
        with selectors.DefaultSelector() as selector:
            selector.register(self.consumer.stdout, selectors.EVENT_READ)
            ready = selector.select(timeout=10)
            marker = self.consumer.stdout.readline().strip() if ready else None
        if marker != "KTNG_PROBE_READY":
            detail = self.consumer.stderr.read() if self.consumer.poll() is not None else "readiness timed out"
            raise ProbeFailure("admission consumer failed readiness: " + detail)
        # Remove the primitive fixture only after the consumer has installed its own
        # default-drop two-hook contract. No external host/uplink exists in this fixture.
        self.run(["nft", "delete", "table", "inet", "ktng_probe"], role="gw")
        self.echo_check(allowed=True)
        import sqlite3
        with sqlite3.connect(self.ledger) as db:
            if db.execute("SELECT COUNT(*) FROM gateway_admissions").fetchone()[0] != 1:
                raise ProbeFailure("expected one durable admission for first flow")

    def consumer_crash(self):
        self.consumer.kill()
        self.consumer.communicate(timeout=3)
        used_ports = {identity["port"] for _, identity in self.clients}
        used_ports.add(self.last_admitted_port)
        port = 54000
        for ipv6 in (False, True):
            for protocol in (6, 17):
                while port in used_ports:
                    port += 1
                self.echo_check(allowed=False, source_port=port, protocol=protocol, ipv6=ipv6)
                port += 1

    @staticmethod
    def read_line(stream, timeout=5):
        import selectors
        with selectors.DefaultSelector() as selector:
            selector.register(stream, selectors.EVENT_READ)
            if not selector.select(timeout=timeout):
                raise ProbeFailure("fixture peer response timed out")
        line = stream.readline()
        if not line:
            raise ProbeFailure("fixture peer exited unexpectedly")
        return line.strip()

    def protocol_admissions(self):
        for address in ("10.231.252.2", "fd7a:6b74:252::2"):
            for protocol in (6, 17):
                prefix = ["nsenter", f"--net=/proc/{os.getpid()}/fd/{self.retained_source_fd}", "--"]
                process = subprocess.Popen(
                    prefix + [sys.executable, "-u", "-c", CLIENT_PROGRAM, address, str(protocol)],
                    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                )
                # Own the child before waiting for readiness, including initialization failure.
                self.clients.append((process, None))
                identity = json.loads(self.read_line(process.stdout))
                if identity.get("ready") is not True or identity.get("protocol") != protocol:
                    raise ProbeFailure("invalid persistent fixture client identity")
                self.clients[-1] = (process, identity)
                self.client_exchange(process, allowed=True)

    def client_exchange(self, process, *, allowed):
        payload = uuid.uuid4().hex
        process.stdin.write(json.dumps({"action": "exchange", "payload": payload}) + "\n")
        process.stdin.flush()
        response = json.loads(self.read_line(process.stdout))["response"]
        if allowed:
            if response != payload or self.read_line(self.echo.stdout) != payload:
                raise ProbeFailure("persistent allowed flow did not deliver its unique bytes with audit")
        else:
            if response is not None:
                raise ProbeFailure("revoked persistent flow returned bytes")
            import selectors
            with selectors.DefaultSelector() as selector:
                selector.register(self.echo.stdout, selectors.EVENT_READ)
                if selector.select(timeout=0.2):
                    raise ProbeFailure("sink received bytes after kernel grant removal")

    def fast_path_after_crash(self):
        if self.consumer.poll() is None:
            raise ProbeFailure("fast-path check requires the consumer to be stopped")
        for process, _ in self.clients:
            self.client_exchange(process, allowed=True)

    def kernel_revocation(self):
        import sqlite3
        with sqlite3.connect(self.ledger) as db:
            handles = [row[0] for row in db.execute("SELECT id FROM gateway_admissions")]
        for handle in handles:
            if not isinstance(handle, int) or not 0 < handle <= 0xFFFFFFFF:
                raise ProbeFailure("unexpected fixture admission handle")
            self.run(["nft", "destroy", "element", "inet", "ktng_admission", "valid", "{", str(handle), "}"], role="gw")
        for process, identity in self.clients:
            self.client_exchange(process, allowed=False)
            # Test the reply direction independently: ask the sink to send fresh bytes.
            payload = uuid.uuid4().hex
            request = {**identity, "payload": payload}
            self.echo.stdin.write(json.dumps(request) + "\n")
            self.echo.stdin.flush()
            if self.read_line(self.echo.stdout) != "REVERSE_SENT":
                raise ProbeFailure("sink did not attempt independent reply-direction traffic")
            process.stdin.write(json.dumps({"action": "receive"}) + "\n")
            process.stdin.flush()
            if json.loads(self.read_line(process.stdout))["response"] is not None:
                raise ProbeFailure("source received bytes after kernel grant removal")

    def wrong_ingress(self):
        # Reuse the admitted five-tuple from a new interface. Conntrack itself does
        # not key by ingress, so a valid-mark-only fast path would otherwise leak it.
        if self.last_admitted_port is None:
            raise ProbeFailure("wrong-ingress test requires the previously admitted source port")
        self.names["spoof"] = self.prefix + "-spoof"
        self.run(["ip", "netns", "add", self.names["spoof"]])
        self.created.append(self.names["spoof"])
        self.run(["ip", "link", "set", "lo", "up"], role="spoof")
        self.run(["ip", "link", "add", "a0", "type", "veth", "peer", "name", "g2"], role="spoof")
        self.run(["ip", "link", "set", "g2", "netns", self.names["gw"]], role="spoof")
        self.run(["ip", "address", "add", "10.231.253.2/30", "dev", "a0"], role="spoof")
        self.run(["ip", "address", "add", "10.231.251.2/32", "dev", "a0"], role="spoof")
        self.run(["ip", "address", "add", "10.231.253.1/30", "dev", "g2"], role="gw")
        self.run(["ip", "link", "set", "a0", "up"], role="spoof")
        self.run(["ip", "link", "set", "g2", "up"], role="gw")
        self.run(["ip", "route", "add", "default", "via", "10.231.253.1"], role="spoof")
        self.run(["sysctl", "-qw", "net.ipv4.conf.all.rp_filter=0", "net.ipv4.conf.g2.rp_filter=0"], role="gw")
        self.echo_check(role="spoof", allowed=False, source_port=self.last_admitted_port)
        counters = json.loads(self.run(["nft", "--json", "list", "counter", "inet", "ktng_admission", "boundary_denials"], role="gw"))
        if not any(entry.get("counter", {}).get("packets", 0) > 0 for entry in counters["nftables"]):
            raise ProbeFailure("wrong-ingress test did not reach the explicit source-boundary guard")

    def namespace_retirement(self):
        # Drop the old interface first. A surviving process/netns reference is not a bypass.
        self.rules("")
        self.retained_source_fd = os.open("/run/netns/" + self.names["src"], os.O_RDONLY)
        self.run(["ip", "netns", "delete", self.names["src"]])
        self.created.remove(self.names["src"])
        existing = self.run(["ip", "netns", "list"])
        if self.names["src"] in existing.split():
            raise ProbeFailure("deleted namespace name remains registered")
        self.echo_check(allowed=False)

    def cleanup(self):
        failures = []
        for process, _ in self.clients:
            if process.poll() is None:
                try:
                    process.terminate()
                    process.communicate(timeout=3)
                except subprocess.TimeoutExpired:
                    try:
                        process.kill()
                        process.communicate(timeout=3)
                    except (OSError, subprocess.SubprocessError) as error:
                        failures.append(f"client cleanup: {error}")
                except (OSError, subprocess.SubprocessError) as error:
                    failures.append(f"client cleanup: {error}")
        if self.consumer is not None and self.consumer.poll() is None:
            try:
                self.consumer.terminate()
                self.consumer.communicate(timeout=3)
            except subprocess.TimeoutExpired:
                try:
                    self.consumer.kill()
                    self.consumer.communicate(timeout=3)
                except (OSError, subprocess.SubprocessError) as error:
                    failures.append(f"consumer cleanup: {error}")
            except (OSError, subprocess.SubprocessError) as error:
                failures.append(f"consumer cleanup: {error}")
        if self.echo is not None:
            try:
                self.echo.terminate()
                self.echo.communicate(timeout=3)
            except subprocess.TimeoutExpired:
                try:
                    self.echo.kill()
                    self.echo.communicate(timeout=3)
                except (OSError, subprocess.SubprocessError) as error:
                    failures.append(f"echo cleanup: {error}")
            except (OSError, subprocess.SubprocessError) as error:
                failures.append(f"echo cleanup: {error}")
        if self.retained_source_fd is not None:
            try:
                os.close(self.retained_source_fd)
            except OSError as error:
                failures.append(f"source namespace fd cleanup: {error}")
        if self.borrowed_source_link:
            try:
                self.run(["ip", "link", "delete", "s0"], role="src")
            except (OSError, subprocess.SubprocessError, ProbeFailure) as error:
                failures.append(f"borrowed source interface cleanup: {error}")
        for name in reversed(self.created):
            try:
                result = subprocess.run(["ip", "netns", "delete", name], text=True, capture_output=True, timeout=5)
                if result.returncode:
                    failures.append(f"cleanup {name}: {result.stderr.strip()}")
            except (OSError, subprocess.SubprocessError) as error:
                failures.append(f"cleanup {name}: {error}")
        try:
            self.state.cleanup()
        except OSError as error:
            failures.append(f"probe state cleanup: {error}")
        return failures


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True, help="New JSON evidence file (never overwritten).")
    parser.add_argument("--admission-binary", type=Path, help="Optional published Linux admission probe executable.")
    options = parser.parse_args()
    if sys.platform != "linux" or os.geteuid() != 0:
        parser.error("requires Linux root; no resources created")
    missing = [name for name in ("ip", "nft", "sysctl", "nsenter") if shutil.which(name) is None]
    if missing:
        parser.error("missing required tools: " + ", ".join(missing))
    # Exclusive create before mutations prevents accidentally replacing operator evidence.
    with options.report.open("x", encoding="utf-8") as report:
        topology = Topology()
        result = {
            "schemaVersion": 1, "scope": "isolated-kernel-primitives-only", "gate1Passed": False,
            "primitivesPassed": False,
            "kernel": os.uname().release, "namespaces": topology.names, "checks": topology.steps,
            "limitations": [
                "Optional admission checks exercise basic TCP/UDP IPv4/IPv6; no IPv6 extension/PMTU or full conntrack reconciliation proof.",
                "No rootless helper, signed pull, native Linux support matrix, or full WSL proof yet.",
                "A successful primitive probe does not pass Gate 1 or enable enforcement.",
            ],
        }
        try:
            topology.record("isolated-veth-routing", topology.create)
            topology.record("allowed-forwarded-udp", lambda: topology.echo_check(allowed=True))
            topology.record("queue-no-consumer-forward-drop", topology.queue_absence)
            topology.record("queue-no-consumer-output-drop", topology.output_queue_absence)
            topology.record("forward-default-drop", topology.default_drop)
            topology.record("namespace-retirement", topology.namespace_retirement)
            if options.admission_binary is not None:
                topology.record("durable-nfqueue-admission", lambda: topology.start_admission(options.admission_binary))
                topology.record("tcp-udp-ipv4-ipv6-admission", topology.protocol_admissions)
                topology.record("consumer-crash-new-flow-drop", topology.consumer_crash)
                topology.record("admitted-tuple-wrong-ingress-drop", topology.wrong_ingress)
                topology.record("existing-flow-fast-path-after-crash", topology.fast_path_after_crash)
                topology.record("kernel-grant-removal-both-directions", topology.kernel_revocation)
            result["primitivesPassed"] = True
        except (ProbeFailure, OSError, subprocess.SubprocessError) as error:
            result["primitivesPassed"] = False
            result["error"] = str(error)
        finally:
            result["cleanupErrors"] = topology.cleanup()
            if result["cleanupErrors"]:
                result["primitivesPassed"] = False
            json.dump(result, report, indent=2)
            report.write("\n")
        print(json.dumps(result, indent=2))
        return 0 if result["primitivesPassed"] else 1


if __name__ == "__main__":
    def interrupted(signum, frame):
        raise InterruptedError(f"probe interrupted by signal {signum}")

    signal.signal(signal.SIGTERM, interrupted)
    raise SystemExit(main())
