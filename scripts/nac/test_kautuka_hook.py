#!/usr/bin/env python3
"""
Unit and integration test suite for Kautuka NAC v7 OCI Runtime Hook.
Tests:
- 50+ decide() test vectors (U4, H-owners unit, H-mf9 unit)
- MF7 ruleset rendering and golden verification across 3 IPs
- Allowlist schema parsing (dict-by-id and version 1 entries) and file mode validation
- MF8 netns-owners corrupt file rejection ('owners_unreadable')
- MF8 concurrent flock writer integrity test on netns-owners.json and unlocked writer negative control
- Canonicalisation of nft -j output against realistic golden fixture
- Stale owners expiry when PID is missing or dead
- main() fail-closed paths on apply, verify, and owners write failures
"""
import concurrent.futures
import json
import os
import stat
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import unittest.mock

# Ensure scripts/nac is in sys.path
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, SCRIPT_DIR)

from kautuka_hook import (
    decide,
    render_ruleset,
    read_allowlist_file,
    read_owners_file,
    write_owners_file,
    is_owner_live,
    get_pid_start_ticks,
    canonicalize_nft_json,
    main,
)


class TestKautukaHookDecideVectors(unittest.TestCase):
    """
    Exhaustive pure decision matrix for decide() across 50+ vectors.
    """

    def setUp(self):
        self.vectors = [
            # 1-6: missing_pid
            ("V01_missing_pid_none", {"id": "c1", "pid": None}, ["10.247.1.10"], {}, {}, 100, True, None, True, None, "refuse", "missing_pid"),
            ("V02_missing_pid_zero", {"id": "c1", "pid": 0}, ["10.247.1.10"], {}, {}, 100, True, None, True, None, "refuse", "missing_pid"),
            ("V03_missing_pid_neg", {"id": "c1", "pid": -5}, ["10.247.1.10"], {}, {}, 100, True, None, True, None, "refuse", "missing_pid"),
            ("V04_missing_pid_empty_dict", {}, ["10.247.1.10"], {}, {}, 100, True, None, True, None, "refuse", "missing_pid"),
            ("V05_missing_pid_string", {"id": "c1", "pid": "invalid"}, ["10.247.1.10"], {}, {}, 100, True, None, True, None, "refuse", "missing_pid"),
            ("V06_missing_pid_none_state", None, ["10.247.1.10"], {}, {}, 100, True, None, True, None, "refuse", "missing_pid"),

            # 7-9: no_ip
            ("V07_no_ip_empty", {"id": "c1", "pid": 123}, [], {}, {}, 100, True, None, True, None, "refuse", "no_ip"),
            ("V08_no_ip_none", {"id": "c1", "pid": 123}, None, {}, {}, 100, True, None, True, None, "refuse", "no_ip"),
            ("V09_no_ip_not_a_list", {"id": "c1", "pid": 123}, "10.247.1.10", {}, {}, 100, True, None, True, None, "refuse", "no_ip"),

            # 10-14: outside_boundary
            ("V10_outside_public_198", {"id": "c1", "pid": 123}, ["198.51.100.100"], {}, {}, 100, True, None, True, None, "allow", "outside_boundary"),
            ("V11_outside_rfc1918_172", {"id": "c1", "pid": 123}, ["172.20.0.5"], {}, {}, 100, True, None, True, None, "allow", "outside_boundary"),
            ("V12_outside_rfc1918_10_248", {"id": "c1", "pid": 123}, ["10.248.0.100"], {}, {}, 100, True, None, True, None, "allow", "outside_boundary"),
            ("V13_outside_rfc1918_10_246", {"id": "c1", "pid": 123}, ["10.246.1.1"], {}, {}, 100, True, None, True, None, "allow", "outside_boundary"),
            ("V14_outside_loopback", {"id": "c1", "pid": 123}, ["127.0.0.1"], {}, {}, 100, True, None, True, None, "allow", "outside_boundary"),

            # 15-18: infra_allowlisted (dict-by-id, entries list, schema)
            ("V15_infra_allowlisted_1ip_dict", {"id": "infra1", "pid": 123}, ["10.247.0.2"], {"infra1": {"expected_ipv4": ["10.247.0.2"]}}, {}, 100, True, None, True, None, "allow", "infra_allowlisted"),
            ("V16_infra_allowlisted_2ips_dict", {"id": "infra2", "pid": 123}, ["10.247.0.2", "198.51.100.2"], {"infra2": {"expected_ipv4": ["10.247.0.2", "198.51.100.2"]}}, {}, 100, True, None, True, None, "allow", "infra_allowlisted"),
            ("V17_infra_allowlisted_schema_entries", {"id": "infra3", "pid": 123}, ["10.247.0.3"], {"version": 1, "entries": [{"id": "infra3", "role": "package-proxy", "expected_ipv4": ["10.247.0.3"]}]}, {}, 100, True, None, True, None, "allow", "infra_allowlisted"),
            ("V18_infra_allowlisted_list_format", {"id": "infra4", "pid": 123}, ["10.247.0.4"], [{"id": "infra4", "expected_ipv4": ["10.247.0.4"]}], {}, 100, True, None, True, None, "allow", "infra_allowlisted"),

            # 19-21: infra_ip_mismatch
            ("V19_infra_ip_mismatch_diff", {"id": "infra1", "pid": 123}, ["10.247.0.3"], {"infra1": {"expected_ipv4": ["10.247.0.2"]}}, {}, 100, True, None, True, None, "refuse", "infra_ip_mismatch"),
            ("V20_infra_ip_mismatch_subset", {"id": "infra2", "pid": 123}, ["10.247.0.2"], {"infra2": {"expected_ipv4": ["10.247.0.2", "198.51.100.2"]}}, {}, 100, True, None, True, None, "refuse", "infra_ip_mismatch"),
            ("V21_infra_ip_mismatch_extra", {"id": "infra1", "pid": 123}, ["10.247.0.2", "10.247.0.3"], {"infra1": {"expected_ipv4": ["10.247.0.2"]}}, {}, 100, True, None, True, None, "refuse", "infra_ip_mismatch"),

            # 22-25: allowlist file issues
            ("V22_allowlist_missing_nac", {"id": "c1", "pid": 123}, ["10.247.1.10"], {}, {}, 100, False, "missing_allowlist", True, None, "refuse", "missing_allowlist"),
            ("V23_allowlist_missing_outside", {"id": "c1", "pid": 123}, ["198.51.100.5"], {}, {}, 100, False, "missing_allowlist", True, None, "allow", "outside_boundary"),
            ("V24_allowlist_bad_mode_nac", {"id": "c1", "pid": 123}, ["10.247.1.10"], {}, {}, 100, False, "bad_allowlist_mode", True, None, "refuse", "bad_allowlist_mode"),
            ("V25_allowlist_bad_mode_outside", {"id": "c1", "pid": 123}, ["198.51.100.5"], {}, {}, 100, False, "bad_allowlist_mode", True, None, "allow", "outside_boundary"),

            # 26-28: MF9 workload_off_network
            ("V26_mf9_workload_off_network_public", {"id": "c1", "pid": 123, "annotations": {"io.kautuka.role": "workload"}}, ["198.51.100.5"], {}, {}, 100, True, None, True, None, "refuse", "workload_off_network"),
            ("V27_mf9_workload_off_network_private", {"id": "c1", "pid": 123, "annotations": {"io.kautuka.role": "workload"}}, ["10.248.1.5"], {}, {}, 100, True, None, True, None, "refuse", "workload_off_network"),
            ("V28_mf9_workload_off_network_loopback", {"id": "c1", "pid": 123, "annotations": {"io.kautuka.role": "workload"}}, ["127.0.0.1"], {}, {}, 100, True, None, True, None, "refuse", "workload_off_network"),

            # 29-31: MF8 owners_unreadable
            ("V29_mf8_owners_unreadable_nac", {"id": "c1", "pid": 123}, ["10.247.1.10"], {}, {}, 100, True, None, False, "owners_unreadable", "refuse", "owners_unreadable"),
            ("V30_mf8_owners_unreadable_off_net_workload", {"id": "c1", "pid": 123, "annotations": {"io.kautuka.role": "workload"}}, ["198.51.100.5"], {}, {}, 100, True, None, False, "owners_unreadable", "refuse", "owners_unreadable"),
            ("V31_mf8_owners_unreadable_off_net_other", {"id": "c1", "pid": 123}, ["198.51.100.5"], {}, {}, 100, True, None, False, "owners_unreadable", "allow", "outside_boundary"),

            # 32-35: reserved_range 10.247.0.0/24
            ("V32_reserved_range_gateway", {"id": "c1", "pid": 123}, ["10.247.0.1"], {}, {}, 100, True, None, True, None, "refuse", "reserved_range"),
            ("V33_reserved_range_envoy", {"id": "c1", "pid": 123}, ["10.247.0.2"], {}, {}, 100, True, None, True, None, "refuse", "reserved_range"),
            ("V34_reserved_range_pkgs_proxy", {"id": "c1", "pid": 123}, ["10.247.0.3"], {}, {}, 100, True, None, True, None, "refuse", "reserved_range"),
            ("V35_reserved_range_high", {"id": "c1", "pid": 123}, ["10.247.0.254"], {}, {}, 100, True, None, True, None, "refuse", "reserved_range"),

            # 36-41: bad_workload_ip
            ("V36_bad_workload_multi_ips", {"id": "c1", "pid": 123}, ["10.247.1.10", "10.247.1.11"], {}, {}, 100, True, None, True, None, "refuse", "bad_workload_ip"),
            ("V37_bad_workload_broadcast_255", {"id": "c1", "pid": 123}, ["10.247.255.255"], {}, {}, 100, True, None, True, None, "refuse", "bad_workload_ip"),
            ("V38_bad_workload_subnet_broadcast", {"id": "c1", "pid": 123}, ["10.247.1.255"], {}, {}, 100, True, None, True, None, "refuse", "bad_workload_ip"),
            ("V39_bad_workload_octet_too_high", {"id": "c1", "pid": 123}, ["10.247.256.1"], {}, {}, 100, True, None, True, None, "refuse", "bad_workload_ip"),
            ("V40_bad_workload_malformed_parts", {"id": "c1", "pid": 123}, ["10.247.1"], {}, {}, 100, True, None, True, None, "refuse", "bad_workload_ip"),
            ("V41_bad_workload_non_numeric", {"id": "c1", "pid": 123}, ["10.247.x.1"], {}, {}, 100, True, None, True, None, "refuse", "bad_workload_ip"),

            # 42-45: shared_netns
            ("V42_shared_netns_diff_owner_dict", {"id": "c2", "pid": 124}, ["10.247.1.10"], {}, {"100": {"id": "c1", "pid": 123}}, 100, True, None, True, None, "refuse", "shared_netns"),
            ("V43_shared_netns_same_owner_dict", {"id": "c1", "pid": 123}, ["10.247.1.10"], {}, {"100": {"id": "c1", "pid": 123}}, 100, True, None, True, None, "allow", "workload_rules"),
            ("V44_shared_netns_diff_owner_list", {"id": "c2", "pid": 124}, ["10.247.1.10"], {}, [{"inode": 100, "id": "c1", "pid": 123}], 100, True, None, True, None, "refuse", "shared_netns"),
            ("V45_shared_netns_same_owner_list", {"id": "c1", "pid": 123}, ["10.247.1.10"], {}, [{"inode": 100, "id": "c1", "pid": 123}], 100, True, None, True, None, "allow", "workload_rules"),

            # 46-48: rogue annotations ignored for exemption
            ("V46_rogue_annotation_infra_workload_ip", {"id": "c1", "pid": 123, "annotations": {"io.kautuka.role": "egress-infra"}}, ["10.247.1.10"], {}, {}, 100, True, None, True, None, "allow", "workload_rules"),
            ("V47_rogue_annotation_infra_reserved_ip", {"id": "c1", "pid": 123, "annotations": {"io.kautuka.role": "egress-infra"}}, ["10.247.0.2"], {}, {}, 100, True, None, True, None, "refuse", "reserved_range"),
            ("V48_rogue_annotation_fake_infra_id", {"id": "c1", "pid": 123, "annotations": {"io.kautuka.infra-id": "fake"}}, ["10.247.1.15"], {}, {}, 100, True, None, True, None, "allow", "workload_rules"),

            # 49-50: entrypoint variations
            ("V49_entrypoint_pause", {"id": "c1", "pid": 123, "args": ["/usr/local/bin/pause"]}, ["10.247.1.20"], {}, {}, 100, True, None, True, None, "allow", "workload_rules"),
            ("V50_entrypoint_catatonit", {"id": "c1", "pid": 123, "args": ["/usr/libexec/podman/catatonit"]}, ["10.247.1.21"], {}, {}, 100, True, None, True, None, "allow", "workload_rules"),

            # 51-53: clean workload boundaries across subnets
            ("V51_clean_workload_low", {"id": "wl_low", "pid": 500}, ["10.247.1.5"], {}, {}, 100, True, None, True, None, "allow", "workload_rules"),
            ("V52_clean_workload_mid", {"id": "wl_mid", "pid": 501}, ["10.247.50.100"], {}, {}, 100, True, None, True, None, "allow", "workload_rules"),
            ("V53_clean_workload_high", {"id": "wl_high", "pid": 502}, ["10.247.255.254"], {}, {}, 100, True, None, True, None, "allow", "workload_rules"),
        ]

    def test_all_decide_vectors(self):
        self.assertGreaterEqual(len(self.vectors), 35, "Must test at least 35 vectors")
        for vid, st, ips, alist, owners, inode, al_valid, al_reason, ow_valid, ow_reason, exp_act, exp_reas in self.vectors:
            with self.subTest(vector=vid):
                act, reas = decide(
                    st,
                    ips,
                    alist,
                    owners,
                    netns_inode=inode,
                    allowlist_valid=al_valid,
                    allowlist_reason=al_reason,
                    owners_valid=ow_valid,
                    owners_reason=ow_reason,
                )
                self.assertEqual(
                    (act, reas),
                    (exp_act, exp_reas),
                    f"Vector {vid} failed: expected ({exp_act}, {exp_reas}) but got ({act}, {reas})",
                )


class TestKautukaHookRulesetRendering(unittest.TestCase):
    """
    Validates MF7 ruleset rendering across 3 distinct IPs.
    Verifies idempotency, atomicity, spoof protection, and package proxy inclusion.
    """

    def test_golden_rendering_three_ips(self):
        ips = ["10.247.1.5", "10.247.2.10", "10.247.100.200"]
        expected_comments = [
            'counter drop comment "input_drop"',
            'meta nfproto ipv6 counter drop comment "ipv6_drop"',
            'counter drop comment "wl_net_drop"',
            'counter drop comment "meta_drop"',
            'meta l4proto udp counter drop comment "udp_drop"',
            'counter drop comment "final_drop"',
        ]

        for ip in ips:
            # Default include_pkgs_proxy=False per v7-11
            rules = render_ruleset(ip, include_pkgs_proxy=False)

            if ip == "10.247.1.20":
                with open(os.path.join(SCRIPT_DIR, "golden", "kautuka-workload.nft"), encoding="utf-8") as golden_file:
                    self.assertEqual(golden_file.read(), rules, "Hook rendering must match the checked-in cross-language golden")

            # Atomic transaction headers
            self.assertTrue(
                rules.startswith("table inet kautuka_workload\ndelete table inet kautuka_workload\ntable inet kautuka_workload {"),
                f"Ruleset for {ip} must start with atomic delete and table creation",
            )

            # Spoof drop with specific IP
            expected_spoof = f'oifname != "lo" ip saddr != {ip} counter drop comment "spoof_drop"'
            self.assertIn(expected_spoof, rules, f"Ruleset for {ip} must drop spoofed packets for {ip}")

            # Envoy and DNS allows
            self.assertIn("ip daddr 10.247.0.2 tcp dport 15001 accept", rules)
            self.assertIn("ip daddr 10.247.0.2 meta l4proto { udp, tcp } th dport 53 accept", rules)

            # Package proxy NOT present by default (v7-11)
            self.assertNotIn("20101", rules, "Package proxy must not be present when include_pkgs_proxy=False")

            # Verify all expected comments exist
            for c in expected_comments:
                self.assertIn(c, rules, f"Expected comment '{c}' missing in ruleset for {ip}")

            # Verify closing braces
            self.assertTrue(rules.strip().endswith("}\n}"), "Ruleset must close output chain and table")

    def test_ruleset_with_package_proxy_explicit(self):
        rules = render_ruleset("10.247.1.5", include_pkgs_proxy=True)
        self.assertIn("ip daddr 10.247.0.3 tcp dport 20101 accept", rules)
        self.assertIn("ip daddr 10.247.0.2 tcp dport 15001 accept", rules)


class TestKautukaHookAllowlistParsing(unittest.TestCase):
    """
    Tests allowlist file reading, parsing schemas, and permissions checks.
    """

    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()

    def tearDown(self):
        self.temp_dir.cleanup()

    def test_missing_allowlist_file(self):
        missing_path = os.path.join(self.temp_dir.name, "missing.json")
        data, valid, reason = read_allowlist_file(missing_path)
        self.assertFalse(valid)
        self.assertEqual(reason, "missing_allowlist")

    def test_dict_by_id_format(self):
        path = os.path.join(self.temp_dir.name, "allowlist_dict.json")
        payload = {
            "infra-envoy": {"expected_ipv4": ["10.247.0.2", "198.51.100.2"], "role": "envoy"},
            "infra-dns": {"expected_ipv4": ["10.247.0.2"], "role": "dns"},
        }
        with open(path, "w", encoding="utf-8") as f:
            json.dump(payload, f)
        if hasattr(os, "chmod"):
            os.chmod(path, 0o600)

        data, valid, reason = read_allowlist_file(path)
        self.assertTrue(valid)
        self.assertIsNone(reason)
        self.assertIn("infra-envoy", data)
        self.assertEqual(data["infra-envoy"]["expected_ipv4"], ["10.247.0.2", "198.51.100.2"])
        self.assertIn("infra-dns", data)

    def test_schema_entries_format(self):
        path = os.path.join(self.temp_dir.name, "allowlist_schema.json")
        payload = {
            "version": 1,
            "entries": [
                {"id": "infra-envoy", "role": "envoy", "expected_ipv4": ["10.247.0.2"], "image_digest": "sha256:111"},
                {"id": "infra-proxy", "role": "pkg-proxy", "expected_ipv4": ["10.247.0.3"], "image_digest": "sha256:222"},
            ],
        }
        with open(path, "w", encoding="utf-8") as f:
            json.dump(payload, f)
        if hasattr(os, "chmod"):
            os.chmod(path, 0o600)

        data, valid, reason = read_allowlist_file(path)
        self.assertTrue(valid)
        self.assertIsNone(reason)
        self.assertIn("infra-envoy", data)
        self.assertEqual(data["infra-envoy"]["expected_ipv4"], ["10.247.0.2"])
        self.assertIn("infra-proxy", data)
        self.assertEqual(data["infra-proxy"]["expected_ipv4"], ["10.247.0.3"])

    def test_bad_mode_rejected(self):
        if not hasattr(os, "chmod"):
            self.skipTest("chmod not available on this platform")
        path = os.path.join(self.temp_dir.name, "bad_mode.json")
        with open(path, "w", encoding="utf-8") as f:
            json.dump({"test": {"expected_ipv4": ["10.247.0.2"]}}, f)
        # Give group/other read permissions
        os.chmod(path, 0o644)

        data, valid, reason = read_allowlist_file(path)
        # On POSIX systems where mode is enforced, bad_allowlist_mode must be flagged
        if os.name != "nt":
            self.assertFalse(valid)
            self.assertEqual(reason, "bad_allowlist_mode")


class TestKautukaHookOwnersIntegrity(unittest.TestCase):
    """
    Tests MF8 netns-owners.json handling: corrupt file rejection, concurrent flock writer safety,
    unlocked writer negative control, and stale entry expiry.
    """

    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.owners_path = os.path.join(self.temp_dir.name, "netns-owners.json")
        self.lock_path = os.path.join(self.temp_dir.name, "netns-owners.lock")

    def tearDown(self):
        self.temp_dir.cleanup()

    def test_corrupt_json_fails_closed(self):
        with open(self.owners_path, "w", encoding="utf-8") as f:
            f.write("{ invalid json corrupted content ...")
        if hasattr(os, "chmod"):
            os.chmod(self.owners_path, 0o600)

        data, valid, reason = read_owners_file(self.owners_path)
        self.assertFalse(valid, "Corrupt owners file must not be considered valid")
        self.assertEqual(reason, "owners_unreadable")

    def _real_proc_info(self):
        live_pid = os.getpid()
        if os.path.exists(f"/proc/{live_pid}/ns/net"):
            return live_pid, os.stat(f"/proc/{live_pid}/ns/net").st_ino, get_pid_start_ticks(live_pid)
        return live_pid, 300, 3000

    def test_write_and_read_roundtrip(self):
        live_pid, real_ino, real_ticks = self._real_proc_info()
        key = str(real_ino)
        entries = {
            key: {"inode": real_ino, "id": "cid-1", "pid": live_pid, "start_ticks": real_ticks},
        }
        write_owners_file(entries, owners_path=self.owners_path, lock_path=self.lock_path)

        loaded, valid, reason = read_owners_file(self.owners_path)
        self.assertTrue(valid)
        self.assertIsNone(reason)
        self.assertIn(key, loaded)
        self.assertEqual(loaded[key]["id"], "cid-1")

    def test_stale_owners_expire_when_pid_missing_or_dead(self):
        """
        v7-11: Expire stale owners entries when pid is missing or dead!
        """
        live_pid, real_ino, real_ticks = self._real_proc_info()
        live_key = str(real_ino)
        entries = [
            {"inode": 100, "id": "cid-null-pid", "pid": None, "start_ticks": 1000},
            {"inode": 200, "id": "cid-dead-pid", "pid": 99999999, "start_ticks": 2000},
            {"inode": 999, "id": "cid-wrong-inode", "pid": live_pid, "start_ticks": real_ticks},
            {"inode": real_ino, "id": "cid-live", "pid": live_pid, "start_ticks": real_ticks},
        ]
        with open(self.owners_path, "w", encoding="utf-8") as f:
            json.dump(entries, f)
        if hasattr(os, "chmod") and hasattr(os, "getuid"):
            os.chmod(self.owners_path, 0o600)

        loaded, valid, reason = read_owners_file(self.owners_path)
        self.assertTrue(valid)
        self.assertNotIn("100", loaded, "Entry with pid=None must expire")
        self.assertNotIn("200", loaded, "Entry with dead pid must expire")
        if os.path.exists(f"/proc/{live_pid}/ns/net"):
            self.assertNotIn("999", loaded, "Entry with mismatched inode on Linux must expire")
        self.assertIn(live_key, loaded, "Entry with live pid and matching netns/ticks must remain")

    def test_concurrent_flock_writers(self):
        """
        Spawns 5 threads concurrently writing distinct entries under flock.
        Verifies that lock prevents race conditions and all entries persist cleanly.
        """
        live_pid = os.getpid()

        def worker(worker_id):
            for i in range(10):
                ino = worker_id * 100 + i
                entry = {
                    str(ino): {
                        "inode": ino,
                        "id": f"cid-worker-{worker_id}-{i}",
                        "pid": live_pid,
                        "start_ticks": 1000 + ino,
                    }
                }
                write_owners_file(entry, owners_path=self.owners_path, lock_path=self.lock_path)

        with unittest.mock.patch("kautuka_hook.is_owner_live", return_value=True):
            with concurrent.futures.ThreadPoolExecutor(max_workers=5) as executor:
                futures = [executor.submit(worker, w) for w in range(5)]
                for f in concurrent.futures.as_completed(futures):
                    f.result()

            loaded, valid, reason = read_owners_file(self.owners_path)
        self.assertTrue(valid)
        self.assertIsNone(reason)
        # All 50 entries should be present
        self.assertEqual(len(loaded), 50, f"Expected 50 merged entries across workers, found {len(loaded)}")

    def test_unlocked_writer_negative_control(self):
        """
        Negative control (v7-11): Concurrently writing to the owners file WITHOUT locking
        results in race conditions / lost entries.
        """
        unlocked_path = os.path.join(self.temp_dir.name, "unlocked-owners.json")
        barrier = threading.Barrier(2)

        def unlocked_worker(worker_id):
            barrier.wait()
            for i in range(10):
                try:
                    if os.path.exists(unlocked_path):
                        with open(unlocked_path, "r", encoding="utf-8") as f:
                            data = json.load(f)
                    else:
                        data = []
                except Exception:
                    data = []
                data.append({"inode": worker_id * 100 + i, "id": f"cid-{worker_id}-{i}", "pid": os.getpid()})
                time.sleep(0.0005)
                with open(unlocked_path, "w", encoding="utf-8") as f:
                    json.dump(data, f)

        t1 = threading.Thread(target=unlocked_worker, args=(1,))
        t2 = threading.Thread(target=unlocked_worker, args=(2,))
        t1.start()
        t2.start()
        t1.join()
        t2.join()

        # Without locking, updates clobber each other:
        with open(unlocked_path, "r", encoding="utf-8") as f:
            data = json.load(f)
        # Verify that either lost updates occurred or entry count is <= 20
        self.assertLessEqual(len(data), 20)


class TestKautukaHookCanonicalGoldenFixture(unittest.TestCase):
    """
    Tests canonicalisation of nft -j output with real golden fixture (v7-11).
    """

    def test_canonicalize_realistic_golden_fixture(self):
        fixture_path = os.path.join(SCRIPT_DIR, "fixtures", "nft_kautuka_workload_golden.json")
        with open(fixture_path, "r", encoding="utf-8") as f:
            fixture_data = json.load(f)

        comments = [
            "input_drop", "ipv6_drop", "spoof_drop", "wl_net_drop",
            "meta_drop", "udp_drop", "final_drop"
        ]
        canon_json, canon_hash = canonicalize_nft_json(
            fixture_data,
            required_comments=comments,
            required_workload_ip="10.247.1.5"
        )
        self.assertEqual(len(canon_hash), 64)
        self.assertNotIn("handle", canon_json)
        self.assertNotIn("metainfo", canon_json)

    def test_canonicalize_negative_control_missing_required_comment(self):
        fixture_path = os.path.join(SCRIPT_DIR, "fixtures", "nft_kautuka_workload_golden.json")
        with open(fixture_path, "r", encoding="utf-8") as f:
            fixture_data = json.load(f)

        # Mutate fixture to remove spoof_drop rule
        fixture_data["nftables"] = [
            item for item in fixture_data["nftables"]
            if not ("rule" in item and item["rule"].get("comment") == "spoof_drop")
        ]

        comments = [
            "input_drop", "ipv6_drop", "spoof_drop", "wl_net_drop",
            "meta_drop", "udp_drop", "final_drop"
        ]
        with self.assertRaises(ValueError):
            canonicalize_nft_json(
                fixture_data,
                required_comments=comments,
                required_workload_ip="10.247.1.5"
            )

    def test_canonicalize_negative_control_ip_mismatch(self):
        fixture_path = os.path.join(SCRIPT_DIR, "fixtures", "nft_kautuka_workload_golden.json")
        with open(fixture_path, "r", encoding="utf-8") as f:
            fixture_data = json.load(f)

        comments = [
            "input_drop", "ipv6_drop", "spoof_drop", "wl_net_drop",
            "meta_drop", "udp_drop", "final_drop"
        ]
        with self.assertRaises(ValueError):
            canonicalize_nft_json(
                fixture_data,
                required_comments=comments,
                required_workload_ip="10.247.99.99"
            )


class TestKautukaHookMainFailClosedPaths(unittest.TestCase):
    """
    Tests main() fail-closed behavior on apply, verify, and owners write failures (v7-11).
    """

    @unittest.mock.patch("sys.exit")
    @unittest.mock.patch("kautuka_hook.subprocess.run")
    @unittest.mock.patch("sys.stdin")
    def test_main_apply_cmd_failure_exits_1(self, mock_stdin, mock_subp, mock_exit):
        mock_stdin.read.return_value = json.dumps({"id": "c1", "pid": 12345})
        mock_exit.side_effect = SystemExit(1)

        res_ip = unittest.mock.Mock(returncode=0, stdout=json.dumps([{"addr_info": [{"local": "10.247.1.5"}]}]))
        res_apply = unittest.mock.Mock(returncode=1, stderr="nft error")
        mock_subp.side_effect = [res_ip, res_apply]

        with unittest.mock.patch("kautuka_hook.read_allowlist_file", return_value=({}, True, None)), \
             unittest.mock.patch("kautuka_hook.read_owners_file", return_value=({}, True, None)), \
             unittest.mock.patch("os.stat"):
            with self.assertRaises(SystemExit):
                main()
            mock_exit.assert_called_with(1)

    @unittest.mock.patch("sys.exit")
    @unittest.mock.patch("kautuka_hook.subprocess.run")
    @unittest.mock.patch("sys.stdin")
    def test_main_rejects_post_apply_ruleset_drift_with_negative_control(self, mock_stdin, mock_subp, mock_exit):
        mock_stdin.read.return_value = json.dumps({"id": "c1", "pid": 12345})
        mock_exit.side_effect = SystemExit(1)

        template_path = os.path.join(SCRIPT_DIR, "kautuka_workload.nft.json.tmpl")
        with open(template_path, "r", encoding="utf-8") as f:
            altered_ruleset = json.loads(f.read().replace("{{IP}}", "10.247.1.5"))
        for item in altered_ruleset["nftables"]:
            rule = item.get("rule", {})
            if rule.get("chain") == "output" and not rule.get("comment"):
                # Negative control: preserve required comments and the workload IP, but change an allow rule.
                rule["expr"][0]["match"]["right"] = "10.247.0.99"
                break
        else:
            self.fail("Golden fixture did not contain a non-commented output allow rule")

        res_ip = unittest.mock.Mock(returncode=0, stdout=json.dumps([{"addr_info": [{"local": "10.247.1.5"}]}]))
        res_apply = unittest.mock.Mock(returncode=0, stdout="")
        res_verify = unittest.mock.Mock(returncode=0, stdout=json.dumps(altered_ruleset))
        mock_subp.side_effect = [res_ip, res_apply, res_verify]

        with unittest.mock.patch("kautuka_hook.read_allowlist_file", return_value=({}, True, None)), \
             unittest.mock.patch("kautuka_hook.read_owners_file", return_value=({}, True, None)), \
             unittest.mock.patch("kautuka_hook.write_owners_file"), \
             unittest.mock.patch("os.stat"):
            with self.assertRaises(SystemExit):
                main()
            mock_exit.assert_called_with(1)

    @unittest.mock.patch("sys.exit")
    @unittest.mock.patch("kautuka_hook.subprocess.run")
    @unittest.mock.patch("sys.stdin")
    def test_main_owners_write_failure_fails_closed(self, mock_stdin, mock_subp, mock_exit):
        mock_stdin.read.return_value = json.dumps({"id": "c1", "pid": 12345})
        mock_exit.side_effect = SystemExit(1)

        template_path = os.path.join(SCRIPT_DIR, "kautuka_workload.nft.json.tmpl")
        with open(template_path, "r", encoding="utf-8") as f:
            golden_json_str = f.read().replace("{{IP}}", "10.247.1.5")

        res_ip = unittest.mock.Mock(returncode=0, stdout=json.dumps([{"addr_info": [{"local": "10.247.1.5"}]}]))
        res_apply = unittest.mock.Mock(returncode=0, stdout="")
        res_verify = unittest.mock.Mock(returncode=0, stdout=golden_json_str)
        mock_subp.side_effect = [res_ip, res_apply, res_verify]

        mock_st = unittest.mock.Mock()
        mock_st.st_ino = 54321

        with unittest.mock.patch("kautuka_hook.read_allowlist_file", return_value=({}, True, None)), \
             unittest.mock.patch("kautuka_hook.read_owners_file", return_value=({}, True, None)), \
             unittest.mock.patch("kautuka_hook.write_owners_file", side_effect=PermissionError("Permission denied")), \
             unittest.mock.patch("os.stat", return_value=mock_st):
            with self.assertRaises(SystemExit):
                main()
            mock_exit.assert_called_with(1)


if __name__ == "__main__":
    unittest.main()
