#!/usr/bin/env python3
"""Positive-only signed-plan/TLS/digest pull proof in an isolated simulated uplink."""

import argparse
import base64
import importlib.util
import json
import os
from pathlib import Path
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


owners = module("runtime_owners", "probe-runtime-owners.py")
primitives = module("primitives", "probe-primitives.py")
registry = module("registry", "registry-fixture.py")

ACQUIRE = owners.IDENTITY_INSPECT + r"""
def command(args):
    return subprocess.run(podman+args,capture_output=True,text=True,timeout=25)
plan=json.load(open(archive))
reference=plan['ImageReference']
pulled=command(['pull','--tls-verify=true','--cert-dir',plan['CaDirectory'],reference])
if pulled.returncode: raise RuntimeError('Approved TLS digest pull failed: '+pulled.stderr)
image=command(['image','inspect','--format=json',reference])
if image.returncode: raise RuntimeError('Approved image inspection failed: '+image.stderr)
image=json.loads(image.stdout)[0]
configuration='sha256:'+image['Id'].removeprefix('sha256:')
if configuration != plan['ConfigurationDigest'] or image['Digest'] != plan['ManifestDigest']:
    raise RuntimeError('Pulled image differs from verified manifest/configuration: '+json.dumps({'id':image['Id'],'digest':image['Digest'],'expected_id':plan['ConfigurationDigest'],'expected_digest':plan['ManifestDigest']}))
identifier=configuration
result['acquisition']={'raw_image_id':image['Id'],'image_id':identifier,'manifest_digest':image['Digest'],'reference':reference}
limits=['--pull=never','--network=none','--user=65532:65532','--cap-drop=ALL',
        '--security-opt=no-new-privileges','--read-only','--cgroupns=private',
        '--memory=32m','--memory-swap=32m','--pids-limit=16','--cpu-period=100000','--cpu-quota=25000']
name=prefix+'-hold'
try:
    started=command(['run','--detach','--name',name]+limits+[identifier,'/probe','hold'])
    if started.returncode: raise RuntimeError('Approved hold failed: '+started.stderr)
    state=command(['inspect','--format=json',name])
    if state.returncode: raise RuntimeError('Approved hold inspection failed: '+state.stderr)
    pid=json.loads(state.stdout)[0]['State']['Pid']
    if pid <= 0: raise RuntimeError('Approved hold exited before observation')
    proc='/proc/'+str(pid)
    held={'pid':pid,'netns':os.readlink(proc+'/ns/net'),'cgroup':open(proc+'/cgroup').read().strip()}
    marker=os.path.join(os.environ['HOME'],'held.json')
    with open(marker,'w') as stream: json.dump(held,stream)
    time.sleep(2)
    os.unlink(marker)
finally:
    removed=command(['rm','--force',name])
    if removed.returncode: raise RuntimeError('Approved hold cleanup failed: '+removed.stderr)
activated=command(['run','--rm']+limits+[identifier,'/probe','artifact',plan['ActivationNonce']])
if activated.returncode or activated.stdout.strip() != plan['ActivationNonce']:
    raise RuntimeError('Approved artifact activation marker mismatch: '+activated.stderr)
result['acquisition']['activated_nonce']=activated.stdout.strip()
if FAILURE_CHECK:
    def control(phase):
        path=os.path.join(os.environ['HOME'],'control.json')
        ack=os.path.join(os.environ['HOME'],'control-ack')
        with open(path,'w') as stream: json.dump({'phase':phase},stream)
        deadline=time.monotonic()+10
        while not os.path.exists(ack):
            if time.monotonic()>deadline: raise RuntimeError('Acquisition root control timed out')
            time.sleep(.05)
        if open(ack).read()!=phase: raise RuntimeError('Wrong acquisition control acknowledgement')
        os.unlink(path)
        os.unlink(ack)
    def cold_pull(label):
        base=podman.copy()
        roots={}
        for flag,suffix in (('--root','graph'),('--runroot','runroot'),('--tmpdir','tmp')):
            directory=os.path.join(os.environ['HOME'],label+'-'+suffix)
            os.mkdir(directory,0o700)
            base[base.index(flag)+1]=directory
            roots[flag]=directory
        state=subprocess.run(base+['info','--format=json'],capture_output=True,text=True,timeout=20)
        if state.returncode or json.loads(state.stdout)['store']['graphRoot'] != roots['--root']:
            raise RuntimeError('Cold store scope not verified')
        before=subprocess.run(base+['images','--format=json'],capture_output=True,text=True,timeout=10)
        if before.returncode or json.loads(before.stdout): raise RuntimeError('Cold store was not empty')
        attempted=subprocess.run(base+['pull','--retry=0','--tls-verify=true','--cert-dir',plan['CaDirectory'],reference],capture_output=True,text=True,timeout=25)
        after=subprocess.run(base+['images','--format=json'],capture_output=True,text=True,timeout=10)
        containers=subprocess.run(base+['ps','--all','--format=json'],capture_output=True,text=True,timeout=10)
        if attempted.returncode == 0 or after.returncode or containers.returncode or json.loads(after.stdout) or json.loads(containers.stdout):
            raise RuntimeError('Rejected acquisition installed an image or created a container')
        return {'exit_code':attempted.returncode,'stderr':attempted.stderr,'empty_images':True,'empty_containers':True,'graph_root':roots['--root']}
    control('corrupt-content')
    corrupted=cold_pull('corrupt')
    import re
    mismatch=re.search(r'download config\.json digest (sha256:[0-9a-f]{64}) does not match expected (sha256:[0-9a-f]{64})',corrupted['stderr'].lower())
    if not mismatch or mismatch.group(2)!=plan['ConfigurationDigest'] or mismatch.group(1)==mismatch.group(2):
        raise RuntimeError('Corrupt pull did not report a recognized digest verification failure: '+corrupted['stderr'])
    corrupted['reported_calculated_digest']=mismatch.group(1)
    result['acquisition']['corrupted_content']=corrupted
    control('consumer-stop')
    result['acquisition']['consumer_absent']=cold_pull('absent')
print(json.dumps(result),flush=True)
"""


def prepare(fixture, binary):
    fixture.build_workload(Path(__file__).with_name("resource-workload.c"))
    image_directory = fixture.root / "registry"
    image = registry.build_image(image_directory, fixture.archive)
    private = fixture.root / "signing"
    private.mkdir(mode=0o700)
    trust = fixture.root / "trust"
    trust.mkdir(mode=0o755)
    run = owners.run
    common = ["openssl", "req", "-newkey", "ec", "-pkeyopt", "ec_paramgen_curve:prime256v1", "-nodes"]
    run(common + ["-x509", "-days", "1", "-subj", "/CN=KTNG-Fixture-CA", "-addext", "basicConstraints=critical,CA:TRUE",
                  "-keyout", str(private / "ca.key"), "-out", str(trust / "ca.crt")])
    run(common + ["-new", "-subj", "/CN=KTNG-Fixture-Registry", "-keyout", str(image_directory / "server.key"), "-out", str(private / "server.csr")])
    extensions = private / "extensions.conf"
    extensions.write_text("subjectAltName=IP:10.231.252.2\nbasicConstraints=critical,CA:FALSE\nextendedKeyUsage=serverAuth\n")
    run(["openssl", "x509", "-req", "-in", str(private / "server.csr"), "-CA", str(trust / "ca.crt"),
         "-CAkey", str(private / "ca.key"), "-CAcreateserial", "-CAserial", str(private / "ca.srl"),
         "-days", "1", "-extfile", str(extensions), "-out", str(image_directory / "server.crt")])
    run(["openssl", "ecparam", "-name", "prime256v1", "-genkey", "-noout", "-out", str(private / "release.key")])
    run(["openssl", "pkey", "-in", str(private / "release.key"), "-pubout", "-out", str(private / "release.pub")])
    manifest = {"schemaVersion": 1, "releaseId": "ktng-" + uuid.uuid4().hex, "registry": "10.231.252.2:20443",
                "repository": "ktng/probe", "platform": "linux/amd64", "manifestDigest": image["manifestDigest"],
                "configurationDigest": image["configurationDigest"], "compatibility": "ktng-probe-v1"}
    path = private / "release.json"
    path.write_bytes(registry.encode(manifest))
    signature = subprocess.run(["openssl", "dgst", "-sha256", "-sign", str(private / "release.key"), str(path)], capture_output=True, check=True, timeout=10).stdout
    signature_path = private / "release.sig"
    signature_path.write_bytes(base64.b64encode(signature))
    plan = json.loads(run([str(binary), "probe-verify-artifact", str(path), str(signature_path), str(private / "release.pub")]))
    plan["CaDirectory"] = str(trust)
    plan["ActivationNonce"] = uuid.uuid4().hex
    plan_path = fixture.root / "verified-plan.json"
    plan_path.write_bytes(registry.encode(plan))
    plan_path.chmod(0o644)
    fixture.archive = plan_path  # Inspector receives only the authenticated, root-owned plan.
    return image_directory, plan


def start_registry(topology, directory, binary):
    topology.echo.terminate()
    topology.echo.communicate(timeout=3)
    topology.echo = subprocess.Popen(["ip", "netns", "exec", topology.names["sink"], sys.executable,
        str(Path(__file__).with_name("registry-fixture.py")), "--directory", str(directory), "--ledger", topology.ledger],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    if topology.read_line(topology.echo.stdout) != "KTNG_REGISTRY_READY":
        raise RuntimeError("TLS registry did not become ready")
    topology.consumer = subprocess.Popen(["ip", "netns", "exec", topology.names["gw"], str(binary), "probe-registry",
        topology.names["gw"], topology.ledger, "23199"], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    if topology.read_line(topology.consumer.stdout, timeout=10) != "KTNG_PROBE_READY":
        raise RuntimeError("Registry admission consumer did not become ready")
    topology.run(["nft", "delete", "table", "inet", "ktng_probe"], role="gw")
    topology.registry_started = True


def capture_registry(topology, owner):
    topology.echo.terminate()
    output, errors = topology.echo.communicate(timeout=3)
    owner["registry_events"] = [json.loads(line) for line in output.splitlines()]
    topology.registry_started = False
    return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--admission-binary", type=Path, required=True)
    parser.add_argument("--failure-check", action="store_true", help="Cold stores reject corrupt content and consumer-absent acquisition")
    options = parser.parse_args()
    if sys.platform != "linux" or os.geteuid() != 0:
        parser.error("requires Linux root; no resources created")
    tools = ("podman", "ip", "nft", "nsenter", "setpriv", "env", "systemd-run", "systemctl", "openssl",
             "useradd", "userdel", "groupadd", "groupdel", "usermod", "gcc", "ldd")
    if options.failure_check:
        tools += ("conntrack",)
    if any(shutil.which(tool) is None for tool in tools) or not options.admission_binary.is_file():
        parser.error("missing required fixture tools/binary; no resources created")
    with options.report.open("x", encoding="utf-8") as report:
        result = {"schemaVersion": 1, "scope": "offline-positive-signed-plan-tls-digest-acquisition", "gate1Passed": False,
                  "feasibilityPassed": False, "owners": [], "cleanupErrors": [], "limitations": [
                      "Fixture-only root-supplied signing trust, IPv4 TLS registry without authentication, redirects or CDN.",
                      "No corrupted-content/cold-pull consumer-loss, DNS, real registry interoperability, anti-rollback or updater transaction proof.",
                      "WSL managed non-login candidate; native Linux PAM/SELinux parity and short-lived helper coverage unvalidated."]}
        result["failureCheck"] = options.failure_check
        if options.failure_check:
            result["limitations"][1] = "Consumer-absence tests use fixture-only conntrack reset and reduced source SYN retries. No restart, DNS, real registry interoperability, anti-rollback or updater transaction proof."
        try:
            with owners.fixture_lock():
                fixture = owners.RuntimeOwners("systemd", True, "FAILURE_CHECK=" + repr(options.failure_check) + "\n" + ACQUIRE)
                result["createdResources"] = fixture.owners
                result["fixtureRoot"] = str(fixture.root)
                try:
                    directory, plan = prepare(fixture, options.admission_binary)
                    result["verifiedPlan"] = plan
                    core, work = [fixture.create(role) for role in ("core", "work")]
                    for owner, peer in ((core, work), (work, core)):
                        topology = primitives.Topology()
                        descriptor = os.open("/run/netns/" + owner["name"], os.O_RDONLY | os.O_CLOEXEC)
                        owner["network_topology"] = {**topology.names, "src": owner["name"]}
                        try:
                            topology.create(borrowed_source=(owner["name"], descriptor))
                            start_registry(topology, directory, options.admission_binary)
                            def counters():
                                values = json.loads(topology.run(["nft", "-j", "list", "counter", "inet", "ktng_admission", "queued_tcp4"], role="gw"))
                                return next(entry["counter"]["packets"] for entry in values["nftables"] if "counter" in entry)

                            def control(event):
                                if event == {"phase": "corrupt-content"}:
                                    (directory / "corrupt").write_text("trusted fixture fault")
                                elif event == {"phase": "consumer-stop"}:
                                    (directory / "corrupt").unlink()
                                    topology.consumer.kill()
                                    topology.consumer.communicate(timeout=3)
                                    for family in ("ipv4", "ipv6"):
                                        topology.run(["conntrack", "-F", "-f", family], role="gw")
                                        if topology.run(["conntrack", "-L", "-f", family], role="gw"):
                                            raise RuntimeError("Acquisition fixture conntrack reset failed")
                                    with sqlite3.connect("file:" + topology.ledger + "?mode=ro", uri=True) as ledger:
                                        baseline = ledger.execute("SELECT COUNT(*) FROM gateway_admissions").fetchone()[0]
                                    owner["consumer_loss_setup"] = {"baseline_admissions": baseline, "before_queue": counters(), "conntrack_emptied": True,
                                        "registry_event_boundary": len((directory / "events.jsonl").read_bytes().splitlines())}
                                else:
                                    raise RuntimeError("Unexpected acquisition control event")

                            if options.failure_check:
                                topology.run(["sysctl", "-qw", "net.ipv4.tcp_syn_retries=1"], role="src")
                            observation = fixture.inspect(owner, peer, on_event=control if options.failure_check else None)
                            result["owners"].append(observation)
                            errors = capture_registry(topology, owner)
                            events = owner["registry_events"]
                            if options.failure_check:
                                if len(events) != owner["consumer_loss_setup"]["registry_event_boundary"]:
                                    raise RuntimeError("Registry received events after consumer-loss boundary")
                                if not any(event["event"] == "corrupt_blob" and event["expected"] == plan["ConfigurationDigest"] and event["served"] == observation["acquisition"]["corrupted_content"]["reported_calculated_digest"] for event in events):
                                    raise RuntimeError("No corrupt bytes were served in the negative test")
                                with sqlite3.connect("file:" + topology.ledger + "?mode=ro", uri=True) as ledger:
                                    after = ledger.execute("SELECT COUNT(*) FROM gateway_admissions").fetchone()[0]
                                owner["consumer_loss_setup"]["after_queue"] = counters()
                                if after != owner["consumer_loss_setup"]["baseline_admissions"] or counters() <= owner["consumer_loss_setup"]["before_queue"]:
                                    raise RuntimeError("Cold acquisition did not reach unavailable queue without new admission")
                            if any(event["event"] == "AUDIT_MISSING" for event in events):
                                raise RuntimeError("Registry bytes preceded durable admission")
                            metadata = json.loads((directory / "metadata.json").read_bytes())
                            required_paths = {"/v2/ktng/probe/manifests/" + plan["ManifestDigest"],
                                              "/v2/ktng/probe/blobs/" + plan["ConfigurationDigest"],
                                              "/v2/ktng/probe/blobs/" + metadata["layerDigest"]}
                            requested = {event["path"] for event in events if event["event"] == "request" and event["method"] == "GET"}
                            if not any(event["event"] == "admitted_connection" for event in events) or not required_paths <= requested:
                                raise RuntimeError("Missing audited registry manifest/config/layer evidence: " + errors)
                        finally:
                            if getattr(topology, "registry_started", False):
                                try:
                                    capture_registry(topology, owner)
                                except (OSError, subprocess.SubprocessError, ValueError) as error:
                                    owner["registry_capture_error"] = str(error)
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
