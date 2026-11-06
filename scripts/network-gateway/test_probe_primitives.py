"""Unprivileged failure-injection tests for the isolated probe's resource ownership."""

import ast
import importlib.util
from pathlib import Path
import subprocess
import unittest
from unittest.mock import Mock, patch


spec = importlib.util.spec_from_file_location("primitive_probe", Path(__file__).with_name("probe-primitives.py"))
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


class ProbeCleanupTests(unittest.TestCase):
    def test_embedded_peer_programs_compile(self):
        tree = ast.parse(Path(probe.__file__).read_text(encoding="utf-8"))
        programs = [node.value for node in ast.walk(tree)
                    if isinstance(node, ast.Constant) and isinstance(node.value, str) and node.value.startswith("import ")]
        self.assertGreaterEqual(len(programs), 3)
        for program in programs:
            compile(program, "embedded-fixture-peer", "exec")

    def test_cleanup_continues_after_timeout_and_records_failure(self):
        topology = probe.Topology()
        topology.created = ["owned-first", "owned-second", "owned-third"]
        with patch.object(probe.subprocess, "run", side_effect=[
            subprocess.TimeoutExpired("ip", 5),
            OSError("injected failure"),
            subprocess.CompletedProcess([], 0, "", ""),
        ]) as run:
            failures = topology.cleanup()
        self.assertEqual(3, run.call_count)
        self.assertEqual(2, len(failures))
        self.assertEqual(["ip", "netns", "delete", "owned-first"], run.call_args_list[-1].args[0])

    def test_failed_namespace_creation_never_claims_preexisting_resource(self):
        topology = probe.Topology()
        topology.run = Mock(side_effect=["", probe.ProbeFailure("namespace already exists")])
        with self.assertRaises(probe.ProbeFailure):
            topology.create()
        self.assertEqual([topology.names["src"]], topology.created)
        with patch.object(probe.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "", "")) as run:
            topology.cleanup()
        run.assert_called_once()
        self.assertEqual(["ip", "netns", "delete", topology.names["src"]], run.call_args.args[0])

    def test_echo_cleanup_error_does_not_skip_namespace_cleanup(self):
        topology = probe.Topology()
        topology.created = ["owned"]
        topology.echo = Mock()
        topology.echo.communicate.side_effect = [subprocess.TimeoutExpired("echo", 3), OSError("reap failed")]
        with patch.object(probe.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "", "")) as run:
            failures = topology.cleanup()
        topology.echo.kill.assert_called_once()
        run.assert_called_once()
        self.assertTrue(any("reap failed" in error for error in failures))


if __name__ == "__main__":
    unittest.main()
