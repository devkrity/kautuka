#!/usr/bin/env python3
"""Isolated Gate 1 rootless execution probe, not an installer or containment claim.

Creates two disposable accounts, disjoint subordinate mappings, private storage and
network namespaces. Runs only Podman info/unshare under systemd-managed units.
No image acquisition, real-uplink route, existing account, or existing store is used.
"""

import argparse
from contextlib import contextmanager
import fcntl
import grp
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
import uuid


INSPECT = r"""import json,os,subprocess,sys
def identity():
    return {'euid':os.geteuid(),'netns':os.readlink('/proc/self/ns/net'),'uid_map':open('/proc/self/uid_map').read().strip(),'gid_map':open('/proc/self/gid_map').read().strip(),'cgroup':open('/proc/self/cgroup').read().strip()}
peer_home=sys.argv[1]
podman=sys.argv[2:]
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
print(json.dumps({'outer':outer,'podman':json.loads(info.stdout),'inner':json.loads(inner.stdout),'warnings':info.stderr}),flush=True)
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
    def __init__(self, cgroup_manager):
        self.prefix = "ktng-" + uuid.uuid4().hex[:8]
        self.root = Path(tempfile.mkdtemp(prefix="ktng-owners-", dir="/var/tmp")).resolve()
        self.root.chmod(0o711)
        self.cgroup_manager = cgroup_manager
        self.owners = []

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

    def inspect(self, owner, peer):
        home = Path(owner["home"])
        base = ["podman", "--cgroup-manager", self.cgroup_manager, "--root", str(home / "graph"),
                "--runroot", str(home / "runroot"), "--tmpdir", str(home / "tmp"),
                "--network-config-dir", str(home / "networks")]
        unit = ["systemd-run", "--quiet", "--wait", "--pipe", "--collect", "--unit", owner["unit"],
                "--property", "User=" + owner["name"], "--property", "Group=" + owner["name"],
                "--property", "Delegate=yes", "--property", "NetworkNamespacePath=/run/netns/" + owner["name"],
                "--property", "WorkingDirectory=" + owner["home"],
                "--property", "UnsetEnvironment=CONTAINER_HOST CONTAINER_CONNECTION DOCKER_HOST DBUS_SESSION_BUS_ADDRESS REGISTRY_AUTH_FILE CONTAINERS_STORAGE_CONF",
                "--setenv", "HOME=" + owner["home"], "--setenv", "XDG_RUNTIME_DIR=" + str(home / "runtime"),
                "--setenv", "XDG_CONFIG_HOME=" + str(home / ".config"),
                sys.executable, "-c", INSPECT, peer["home"]] + base
        result = json.loads(run(unit, timeout=50))
        owner["observation"] = {"outer": result["outer"], "inner": result["inner"],
                                "rootless": result["podman"]["host"]["security"]["rootless"],
                                "cgroup_manager": result["podman"]["host"]["cgroupManager"]}
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
        return {"role": owner["name"].rsplit("-", 1)[1], "uid": owner["uid"],
                "subid_start": owner["subid_start"], "requested_cgroup_manager": self.cgroup_manager,
                "effective_cgroup_manager": effective, "outer": result["outer"], "inner": result["inner"],
                "podman_version": result["podman"]["version"]["Version"], "warnings": result["warnings"]}

    def cleanup(self):
        failures = []
        for owner in reversed(self.owners):
            try:
                # Units are fixture-named; never stop an existing application unit.
                subprocess.run(["systemctl", "stop", owner["unit"]], capture_output=True, timeout=10)
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--cgroup-manager", choices=("systemd", "cgroupfs"), required=True)
    options = parser.parse_args()
    if sys.platform != "linux" or os.geteuid() != 0:
        parser.error("requires Linux root; no resources created")
    required = ("podman", "ip", "systemd-run", "systemctl", "useradd", "userdel", "groupadd", "groupdel", "usermod")
    if any(shutil.which(command) is None for command in required):
        parser.error("missing required runtime/account tools; no resources created")
    with options.report.open("x", encoding="utf-8") as report:
        result = {"schemaVersion": 1, "scope": "managed-rootless-identity-feasibility-only",
                  "gate1Passed": False, "feasibilityPassed": False, "owners": [], "cleanupErrors": [],
                  "limitations": ["No networking helper placement, image pull, container cgroup/resource-limit, or external traffic containment proof."]}
        try:
            with fixture_lock():
                fixture = RuntimeOwners(options.cgroup_manager)
                result["createdResources"] = fixture.owners
                result["fixtureRoot"] = str(fixture.root)
                try:
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
