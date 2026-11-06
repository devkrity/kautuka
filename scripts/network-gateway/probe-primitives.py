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
import time
import uuid


class ProbeFailure(RuntimeError):
    pass


class Topology:
    def __init__(self):
        self.prefix = "ktng-" + uuid.uuid4().hex[:10]
        self.names = {role: self.prefix + "-" + role for role in ("src", "gw", "sink")}
        self.created = []
        self.echo = None
        self.retained_source_fd = None
        self.steps = []

    def run(self, args, *, role=None, data=None, timeout=10):
        if role == "src" and self.retained_source_fd is not None:
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

    def create(self):
        for name in self.names.values():
            self.run(["ip", "netns", "add", name])
            # Record only after successful creation: never delete a pre-existing name.
            self.created.append(name)
        for role in self.names:
            self.run(["ip", "link", "set", "lo", "up"], role=role)
        for role, local, peer, subnet in (("src", "s0", "g0", "10.231.251"), ("sink", "u0", "g1", "10.231.252")):
            self.run(["ip", "link", "add", local, "type", "veth", "peer", "name", peer], role=role)
            self.run(["ip", "link", "set", peer, "netns", self.names["gw"]], role=role)
            self.run(["ip", "address", "add", subnet + ".2/30", "dev", local], role=role)
            self.run(["ip", "address", "add", subnet + ".1/30", "dev", peer], role="gw")
            self.run(["ip", "link", "set", local, "up"], role=role)
            self.run(["ip", "link", "set", peer, "up"], role="gw")
            self.run(["ip", "route", "add", "default", "via", subnet + ".1"], role=role)
        self.run(["sysctl", "-qw", "net.ipv4.ip_forward=1"], role="gw")
        self.rules("ct state established,related accept\niifname g0 accept")
        code = """import socket
s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM)
s.bind(('10.231.252.2',20199))
print('ready',flush=True)
while True:
    data,peer=s.recvfrom(1024)
    print(data.decode(),flush=True)
    s.sendto(data,peer)
"""
        self.echo = subprocess.Popen(
            ["ip", "netns", "exec", self.names["sink"], sys.executable, "-u", "-c", code],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
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

    def echo_check(self, *, role="src", allowed):
        payload = uuid.uuid4().hex
        code = """import socket,sys
s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM)
s.settimeout(0.5)
payload=sys.argv[1].encode()
s.sendto(payload,('10.231.252.2',20199))
try:
    data,_=s.recvfrom(1024)
    print('delivered' if data == payload else 'corrupt')
except TimeoutError:
    print('blocked')
"""
        actual = self.run([sys.executable, "-c", code, payload], role=role)
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
        for name in reversed(self.created):
            try:
                result = subprocess.run(["ip", "netns", "delete", name], text=True, capture_output=True, timeout=5)
                if result.returncode:
                    failures.append(f"cleanup {name}: {result.stderr.strip()}")
            except (OSError, subprocess.SubprocessError) as error:
                failures.append(f"cleanup {name}: {error}")
        return failures


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True, help="New JSON evidence file (never overwritten).")
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
                "No durable policy admission, NFQUEUE consumer, conntrack revocation, or IPv6 proof yet.",
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
