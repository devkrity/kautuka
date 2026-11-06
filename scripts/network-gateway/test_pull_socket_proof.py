"""Unprivileged parsing/ownership checks for captured fixture socket tables."""

import importlib.util
from pathlib import Path
import unittest


spec = importlib.util.spec_from_file_location("pull_socket_proof", Path(__file__).with_name("pull-socket-proof.py"))
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)


class PullSocketTests(unittest.TestCase):
    row = "0: 02FBE70A:C350 02FCE70A:4FDB 01 00000000:00000000 00:00000000 00000000 1800000000 0 42 1"

    def test_observed_tuple_and_uid_select_the_exact_socket_inode(self):
        observed = proof.find_socket("header\n" + self.row, ["10.231.251.2", 50000], 1800000000)
        self.assertEqual(42, observed["inode"])
        self.assertEqual(["10.231.252.2", 20443], observed["remote"])

    def test_wrong_uid_cannot_claim_the_socket(self):
        with self.assertRaises(RuntimeError):
            proof.find_socket("header\n" + self.row, ["10.231.251.2", 50000], 1800000001)

    def test_missing_or_ambiguous_socket_is_rejected(self):
        for table in ("header\n", "header\n" + self.row + "\n" + self.row):
            with self.assertRaises(RuntimeError):
                proof.find_socket(table, ["10.231.251.2", 50000], 1800000000)

    def test_truncated_rows_fail_instead_of_guessing(self):
        with self.assertRaises(RuntimeError):
            proof.find_socket("header\n0: truncated", ["10.231.251.2", 50000], 1800000000)


if __name__ == "__main__":
    unittest.main()
