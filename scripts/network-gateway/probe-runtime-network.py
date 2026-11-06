#!/usr/bin/env python3
"""Offline Gate 1 pasta traffic experiment; no real host uplink is connected."""

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


owners = module("runtime_owners", "probe-runtime-owners.py")
primitives = module("primitives", "probe-primitives.py")

NETWORK_INSPECT = owners.IDENTITY_INSPECT + r"""
def command(args):
    return subprocess.run(podman+args,capture_output=True,text=True,timeout=15)
imported=command(['import','--quiet',archive])
if imported.returncode: raise RuntimeError('Offline import failed: '+imported.stderr)
image=imported.stdout.strip()
if len(image.removeprefix('sha256:')) != 64: raise RuntimeError('Unexpected imported image ID')
result['network']={'image_id':image,'flows':[]}
def exchange(mode, allowed):
    import uuid
    nonce=uuid.uuid4().hex
    name=prefix+('-' if allowed else '-deny-')+mode
    flow={'mode':mode,'nonce':nonce,'exit_code':-1}
    result['network']['flows' if allowed else 'denied_flows'].append(flow)
    try:
        started=command(['run','--detach','--name',name,'--pull=never','--network=pasta:--no-map-gw',
            '--user=65532:65532','--cap-drop=ALL','--security-opt=no-new-privileges','--read-only',
            '--cgroupns=private','--memory=32m','--memory-swap=32m','--pids-limit=16',
            '--cpu-period=100000','--cpu-quota=25000',image,'/probe','echo-'+mode,nonce])
        flow['start_stderr']=started.stderr
        if started.returncode: raise RuntimeError('Container start failed: '+started.stderr)
        state=command(['inspect','--format=json',name])
        if state.returncode: raise RuntimeError('Container inspect failed: '+state.stderr)
        pid=json.loads(state.stdout)[0]['State']['Pid']
        if allowed:
            if pid <= 0: raise RuntimeError('Workload exited before process observation')
            proc='/proc/'+str(pid)
            flow['host']={'pid':pid,'netns':os.readlink(proc+'/ns/net'),'cgroup':open(proc+'/cgroup').read().strip()}
        if allowed and mode == 'tcp4':
            marker=os.path.join(os.environ['HOME'],'held.json')
            with open(marker,'w') as stream: json.dump(flow['host'],stream)
            time.sleep(2)
            os.unlink(marker)
        waited=command(['wait',name])
        if waited.returncode: raise RuntimeError('Container wait failed: '+waited.stderr)
        flow['exit_code']=int(waited.stdout.strip())
        logs=command(['logs',name])
        flow['stdout'],flow['stderr']=logs.stdout,logs.stderr
    except (RuntimeError,subprocess.TimeoutExpired,ValueError) as error:
        flow['error']=str(error)
    finally:
        try:
            removed=command(['rm','--force',name])
            if removed.returncode: flow['cleanup_error']=removed.stderr
        except subprocess.TimeoutExpired as error:
            flow['cleanup_error']=str(error)
for mode in ('tcp4','udp4','tcp6','udp6'):
    exchange(mode,True)
if FAILURE_CHECK:
    control=os.path.join(os.environ['HOME'],'control.json')
    ack=os.path.join(os.environ['HOME'],'control-ack')
    with open(control,'w') as stream: json.dump({'phase':'consumer-stop'},stream)
    deadline=time.monotonic()+10
    while not os.path.exists(ack):
        if time.monotonic()>deadline: raise RuntimeError('Root fixture control acknowledgement timed out')
        time.sleep(.05)
    if open(ack).read() != 'consumer-stop': raise RuntimeError('Unexpected control acknowledgement')
    os.unlink(control)
    os.unlink(ack)
    result['network']['denied_flows']=[]
    for mode in ('tcp4','udp4','tcp6','udp6'):
        exchange(mode,False)
print(json.dumps(result),flush=True)
"""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--admission-binary", type=Path, required=True)
    parser.add_argument("--workload-source", type=Path, required=True)
    parser.add_argument("--failure-check", action="store_true", help="After baseline, stop consumer and clear fixture conntrack state before new-flow denial checks")
    options = parser.parse_args()
    if sys.platform != "linux" or os.geteuid() != 0:
        parser.error("requires Linux root; no resources created")
    required = ("podman", "pasta", "ip", "nft", "nsenter", "setpriv", "env", "systemd-run", "systemctl",
                "useradd", "userdel", "usermod", "groupadd", "groupdel", "gcc", "ldd")
    if options.failure_check:
        required += ("conntrack",)
    if any(shutil.which(tool) is None for tool in required) or not options.admission_binary.is_file() or not options.workload_source.is_file():
        parser.error("missing required tools or fixture inputs; no resources created")
    with options.report.open("x", encoding="utf-8") as report:
        result = {"schemaVersion": 1, "scope": "offline-rootless-pasta-admitted-traffic",
                  "gate1Passed": False, "feasibilityPassed": False, "owners": [], "cleanupErrors": [],
                  "limitations": ["WSL managed non-login user-manager candidate; native Linux PAM/SELinux parity unvalidated.",
                                  "Runtime-owner attribution only; no per-connector identity, DNS, signed acquisition, alternate-uplink or consumer-failure proof.",
                                  "Process snapshots do not prove placement of every socket or short-lived helper."]}
        if options.failure_check:
            result["limitations"][1] = "Runtime-owner attribution only; consumer-absence checks use a fixture-only conntrack reset. No existing-flow/restart, per-connector identity, DNS, signed acquisition or alternate-uplink proof."
        result["failureCheck"] = options.failure_check
        try:
            with owners.fixture_lock():
                fixture = owners.RuntimeOwners("systemd", True, "FAILURE_CHECK=" + repr(options.failure_check) + "\n" + NETWORK_INSPECT)
                result["createdResources"] = fixture.owners
                result["fixtureRoot"] = str(fixture.root)
                try:
                    result["workload"] = fixture.build_workload(options.workload_source)
                    core, work = [fixture.create(role) for role in ("core", "work")]
                    for owner, peer in ((core, work), (work, core)):
                        topology = primitives.Topology()
                        descriptor = os.open("/run/netns/" + owner["name"], os.O_RDONLY | os.O_CLOEXEC)
                        try:
                            owner["network_topology"] = {**topology.names, "src": owner["name"]}
                            topology.create(borrowed_source=(owner["name"], descriptor))
                            topology.start_admission(options.admission_binary)
                            def counters():
                                values = json.loads(topology.run(["nft", "-j", "list", "counters", "table", "inet", "ktng_admission"], role="gw"))
                                return {entry["counter"]["name"]: entry["counter"]["packets"] for entry in values["nftables"] if "counter" in entry}

                            def control(event):
                                if event != {"phase": "consumer-stop"}:
                                    raise RuntimeError("Unknown fixture control event")
                                topology.consumer.kill()
                                topology.consumer.communicate(timeout=3)
                                # Only this fixture gateway's state is touched. Clear prior entries
                                # so translated tuple reuse cannot inherit an established grant.
                                for family in ("ipv4", "ipv6"):
                                    topology.run(["conntrack", "-F", "-f", family], role="gw")
                                    if topology.run(["conntrack", "-L", "-f", family], role="gw"):
                                        raise RuntimeError("Fixture conntrack table did not become empty")
                                with sqlite3.connect("file:" + topology.ledger + "?mode=ro", uri=True) as ledger:
                                    baseline = ledger.execute("SELECT COUNT(*) FROM gateway_admissions").fetchone()[0]
                                if baseline != 5:
                                    raise RuntimeError("Expected baseline plus four admitted pasta flows before consumer loss")
                                owner["consumer_loss_setup"] = {"conntrack_emptied": True, "baseline_admissions": baseline, "before": counters()}

                            observation = fixture.inspect(owner, peer, on_event=control if options.failure_check else None)
                            result["owners"].append(observation)
                            flows = observation["network"]["flows"]
                            if {flow["mode"] for flow in flows} != {"tcp4", "udp4", "tcp6", "udp6"}:
                                raise RuntimeError("Missing protocol evidence")
                            topology.echo.terminate()
                            received, sink_error = topology.echo.communicate(timeout=3)
                            if received.splitlines() != [flow["nonce"] for flow in flows]:
                                raise RuntimeError("Sink nonce/durable admission mismatch: " + received + sink_error)
                            with sqlite3.connect("file:" + topology.ledger + "?mode=ro", uri=True) as ledger:
                                ledger.row_factory = sqlite3.Row
                                owner["admissions"] = [dict(row) for row in ledger.execute("SELECT * FROM gateway_admissions")]
                            for flow in flows:
                                if flow["exit_code"] != 0 or "cleanup_error" in flow or json.loads(flow["stdout"]) != {"echoed": True}:
                                    raise RuntimeError("Pasta workload failed: " + json.dumps(flow))
                            if options.failure_check:
                                denied = observation["network"].get("denied_flows", [])
                                if len(denied) != 4 or {flow["mode"] for flow in denied} != {"tcp4", "udp4", "tcp6", "udp6"}:
                                    raise RuntimeError("Missing denied-flow protocol evidence")
                                after = counters()
                                owner["consumer_loss_setup"]["after"] = after
                                for flow in denied:
                                    if flow["exit_code"] != 3 or "error" in flow or "cleanup_error" in flow:
                                        raise RuntimeError("Consumer-loss workload did not fail closed: " + json.dumps(flow))
                                    counter = "queued_" + flow["mode"]
                                    if after[counter] <= owner["consumer_loss_setup"]["before"][counter]:
                                        raise RuntimeError("New-flow queue path was not observed: " + counter)
                                if len(owner["admissions"]) != owner["consumer_loss_setup"]["baseline_admissions"]:
                                    raise RuntimeError("Unexpected new durable admission after consumer death")
                            if not any(item["comm"] in {"pasta", "pasta.avx2", "pasta.avx512"} for item in owner.get("held_processes", [])):
                                raise RuntimeError("Pasta support-process snapshot missing")
                        finally:
                            result["cleanupErrors"].extend(topology.cleanup())
                            os.close(descriptor)
                    result["feasibilityPassed"] = True
                finally:
                    result["cleanupErrors"].extend(fixture.cleanup())
                    if result["cleanupErrors"]:
                        result["feasibilityPassed"] = False
        except (OSError, RuntimeError, subprocess.SubprocessError, ValueError, KeyError) as error:
            result["feasibilityPassed"] = False
            result["error"] = str(error)
        json.dump(result, report, indent=2)
        report.write("\n")
        print(json.dumps(result, indent=2))
        return 0 if result["feasibilityPassed"] else 1


if __name__ == "__main__":
    def interrupted(signum, frame):
        raise InterruptedError(f"probe interrupted by signal {signum}")

    signal.signal(signal.SIGTERM, interrupted)
    raise SystemExit(main())
