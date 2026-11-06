"""Pure allocation/mapping checks. No accounts or privileged resources are created."""

import importlib.util
import json
import copy
from pathlib import Path
import sys
import unittest


owners = None
if sys.platform == "linux":
    spec = importlib.util.spec_from_file_location("runtime_owner_probe", Path(__file__).with_name("probe-runtime-owners.py"))
    owners = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(owners)


@unittest.skipUnless(sys.platform == "linux", "Linux pwd/grp and namespace tooling required")
class RuntimeOwnerSafetyTests(unittest.TestCase):
    def setUp(self):
        self.owner = {"name": "ktng-owned-core", "uid": 1800000000, "subid_start": 131072}
        self.range = (self.owner["name"], 131072, 65536)

    def test_other_owner_overlap_is_rejected_after_allocation(self):
        with self.assertRaises(RuntimeError):
            owners.validate_allocation(self.owner, [self.range, ("other", 150000, 65536)], {1000})

    def test_actual_account_identity_inside_range_is_rejected(self):
        with self.assertRaises(RuntimeError):
            owners.validate_allocation(self.owner, [self.range], {150000})

    def test_unexpected_extra_mapping_is_not_silently_adopted(self):
        with self.assertRaises(RuntimeError):
            owners.validate_allocation(self.owner, [self.range, (self.owner["name"], 300000, 65536)], {1000})

    def test_only_effective_owner_and_reserved_mapping_passes(self):
        owners.validate_effective_map("0 1800000000 1\n1 131072 65536", self.owner)
        with self.assertRaises(RuntimeError):
            owners.validate_effective_map("0 1800000000 1\n1 196608 65536", self.owner)

    def test_resource_validation_requires_kernel_limits_and_confirmed_oom(self):
        resources = {mode: {"exit_code": 0} for mode in ("limits", "pids", "cpu")}
        resources["limits"]["stdout"] = json.dumps({"uid": 65532, "memory_max": "33554432", "pids_max": "16", "cpu_max": "25000 100000"})
        resources["memory"] = {"exit_code": 137, "state": {"OOMKilled": True}}
        owners.validate_resources(resources)
        for mode, change in (("memory", {"state": {"OOMKilled": False}}), ("pids", {"exit_code": 3}), ("cpu", {"cleanup_error": "failed"}), ("limits", {"stdout": "{}"})):
            invalid = copy.deepcopy(resources)
            invalid[mode].update(change)
            with self.assertRaises(RuntimeError):
                owners.validate_resources(invalid)


if __name__ == "__main__":
    unittest.main()
