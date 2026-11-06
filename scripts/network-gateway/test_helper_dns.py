"""Offline compilation and filesystem-boundary checks for managed DNS configuration."""

import ast
import importlib.util
from pathlib import Path
import socket
import sys
import tempfile
import unittest


class HelperDnsTests(unittest.TestCase):
    def test_embedded_view_and_workload_programs_compile(self):
        source = Path(__file__).with_name("probe-helper-dns.py")
        values = {"owners": type("Owners", (), {"IDENTITY_INSPECT": ""})()}
        for node in ast.parse(source.read_text()).body:
            if isinstance(node, ast.Assign) and isinstance(node.targets[0], ast.Name) and node.targets[0].id in ("VIEW", "INSPECT"):
                value = eval(compile(ast.Expression(node.value), str(source), "eval"), {}, values)
                values[node.targets[0].id] = value
                compile(value, node.targets[0].id, "exec")
        self.assertIn("VIEW", values)
        self.assertIn("INSPECT", values)

    @unittest.skipUnless(sys.platform == "linux", "Linux account helper imports")
    def test_configuration_uses_private_files_and_preserves_host_files(self):
        self.check_configuration(False)

    @unittest.skipUnless(sys.platform == "linux", "Linux account helper imports")
    def test_unregistered_owner_and_nonsocket_or_symlink_canary_are_rejected(self):
        self.check_configuration(True)

    def check_configuration(self, invalid):
        spec = importlib.util.spec_from_file_location("runtime_owners", Path(__file__).with_name("probe-runtime-owners.py"))
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        with tempfile.TemporaryDirectory(prefix="ktng-dns-unit-") as temporary:
            root = Path(temporary)
            fixture = object.__new__(module.RuntimeOwners)
            fixture.root = root
            owner = {"name": "ktng-dns-unit-core"}
            fixture.owners = [owner]
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as canary:
                path = root / "canary.sock"
                canary.bind(str(path))
                if invalid:
                    with self.assertRaises(RuntimeError):
                        fixture.configure_resolver(dict(owner), blocked_socket=path)
                    ordinary = root / "ordinary"; ordinary.write_text("not a socket")
                    with self.assertRaises(RuntimeError):
                        fixture.configure_resolver(owner, blocked_socket=ordinary)
                    link = root / "link"; link.symlink_to(path)
                    with self.assertRaises(RuntimeError):
                        fixture.configure_resolver(owner, blocked_socket=link)
                    return
                paths = ("/etc/resolv.conf", "/etc/nsswitch.conf", "/etc/hosts")
                before = {logical: Path(logical).read_bytes() for logical in paths}
                entries = fixture.configure_resolver(owner, blocked_socket=path)
                self.assertEqual(3, len(entries))
                self.assertEqual(before, {logical: Path(logical).read_bytes() for logical in paths})
                for entry in entries:
                    source = Path(entry["source"])
                    self.assertTrue(source.is_relative_to(root))
                    self.assertFalse(source.is_symlink())
                    self.assertEqual(0o444, source.stat().st_mode & 0o777)
                    self.assertEqual(str(Path(entry["logical"]).resolve()), entry["target"])
                contents = {entry["logical"]: Path(entry["source"]).read_text() for entry in entries}
                self.assertIn("hosts: files dns", contents["/etc/nsswitch.conf"])
                self.assertNotIn("ktng.test", contents["/etc/hosts"])
                self.assertEqual(str(path), owner["blocked_host_sockets"][0])


if __name__ == "__main__":
    unittest.main()
