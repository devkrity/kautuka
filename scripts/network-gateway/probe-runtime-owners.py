#!/usr/bin/env python3
"""Isolated Gate 1 rootless execution probe, not an installer or containment claim.

Creates two disposable accounts, disjoint subordinate mappings, private storage and
network namespaces. Runs Podman info/unshare under systemd-managed units; an optional
offline workload tests actual container resource limits.
No image acquisition, real-uplink route, existing account, or existing store is used.
"""

import argparse
from contextlib import contextmanager
import fcntl
import grp
import hashlib
import json
import os
from pathlib import Path
import pwd
import secrets
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import tarfile
import uuid


IDENTITY_INSPECT = r"""import json,os,subprocess,sys,time
def identity():
    return {'euid':os.geteuid(),'netns':os.readlink('/proc/self/ns/net'),'uid_map':open('/proc/self/uid_map').read().strip(),'gid_map':open('/proc/self/gid_map').read().strip(),'cgroup':open('/proc/self/cgroup').read().strip()}
peer_home,archive,prefix=sys.argv[1:4]
podman=sys.argv[4:]
outer=identity()
try:
    os.listdir(peer_home)
    outer['peer_home_access_denied']=False
except PermissionError:
    outer['peer_home_access_denied']=True
info=subprocess.run(podman+['info','--format=json'],capture_output=True,text=True,timeout=20)
if info.returncode:
    raise RuntimeError('Podman info failed: '+info.stderr)
inner_code="import json,os; print(json.dumps({'euid':os.geteuid(),'netns':os.readlink('/proc/self/ns/net'),'uid_map':open('/proc/self/uid_map').read().strip(),'gid_map':open('/proc/self/gid_map').read().strip()}))"
inner=subprocess.run(podman+['unshare',sys.executable,'-c',inner_code],capture_output=True,text=True,timeout=20)
if inner.returncode:
    raise RuntimeError('Podman unshare failed: '+inner.stderr)
result={'outer':outer,'podman':json.loads(info.stdout),'inner':json.loads(inner.stdout),'warnings':info.stderr}
"""


INSPECT = IDENTITY_INSPECT + r"""if archive != '-':
    def command(args):
        return subprocess.run(podman+args,capture_output=True,text=True,timeout=20)
    imported=command(['import','--quiet',archive])
    if imported.returncode: raise RuntimeError('Offline import failed: '+imported.stderr)
    image=imported.stdout.strip()
    if len(image.removeprefix('sha256:')) != 64: raise RuntimeError('Unexpected imported image ID')
    results={'image_id':image}
    result['resources']=results
    for mode in ('hold','limits','pids','cpu','memory'):
        name=prefix+'-'+mode
        args=['run','--name',name,'--pull=never','--network=none','--user=65532:65532',
              '--cap-drop=ALL','--security-opt=no-new-privileges','--read-only','--cgroupns=private',
              '--memory=32m','--memory-swap=32m','--pids-limit=16','--cpu-period=100000','--cpu-quota=25000',
              image,'/probe',mode]
        if mode == 'hold': args.insert(1,'--detach')
        results[mode]={'exit_code':-1}
        try:
            observed=command(args)
            results[mode]={'exit_code':observed.returncode,'stdout':observed.stdout,'stderr':observed.stderr}
            if mode == 'hold' and observed.returncode == 0:
                state=command(['inspect','--format=json',name])
                if state.returncode == 0:
                    pid=json.loads(state.stdout)[0]['State']['Pid']
                    proc='/proc/'+str(pid)
                    cgroup=open(proc+'/cgroup').read().strip()
                    path=cgroup.removeprefix('0::')
                    if not path.startswith('/') or '..' in path.split('/'): raise RuntimeError('Unexpected cgroup path')
                    root='/sys/fs/cgroup'+path
                    results[mode]['host']={'pid':pid,'cgroup':cgroup,'netns':os.readlink(proc+'/ns/net'),
                        'limits':{field:open(root+'/'+field).read().strip() for field in ('memory.max','pids.max','cpu.max')}}
                    marker=os.path.join(os.environ['HOME'],'held.json')
                    with open(marker,'w') as stream: json.dump(results[mode]['host'],stream)
                    time.sleep(2)
                    os.unlink(marker)
            if mode == 'memory':
                state=command(['inspect','--format=json',name])
                if state.returncode == 0: results[mode]['state']=json.loads(state.stdout)[0]['State']
        except subprocess.TimeoutExpired as error:
            results[mode]['error']=str(error)
        finally:
            try:
                removed=command(['rm','--force',name])
                if removed.returncode: results[mode]['cleanup_error']=removed.stderr
            except subprocess.TimeoutExpired as error:
                results[mode]['cleanup_error']=str(error)
print(json.dumps(result),flush=True)
"""


def run(args, *, timeout=30):
    result = subprocess.run(args, text=True, capture_output=True, timeout=timeout)
    if result.returncode:
        raise RuntimeError(f"{args[0]} failed ({result.returncode}): {result.stderr.strip()}")
    return result.stdout.strip()


def mappings(path):
    result = []
    for line in Path(path).read_text(encoding="utf-8").splitlines():
        if not line.strip() or line.startswith("#"):
            continue
        fields = line.split(":")
        if len(fields) != 3:
            raise RuntimeError(f"Cannot safely parse {path}")
        start, count = int(fields[1]), int(fields[2])
        if start < 0 or count <= 0 or start + count > 0xFFFFFFFF:
            raise RuntimeError(f"Invalid subordinate allocation in {path}")
        result.append((fields[0], start, count))
    return result


def validate_allocation(owner, allocations, actual_ids):
    name, start = owner["name"], owner["subid_start"]
    own = [entry for entry in allocations if entry[0] == name]
    if own != [(name, start, 65536)]:
        raise RuntimeError("Unexpected subordinate mappings; refusing to proceed")
    if any(other != name and offset < start + 65536 and start < offset + count
           for other, offset, count in allocations):
        raise RuntimeError("Subordinate range overlaps another owner's allocation")
    if any(start <= identity < start + 65536 for identity in actual_ids):
        raise RuntimeError("Subordinate range overlaps an existing account identity")


def validate_effective_map(value, owner):
    observed = [tuple(int(part) for part in line.split()) for line in value.splitlines()]
    expected = [(0, owner["uid"], 1), (1, owner["subid_start"], 65536)]
    if observed != expected:
        raise RuntimeError("Effective user-namespace mapping differs from the reserved allocation")


@contextmanager
def fixture_lock():
    descriptor = os.open("/run/lock/ktng-runtime-owners.lock", os.O_RDWR | os.O_CREAT | os.O_CLOEXEC | os.O_NOFOLLOW, 0o600)
    try:
        status = os.fstat(descriptor)
        if status.st_uid != 0 or status.st_nlink != 1 or status.st_mode & 0o077:
            raise RuntimeError("Refusing unsafe fixture-lock ownership or mode")
        fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        yield
    finally:
        os.close(descriptor)


class RuntimeOwners:
    def __init__(self, cgroup_manager, managed_user_session=False, inspect_program=None):
        self.prefix = "ktng-" + uuid.uuid4().hex[:8]
        self.root = Path(tempfile.mkdtemp(prefix="ktng-owners-", dir="/var/tmp")).resolve()
        self.root.chmod(0o711)
        self.cgroup_manager = cgroup_manager
        self.managed_user_session = managed_user_session
        self.inspect_program = inspect_program or INSPECT
        self.owners = []

    def build_workload(self, source):
        binary = self.root / "probe"
        run(["gcc", "-O2", "-Wall", "-Wextra", "-Werror", str(source), "-o", str(binary)])
        dependencies = run(["ldd", str(binary)])
        libraries = set()
        for line in dependencies.splitlines():
            for token in line.split():
                if token.startswith("/"):
                    library = Path(token)
                    if not library.is_file():
                        raise RuntimeError("Unexpected dynamic library dependency")
                    libraries.add(library)
        if not libraries:
            raise RuntimeError("No dynamic runtime dependencies found")
        archive = self.root / "workload.tar"
        with tarfile.open(archive, "w", dereference=True) as output:
            output.add(binary, arcname="probe")
            for library in sorted(libraries):
                output.add(library, arcname=str(library).lstrip("/"))
        archive.chmod(0o644)
        self.archive = archive
        return {"source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
                "archive_sha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
                "libraries": [str(library) for library in sorted(libraries)]}

    def create(self, role):
        name = self.prefix + "-" + role
        try:
            pwd.getpwnam(name)
        except KeyError:
            pass
        else:
            raise RuntimeError("Refusing a pre-existing account")
        try:
            grp.getgrnam(name)
        except KeyError:
            pass
        else:
            raise RuntimeError("Refusing a pre-existing group")
        allocations = mappings("/etc/subuid") + mappings("/etc/subgid")
        used = {entry.pw_uid for entry in pwd.getpwall()} | {entry.gr_gid for entry in grp.getgrall()}
        account_id = 1_800_000_000 + secrets.randbelow(100_000_000)
        while account_id in used or any(start <= account_id < start + count for _, start, count in allocations):
            account_id += 1
        start = max([100000] + [offset + count for _, offset, count in allocations])
        start = (start + 65535) // 65536 * 65536
        while any(start <= identity < start + 65536 for identity in used | {account_id}):
            start += 65536
        if start + 65536 > 0xFFFFFFFF:
            raise RuntimeError("No safe subordinate allocation available")
        home = self.root / role
        home.mkdir(mode=0o700)
        owner = {"name": name, "uid": account_id, "home": str(home), "subid_start": start,
                 "group_created": False, "user_created": False, "mapping_created": False,
                 "namespace_created": False, "unit": name + ".service"}
        self.owners.append(owner)
        run(["groupadd", "--gid", str(account_id), name])
        owner["group_created"] = True
        run(["useradd", "--system", "--uid", str(account_id), "--gid", name, "--home-dir", str(home),
             "--shell", "/usr/sbin/nologin", "--no-create-home", "--no-log-init", name])
        owner["user_created"] = True
        os.chown(home, account_id, account_id)
        for child in ("runtime", "graph", "runroot", "tmp", "networks"):
            directory = home / child
            directory.mkdir(mode=0o700)
            os.chown(directory, account_id, account_id)
        span = f"{start}-{start + 65535}"
        run(["usermod", "--add-subuids", span, "--add-subgids", span, name])
        owner["mapping_created"] = True
        actual_ids = {entry.pw_uid for entry in pwd.getpwall()} | {entry.gr_gid for entry in grp.getgrall()}
        for file in ("/etc/subuid", "/etc/subgid"):
            validate_allocation(owner, mappings(file), actual_ids)
        run(["ip", "netns", "add", name])
        owner["namespace_created"] = True
        run(["ip", "-n", name, "link", "set", "lo", "up"])
        return owner

    def start_user_manager(self, owner):
        uid = str(owner["uid"])
        unit = "user@" + uid + ".service"
        directory = Path("/run/systemd/system") / (unit + ".d")
        if directory.exists() or subprocess.run(["systemctl", "is-active", "--quiet", unit]).returncode == 0:
            raise RuntimeError("Refusing existing user-manager configuration or running instance")
        directory.mkdir(mode=0o755)
        owner["manager_dropin"] = str(directory)
        (directory / "90-ktng-fixture.conf").write_text(
            "[Service]\nPAMName=\nEnvironment=HOME=" + owner["home"] + "\nEnvironment=XDG_RUNTIME_DIR=/run/user/" + uid +
            "\nEnvironment=DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/" + uid + "/bus\nNetworkNamespacePath=/run/netns/" + owner["name"] +
            "\nMemoryMax=256M\nMemorySwapMax=0\nTasksMax=128\nCPUQuota=100%\nRuntimeMaxSec=120\n")
        run(["systemctl", "daemon-reload"])
        owner["manager_started"] = True
        run(["systemctl", "start", unit])
        runtime = Path("/run/user") / uid
        status = runtime.stat()
        if status.st_uid != owner["uid"] or status.st_mode & 0o777 != 0o700:
            raise RuntimeError("User runtime directory ownership/mode incorrect")
        dependencies = run(["systemctl", "show", "--property=BindsTo", "--property=After", unit])
        if dependencies.count("user-runtime-dir@" + uid + ".service") < 2:
            raise RuntimeError("User manager lacks expected runtime directory dependencies")
        owner["manager_dependencies"] = dependencies
        pid = int(run(["systemctl", "show", "--property=MainPID", "--value", unit]))
        owner["manager_observation"] = {"pid": pid, "netns": os.readlink(f"/proc/{pid}/ns/net")}
        if owner["manager_observation"]["netns"] != f"net:[{os.stat('/run/netns/' + owner['name']).st_ino}]":
            raise RuntimeError("User manager outside controlled network namespace")

    def inspect(self, owner, peer, *, on_event=None):
        home = Path(owner["home"])
        base = ["podman", "--cgroup-manager", self.cgroup_manager, "--root", str(home / "graph"),
                "--runroot", str(home / "runroot"), "--tmpdir", str(home / "tmp"),
                "--network-config-dir", str(home / "networks")]
        unit = ["systemd-run", "--quiet", "--wait", "--pipe", "--collect", "--unit", owner["unit"],
                "--property", "User=" + owner["name"], "--property", "Group=" + owner["name"],
                "--property", "Delegate=yes",
                "--property", "MemoryMax=256M", "--property", "MemorySwapMax=0",
                "--property", "TasksMax=128", "--property", "CPUQuota=100%",
                "--property", "RuntimeMaxSec=75", "--property", "NetworkNamespacePath=/run/netns/" + owner["name"],
                "--property", "WorkingDirectory=" + owner["home"],
                "--property", "UnsetEnvironment=CONTAINER_HOST CONTAINER_CONNECTION DOCKER_HOST DBUS_SESSION_BUS_ADDRESS REGISTRY_AUTH_FILE CONTAINERS_STORAGE_CONF HTTP_PROXY HTTPS_PROXY ALL_PROXY NO_PROXY http_proxy https_proxy all_proxy no_proxy",
                "--setenv", "HOME=" + owner["home"], "--setenv", "XDG_RUNTIME_DIR=" + str(home / "runtime"),
                "--setenv", "XDG_CONFIG_HOME=" + str(home / ".config"),
                sys.executable, "-c", self.inspect_program, peer["home"], str(getattr(self, "archive", "-")), owner["name"]] + base
        if self.managed_user_session:
            self.start_user_manager(owner)
            runtime = "/run/user/" + str(owner["uid"])
            environment = ["HOME=" + owner["home"], "XDG_RUNTIME_DIR=" + runtime,
                           "DBUS_SESSION_BUS_ADDRESS=unix:path=" + runtime + "/bus"]
            user_command = ["systemd-run", "--user", "--quiet", "--wait", "--pipe", "--collect", "--unit", owner["unit"],
                            "--property", "Delegate=yes", "--property", "WorkingDirectory=" + owner["home"],
                            "--property", "UnsetEnvironment=CONTAINER_HOST CONTAINER_CONNECTION DOCKER_HOST REGISTRY_AUTH_FILE CONTAINERS_STORAGE_CONF HTTP_PROXY HTTPS_PROXY ALL_PROXY NO_PROXY http_proxy https_proxy all_proxy no_proxy",
                            "--setenv", "HOME=" + owner["home"], "--setenv", "XDG_RUNTIME_DIR=" + runtime,
                            "--setenv", "DBUS_SESSION_BUS_ADDRESS=unix:path=" + runtime + "/bus",
                            "--setenv", "XDG_CONFIG_HOME=" + str(home / ".config"),
                            sys.executable, "-c", self.inspect_program, peer["home"], str(getattr(self, "archive", "-")), owner["name"]] + base
            unit = ["nsenter", "--net=/run/netns/" + owner["name"], "setpriv", "--reuid=" + str(owner["uid"]), "--regid=" + str(owner["uid"]),
                    "--init-groups", "env", "-i", "PATH=/usr/sbin:/usr/bin:/sbin:/bin"] + environment + user_command
        process = subprocess.Popen(unit, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        deadline = time.monotonic() + 85
        handled_events = set()
        try:
            while True:
                control = home / "control.json"
                if control.exists():
                    try:
                        event = json.loads(control.read_text())
                    except (FileNotFoundError, json.JSONDecodeError):
                        event = None
                    if event and event.get("phase") not in handled_events:
                        if on_event is None:
                            raise RuntimeError("Unexpected fixture control event")
                        on_event(event)
                        handled_events.add(event["phase"])
                        (home / "control-ack").write_text(event["phase"])
                marker = home / "held.json"
                if marker.exists() and "held_processes" not in owner:
                    try:
                        held = json.loads(marker.read_text())
                        inventory = []
                        for proc in Path("/proc").glob("[0-9]*"):
                            try:
                                uid_line = next(line for line in (proc / "status").read_text().splitlines() if line.startswith("Uid:"))
                                uid = int(uid_line.split()[1])
                                if uid != owner["uid"] and not owner["subid_start"] <= uid < owner["subid_start"] + 65536:
                                    continue
                                inventory.append({"pid": int(proc.name), "uid": uid,
                                    "comm": (proc / "comm").read_text().strip(), "cgroup": (proc / "cgroup").read_text().strip(),
                                    "netns": os.readlink(proc / "ns/net")})
                            except (FileNotFoundError, ProcessLookupError):
                                continue
                        owner["held_processes"] = inventory
                        owner["held_workload"] = held
                    except (FileNotFoundError, json.JSONDecodeError):
                        pass
                try:
                    stdout, stderr = process.communicate(timeout=0.1)
                    break
                except subprocess.TimeoutExpired:
                    if time.monotonic() > deadline:
                        raise RuntimeError("Managed runtime inspection timed out")
            if process.returncode:
                raise RuntimeError(f"Managed runtime inspection failed ({process.returncode}): {stderr}")
            result = json.loads(stdout)
        finally:
            if process.poll() is None:
                process.kill()
                try:
                    process.communicate(timeout=2)
                except subprocess.TimeoutExpired:
                    process.stdout.close()
                    process.stderr.close()
                    process.wait(timeout=2)
        if hasattr(self, "archive"):
            held = owner.get("held_workload")
            inventory = owner.get("held_processes", [])
            if not held or not any(item["pid"] == held["pid"] for item in inventory):
                raise RuntimeError("Held workload process inventory missing")
            outer_namespace = f"net:[{os.stat('/run/netns/' + owner['name']).st_ino}]"
            for item in inventory:
                expected_namespace = held["netns"] if item["pid"] == held["pid"] else outer_namespace
                if item["netns"] != expected_namespace:
                    raise RuntimeError("Held runtime support process outside registered namespace: " + item["comm"])
        owner["observation"] = {"outer": result["outer"], "inner": result["inner"],
                                "rootless": result["podman"]["host"]["security"]["rootless"],
                                "cgroup_manager": result["podman"]["host"]["cgroupManager"],
                                "resources": result.get("resources")}
        expected = os.stat("/run/netns/" + owner["name"]).st_ino
        if result["outer"]["euid"] != owner["uid"] or result["outer"]["netns"] != f"net:[{expected}]":
            raise RuntimeError("Managed unit did not use its registered UID and namespace")
        if not result["outer"]["peer_home_access_denied"]:
            raise RuntimeError("Runtime owner can read the other owner's private home")
        if result["inner"]["euid"] != 0 or result["inner"]["netns"] != result["outer"]["netns"]:
            raise RuntimeError("Podman unshare escaped its registered outer network namespace")
        validate_effective_map(result["inner"]["uid_map"], owner)
        validate_effective_map(result["inner"]["gid_map"], owner)
        if result["podman"]["host"]["security"]["rootless"] is not True:
            raise RuntimeError("Podman is not rootless")
        if Path(result["podman"]["store"]["graphRoot"]).resolve() != home / "graph":
            raise RuntimeError("Podman selected another storage root")
        effective = result["podman"]["host"]["cgroupManager"]
        if effective != self.cgroup_manager:
            raise RuntimeError(f"Requested {self.cgroup_manager} but observed {effective}; no silent fallback")
        if self.managed_user_session:
            observed = []
            for proc in Path("/proc").glob("[0-9]*"):
                try:
                    status = (proc / "status").read_text()
                    uid_line = next(line for line in status.splitlines() if line.startswith("Uid:"))
                    if int(uid_line.split()[1]) != owner["uid"]:
                        continue
                    comm = (proc / "comm").read_text().strip()
                    ns = os.readlink(proc / "ns/net")
                    observed.append({"pid": int(proc.name), "comm": comm, "netns": ns})
                    if ns != result["outer"]["netns"]:
                        raise RuntimeError("Runtime-owner process outside registered outer namespace: " + comm)
                except (FileNotFoundError, ProcessLookupError):
                    continue
            owner["session_processes"] = observed
            if not any(process["comm"].startswith("dbus-broker") for process in observed):
                raise RuntimeError("Session bus broker namespace was not observed")
        if "resources" in result:
            validate_resources(result["resources"])
        return {"acquisition": result.get("acquisition"), "network": result.get("network"), "resources": result.get("resources"), "role": owner["name"].rsplit("-", 1)[1], "uid": owner["uid"],
                "subid_start": owner["subid_start"], "requested_cgroup_manager": self.cgroup_manager,
                "effective_cgroup_manager": effective, "outer": result["outer"], "inner": result["inner"],
                "podman_version": result["podman"]["version"]["Version"], "warnings": result["warnings"]}

    def cleanup(self):
        failures = []
        for owner in reversed(self.owners):
            try:
                # Units are fixture-named; never stop an existing application unit.
                subprocess.run(["systemctl", "stop", owner["unit"]], capture_output=True, timeout=10)
                if owner.get("manager_started"):
                    run(["systemctl", "stop", "user@" + str(owner["uid"]) + ".service", "user-runtime-dir@" + str(owner["uid"]) + ".service"])
                if owner.get("manager_dropin"):
                    directory = Path(owner["manager_dropin"])
                    if directory != Path("/run/systemd/system") / ("user@" + str(owner["uid"]) + ".service.d"):
                        raise RuntimeError("Unexpected user-manager drop-in cleanup path")
                    (directory / "90-ktng-fixture.conf").unlink()
                    directory.rmdir()
                    run(["systemctl", "daemon-reload"])
                if owner["user_created"]:
                    identity = pwd.getpwnam(owner["name"])
                    if identity.pw_uid != owner["uid"] or identity.pw_dir != owner["home"]:
                        raise RuntimeError("Account identity changed; refusing cleanup")
                    survivors = []
                    for status in Path("/proc").glob("[0-9]*/status"):
                        try:
                            line = next(line for line in status.read_text().splitlines() if line.startswith("Uid:"))
                            identities = [int(value) for value in line.split()[1:]]
                            if any(identity == owner["uid"] or owner["subid_start"] <= identity < owner["subid_start"] + 65536 for identity in identities):
                                survivors.append(status.parent.name)
                        except (OSError, StopIteration):
                            continue
                    if survivors:
                        raise RuntimeError("Fixture-owned processes survive unit stop: " + ",".join(survivors))
                    if owner["mapping_created"]:
                        span = f"{owner['subid_start']}-{owner['subid_start'] + 65535}"
                        run(["usermod", "--del-subuids", span, "--del-subgids", span, owner["name"]])
                    run(["userdel", owner["name"]])
                if owner["group_created"]:
                    try:
                        grp.getgrnam(owner["name"])
                    except KeyError:
                        pass
                    else:
                        if grp.getgrnam(owner["name"]).gr_gid != owner["uid"]:
                            raise RuntimeError("Group identity changed; refusing cleanup")
                        run(["groupdel", owner["name"]])
                if owner["namespace_created"]:
                    run(["ip", "netns", "delete", owner["name"]])
            except (OSError, RuntimeError, subprocess.SubprocessError) as error:
                failures.append(f"cleanup {owner['name']}: {error}")
        if not failures:
            resolved = self.root.resolve()
            if resolved.parent != Path("/var/tmp") or not resolved.name.startswith("ktng-owners-"):
                failures.append("Refusing unexpected cleanup root")
            else:
                try:
                    shutil.rmtree(resolved)
                except OSError as error:
                    failures.append(f"cleanup root: {error}")
        return failures


def validate_resources(resources):
    for mode in ("limits", "pids", "cpu"):
        if resources[mode]["exit_code"] != 0 or "cleanup_error" in resources[mode]:
            raise RuntimeError(f"Container {mode} enforcement probe failed: {resources[mode]}")
    limits = json.loads(resources["limits"]["stdout"])
    if limits != {"uid": 65532, "memory_max": "33554432", "pids_max": "16", "cpu_max": "25000 100000"}:
        raise RuntimeError(f"Unexpected kernel resource limits: {limits}")
    memory = resources["memory"]
    if memory["exit_code"] != 137 or memory.get("state", {}).get("OOMKilled") is not True or "cleanup_error" in memory:
        raise RuntimeError(f"Container memory limit did not produce a confirmed OOM kill: {memory}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--cgroup-manager", choices=("systemd", "cgroupfs"), required=True)
    parser.add_argument("--workload-source", type=Path, help="Optional offline C workload; requires gcc and ldd")
    parser.add_argument("--managed-user-session", action="store_true", help="Test entire managed user@ instance inside its namespace; systemd cgroups only")
    options = parser.parse_args()
    if options.managed_user_session and options.cgroup_manager != "systemd":
        parser.error("managed user session requires explicit systemd cgroup manager")
    if sys.platform != "linux" or os.geteuid() != 0:
        parser.error("requires Linux root; no resources created")
    required = ("podman", "ip", "systemd-run", "systemctl", "useradd", "userdel", "groupadd", "groupdel", "usermod")
    if options.managed_user_session:
        required += ("setpriv", "env", "nsenter")
    if any(shutil.which(command) is None for command in required):
        parser.error("missing required runtime/account tools; no resources created")
    if options.workload_source and (not options.workload_source.is_file() or any(shutil.which(tool) is None for tool in ("gcc", "ldd"))):
        parser.error("workload source, gcc and ldd required; no resources created")
    with options.report.open("x", encoding="utf-8") as report:
        result = {"schemaVersion": 1, "scope": "managed-rootless-resource-feasibility" if options.workload_source else "managed-rootless-identity-feasibility-only",
                  "gate1Passed": False, "feasibilityPassed": False, "owners": [], "cleanupErrors": [],
                  "limitations": ["No networking helper placement, signed image acquisition, or external traffic containment proof. Resource-limit evidence requires the optional workload to pass."]}
        if options.managed_user_session:
            result["limitations"].append("Managed non-login user-manager candidate: PAM security/session parity on native Linux remains unvalidated. Process snapshots do not prove placement of every short-lived helper.")
        try:
            with fixture_lock():
                fixture = RuntimeOwners(options.cgroup_manager, options.managed_user_session)
                result["createdResources"] = fixture.owners
                result["fixtureRoot"] = str(fixture.root)
                try:
                    if options.workload_source:
                        result["workload"] = fixture.build_workload(options.workload_source)
                    core, work = [fixture.create(role) for role in ("core", "work")]
                    result["owners"].append(fixture.inspect(core, work))
                    result["owners"].append(fixture.inspect(work, core))
                    result["feasibilityPassed"] = True
                finally:
                    result["cleanupErrors"] = fixture.cleanup()
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
