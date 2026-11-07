#!/usr/bin/env python3
"""Focused tests for the database/key snapshot engine."""
import importlib.util
import json
import os
import sqlite3
import signal
import stat
import tempfile
import unittest
from contextlib import closing
from pathlib import Path

MODULE_PATH = Path(__file__).with_name("kautuka-update-recovery.py")
SPEC = importlib.util.spec_from_file_location("update_recovery", MODULE_PATH)
recovery = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(recovery)


class RecoverySnapshotTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / "data"
        self.root.mkdir()
        self.backups = Path(self.temp.name) / "candidate"
        self._make_database(self.root / "kautuka.db", "registry", "wal-committed")
        self._make_database(self.root / "channel-outbox.db", "outbox", "pending-dispatch")
        self._make_database(self.root / "credentialsHost/vault/kautuka.credentials.db", "credentials", None)
        key_root = self.root / "credentialsHost/vault/vault-keys"
        key_root.mkdir(parents=True)
        (key_root / "active").write_text("A1B2C3D4\n", encoding="ascii")
        (key_root / "A1B2C3D4.key").write_bytes(b"k" * 32)
        db = sqlite3.connect(self.root / "credentialsHost/vault/kautuka.credentials.db")
        db.execute("CREATE TABLE user_credentials(encrypted_value TEXT)")
        db.execute("CREATE TABLE system_credentials(encrypted_value TEXT)")
        db.execute("INSERT INTO user_credentials VALUES (?)", ("KAUTUKA-CREDENTIAL-V2:A1B2C3D4:aa:bb:cc",))
        db.commit()
        db.close()
        protected_cert = self.root / "security/channel/channel-private/channel-server.pfx"
        protected_cert.parent.mkdir(parents=True)
        protected_cert.write_bytes(b"channel-server-certificate")
        if os.name != "nt":
            protected_cert.chmod(0o640)

    def tearDown(self):
        self.temp.cleanup()

    @staticmethod
    def _make_database(path, table, value):
        path.parent.mkdir(parents=True, exist_ok=True)
        db = sqlite3.connect(path)
        db.execute("PRAGMA journal_mode=WAL")
        db.execute(f"DROP TABLE IF EXISTS {table}")
        db.execute(f"CREATE TABLE {table}(value TEXT)")
        if value is not None:
            db.execute(f"INSERT INTO {table} VALUES (?)", (value,))
        db.commit()
        db.close()

    def _args(self, **kwargs):
        values = dict(data_root=str(self.root), destination=str(self.backups),
                      snapshot=str(self.backups), operation="op-123",
                      source="0.1.0", target="0.2.0")
        values.update(kwargs)
        return type("Args", (), values)()

    def test_uninitialized_empty_vault_can_be_backed_up(self):
        db = sqlite3.connect(self.root / "credentialsHost/vault/kautuka.credentials.db")
        db.execute("DELETE FROM user_credentials")
        db.commit()
        db.close()
        keys = self.root / "credentialsHost/vault/vault-keys"
        (keys / "active").unlink()
        (keys / "A1B2C3D4.key").unlink()
        recovery.capture(self._args())
        recovery.verify(self._args())
        (keys / "active").write_text("NEWKEY", encoding="ascii")
        (keys / "NEWKEY.key").write_bytes(b"n" * 32)
        recovery.restore(self._args())
        self.assertFalse((keys / "active").exists())
        self.assertFalse((keys / "NEWKEY.key").exists())

    def test_missing_active_marker_with_credentials_is_rejected(self):
        keys = self.root / "credentialsHost/vault/vault-keys"
        (keys / "active").unlink()
        (keys / "A1B2C3D4.key").unlink()
        with self.assertRaises(Exception):
            recovery.capture(self._args())

    def test_missing_active_marker_with_orphaned_key_is_rejected(self):
        (self.root / "credentialsHost/vault/vault-keys/active").unlink()
        with self.assertRaises(Exception):
            recovery.capture(self._args())

    def test_wal_rows_credentials_and_exact_db_restore(self):
        args = self._args()
        recovery.capture(args)
        # Target migration writes a modified WAL database and introduces a DB.
        self._make_database(self.root / "kautuka.db", "registry", "target-migration")
        agent_db = self.root / "agents/new-agent/agent.db"
        self._make_database(agent_db, "agent", "target-only")
        (self.root / "agents/new-agent/files/sentinel.txt").parent.mkdir(parents=True)
        (self.root / "agents/new-agent/files/sentinel.txt").write_text("user file", encoding="utf-8")

        recovery.restore(args)

        db = sqlite3.connect(self.root / "kautuka.db")
        self.assertEqual([("wal-committed",)], db.execute("SELECT value FROM registry").fetchall())
        db.close()
        outbox = sqlite3.connect(self.root / "channel-outbox.db")
        self.assertEqual([("pending-dispatch",)], outbox.execute("SELECT value FROM outbox").fetchall())
        outbox.close()
        self.assertFalse(agent_db.exists())
        self.assertEqual("user file", (self.root / "agents/new-agent/files/sentinel.txt").read_text())
        self.assertTrue((self.root / "credentialsHost/vault/vault-keys/A1B2C3D4.key").exists())
        cert = self.root / "security/channel/channel-private/channel-server.pfx"
        self.assertEqual(b"channel-server-certificate", cert.read_bytes())
        if os.name != "nt":
            self.assertEqual(0o640, cert.stat().st_mode & 0o777)

    def test_capture_reads_committed_rows_from_live_wal(self):
        connection = sqlite3.connect(self.root / "kautuka.db")
        connection.execute("PRAGMA journal_mode=WAL")
        connection.execute("PRAGMA wal_autocheckpoint=0")
        connection.execute("INSERT INTO registry VALUES ('live-wal-row')")
        connection.commit()
        self.assertTrue(Path(str(self.root / "kautuka.db") + "-wal").exists())

        recovery.capture(self._args())
        connection.close()

        snapshot_db = sqlite3.connect(self.backups / "state/kautuka.db")
        rows = snapshot_db.execute("SELECT value FROM registry ORDER BY value").fetchall()
        snapshot_db.close()
        self.assertEqual([("live-wal-row",), ("wal-committed",)], rows)

    def test_container_gateway_state_and_agents_restore_without_touching_user_files(self):
        gateway = self.root / "gateway-data"
        for name in recovery.GATEWAY_DATABASES:
            self._make_database(gateway / name, "source", name)
        (gateway / "kautuka.key").write_bytes(b"source-machine-key")
        agent = gateway / "agents/agent-123/agent.db"
        self._make_database(agent, "source", "agent-before-update")
        user_file = gateway / "agents/agent-123/files/sentinel.txt"
        user_file.parent.mkdir()
        user_file.write_text("user file")
        recovery.capture(self._args())
        for name in recovery.GATEWAY_DATABASES:
            self._make_database(gateway / name, "target", "migration")
        (gateway / "kautuka.key").write_bytes(b"target-machine-key")
        agent.unlink()
        target_agent = gateway / "agents/new-agent/agent.db"
        self._make_database(target_agent, "target", "target-only")
        arbitrary = gateway / "untracked.db"
        self._make_database(arbitrary, "user", "preserve")

        recovery.restore(self._args())

        for name in recovery.GATEWAY_DATABASES:
            with closing(sqlite3.connect(gateway / name)) as db:
                self.assertEqual([(name,)], db.execute("SELECT value FROM source").fetchall())
        with closing(sqlite3.connect(agent)) as db:
            self.assertEqual([("agent-before-update",)], db.execute("SELECT value FROM source").fetchall())
        self.assertFalse(target_agent.exists())
        self.assertEqual(b"source-machine-key", (gateway / "kautuka.key").read_bytes())
        self.assertEqual("user file", user_file.read_text())
        self.assertTrue(arbitrary.exists())

    def test_format3_absent_gateway_entries_remove_only_target_created_supported_state(self):
        recovery.capture(self._args())
        gateway = self.root / "gateway-data"
        self._make_database(gateway / "kautuka.db", "target", "new-layout")
        self._make_database(gateway / "agents/new-agent/agent.db", "target", "new-agent")
        (gateway / "gateway-auth.key").write_bytes(b"target-key")
        sentinel = gateway / "keep.txt"
        sentinel.write_text("user state")
        recovery.restore(self._args())
        self.assertFalse((gateway / "kautuka.db").exists())
        self.assertFalse((gateway / "gateway-auth.key").exists())
        self.assertFalse((gateway / "agents/new-agent/agent.db").exists())
        self.assertEqual("user state", sentinel.read_text())

    def test_format2_recovery_does_not_delete_uncaptured_container_state(self):
        recovery.capture(self._args())
        manifest_path = self.backups / "manifest.json"
        manifest = json.loads(manifest_path.read_text())
        manifest["format"] = 2
        manifest["entries"] = [entry for entry in manifest["entries"]
                               if not entry["path"].startswith(("gateway-data/", "manager/egress/policy/"))]
        manifest_path.write_text(json.dumps(manifest))
        gateway_db = self.root / "gateway-data/kautuka.db"
        self._make_database(gateway_db, "existing", "uncaptured")
        self._make_database(self.root / "kautuka.db", "target", "changed")
        recovery.restore(self._args())
        with closing(sqlite3.connect(gateway_db)) as db:
            self.assertEqual([("uncaptured",)], db.execute("SELECT value FROM existing").fetchall())
        with closing(sqlite3.connect(self.root / "kautuka.db")) as db:
            self.assertEqual([("wal-committed",)], db.execute("SELECT value FROM registry").fetchall())

    def test_format3_requires_explicit_absent_gateway_inventory(self):
        recovery.capture(self._args())
        manifest_path = self.backups / "manifest.json"
        manifest = json.loads(manifest_path.read_text())
        manifest["entries"] = [entry for entry in manifest["entries"]
                               if entry["path"] != "gateway-data/kautuka.db"]
        manifest_path.write_text(json.dumps(manifest))
        with self.assertRaisesRegex(RuntimeError, "inventory is incomplete"):
            recovery.restore(self._args())

    def test_format4_restores_network_policy_and_decisions(self):
        policy = self.root / "manager/egress/policy/state.json"
        policy.parent.mkdir(parents=True)
        original = b'{"schemaVersion":1,"revision":7,"rules":[],"requests":[]}'
        policy.write_bytes(original)
        recovery.capture(self._args())
        policy.write_bytes(b'{"schemaVersion":1,"revision":8,"rules":[],"requests":[]}')
        recovery.restore(self._args())
        self.assertEqual(original, policy.read_bytes())

    def test_format4_absent_policy_removes_target_only_policy(self):
        recovery.capture(self._args())
        policy = self.root / "manager/egress/policy/state.json"
        policy.parent.mkdir(parents=True)
        policy.write_text("target-only-policy")
        recovery.restore(self._args())
        self.assertFalse(policy.exists())

    def test_format3_does_not_modify_uncaptured_network_policy(self):
        recovery.capture(self._args())
        manifest_path = self.backups / "manifest.json"
        manifest = json.loads(manifest_path.read_text())
        manifest["format"] = 3
        manifest["entries"] = [entry for entry in manifest["entries"]
                               if entry["path"] != "manager/egress/policy/state.json"]
        manifest_path.write_text(json.dumps(manifest))
        policy = self.root / "manager/egress/policy/state.json"
        policy.parent.mkdir(parents=True)
        policy.write_text("uncaptured-policy")
        recovery.restore(self._args())
        self.assertEqual("uncaptured-policy", policy.read_text())

    def test_format4_requires_explicit_policy_inventory(self):
        recovery.capture(self._args())
        manifest_path = self.backups / "manifest.json"
        manifest = json.loads(manifest_path.read_text())
        manifest["entries"] = [entry for entry in manifest["entries"]
                               if entry["path"] != "manager/egress/policy/state.json"]
        manifest_path.write_text(json.dumps(manifest))
        with self.assertRaisesRegex(RuntimeError, "inventory is incomplete"):
            recovery.restore(self._args())

    def test_format2_rejects_injected_container_state(self):
        recovery.capture(self._args())
        manifest_path = self.backups / "manifest.json"
        manifest = json.loads(manifest_path.read_text())
        manifest["format"] = 2
        manifest_path.write_text(json.dumps(manifest))
        with self.assertRaisesRegex(RuntimeError, "outside the supported"):
            recovery.verify(self._args())

    @unittest.skipIf(os.name == "nt", "Linux ancestor and ownership checks")
    def test_container_gateway_symlinked_ancestors_fail_closed(self):
        external = Path(self.temp.name) / "external"
        external.mkdir()
        gateway = self.root / "gateway-data"
        gateway.symlink_to(external, target_is_directory=True)
        with self.assertRaisesRegex(RuntimeError, "symbolic|unsupported"):
            recovery.capture(self._args())
        gateway.unlink()
        gateway.mkdir()
        (gateway / "agents").symlink_to(external, target_is_directory=True)
        with self.assertRaisesRegex(RuntimeError, "unsupported agents state root"):
            recovery.capture(self._args())

    @unittest.skipIf(os.name == "nt", "Linux directory metadata checks")
    def test_container_gateway_directory_metadata_restored(self):
        agent = self.root / "gateway-data/agents/agent-123/agent.db"
        self._make_database(agent, "source", "saved")
        directories = [self.root / path for path in
                       ("gateway-data", "gateway-data/agents", "gateway-data/agents/agent-123")]
        for directory, mode in zip(directories, (0o750, 0o710, 0o700)):
            directory.chmod(mode)
        expected = [(directory.stat().st_uid, directory.stat().st_gid,
                     stat.S_IMODE(directory.stat().st_mode)) for directory in directories]
        recovery.capture(self._args())
        for directory in directories:
            directory.chmod(0o777)
        recovery.restore(self._args())
        self.assertEqual(expected, [(directory.stat().st_uid, directory.stat().st_gid,
                                    stat.S_IMODE(directory.stat().st_mode))
                                   for directory in directories])

    def test_restore_requires_exact_operation_identity(self):
        recovery.capture(self._args())
        with self.assertRaisesRegex(RuntimeError, "operationId"):
            recovery.restore(self._args(operation="different-operation"))

    def test_tampered_snapshot_is_rejected(self):
        recovery.capture(self._args())
        db_copy = self.backups / "state/kautuka.db"
        db_copy.write_bytes(db_copy.read_bytes() + b"tampered")
        with self.assertRaisesRegex(RuntimeError, "hash mismatch"):
            recovery.verify(self._args())

    def test_snapshot_keeps_dynamic_agent_and_retired_key_inventory_after_target_deletes_them(self):
        agent_db = self.root / "agents/agent-123/agent.db"
        self._make_database(agent_db, "agent", "source-agent")
        recovery.capture(self._args())
        agent_db.unlink()
        (self.root / "credentialsHost/vault/vault-keys/A1B2C3D4.key").unlink()

        recovery.verify(self._args())
        recovery.restore(self._args())

        restored = sqlite3.connect(agent_db)
        self.assertEqual([("source-agent",)], restored.execute("SELECT value FROM agent").fetchall())
        restored.close()
        self.assertEqual(b"k" * 32, (self.root / "credentialsHost/vault/vault-keys/A1B2C3D4.key").read_bytes())

    def test_snapshot_manifest_must_include_every_fixed_entry_even_if_absent(self):
        recovery.capture(self._args())
        manifest_path = self.backups / "manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest["entries"] = [entry for entry in manifest["entries"] if entry["path"] != "kautuka-usage.db"]
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        with self.assertRaisesRegex(RuntimeError, "inventory is incomplete"):
            recovery.verify(self._args())

    def test_manifest_binding_detects_removed_dynamic_agent_entry(self):
        agent_db = self.root / "agents/agent-123/agent.db"
        self._make_database(agent_db, "agent", "source-agent")
        recovery.capture(self._args())
        manifest_path = self.backups / "manifest.json"
        manifest_digest = recovery.digest(manifest_path)
        recovery.verify(self._args(manifest_sha256=manifest_digest))
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest["entries"] = [entry for entry in manifest["entries"] if entry["path"] != "agents/agent-123/agent.db"]
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        with self.assertRaisesRegex(RuntimeError, "durable journal binding"):
            recovery.verify(self._args(manifest_sha256=manifest_digest))

    def test_incomplete_manifest_cannot_delete_new_live_file_during_restore(self):
        recovery.capture(self._args())
        created_after_capture = self.root / "kautuka-usage.db"
        self._make_database(created_after_capture, "usage", "new-data")
        manifest_path = self.backups / "manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest["entries"] = [entry for entry in manifest["entries"] if entry["path"] != "kautuka-usage.db"]
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

        with self.assertRaisesRegex(RuntimeError, "inventory is incomplete"):
            recovery.restore(self._args())
        check = sqlite3.connect(created_after_capture)
        self.assertEqual([("new-data",)], check.execute("SELECT value FROM usage").fetchall())
        check.close()

    def test_snapshot_directory_symlink_is_rejected(self):
        recovery.capture(self._args())
        external = Path(self.temp.name) / "external"
        external.mkdir()
        original = self.backups
        self.backups = Path(self.temp.name) / "linked"
        self.backups.symlink_to(external, target_is_directory=True)
        with self.assertRaisesRegex(RuntimeError, "real directory"):
            recovery.verify(self._args(snapshot=self.backups))
        self.backups = original

    @unittest.skipIf(os.name == "nt", "dirfd symlink attack test requires Linux")
    def test_restore_rejects_preplanted_dangling_temporary_symlink(self):
        recovery.capture(self._args())
        target = self.root / "kautuka.db"
        protected = Path(self.temp.name) / "root-only"
        protected.mkdir()
        external = protected / "new-secret"
        temporary = target.with_name(".kautuka.db.restore-" + "a" * 32)
        temporary.symlink_to(external)
        original_token_hex = recovery.secrets.token_hex
        recovery.secrets.token_hex = lambda _count: "a" * 32
        try:
            with self.assertRaises(FileExistsError):
                recovery.restore(self._args())
        finally:
            recovery.secrets.token_hex = original_token_hex
        self.assertFalse(external.exists(), "restore must not create a file through a dangling symlink")
        self.assertFalse(temporary.is_symlink(), "rejected attacker staging entry must be cleaned up")

    @unittest.skipIf(os.name == "nt", "dirfd symlink attack test requires Linux")
    def test_restore_rejects_symlinked_destination_ancestor(self):
        agent_db = self.root / "agents/agent-123/agent.db"
        self._make_database(agent_db, "agent", "source-agent")
        recovery.capture(self._args())
        external = Path(self.temp.name) / "outside"
        external.mkdir()
        (self.root / "agents").rename(self.root / "agents-original")
        (self.root / "agents").symlink_to(external, target_is_directory=True)
        with self.assertRaisesRegex(RuntimeError, "symbolic|unsafe|not a directory|unsupported agents state root|refusing symlinked"):
            recovery.restore(self._args())
        self.assertFalse((external / "agent-123/agent.db").exists())

    @unittest.skipIf(os.name == "nt", "dirfd restore locking is Linux-only")
    def test_restore_keeps_destination_ancestors_nonwritable_during_atomic_replace(self):
        recovery.capture(self._args())
        observed = []
        original_replace = recovery.os.replace

        def check_locked_replace(*args, **kwargs):
            if kwargs.get("dst_dir_fd") is not None:
                for directory in (self.root, self.root / "credentialsHost",
                                  self.root / "credentialsHost/vault",
                                  self.root / "credentialsHost/vault/vault-keys"):
                    self.assertEqual(0, directory.stat().st_mode & 0o222,
                                     f"ancestor was writable during restore: {directory}")
                observed.append(True)
            return original_replace(*args, **kwargs)

        recovery.os.replace = check_locked_replace
        try:
            recovery.restore(self._args())
        finally:
            recovery.os.replace = original_replace
        self.assertTrue(observed)
        self.assertNotEqual(0, (self.root / "credentialsHost/vault/vault-keys").stat().st_mode & 0o222,
                            "captured directory permissions must be restored after the protected window")

    @unittest.skipIf(os.name == "nt" or os.geteuid() != 0, "ancestor-swap test requires Linux root")
    def test_unprivileged_ancestor_swap_attempt_fails_during_restore(self):
        import pwd
        import grp

        agent_directory = self.root / "agents/agent-123"
        agent_db = agent_directory / "agent.db"
        self._make_database(agent_db, "agent", "source-agent")
        nobody = pwd.getpwnam("nobody")
        self.root.parent.chmod(0o755)
        (self.root / "agents").chmod(0o770)
        agent_directory.chmod(0o770)
        os.chown(self.root / "agents", nobody.pw_uid, nobody.pw_gid)
        os.chown(agent_directory, nobody.pw_uid, nobody.pw_gid)
        recovery.capture(self._args())
        outside = Path(self.temp.name) / "outside"
        outside.mkdir(mode=0o770)
        os.chown(outside, nobody.pw_uid, nobody.pw_gid)
        attempts = []
        original_replace = recovery.os.replace

        def attempt_swap(*args, **kwargs):
            if kwargs.get("dst_dir_fd") is not None and not attempts:
                pid = os.fork()
                if pid == 0:
                    try:
                        os.setgid(nobody.pw_gid)
                        os.setuid(nobody.pw_uid)
                        os.rename(agent_directory, outside / "agent-123")
                    except PermissionError:
                        os._exit(1)
                    except BaseException:
                        os._exit(2)
                    os._exit(0)
                _, status = os.waitpid(pid, 0)
                attempts.append(os.waitstatus_to_exitcode(status))
            return original_replace(*args, **kwargs)

        recovery.os.replace = attempt_swap
        try:
            recovery.restore(self._args())
        finally:
            recovery.os.replace = original_replace
        self.assertEqual([1], attempts, "service-user ancestor rename must be denied while restore is pinned")
        self.assertTrue(agent_directory.is_dir())
        self.assertFalse((outside / "agent-123").exists())

    @unittest.skipIf(os.name == "nt" or os.geteuid() != 0, "crash replay test requires Linux root")
    def test_sigkill_during_restore_replays_durable_nested_directory_metadata(self):
        import pwd

        agent_directory = self.root / "agents/agent-123"
        self._make_database(agent_directory / "agent.db", "agent", "source-agent")
        nobody = pwd.getpwnam("nobody")
        os.chown(self.root / "agents", nobody.pw_uid, nobody.pw_gid)
        os.chown(agent_directory, nobody.pw_uid, nobody.pw_gid)
        recovery.capture(self._args())
        manifest = json.loads((self.backups / "manifest.json").read_text(encoding="utf-8"))
        expected_directories = {item["path"]: item for item in manifest["directories"]}
        for directory in expected_directories:
            path = self.root if directory == "." else self.root / directory
            os.chmod(path, stat.S_IMODE(path.stat().st_mode) | 0o077)

        lock_journal = Path(self.temp.name) / "restore-metadata.json"
        pid = os.fork()
        if pid == 0:
            original_replace = recovery.os.replace

            def kill_after_live_replacement(*args, **kwargs):
                result = original_replace(*args, **kwargs)
                if kwargs.get("dst_dir_fd") is not None:
                    os.kill(os.getpid(), signal.SIGKILL)
                return result

            recovery.os.replace = kill_after_live_replacement
            recovery.restore(self._args(lock_journal=str(lock_journal)))
            os._exit(0)
        _, status = os.waitpid(pid, 0)
        self.assertTrue(os.WIFSIGNALED(status) and os.WTERMSIG(status) == signal.SIGKILL)
        self.assertTrue(lock_journal.is_file(), "directory metadata recovery journal must survive SIGKILL")
        for directory in (".", "agents", "agents/agent-123", "credentialsHost", "credentialsHost/vault"):
            path = self.root if directory == "." else self.root / directory
            current = path.stat()
            self.assertEqual((0, 0o500), (current.st_uid, stat.S_IMODE(current.st_mode)))

        swap_pid = os.fork()
        if swap_pid == 0:
            try:
                os.setgid(nobody.pw_gid)
                os.setuid(nobody.pw_uid)
                os.chmod(self.root / "agents", 0o777)
            except PermissionError:
                os._exit(1)
            except BaseException:
                os._exit(2)
            os._exit(0)
        _, swap_status = os.waitpid(swap_pid, 0)
        self.assertEqual(1, os.waitstatus_to_exitcode(swap_status),
                         "the former directory owner must not undo the durable lock")

        recovery.restore(self._args(lock_journal=str(lock_journal)))
        self.assertFalse(lock_journal.exists())
        for directory, expected in expected_directories.items():
            path = self.root if directory == "." else self.root / directory
            current = path.stat()
            self.assertEqual((expected["uid"], expected["gid"], expected["mode"]),
                             (current.st_uid, current.st_gid, stat.S_IMODE(current.st_mode)), directory)

    def test_missing_referenced_credential_key_fails_capture(self):
        (self.root / "credentialsHost/vault/vault-keys/A1B2C3D4.key").unlink()
        with self.assertRaisesRegex(RuntimeError, "missing key"):
            recovery.capture(self._args())

    def test_channel_rotation_snapshot_requires_complete_active_overlap(self):
        private = self.root / "security/channel/gateway-private"
        trust = self.root / "security/channel/channel-trust"
        private.mkdir(parents=True)
        trust.mkdir(parents=True)
        names = (
            (private, "gateway-channel-client.pfx.previous"),
            (private, "gateway-channel-delegation.pfx.previous"),
            (trust, "gateway-channel-client.cer.previous"),
            (trust, "gateway-channel-delegation.cer.previous"),
        )
        for directory, name in names:
            (directory / name).write_bytes(b"previous certificate")
        (trust / "channel-security-rotation.json").write_text(
            "version=1\nphase=ACTIVE\n", encoding="utf-8")
        recovery.capture(self._args())

        (trust / "channel-security-rotation.json").write_text(
            "version=1\nphase=unknown\n", encoding="utf-8")
        with self.assertRaisesRegex(RuntimeError, "unsupported state"):
            recovery.capture(self._args(destination=str(Path(self.temp.name) / "invalid-candidate")))

    def test_incomplete_channel_rotation_slot_fails_closed(self):
        trust = self.root / "security/channel/channel-trust"
        trust.mkdir(parents=True)
        (trust / "gateway-channel-client.cer.next").write_bytes(b"partial rotation")
        with self.assertRaisesRegex(RuntimeError, "next rotation slot is incomplete"):
            recovery.capture(self._args())

    def test_untracked_channel_auth_material_fails_closed(self):
        trust = self.root / "security/channel/channel-trust"
        trust.mkdir(parents=True)
        (trust / "channel-ca.cer").write_bytes(b"ca")
        (trust / "unexpected-private.key").write_bytes(b"must not be omitted")
        with self.assertRaisesRegex(RuntimeError, "unsupported channel security material file"):
            recovery.capture(self._args())


if __name__ == "__main__":
    unittest.main()
