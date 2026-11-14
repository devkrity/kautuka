#!/usr/bin/env python3
"""
Kautuka OCI Runtime Hook v7
Enforces network access control boundaries on container creation.
Implements Sections 4.3.2 and 4.3.3 of Kautuka NAC v7 specification.
"""
import hashlib
import json
import ipaddress
import os
import stat
import subprocess
import sys
import tempfile
import threading
import time

try:
    import fcntl
except ImportError:
    fcntl = None

_MODULE_THREAD_LOCK = threading.Lock()
HOOK_VERSION = "7.0.0"


def get_xdg_state_home():
    base = os.environ.get("XDG_STATE_HOME")
    if not base:
        base = os.path.expanduser("~/.local/state")
    return os.path.join(base, "kautuka", "nac")


STATE_DIR = get_xdg_state_home()
HOOK_LOG_FILE = os.path.join(STATE_DIR, "hook.log")
ALLOWLIST_FILE = os.path.join(STATE_DIR, "infra-allowlist.json")
OWNERS_FILE = os.path.join(STATE_DIR, "netns-owners.json")
OWNERS_LOCK_FILE = os.path.join(STATE_DIR, "netns-owners.lock")


def get_pid_start_ticks(pid):
    """
    Reads start_ticks (field 22) from /proc/<pid>/stat when available.
    Returns int start_ticks or None.
    """
    try:
        with open(f"/proc/{pid}/stat", "r", encoding="utf-8") as f:
            data = f.read()
        rparen = data.rfind(")")
        if rparen != -1:
            rest = data[rparen + 2:].split()
            if len(rest) > 19:
                return int(rest[19])
    except Exception:
        pass
    return None


def is_owner_live(entry):
    """
    Checks if an owner entry is still live.
    An entry is live only while /proc/<pid>/ns/net still has that inode
    and /proc/<pid>/stat has the same start_ticks.
    """
    if not isinstance(entry, dict):
        return False
    pid = entry.get("pid")
    expected_inode = entry.get("inode")
    expected_ticks = entry.get("start_ticks")
    if not pid or not isinstance(pid, int) or pid <= 0:
        return False
    try:
        st_net = os.stat(f"/proc/{pid}/ns/net")
        if expected_inode is not None and st_net.st_ino != int(expected_inode):
            return False
        if expected_ticks is not None:
            ticks = get_pid_start_ticks(pid)
            if ticks is None or ticks != int(expected_ticks):
                return False
        return True
    except Exception:
        if not os.path.exists("/proc"):
            if sys.platform == "win32":
                # Never call os.kill(pid, 0) on Windows: signal 0 maps to CTRL_C_EVENT / process termination.
                try:
                    import ctypes
                    kernel32 = ctypes.windll.kernel32
                    PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
                    STILL_ACTIVE = 259
                    handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, int(pid))
                    if not handle:
                        return False
                    try:
                        exit_code = ctypes.c_uint(0)
                        if kernel32.GetExitCodeProcess(handle, ctypes.byref(exit_code)) == 0:
                            return False
                        return exit_code.value == STILL_ACTIVE
                    finally:
                        kernel32.CloseHandle(handle)
                except Exception:
                    return False
            try:
                os.kill(pid, 0)
                return True
            except Exception:
                return False
        return False


def read_owners_file(owners_path=OWNERS_FILE):
    """
    Reads and validates netns-owners.json.
    Returns (owners_dict, owners_valid, owners_reason).
    owners_dict maps str(inode) -> entry dict.
    If the file exists and has a JSON parse error, unreadable permissions,
    or wrong mode (st_mode & 0o077 != 0 or st_uid != os.geteuid()),
    returns ({}, False, "owners_unreadable").
    """
    if not os.path.exists(owners_path):
        return {}, True, None

    try:
        st = os.stat(owners_path)
        if hasattr(os, "getuid"):
            if (st.st_mode & 0o077) != 0:
                return {}, False, "owners_unreadable"
            if st.st_uid != os.geteuid():
                return {}, False, "owners_unreadable"

        raw = None
        for attempt in range(5):
            try:
                with open(owners_path, "r", encoding="utf-8") as f:
                    raw = f.read()
                break
            except PermissionError:
                if attempt == 4:
                    raise
                time.sleep(0.01)

        if not raw or not raw.strip():
            return {}, True, None
        data = json.loads(raw)

        owners = {}
        if isinstance(data, list):
            for entry in data:
                if isinstance(entry, dict):
                    ino = entry.get("inode")
                    if ino is not None:
                        owners[str(ino)] = entry
        elif isinstance(data, dict):
            for ino, val in data.items():
                if isinstance(val, dict):
                    entry = dict(val)
                    if "inode" not in entry:
                        entry["inode"] = int(ino) if str(ino).isdigit() else ino
                    owners[str(ino)] = entry
                else:
                    owners[str(ino)] = {
                        "inode": int(ino) if str(ino).isdigit() else ino,
                        "id": val,
                        "pid": None,
                        "start_ticks": None,
                    }
        else:
            return {}, False, "owners_unreadable"

        live_owners = {}
        for ino, entry in owners.items():
            pid = entry.get("pid")
            if pid and is_owner_live(entry):
                live_owners[ino] = entry

        return live_owners, True, None
    except Exception:
        return {}, False, "owners_unreadable"


def write_owners_file(new_owners, owners_path=OWNERS_FILE, lock_path=OWNERS_LOCK_FILE):
    """
    Reads and writes netns-owners.json under an exclusive flock on lock_path
    and replaces via temp file + os.replace.
    """
    os.makedirs(os.path.dirname(owners_path), exist_ok=True)
    lock_fd = None
    lock_acquired_thread = False
    try:
        if fcntl:
            lock_fd = open(lock_path, "a+")
            fcntl.flock(lock_fd.fileno(), fcntl.LOCK_EX)
        else:
            _MODULE_THREAD_LOCK.acquire()
            lock_acquired_thread = True

        existing_owners, valid, _ = read_owners_file(owners_path)
        if valid:
            existing_owners.update(new_owners)
            merged = existing_owners
        else:
            merged = new_owners

        entries = list(merged.values())

        temp_dir = os.path.dirname(owners_path)
        fd, temp_path = tempfile.mkstemp(dir=temp_dir, prefix="owners-", suffix=".tmp")
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            json.dump(entries, f, indent=2)
            f.flush()
            os.fsync(f.fileno())

        if hasattr(os, "chmod") and hasattr(os, "getuid"):
            os.chmod(temp_path, 0o600)

        for attempt in range(5):
            try:
                os.replace(temp_path, owners_path)
                break
            except PermissionError:
                if attempt == 4:
                    raise
                time.sleep(0.01)
    finally:
        if lock_fd:
            try:
                if fcntl:
                    fcntl.flock(lock_fd.fileno(), fcntl.LOCK_UN)
            finally:
                lock_fd.close()
        if lock_acquired_thread:
            _MODULE_THREAD_LOCK.release()


def read_allowlist_file(allowlist_path=ALLOWLIST_FILE):
    """
    Reads infra-allowlist.json.
    Returns (allowlist_map, allowlist_valid, allowlist_reason).
    Supports both {"version": 1, "entries": [...]} and dict-by-id formats.
    """
    if not os.path.exists(allowlist_path):
        return {}, False, "missing_allowlist"

    try:
        st = os.stat(allowlist_path)
        if hasattr(os, "getuid"):
            if (st.st_mode & 0o077) != 0:
                return {}, False, "bad_allowlist_mode"
            if st.st_uid != os.geteuid():
                return {}, False, "bad_allowlist_owner"

        with open(allowlist_path, "r", encoding="utf-8") as f:
            data = json.load(f)

        allowlist = {}
        if isinstance(data, dict):
            if "entries" in data and isinstance(data["entries"], list):
                for entry in data["entries"]:
                    if isinstance(entry, dict) and "id" in entry:
                        allowlist[entry["id"]] = entry
            else:
                for k, v in data.items():
                    if isinstance(v, dict):
                        allowlist[k] = v
                    elif isinstance(v, list):
                        allowlist[k] = {"expected_ipv4": v}
                    else:
                        allowlist[k] = {"expected_ipv4": []}
        elif isinstance(data, list):
            for entry in data:
                if isinstance(entry, dict) and "id" in entry:
                    allowlist[entry["id"]] = entry
        else:
            return {}, False, "bad_allowlist_format"

        return allowlist, True, None
    except Exception as ex:
        return {}, False, f"allowlist_read_error: {ex}"


def decide(state, ips, allowlist, owners, netns_inode="100", allowlist_valid=True, allowlist_reason=None, owners_valid=True, owners_reason=None):
    """
    Pure decision function unit-testable without Podman.
    Returns (action, reason) where action in ('allow', 'refuse').
    """
    if not state or not isinstance(state, dict):
        return ("refuse", "missing_pid")

    pid = state.get("pid")
    if pid is None or not isinstance(pid, (int, float)) or int(pid) <= 0:
        return ("refuse", "missing_pid")

    if ips is None or not isinstance(ips, list) or len(ips) == 0:
        return ("refuse", "no_ip")

    cid = state.get("id", "")
    annotations = state.get("annotations", {}) or {}

    in_nac = False
    for ip in ips:
        if isinstance(ip, str) and (ip.startswith("10.247.") or ip == "10.247"):
            in_nac = True
            break

    # MF9: Container not on NAC network
    if not in_nac:
        if annotations.get("io.kautuka.role") == "workload":
            if not owners_valid:
                return ("refuse", owners_reason or "owners_unreadable")
            return ("refuse", "workload_off_network")
        return ("allow", "outside_boundary")

    # In NAC: Allowlist validation check
    if not allowlist_valid:
        return ("refuse", allowlist_reason or "missing_allowlist")

    # Normalize allowlist format
    normalized_allowlist = {}
    if allowlist is not None:
        if isinstance(allowlist, dict):
            if "entries" in allowlist and isinstance(allowlist["entries"], list):
                for entry in allowlist["entries"]:
                    if isinstance(entry, dict) and "id" in entry:
                        normalized_allowlist[entry["id"]] = entry
            else:
                for k, v in allowlist.items():
                    if isinstance(v, dict):
                        normalized_allowlist[k] = v
                    elif isinstance(v, list):
                        normalized_allowlist[k] = {"expected_ipv4": v}
        elif isinstance(allowlist, list):
            for entry in allowlist:
                if isinstance(entry, dict) and "id" in entry:
                    normalized_allowlist[entry["id"]] = entry

    if cid in normalized_allowlist:
        entry = normalized_allowlist[cid]
        expected = entry.get("expected_ipv4", [])
        if (not isinstance(expected, list) or not expected
                or not all(_is_ipv4(ip) for ip in expected)
                or len(set(expected)) != len(expected)):
            return ("refuse", "infra_ip_mismatch")
        if not all(_is_ipv4(ip) for ip in ips) or len(set(ips)) != len(ips):
            return ("refuse", "infra_ip_mismatch")
        accepted_sets = [expected]
        if "expected_ipv4_alternates" in entry:
            alternates = entry["expected_ipv4_alternates"]
            # Only the boundary init container may have the known restart-time
            # address added by its own init command. Keep every accepted state
            # explicit and exact; malformed alternate data fails closed.
            if (entry.get("role") != "egress-boundary"
                    or not isinstance(alternates, list)
                    or len(alternates) != 1
                    or set(expected) != {"10.247.0.2", "10.248.0.2"}
                    or len(expected) != 2):
                return ("refuse", "infra_ip_mismatch")
            for alternate in alternates:
                if (not isinstance(alternate, list) or not alternate
                        or not all(_is_ipv4(ip) for ip in alternate)
                        or len(set(alternate)) != len(alternate)
                        or set(alternate) != set(expected) | {"10.248.0.3"}
                        or len(alternate) != len(expected) + 1):
                    return ("refuse", "infra_ip_mismatch")
                accepted_sets.append(alternate)
        if any(set(ips) == set(candidate) and len(ips) == len(candidate) for candidate in accepted_sets):
            return ("allow", "infra_allowlisted")
        else:
            return ("refuse", "infra_ip_mismatch")

    # MF8: Corrupt or unreadable owners file refuses every NAC container
    if not owners_valid:
        return ("refuse", owners_reason or "owners_unreadable")

    # Step 6: In NAC and not allowlisted
    # 1. Reserved range 10.247.0.0/24
    for ip in ips:
        if isinstance(ip, str):
            parts = ip.split(".")
            if len(parts) == 4 and parts[0] == "10" and parts[1] == "247" and parts[2] == "0":
                return ("refuse", "reserved_range")

    # 2. More than 1 IPv4 or outside 10.247.1.0 - 10.247.255.254
    if len(ips) != 1:
        return ("refuse", "bad_workload_ip")

    ip = ips[0]
    if not isinstance(ip, str):
        return ("refuse", "bad_workload_ip")
    parts = ip.split(".")
    if len(parts) != 4:
        return ("refuse", "bad_workload_ip")
    try:
        p0, p1, p2, p3 = int(parts[0]), int(parts[1]), int(parts[2]), int(parts[3])
    except ValueError:
        return ("refuse", "bad_workload_ip")

    if p0 != 10 or p1 != 247:
        return ("refuse", "bad_workload_ip")
    if p2 < 1 or p2 > 255:
        return ("refuse", "bad_workload_ip")
    if p3 < 0 or p3 > 254:
        return ("refuse", "bad_workload_ip")

    # 3. Shared netns check
    if netns_inode is not None and owners is not None:
        sinode = str(netns_inode)
        norm_owners = {}
        if isinstance(owners, dict):
            for k, v in owners.items():
                if isinstance(v, dict):
                    norm_owners[str(k)] = v.get("id") or v.get("cid")
                else:
                    norm_owners[str(k)] = v
        elif isinstance(owners, list):
            for entry in owners:
                if isinstance(entry, dict) and "inode" in entry:
                    norm_owners[str(entry["inode"])] = entry.get("id") or entry.get("cid")

        if sinode in norm_owners:
            owner_cid = norm_owners[sinode]
            if owner_cid != cid:
                return ("refuse", "shared_netns")

    return ("allow", "workload_rules")


def _is_ipv4(value):
    if not isinstance(value, str):
        return False
    try:
        return isinstance(ipaddress.ip_address(value), ipaddress.IPv4Address)
    except ValueError:
        return False


def canonicalize_nft_json(nft_json_data, required_comments=None, required_workload_ip=None):
    """
    Strips transient runtime state (handle, metainfo, dynamic counter packet/byte counts),
    verifies that all required comment tags and the workload IP are present,
    and returns (canonical_json_string, canonical_hash).
    """
    if isinstance(nft_json_data, str):
        root = json.loads(nft_json_data)
    else:
        root = nft_json_data

    if isinstance(root, dict) and "nftables" in root:
        nftables_list = root["nftables"]
    elif isinstance(root, list):
        nftables_list = root
    else:
        raise ValueError("Unexpected nftables JSON format")

    found_comments = set()
    found_workload_ip = [False]

    def _canonicalize_item(item):
        if isinstance(item, dict):
            if "metainfo" in item:
                return None
            res = {}
            for k in sorted(item.keys()):
                if k == "handle":
                    continue
                v = item[k]
                if k == "comment" and isinstance(v, str):
                    found_comments.add(v.strip())
                if k == "counter":
                    res[k] = {"packets": 0, "bytes": 0}
                    continue
                if isinstance(v, str) and required_workload_ip and required_workload_ip in v:
                    found_workload_ip[0] = True
                canon_v = _canonicalize_item(v)
                res[k] = canon_v
            return res
        elif isinstance(item, list):
            res = []
            for elem in item:
                canon_elem = _canonicalize_item(elem)
                if canon_elem is not None:
                    res.append(canon_elem)
            return res
        else:
            if isinstance(item, str) and required_workload_ip and required_workload_ip in item:
                found_workload_ip[0] = True
            return item

    canonical_list = []
    for elem in nftables_list:
        c = _canonicalize_item(elem)
        if c is not None:
            canonical_list.append(c)

    if required_comments:
        for req in required_comments:
            if req not in found_comments:
                raise ValueError(f"Missing required comment tag '{req}'")

    if required_workload_ip and not found_workload_ip[0]:
        raise ValueError(f"Workload IP '{required_workload_ip}' not found in ruleset")

    canonical_json = json.dumps(canonical_list, sort_keys=True, separators=(",", ":"))
    canonical_hash = hashlib.sha256(canonical_json.encode("utf-8")).hexdigest()
    return canonical_json, canonical_hash


def expected_rendered_ruleset_hash(workload_ip, required_comments):
    """Hash the independent rendered nft JSON golden for the workload address."""
    template_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "kautuka_workload.nft.json.tmpl")
    with open(template_path, "r", encoding="utf-8") as template_file:
        template = template_file.read().replace("{{IP}}", workload_ip)
    _, expected_hash = canonicalize_nft_json(
        template,
        required_comments=required_comments,
        required_workload_ip=workload_ip,
    )
    return expected_hash


def render_ruleset(ip, include_pkgs_proxy=False):
    """
    Renders idempotent, atomic table inet kautuka_workload ruleset (Section 4.3.2).
    """
    pkg_rule = "    ip daddr 10.247.0.3 tcp dport 20101 accept\n" if include_pkgs_proxy else ""
    return f"""table inet kautuka_workload
delete table inet kautuka_workload
table inet kautuka_workload {{
  chain input {{ type filter hook input priority 0; policy drop;
    iifname "lo" accept
    ct state established,related accept
    counter drop comment "input_drop"
  }}
  chain output {{ type filter hook output priority 0; policy drop;
    oifname "lo" accept
    meta nfproto ipv6 counter drop comment "ipv6_drop"
    oifname != "lo" ip saddr != {ip} counter drop comment "spoof_drop"
    ip daddr 10.247.0.2 tcp dport 15001 accept
    ip daddr 10.247.0.2 meta l4proto {{ udp, tcp }} th dport 53 accept
{pkg_rule}    ip daddr 10.247.0.0/16 counter drop comment "wl_net_drop"
    ip daddr {{ 169.254.0.0/16, 168.63.129.16 }} counter drop comment "meta_drop"
    meta l4proto udp counter drop comment "udp_drop"
    meta l4proto tcp accept
    counter drop comment "final_drop"
  }}
}}
"""


def log_hook(ts_ns, dur_ms, cid, pid, ips, inode, action, reason, rules_sha, annotations):
    try:
        os.makedirs(STATE_DIR, exist_ok=True)
        rec = {
            "ts_ns": ts_ns,
            "duration_ms": round(dur_ms, 3),
            "id": cid,
            "pid": pid,
            "hook_version": HOOK_VERSION,
            "ips": ips,
            "netns_inode": inode,
            "action": action,
            "reason": reason,
            "rules_sha256": rules_sha,
            "annotations": annotations or {},
        }
        with open(HOOK_LOG_FILE, "a", encoding="utf-8") as f:
            f.write(json.dumps(rec) + "\n")
    except Exception:
        pass


def main():
    t_start = time.perf_counter_ns()
    ts_ns = time.time_ns()
    cid = ""
    pid = 0
    annotations = {}
    ips = []
    inode = None
    rules_sha = ""

    try:
        try:
            raw_in = sys.stdin.read()
            state = json.loads(raw_in) if raw_in.strip() else {}
        except Exception:
            state = {}

        cid = state.get("id", "")
        pid = state.get("pid", 0)
        annotations = state.get("annotations", {}) or {}

        if not pid or pid <= 0:
            dur_ms = (time.perf_counter_ns() - t_start) / 1_000_000.0
            log_hook(ts_ns, dur_ms, cid, pid, ips, inode, "refuse", "missing_pid", rules_sha, annotations)
            sys.exit(1)

        try:
            cmd = ["nsenter", "-t", str(pid), "-n", "ip", "-j", "-4", "addr", "show"]
            res = subprocess.run(cmd, capture_output=True, text=True, check=True)
            addr_data = json.loads(res.stdout)
            for iface in addr_data:
                for a in iface.get("addr_info", []):
                    local = a.get("local")
                    if local and local != "127.0.0.1":
                        ips.append(local)
        except Exception:
            ips = []

        if not ips:
            dur_ms = (time.perf_counter_ns() - t_start) / 1_000_000.0
            log_hook(ts_ns, dur_ms, cid, pid, ips, inode, "refuse", "no_ip", rules_sha, annotations)
            sys.exit(1)

        try:
            st = os.stat(f"/proc/{pid}/ns/net")
            inode = st.st_ino
        except Exception:
            inode = None

        allowlist, allowlist_valid, allowlist_reason = read_allowlist_file(ALLOWLIST_FILE)
        owners, owners_valid, owners_reason = read_owners_file(OWNERS_FILE)

        action, reason = decide(
            state,
            ips,
            allowlist,
            owners,
            netns_inode=inode,
            allowlist_valid=allowlist_valid,
            allowlist_reason=allowlist_reason,
            owners_valid=owners_valid,
            owners_reason=owners_reason,
        )

        if action == "refuse":
            dur_ms = (time.perf_counter_ns() - t_start) / 1_000_000.0
            log_hook(ts_ns, dur_ms, cid, pid, ips, inode, "refuse", reason, rules_sha, annotations)
            sys.exit(1)

        if reason in ("infra_allowlisted", "outside_boundary"):
            dur_ms = (time.perf_counter_ns() - t_start) / 1_000_000.0
            log_hook(ts_ns, dur_ms, cid, pid, ips, inode, "allow", reason, rules_sha, annotations)
            sys.exit(0)

        workload_ip = ips[0]
        rules_text = render_ruleset(workload_ip, include_pkgs_proxy=False)
        rules_sha = hashlib.sha256(rules_text.encode("utf-8")).hexdigest()

        apply_cmd = ["nsenter", "-t", str(pid), "-n", "nft", "-f", "-"]
        p = subprocess.run(apply_cmd, input=rules_text, capture_output=True, text=True)
        if p.returncode != 0:
            dur_ms = (time.perf_counter_ns() - t_start) / 1_000_000.0
            log_hook(ts_ns, dur_ms, cid, pid, ips, inode, "refuse", "apply_failed", rules_sha, annotations)
            sys.exit(1)

        verify_cmd = ["nsenter", "-t", str(pid), "-n", "nft", "-j", "list", "table", "inet", "kautuka_workload"]
        vp = subprocess.run(verify_cmd, capture_output=True, text=True)
        if vp.returncode != 0:
            dur_ms = (time.perf_counter_ns() - t_start) / 1_000_000.0
            log_hook(ts_ns, dur_ms, cid, pid, ips, inode, "refuse", "apply_failed", rules_sha, annotations)
            sys.exit(1)

        expected_comments = [
            "input_drop",
            "ipv6_drop",
            "spoof_drop",
            "wl_net_drop",
            "meta_drop",
            "udp_drop",
            "final_drop",
        ]

        try:
            _, canonical_hash = canonicalize_nft_json(
                vp.stdout,
                required_comments=expected_comments,
                required_workload_ip=workload_ip,
            )
            expected_hash = expected_rendered_ruleset_hash(workload_ip, expected_comments)
            if canonical_hash != expected_hash:
                raise ValueError("Applied workload ruleset does not match the rendered ruleset")
            rules_sha = canonical_hash
        except Exception:
            dur_ms = (time.perf_counter_ns() - t_start) / 1_000_000.0
            log_hook(ts_ns, dur_ms, cid, pid, ips, inode, "refuse", "apply_failed", rules_sha, annotations)
            sys.exit(1)

        if inode is not None:
            ticks = get_pid_start_ticks(pid)
            owner_entry = {
                "inode": int(inode) if str(inode).isdigit() else inode,
                "id": cid,
                "pid": pid,
                "start_ticks": ticks,
            }
            try:
                write_owners_file({str(inode): owner_entry})
            except Exception:
                dur_ms = (time.perf_counter_ns() - t_start) / 1_000_000.0
                log_hook(ts_ns, dur_ms, cid, pid, ips, inode, "refuse", "owners_write_failed", rules_sha, annotations)
                sys.exit(1)

        dur_ms = (time.perf_counter_ns() - t_start) / 1_000_000.0
        log_hook(ts_ns, dur_ms, cid, pid, ips, inode, "allow", "workload_rules", rules_sha, annotations)
        sys.exit(0)

    except SystemExit:
        raise
    except BaseException as ex:
        dur_ms = (time.perf_counter_ns() - t_start) / 1_000_000.0
        log_hook(ts_ns, dur_ms, cid, pid, ips, inode, "refuse", f"exception_{type(ex).__name__}", rules_sha, annotations)
        sys.exit(1)


if __name__ == "__main__":
    main()
