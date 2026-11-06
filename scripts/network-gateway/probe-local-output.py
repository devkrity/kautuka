#!/usr/bin/env python3
"""Local upstream transport in a trusted-infrastructure-only fixture namespace."""

import argparse
import importlib.util
import json
import os
from pathlib import Path
import selectors
import shutil
import signal
import sqlite3
import subprocess
import sys
import uuid


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


owners = module("owners", "probe-runtime-owners.py")
primitives = module("primitives", "probe-primitives.py")


class OutputProbe:
    def __init__(self, topology):
        self.topology = topology
        self.checks = []
        self.used_ports = set()
        self.next_port = 54000
        self.denials = []

    def counter(self, name):
        data = json.loads(self.topology.run(["nft", "-j", "list", "counter", "inet", "ktng_output", name], role="gw"))
        return next(entry["counter"]["packets"] for entry in data["nftables"] if "counter" in entry)

    def client(self, uid, address, protocol, port=None):
        args = ["ip", "netns", "exec", self.topology.names["gw"], "setpriv", "--reuid=" + str(uid),
                "--regid=" + str(uid), "--init-groups", sys.executable, "-u", "-c", primitives.CLIENT_PROGRAM, address, str(protocol)]
        if port is not None:
            self.used_ports.add(port)
            args.append(str(port))
        process = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        self.topology.clients.append((process, {}))
        return process

    def ready(self, process):
        identity = json.loads(self.topology.read_line(process.stdout, timeout=3))
        self.used_ports.add(identity["port"])
        return identity

    def exchange(self, process, *, allowed):
        nonce = uuid.uuid4().hex
        process.stdin.write(json.dumps({"action": "exchange", "payload": nonce}) + "\n")
        process.stdin.flush()
        response = json.loads(self.topology.read_line(process.stdout))
        if allowed:
            if response["response"] != nonce or self.topology.read_line(self.topology.echo.stdout) != nonce:
                raise RuntimeError("Output payload/audit-before-delivery mismatch")
        else:
            if response["response"] is not None:
                raise RuntimeError("Denied local output received an echo")
            self.sink_silent()
        return response

    def sink_silent(self):
        with selectors.DefaultSelector() as selector:
            selector.register(self.topology.echo.stdout, selectors.EVENT_READ)
            if selector.select(timeout=0.2):
                raise RuntimeError("Denied local output reached sink: " + self.topology.echo.stdout.readline())

    def reverse(self, process, identity, payload, *, allowed):
        self.topology.echo.stdin.write(json.dumps({"address": identity["address"], "port": identity["port"],
            "protocol": identity["protocol"], "payload": payload}) + "\n")
        self.topology.echo.stdin.flush()
        if self.topology.read_line(self.topology.echo.stdout) != "REVERSE_SENT":
            raise RuntimeError("Simulated upstream did not send reverse payload")
        process.stdin.write(json.dumps({"action": "receive"}) + "\n")
        process.stdin.flush()
        response = json.loads(self.topology.read_line(process.stdout))["response"]
        if response != (payload if allowed else None):
            raise RuntimeError("Unexpected local-input reply delivery")

    def denied(self, uid, address, protocol, counter):
        before = self.counter(counter)
        while self.next_port in self.used_ports:
            self.next_port += 1
        process = self.client(uid, address, protocol, self.next_port)
        self.next_port += 1
        try:
            try:
                self.ready(process)
            except primitives.ProbeFailure:
                _, errors = process.communicate(timeout=3)
                if protocol != 6 or process.returncode == 0 or not any(name in errors for name in ("TimeoutError", "PermissionError")):
                    raise RuntimeError("Denied client failed outside bounded connection attempt: " + errors)
                self.sink_silent()
                error = "bounded-connect-failure"
            else:
                error = self.exchange(process, allowed=False).get("errno")
            if self.counter(counter) <= before:
                raise RuntimeError("Denied attempt did not reach expected kernel path: " + counter)
            self.denials.append({"uid": uid, "address": address, "protocol": protocol, "counter": counter, "client_error": error})
        finally:
            if process.poll() is None:
                process.terminate()
                process.communicate(timeout=3)

    def execute(self, binary, resolver, other):
        topology = self.topology
        topology.consumer = subprocess.Popen(["ip", "netns", "exec", topology.names["gw"], str(binary), "probe-output",
            topology.names["gw"], topology.ledger, "23199", str(resolver["uid"])], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        if topology.read_line(topology.consumer.stdout, timeout=10) != "KTNG_PROBE_READY":
            raise RuntimeError("Local-output consumer did not become ready")
        topology.run(["nft", "delete", "table", "inet", "ktng_probe"], role="gw")
        retained = []
        for address in ("10.231.252.2", "fd7a:6b74:252::2"):
            for protocol in (6, 17):
                process = self.client(resolver["uid"], address, protocol)
                identity = self.ready(process)
                self.exchange(process, allowed=True)
                retained.append((process, identity))
        with sqlite3.connect("file:" + topology.ledger + "?mode=ro", uri=True) as database:
            database.row_factory = sqlite3.Row
            rows = [dict(row) for row in database.execute("SELECT * FROM gateway_admissions")]
        if len(rows) != 4 or any(row["hook"] != 3 or row["socket_uid"] != resolver["uid"] or row["subject"] != "probe-resolver" for row in rows):
            raise RuntimeError("Local-output durable identity missing")
        self.checks.append("tcp-udp-ipv4-ipv6-local-admission")

        for uid in (0, other["uid"]):
            for address in ("10.231.252.2", "fd7a:6b74:252::2"):
                for protocol in (6, 17):
                    self.denied(uid, address, protocol, "actor_denials")
        self.checks.append("root-and-unregistered-uid-denied-before-fast-path")

        # Deliberately demonstrate the documented receive-side trust boundary.
        original = self.client(resolver["uid"], "10.231.252.2", 17)
        identity = self.ready(original)
        self.exchange(original, allowed=True)
        original.terminate()
        original.communicate(timeout=3)
        replacement = self.client(other["uid"], "10.231.252.2", 17, identity["port"])
        self.ready(replacement)
        self.reverse(replacement, identity, uuid.uuid4().hex, allowed=True)
        before = self.counter("actor_denials")
        self.exchange(replacement, allowed=False)
        if self.counter("actor_denials") <= before:
            raise RuntimeError("Rebound socket inherited output authority")
        replacement.terminate()
        replacement.communicate(timeout=3)
        self.checks.append("udp-rebind-receive-isolation-limitation-observed")

        with sqlite3.connect("file:" + topology.ledger + "?mode=ro", uri=True) as database:
            baseline = database.execute("SELECT COUNT(*) FROM gateway_admissions").fetchone()[0]
        if baseline != 5:
            raise RuntimeError("Expected four retained flows plus the UDP diagnostic admission")
        topology.consumer.kill()
        topology.consumer.communicate(timeout=3)
        # Retained original sockets occupy their tuples; other ephemeral attempts
        # use fresh tuples and must pass the NEW queue counter to count as proof.
        for address in ("10.231.252.2", "fd7a:6b74:252::2"):
            for protocol in (6, 17):
                self.denied(resolver["uid"], address, protocol, "queued_output")
        self.checks.append("consumer-absent-new-local-flow-denied")
        for process, _ in retained:
            self.exchange(process, allowed=True)
        self.checks.append("existing-local-grants-survive-consumer-loss")
        topology.run(["nft", "flush", "set", "inet", "ktng_output", "valid_output"], role="gw")
        for process, identity in retained:
            self.exchange(process, allowed=False)
            self.reverse(process, identity, uuid.uuid4().hex, allowed=False)
        self.checks.append("output-and-input-cutoff-after-kernel-grant-removal")
        with sqlite3.connect("file:" + topology.ledger + "?mode=ro", uri=True) as database:
            if database.execute("SELECT COUNT(*) FROM gateway_admissions").fetchone()[0] != baseline:
                raise RuntimeError("New local admission appeared after consumer loss")
        return {"checks": self.checks, "initialAdmissions": rows, "actor_denials": self.counter("actor_denials"),
                "queued_output": self.counter("queued_output"), "receiveSideUidIsolation": False, "denials": self.denials}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--admission-binary", type=Path, required=True)
    options = parser.parse_args()
    if sys.platform != "linux" or os.geteuid() != 0:
        parser.error("requires Linux root; no resources created")
    required = ("ip", "nft", "setpriv", "useradd", "userdel", "usermod", "groupadd", "groupdel", "systemctl")
    if any(shutil.which(tool) is None for tool in required) or not options.admission_binary.is_file():
        parser.error("missing required fixture tools or binary; no resources created")
    with options.report.open("x", encoding="utf-8") as report:
        result = {"schemaVersion": 1, "scope": "trusted-infrastructure-local-upstream-transport", "gate1Passed": False,
                  "transportPassed": False, "cleanupErrors": [], "limitations": [
                      "Gateway namespace is trusted infrastructure only; UDP close/rebind demonstrates no hostile-UID receive isolation.",
                      "No DNS protocol/query policy, production namespace supervisor, complete conntrack reconciliation or native Linux validation."]}
        try:
            with owners.fixture_lock():
                fixture = owners.RuntimeOwners("systemd")
                topology = primitives.Topology()
                result["createdResources"] = fixture.owners
                result["fixtureRoot"] = str(fixture.root)
                result["namespaces"] = topology.names
                probe = OutputProbe(topology)
                try:
                    resolver, other = [fixture.create(role) for role in ("resolver", "other")]
                    topology.create()
                    result["transport"] = probe.execute(options.admission_binary, resolver, other)
                    result["transportPassed"] = True
                finally:
                    result["checksCompleted"] = probe.checks
                    result["cleanupErrors"].extend(topology.cleanup())
                    result["cleanupErrors"].extend(fixture.cleanup())
                    if result["cleanupErrors"]:
                        result["transportPassed"] = False
        except (OSError, RuntimeError, subprocess.SubprocessError, ValueError, KeyError) as error:
            result["transportPassed"] = False
            result["error"] = str(error)
        json.dump(result, report, indent=2)
        report.write("\n")
        print(json.dumps(result, indent=2))
        return 0 if result["transportPassed"] else 1


if __name__ == "__main__":
    def interrupted(signum, frame):
        raise InterruptedError(f"probe interrupted by signal {signum}")

    signal.signal(signal.SIGTERM, interrupted)
    raise SystemExit(main())
