"""Root-side evidence for one held IPv4 pull socket in a disposable namespace."""

import os
from pathlib import Path
import socket


def ipv4_endpoint(value):
    address, port = value.split(":")
    if len(address) != 8 or len(port) != 4:
        raise RuntimeError("Malformed fixture TCP endpoint")
    return socket.inet_ntoa(bytes.fromhex(address)[::-1]), int(port, 16)


def find_socket(table, peer, uid):
    matches = []
    for line in table.splitlines()[1:]:
        fields = line.split()
        if len(fields) < 10:
            raise RuntimeError("Malformed managed-namespace TCP table")
        local = ipv4_endpoint(fields[1])
        remote = ipv4_endpoint(fields[2])
        if local == tuple(peer) and remote == ("10.231.252.2", 20443) and fields[3] == "01":
            if int(fields[7]) != uid or int(fields[9]) <= 0:
                raise RuntimeError("Held pull socket ownership/inode mismatch")
            matches.append({"local": list(local), "remote": list(remote), "inode": int(fields[9]), "uid": uid})
    if len(matches) != 1:
        raise RuntimeError("Expected exactly one held registry socket in the managed namespace")
    return matches[0]


def observe(owner, namespace, peer, table):
    held = find_socket(table, peer, owner["uid"])
    inventory = []
    owners = []
    target = "socket:[" + str(held["inode"]) + "]"
    for proc in Path("/proc").glob("[0-9]*"):
        try:
            uid_line = next(line for line in (proc / "status").read_text().splitlines() if line.startswith("Uid:"))
            identities = [int(value) for value in uid_line.split()[1:]]
            if not any(identity == owner["uid"] or owner["subid_start"] <= identity < owner["subid_start"] + 65536 for identity in identities):
                continue
            item = {"pid": int(proc.name), "uids": identities, "comm": (proc / "comm").read_text().strip(),
                    "cgroup": (proc / "cgroup").read_text().strip(), "netns": os.readlink(proc / "ns/net"), "socket_fds": []}
            for descriptor in (proc / "fd").iterdir():
                try:
                    if os.readlink(descriptor) == target:
                        item["socket_fds"].append(int(descriptor.name))
                except (FileNotFoundError, ProcessLookupError):
                    continue
            inventory.append(item)
            if item["netns"] != namespace:
                raise RuntimeError("Active-pull participant outside managed namespace: " + item["comm"])
            if item["socket_fds"]:
                owners.append(item)
        except (FileNotFoundError, ProcessLookupError):
            continue
    expected = "0::/user.slice/user-" + str(owner["uid"]) + ".slice/user@" + str(owner["uid"]) + ".service/"
    if len(owners) != 1 or owners[0]["comm"] != "podman" or not owners[0]["cgroup"].startswith(expected):
        raise RuntimeError("Held registry socket is not owned by the managed Podman process")
    return {"socket": held, "socket_owner": owners[0], "processes": inventory, "managed_namespace": namespace}
