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
    options = parser.parse_args()
    if sys.platform != "linux" or os.geteuid() != 0:
        parser.error("requires Linux root; no resources created")
    tools = ("podman", "ip", "nft", "nsenter", "setpriv", "env", "systemd-run", "systemctl", "openssl",
             "useradd", "userdel", "groupadd", "groupdel", "usermod", "gcc", "ldd")
    if any(shutil.which(tool) is None for tool in tools) or not options.admission_binary.is_file():
        parser.error("missing required fixture tools/binary; no resources created")
    with options.report.open("x", encoding="utf-8") as report:
        result = {"schemaVersion": 1, "scope": "offline-positive-signed-plan-tls-digest-acquisition", "gate1Passed": False,
                  "feasibilityPassed": False, "owners": [], "cleanupErrors": [], "limitations": [
                      "Fixture-only root-supplied signing trust, IPv4 TLS registry without authentication, redirects or CDN.",
                      "No corrupted-content/cold-pull consumer-loss, DNS, real registry interoperability, anti-rollback or updater transaction proof.",
                      "WSL managed non-login candidate; native Linux PAM/SELinux parity and short-lived helper coverage unvalidated."]}
        try:
            with owners.fixture_lock():
                fixture = owners.RuntimeOwners("systemd", True, ACQUIRE)
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
                            observation = fixture.inspect(owner, peer)
                            result["owners"].append(observation)
                            errors = capture_registry(topology, owner)
                            events = owner["registry_events"]
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
