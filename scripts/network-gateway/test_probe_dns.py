"""Offline peer-vector checks and Linux cleanup failure injection for the DNS fixture."""

import ast
import importlib.util
from pathlib import Path
import subprocess
import sys
import unittest
from types import SimpleNamespace
from unittest.mock import Mock


source = Path(__file__).with_name("probe-dns.py")
programs = {}
for node in ast.parse(source.read_text()).body:
    if isinstance(node, ast.Assign) and isinstance(node.targets[0], ast.Name) and node.targets[0].id in ("WIRE", "CLIENT", "UPSTREAM"):
        if isinstance(node.value, ast.Constant):
            value = node.value.value
        else:
            value = programs[node.value.left.id] + node.value.right.value
        programs[node.targets[0].id] = value


class DnsPeerTests(unittest.TestCase):
    def test_all_embedded_peer_programs_compile(self):
        self.assertEqual({"WIRE", "CLIENT", "UPSTREAM"}, set(programs))
        for name, program in programs.items():
            compile(program, name, "exec")

    def test_peer_encodes_and_decodes_an_independent_question_vector(self):
        wire = {}
        exec(compile(programs["WIRE"], "WIRE", "exec"), wire)
        vector = bytes.fromhex("123401000001000000000000016104746573740000010001")
        self.assertEqual(vector, wire["query"](0x1234, "a.test", 1))
        self.assertEqual((0x1234, "a.test", 1), wire["question"](vector))
        reply = wire["response"](vector)
        self.assertEqual(bytes.fromhex("123481800001000100000000"), reply[:12])
        self.assertEqual(bytes.fromhex("0ae7fc02"), reply[-4:])
        self.assertEqual(bytes.fromhex("1235"), wire["response"](vector, True)[:2])

    @unittest.skipUnless(sys.platform == "linux", "Linux account helper imports")
    def test_process_cleanup_failure_does_not_skip_the_other_process(self):
        spec = importlib.util.spec_from_file_location("dns_probe", source)
        probe = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(probe)
        fixture = object.__new__(probe.DnsProbe)
        fixture.process_diagnostics = []
        fixture.topology = SimpleNamespace(consumer=None)
        fixture.capture_process = None
        fixture.resolver_process = Mock()
        fixture.resolver_process.poll.return_value = None
        fixture.resolver_process.communicate.side_effect = subprocess.TimeoutExpired("resolver", 5)
        fixture.resolver_process.kill.side_effect = OSError("injected kill failure")
        fixture.upstream_process = Mock()
        fixture.upstream_process.poll.return_value = None
        fixture.upstream_process.communicate.return_value = ("", "")
        errors = fixture.stop()
        self.assertEqual(1, len(errors))
        self.assertIn("injected kill failure", errors[0])
        fixture.upstream_process.terminate.assert_called_once()
        fixture.upstream_process.communicate.assert_called_once_with(timeout=5)


if __name__ == "__main__":
    unittest.main()
